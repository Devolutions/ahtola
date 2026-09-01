using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core.Execution;

/// <summary>
/// The definitions a compiled schema program declares, which its <c>ParseSchema</c> adopts instead of
/// reconstructing them from the rows it just wrote.
/// </summary>
/// <remarks>
/// <para>
/// Turso's <c>ParseSchema</c> can re-read <c>sqlite_schema</c> because its AST-to-schema mapping is
/// lossless. Ahtola's is not: the owning connection resolves a statement against its routed schemas before
/// handing it over, so a trigger's body may have been rewritten and its home and target schemas decided,
/// and none of that is recoverable from the stored SQL. The compiler therefore hands the authoritative
/// definitions to the runtime alongside the program, exactly as it hands over the rows a
/// <c>CREATE TABLE AS SELECT</c> population scans.
/// </para>
/// <para>
/// This is data, not behavior: a described program carries the same definitions and still adopts nothing,
/// because nothing runs.
/// </para>
/// </remarks>
internal sealed record ManagedSchemaPendingObjects(
    IReadOnlyDictionary<string, ViewDefinition>? Views = null,
    IReadOnlyDictionary<string, TriggerDefinition>? Triggers = null,
    IReadOnlyDictionary<string, EmbeddedTable>? Tables = null,
    IReadOnlyDictionary<string, EmbeddedTable>? AlteredTables = null)
{
    public static ManagedSchemaPendingObjects None { get; } = new();

    public bool TryGetView(string name, out ViewDefinition view)
    {
        if (Views is not null && Views.TryGetValue(name, out var found))
        {
            view = found;
            return true;
        }

        view = null!;
        return false;
    }

    public bool TryGetTrigger(string name, out TriggerDefinition trigger)
    {
        if (Triggers is not null && Triggers.TryGetValue(name, out var found))
        {
            trigger = found;
            return true;
        }

        trigger = null!;
        return false;
    }

    /// <summary>
    /// Resolves a table whose rewritten definition the compiler computed, rather than one
    /// <c>ParseSchema</c> should rebuild from stored SQL.
    /// </summary>
    /// <remarks>
    /// An <c>ALTER TABLE</c> that renames a table or a column has to follow the rename into every table
    /// whose foreign keys named it. Those rewrites carry parsed metadata — the parent table and parent
    /// column lists referential enforcement reads — that the stored <c>CREATE TABLE</c> text alone would
    /// have to be reparsed to recover, and reparsing is precisely what SQLite avoids by editing the text in
    /// place. The connection therefore computes the replacement and this is how it reaches adoption.
    /// </remarks>
    public bool TryGetTable(string name, out EmbeddedTable table)
    {
        if (Tables is not null && Tables.TryGetValue(name, out var found))
        {
            table = found;
            return true;
        }

        table = null!;
        return false;
    }

    /// <summary>
    /// Resolves the replacement an <c>ALTER TABLE</c>'s typed opcode applies to the table it alters.
    /// </summary>
    /// <remarks>
    /// Deciding whether an alteration is legal means building the table it produces and validating every
    /// stored row against it, so the replacement is finished before the program starts. The opcode adopts
    /// that table instead of deriving a second one, which is what keeps a large <c>ALTER</c> to a single
    /// pass over the rows and keeps the pass the planner already made the one that honours the caller's
    /// cancellation. A program compiled without one — the shape <c>EXPLAIN</c> describes, and the shape a
    /// direct opcode test builds — falls back to deriving the replacement from the staged table.
    /// </remarks>
    public bool TryGetAlteredTable(string name, out EmbeddedTable table)
    {
        if (AlteredTables is not null && AlteredTables.TryGetValue(name, out var found))
        {
            table = found;
            return true;
        }

        table = null!;
        return false;
    }
}

/// <summary>
/// The connection-owned index behavior a schema program needs but cannot derive from the staged catalog:
/// validating a rebuilt index against its base rows requires the connection's registered collations and
/// its expression evaluator, neither of which the staged catalog carries.
/// </summary>
/// <param name="ValidateAgainstRows">
/// Projects every qualifying base row's key through the index and enforces the index's uniqueness
/// constraint, raising the same <c>UNIQUE constraint failed</c> diagnostic a conflicting write would.
/// </param>
internal sealed record ManagedSchemaIndexServices(
    Action<string, EmbeddedTable, EmbeddedIndex> ValidateAgainstRows);

/// <summary>
/// The late binding a compiled schema program's deferred index-method operands resolve through.
/// </summary>
/// <remarks>
/// A program is compiled before it has a stage, so an instruction that names a method index cannot carry
/// the attachment it will act on. The compiler captures this slot instead, and the owner fills it with the
/// operations bound to the stage just before the program runs. A program that is only described never
/// fills it, which is precisely why <c>EXPLAIN</c> cannot attach a method or mutate its state.
/// </remarks>
internal sealed class ManagedSchemaOperationsSlot
{
    private ManagedSchemaOperations? _operations;

    public void Bind(ManagedSchemaOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations;
    }

    public ManagedSchemaOperations Require()
        => _operations
            ?? throw new VdbeSchemaExecutionException(
                "A schema program's index-method binding was resolved before the program was bound to a stage.");
}

/// <summary>
/// The concrete <see cref="IVdbeSchemaOperations"/> binding: it applies a DDL program's schema opcodes to
/// one <see cref="ManagedSchemaStage"/> — a working <see cref="EmbeddedDatabase.SchemaCatalog"/> clone, an
/// ordered <c>sqlite_schema</c> row set, staged <see cref="PragmaHeaderMetadata"/>, and a
/// <see cref="ManagedSchemaRootPlan"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every method here mutates staged state and nothing else. No pager is touched, no header is written and
/// no live catalog is published; a failed program is undone by dropping the stage. That keeps the durable
/// boundary exactly where it already is — <c>PersistFileCatalog</c> takes the stage's catalog and header
/// and commits them in one pager/WAL commit.
/// </para>
/// <para>
/// The binding addresses a single routed database, <see cref="DatabaseIndex"/>. Multi-database routing
/// (TEMP and attached schemas) arrives with the statement families that need it; until then an instruction
/// naming another database fails loudly rather than silently landing on <c>main</c>.
/// </para>
/// </remarks>
internal sealed class ManagedSchemaOperations : IVdbeResettableSchemaOperations
{
    private readonly ManagedSchemaStage _stage;
    private readonly ManagedSchemaIndexServices? _indexServices;
    private readonly ManagedSchemaPendingObjects _pendingObjects;

    /// <summary>Virtual tables <c>VCreate</c> instantiated during this run, awaiting <c>ParseSchema</c>.</summary>
    private readonly Dictionary<string, EmbeddedDatabase.VirtualTableDefinition> _stagedVirtualTables =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every instance <c>VCreate</c> created during this run, in creation order. The run owns them until
    /// the caller adopts the stage and calls <see cref="RelinquishCreatedVirtualTables"/>; until then a
    /// discard or reset has to disconnect them, because nothing else holds a reference.
    /// </summary>
    private readonly List<EmbeddedDatabase.VirtualTableDefinition> _createdVirtualTables = [];

    public ManagedSchemaOperations(
        ManagedSchemaStage stage,
        int databaseIndex = 0,
        ManagedSchemaIndexServices? indexServices = null,
        ManagedSchemaPendingObjects? pendingObjects = null)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentOutOfRangeException.ThrowIfNegative(databaseIndex);
        _stage = stage;
        _indexServices = indexServices;
        _pendingObjects = pendingObjects ?? ManagedSchemaPendingObjects.None;
        DatabaseIndex = databaseIndex;
    }

    /// <summary>The one database index this binding serves.</summary>
    public int DatabaseIndex { get; }

    public ManagedSchemaStage Stage => _stage;

    /// <summary>
    /// Builds a schema execution context over <paramref name="stage"/>. The context is bound to a single
    /// database, so an instruction naming another one fails rather than landing on the wrong schema.
    /// </summary>
    public static VdbeSchemaExecutionContext CreateContext(
        ManagedSchemaStage stage,
        bool isReadOnly = false,
        ManagedSchemaIndexServices? indexServices = null)
        => new(new ManagedSchemaOperations(stage, databaseIndex: 0, indexServices), databaseCount: 1, isReadOnly);

    /// <summary>Rebuilds the working catalog, schema rows, staged cookies and root plan.</summary>
    public void ResetStagedState()
    {
        DisconnectCreatedVirtualTables();
        _stage.Reset();
    }

    /// <summary>Releases the working catalog's resources without rebuilding it.</summary>
    public void DiscardStagedState()
    {
        DisconnectCreatedVirtualTables();
        _stage.Discard();
    }

    /// <summary>
    /// Hands ownership of everything <c>VCreate</c> created to the caller that just adopted the stage.
    /// </summary>
    /// <remarks>
    /// A created instance is owned by the run until the stage is published: a program that fails, or a
    /// statement that is reset, has to disconnect it or the module leaks a connection. Once the caller has
    /// copied the staged catalog into the live one, the live catalog owns it and disconnecting it here
    /// would tear down a table the schema still points at.
    /// </remarks>
    public void RelinquishCreatedVirtualTables()
    {
        _createdVirtualTables.Clear();
        _stagedVirtualTables.Clear();
    }

    /// <summary>
    /// Records the virtual table <c>VCreate</c> just instantiated, so the <c>ParseSchema</c> that follows
    /// adopts <em>this</em> instance instead of building a second one from the row.
    /// </summary>
    public void StageVirtualTable(
        string name,
        string moduleName,
        IReadOnlyList<string> arguments,
        ManagedVirtualTable table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(table);

        var catalog = _stage.Catalog;
        if (catalog.Tables.ContainsKey(name) || catalog.VirtualTables.ContainsKey(name))
        {
            throw new VdbeSchemaExecutionException(
                $"VCreate cannot create '{name}' because the schema of '{_stage.DatabaseName}' already has that table.");
        }

        var definition = new EmbeddedDatabase.VirtualTableDefinition(
            name,
            moduleName,
            arguments.ToArray(),
            table.GetPersistencePayload(),
            table);
        _stagedVirtualTables[name] = definition;
        _createdVirtualTables.Add(definition);
    }

    private void DisconnectCreatedVirtualTables()
    {
        foreach (var definition in _createdVirtualTables)
            TryDisconnect(definition);
        _createdVirtualTables.Clear();
        _stagedVirtualTables.Clear();
    }

    public long CreateBtree(int database, VdbeCreateBtreeFlags flags)
    {
        RequireBoundDatabase(database, "CreateBtree");
        var kind = flags switch
        {
            VdbeCreateBtreeFlags.Table => ManagedSchemaRootKind.Table,
            VdbeCreateBtreeFlags.Index => ManagedSchemaRootKind.Index,
            _ => throw new VdbeSchemaExecutionException(
                $"CreateBtree requires exactly one of Table or Index, but got {flags}."),
        };

        return _stage.RootPlan.Reserve(kind);
    }

    public void ClearBtree(int database, long rootPage)
    {
        RequireBoundDatabase(database, "ClearBtree");
        _stage.RootPlan.MarkCleared(RequireRootPage(rootPage, "ClearBtree"));
    }

    public long Destroy(int database, long rootPage, bool isTemporary)
    {
        RequireBoundDatabase(database, "Destroy");
        _ = isTemporary;
        _stage.RootPlan.MarkDestroyed(RequireRootPage(rootPage, "Destroy"));

        // Ahtola's managed persistence rewrites the whole database on commit and assigns every root then,
        // so retiring a tree never relocates another tree's root the way SQLite's auto-vacuum page move
        // does. Reporting zero is the accurate answer for this storage engine, not a placeholder.
        return 0;
    }

    public long ReadCookie(int database, VdbeSchemaCookie cookie)
    {
        RequireBoundDatabase(database, "ReadCookie");
        return cookie switch
        {
            VdbeSchemaCookie.SchemaVersion => _stage.PragmaHeader.SchemaVersion,
            VdbeSchemaCookie.UserVersion => _stage.PragmaHeader.UserVersion,
            VdbeSchemaCookie.ApplicationId => _stage.PragmaHeader.ApplicationId,
            VdbeSchemaCookie.DatabaseFormat => _stage.FixedCookies.DatabaseFormat,
            VdbeSchemaCookie.DefaultPageCacheSize => _stage.FixedCookies.DefaultPageCacheSize,
            VdbeSchemaCookie.LargestRootPageNumber => _stage.FixedCookies.LargestRootPageNumber,
            VdbeSchemaCookie.DatabaseTextEncoding => _stage.FixedCookies.DatabaseTextEncoding,
            VdbeSchemaCookie.IncrementalVacuum => _stage.FixedCookies.IncrementalVacuum,
            _ => throw new VdbeSchemaExecutionException($"ReadCookie has no cookie numbered {(int)cookie}."),
        };
    }

    public void SetCookie(int database, VdbeSchemaCookie cookie, long value)
    {
        RequireBoundDatabase(database, "SetCookie");
        switch (cookie)
        {
            case VdbeSchemaCookie.SchemaVersion:
                _stage.StagePragmaHeader(_stage.PragmaHeader with { SchemaVersion = RequireInt32(cookie, value) });
                return;
            case VdbeSchemaCookie.UserVersion:
                _stage.StagePragmaHeader(_stage.PragmaHeader with { UserVersion = RequireInt32(cookie, value) });
                return;
            case VdbeSchemaCookie.ApplicationId:
                _stage.StagePragmaHeader(_stage.PragmaHeader with { ApplicationId = RequireInt32(cookie, value) });
                return;
            case VdbeSchemaCookie.DatabaseFormat:
            case VdbeSchemaCookie.DefaultPageCacheSize:
            case VdbeSchemaCookie.LargestRootPageNumber:
            case VdbeSchemaCookie.DatabaseTextEncoding:
            case VdbeSchemaCookie.IncrementalVacuum:
                // Upstream re-asserts these during the first CREATE in an empty database. Accepting an
                // assertion of the value already in force is honest; accepting a change would stage
                // something the managed commit cannot publish.
                RequireFixedCookieUnchanged(cookie, value);
                return;
            default:
                throw new VdbeSchemaExecutionException($"SetCookie has no cookie numbered {(int)cookie}.");
        }
    }

    /// <summary>
    /// Reparses the staged <c>sqlite_schema</c> rows matching <paramref name="whereClause"/> and replaces
    /// the affected catalog entries in one step.
    /// </summary>
    /// <remarks>
    /// Adoption is all-or-nothing: every matched row is parsed and validated into candidate definitions
    /// first, and the working catalog is only touched once all of them succeed. A row that fails to parse
    /// therefore leaves the catalog exactly as the program found it, which is what makes a fault mid-DDL
    /// safe to discard. Because the catalog's dictionaries hold shared <see cref="EmbeddedTable"/>
    /// instances, a table an index row has to mutate is cloned into the candidate set first; mutating the
    /// catalog's own instance would publish half an adoption the moment a later row failed.
    /// </remarks>
    public void ParseSchema(int database, string? whereClause, int? triggerTargetDatabase)
    {
        RequireBoundDatabase(database, "ParseSchema");
        if (triggerTargetDatabase is { } target && target != DatabaseIndex)
        {
            throw new VdbeSchemaExecutionException(
                $"ParseSchema names trigger target database {target}, but the schema context is bound to {DatabaseIndex}.");
        }

        var matched = ManagedSchemaRowFilter.Parse(whereClause).Apply(_stage.Rows).ToArray();
        if (matched.Length == 0)
            return;

        var catalog = _stage.Catalog;
        var tables = new Dictionary<string, EmbeddedTable>(catalog.Tables, StringComparer.OrdinalIgnoreCase);
        var virtualTables = new Dictionary<string, EmbeddedDatabase.VirtualTableDefinition>(
            catalog.VirtualTables,
            StringComparer.OrdinalIgnoreCase);
        var views = new Dictionary<string, ViewDefinition>(catalog.Views, StringComparer.OrdinalIgnoreCase);
        var triggers = new Dictionary<string, TriggerDefinition>(catalog.Triggers, StringComparer.OrdinalIgnoreCase);

        // Tables already replaced by a candidate instance in this batch. Anything not listed here is still
        // the catalog's own instance and must be cloned before it is mutated.
        var candidateTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adoptedVirtualTables = new List<EmbeddedDatabase.VirtualTableDefinition>();
        var displacedVirtualTables = new List<EmbeddedDatabase.VirtualTableDefinition>();

        try
        {
            foreach (var row in matched.Where(static row => row.IsTable))
                AdoptTableRow(row, tables, virtualTables, candidateTables, adoptedVirtualTables, displacedVirtualTables);
            foreach (var row in matched.Where(static row => row.IsIndex))
                AdoptIndexRow(row, tables, candidateTables);
            foreach (var row in matched.Where(static row => row.IsView))
                views[row.Name] = AdoptViewRow(row);
            foreach (var row in matched.Where(static row => row.IsTrigger))
                triggers[row.Name] = AdoptTriggerRow(row, tables, views, triggers);
        }
        catch
        {
            foreach (var definition in adoptedVirtualTables)
                TryDisconnect(definition);
            throw;
        }

        Republish(catalog.Tables, tables);
        Republish(catalog.VirtualTables, virtualTables);
        Republish(catalog.Views, views);
        Republish(catalog.Triggers, triggers);

        // Only once the replacements are live can the instances they displaced be released; releasing them
        // earlier would disconnect a virtual table the catalog still points at if a later row failed.
        foreach (var definition in displacedVirtualTables)
            TryDisconnect(definition);
    }

    public void DropObject(int database, VdbeSchemaObjectKind kind, string name)
    {
        RequireBoundDatabase(database, DropOpcodeName(kind));
        var catalog = _stage.Catalog;
        switch (kind)
        {
            case VdbeSchemaObjectKind.Table:
                if (catalog.Tables.Remove(name))
                    return;
                // Evicting a virtual table's schema entry is not the same as releasing its module
                // instance: a DROP program retires the instance with VDestroy, and disconnecting here as
                // well would tear the same resource down twice.
                if (catalog.VirtualTables.Remove(name))
                    return;
                break;
            case VdbeSchemaObjectKind.View:
                if (catalog.Views.Remove(name))
                    return;
                break;
            case VdbeSchemaObjectKind.Trigger:
                if (catalog.Triggers.Remove(name))
                    return;
                break;
            case VdbeSchemaObjectKind.Index:
                if (TryDropIndex(name))
                    return;
                break;
            default:
                throw new VdbeSchemaExecutionException($"Unknown schema object kind {(int)kind}.");
        }

        throw new VdbeSchemaExecutionException(
            $"{DropOpcodeName(kind)} cannot evict '{name}' because the schema of '{_stage.DatabaseName}' has no such object.");
    }

    /// <summary>
    /// Evicts an index from the staged catalog without touching the table the live catalog still points
    /// at.
    /// </summary>
    /// <remarks>
    /// The stage shares <see cref="EmbeddedTable"/> instances with the catalog it overlays, so removing
    /// the index in place would be visible to the caller the moment it happened — and would survive a
    /// program that failed afterwards. Replacing the entry with a clone keeps the drop staged: the clone
    /// carries forked method attachments, so a method index's <c>Destroy</c> hook runs against state
    /// nobody else can see, and discarding the stage restores the original table untouched.
    /// </remarks>
    private bool TryDropIndex(string name)
    {
        foreach (var tableName in _stage.Catalog.Tables.Keys.ToArray())
        {
            var table = _stage.Catalog.Tables[tableName];
            if (!table.Indexes.Any(index => string.Equals(index.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var detached = DetachTable(tableName);
            var dropped = detached.Indexes.FirstOrDefault(
                index => string.Equals(index.Name, name, StringComparison.OrdinalIgnoreCase));
            if (dropped is null)
            {
                // Clone() only carries explicit indexes forward, so a constraint-backed index has no
                // clone counterpart. Reaching here means the program tried to drop one, which the
                // compiler rejects; failing closed keeps the two in agreement.
                throw new VdbeSchemaExecutionException(
                    $"DropIndex cannot evict '{name}' because it is a constraint-backed index of '{tableName}'.");
            }

            detached.Indexes.Remove(dropped);
            detached.ForgetMethodAttachment(name);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Replaces the staged entry for <paramref name="tableName"/> with a private clone the program may
    /// mutate, and returns it.
    /// </summary>
    /// <remarks>
    /// The detachment itself belongs to <see cref="ManagedSchemaStage.TryDetachTable"/>, which owns the
    /// working catalog; this only supplies the diagnostic an opcode naming a missing table has to raise.
    /// </remarks>
    private EmbeddedTable DetachTable(string tableName)
    {
        RequireTable(tableName, "IndexMethod");
        return _stage.TryDetachTable(tableName, out var detached)
            ? detached
            : throw new VdbeSchemaExecutionException(
                $"IndexMethod cannot detach '{tableName}' from the schema of '{_stage.DatabaseName}'.");
    }

    /// <summary>
    /// Rebuilds an index from the staged table's rows. In Ahtola an ordinary index's content is derived —
    /// the planner reads keys straight from the base rows and the persistence adapter rebuilds the index
    /// b-tree on every commit — so the refill's observable work is projecting each qualifying row's key
    /// and rejecting a duplicate in a <paramref name="unique"/> index.
    /// </summary>
    public void BuildIndex(int database, string tableName, string indexName, bool unique)
    {
        RequireBoundDatabase(database, "IndexBuild");
        var table = RequireTable(tableName, "IndexBuild");
        var index = table.Indexes.FirstOrDefault(
            candidate => string.Equals(candidate.Name, indexName, StringComparison.OrdinalIgnoreCase))
            ?? throw new VdbeSchemaExecutionException(
                $"IndexBuild cannot rebuild '{indexName}' because table '{tableName}' has no such index; "
                + "a schema program must adopt an index through ParseSchema before rebuilding it.");
        if (index.Unique != unique)
        {
            throw new VdbeSchemaExecutionException(
                $"IndexBuild declares '{indexName}' as {(unique ? "unique" : "non-unique")}, "
                + $"but the adopted index is {(index.Unique ? "unique" : "non-unique")}.");
        }

        if (!unique)
        {
            // A non-unique b-tree index constrains nothing, so its refill has no observable effect on a
            // derived index. Projecting its keys here would reject rows the direct evaluator accepted.
            return;
        }

        var services = _indexServices
            ?? throw new VdbeSchemaExecutionException(
                "IndexBuild requires index services, but the schema context was created without them.");
        services.ValidateAgainstRows(tableName, table, index);
    }

    /// <summary>
    /// Resolves the live method attachment of a staged index, for a deferred
    /// <see cref="VdbeIndexMethodBinding"/>. Because the index lives in the stage's catalog — a clone
    /// <c>ParseSchema</c> or <see cref="TryDropIndex"/> produced — the attachment and every mutation the
    /// method makes to it are transaction-local, so a program that fails afterwards leaves the live
    /// catalog's attachment untouched.
    /// </summary>
    public (ManagedIndexMethodAttachment Attachment, IManagedIndexSource Source) ResolveMethodIndex(
        string tableName,
        string indexName)
    {
        var table = DetachTable(tableName);
        var index = table.Indexes.FirstOrDefault(
            candidate => string.Equals(candidate.Name, indexName, StringComparison.OrdinalIgnoreCase))
            ?? throw new VdbeSchemaExecutionException(
                $"The schema of '{_stage.DatabaseName}' has no index '{indexName}' on '{tableName}'.");
        if (!index.IsMethodIndex)
        {
            throw new VdbeSchemaExecutionException(
                $"Index '{indexName}' on '{tableName}' does not use an index method.");
        }

        return (
            ManagedIndexMethodSemantics.GetAttachment(tableName, table, index),
            new EmbeddedTableIndexSource(table));
    }

    /// <summary>
    /// Renames a table in the staged schema, following upstream's <c>op_rename_table</c>
    /// (execute.rs). It moves the catalog entry with the rows it already holds — a rename never rebuilds a
    /// table — rewrites the stored text the new name appears in, retargets the triggers that watched it,
    /// and carries the AUTOINCREMENT bookkeeping across with it.
    /// </summary>
    /// <remarks>
    /// The watermark row of <c>sqlite_sequence</c> and the implicit backing table are renamed here rather
    /// than by emitted bytecode, which is where upstream's comment says the in-memory half of that rename
    /// belongs (alter.rs:1720). Ahtola keeps the watermark in catalog state rather than in a b-tree the
    /// program could scan, so a cursor loop would have nothing to walk; the backing table's own
    /// <c>sqlite_schema</c> row is still rewritten by the program, so the row set and the catalog stay in
    /// agreement.
    /// </remarks>
    public void RenameTable(int database, string from, string to)
    {
        RequireBoundDatabase(database, "RenameTable");
        var catalog = _stage.Catalog;
        RequireAvailableName(to, from);

        if (catalog.Tables.Remove(from, out var table))
        {
            var renamed = EmbeddedDatabase.CreateRenamedTable(table, to);
            EmbeddedDatabase.RewriteRenamedTableSelfSql(renamed, from, to);
            catalog.Tables[to] = renamed;
            RetargetTriggers(from, to);
            if (renamed.IsAutoIncrement)
                RenameAutoIncrementState(from, to);

            return;
        }

        if (catalog.VirtualTables.Remove(from, out var virtualTable))
        {
            // The module was already renamed in place by VRename, so the catalog entry moves with the
            // instance it names. Rebuilding it here would connect a second instance and leave the module's
            // create/disconnect hooks unbalanced for a statement that only renamed a table.
            catalog.VirtualTables[to] = (virtualTable with { Name = to }).WithCurrentPersistencePayload();
            RetargetTriggers(from, to);
            return;
        }

        throw new VdbeSchemaExecutionException(
            $"RenameTable cannot rename '{from}' because the schema of '{_stage.DatabaseName}' has no such table.");
    }

    /// <summary>
    /// Carries an AUTOINCREMENT table's allocator state across a rename: the <c>sqlite_sequence</c>
    /// watermark row that names it, and the implicit backing table that records the same watermark under a
    /// derived name.
    /// </summary>
    private void RenameAutoIncrementState(string from, string to)
    {
        // The watermark row is edited in place, so the table holding it has to become the stage's own
        // clone first; mutating the instance the caller's catalog still points at would publish the rename
        // before the program reached Halt.
        if (!_stage.TryDetachTable(EmbeddedDatabase.SqliteSequenceTableName, out _))
        {
            throw new VdbeSchemaExecutionException(
                $"RenameTable cannot rename the AUTOINCREMENT table '{from}' because the schema of "
                + $"'{_stage.DatabaseName}' has no {EmbeddedDatabase.SqliteSequenceTableName}.");
        }

        EmbeddedDatabase.RenameSqliteSequenceRows(_stage.Catalog.Tables, from, to);
        EmbeddedDatabase.RenameAutoIncrementSequenceBackingTable(_stage.Catalog.Tables, from, to);
    }

    /// <summary>
    /// Appends a column to a staged table, following upstream's <c>op_add_column</c>. The declaration is
    /// reparsed with the ordinary DDL parser, the table's stored <c>CREATE TABLE</c> text is extended by
    /// inserting the column's own source text exactly as SQLite edits it, and the column is added — which
    /// is also what widens every stored row with the column's default and computes a generated column's
    /// value for the rows that already exist.
    /// </summary>
    /// <remarks>
    /// When the compiler carried the widened table along with the program, the opcode adopts that instead:
    /// the statement built and validated it while deciding the alteration was legal, so widening a second
    /// time would repeat the backfill over every stored row for a result that is already known.
    /// </remarks>
    public void AddColumn(
        int database,
        string table,
        string columnName,
        string columnDefinition,
        string? columnSql)
    {
        RequireBoundDatabase(database, "AddColumn");
        RequireTable(table, "AddColumn");
        var column = ParseColumnDefinition(table, columnDefinition, "AddColumn");
        if (!string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
        {
            throw new VdbeSchemaExecutionException(
                $"AddColumn names column '{columnName}' but its definition declares '{column.Name}'.");
        }

        if (TryAdoptPreparedReplacement(table, "AddColumn"))
            return;

        // AddColumn widens the table in place, so it must land on the stage's private clone: mutating the
        // instance the caller's catalog shares would survive a program that failed afterwards.
        if (!_stage.TryDetachTable(table, out var target))
        {
            throw new VdbeSchemaExecutionException(
                $"AddColumn cannot detach '{table}' from the schema of '{_stage.DatabaseName}'.");
        }

        // SQLite rewrites a table's stored text by inserting the added column's declaration, and falls back
        // to regenerating it when there is no faithful text to edit.
        target.Sql = target.Sql is not null && columnSql is not null
            ? AlterTableSqlRewriter.InsertAddedColumn(target.Sql, columnSql)
            : null;
        target.AddColumn(column);
    }

    /// <summary>
    /// Removes a column from a staged table, following upstream's <c>op_drop_column</c>. The replacement
    /// carries the narrower row layout: projecting every stored row out of the old one is the row rewrite
    /// upstream emits as a separate loop (<c>emit_rewrite_table_rows</c>, alter.rs:2312), and it lands on a
    /// table the stage owns so a program that fails afterwards leaves the original untouched.
    /// </summary>
    /// <remarks>
    /// The projection is performed once. When the compiler carried the narrowed table along with the
    /// program the opcode adopts it, because the statement already projected every row while deciding the
    /// drop was legal; only a program compiled without one projects here.
    /// </remarks>
    public void DropColumn(int database, string table, int columnIndex)
    {
        RequireBoundDatabase(database, "DropColumn");
        var target = RequireTable(table, "DropColumn");
        var columnName = RequireColumnAt(target, columnIndex, "DropColumn");
        if (TryAdoptPreparedReplacement(table, "DropColumn"))
            return;

        _stage.Catalog.Tables[table] = target.CreateWithoutColumn(columnName, CancellationToken.None);
    }

    /// <summary>
    /// Rewrites one column of a staged table, following upstream's <c>op_alter_column</c>. A rename runs
    /// the token-aware rewrite so every stored expression, index key and partial-index predicate that named
    /// the column follows it; a full replacement rebuilds the column and coerces every stored value to the
    /// replacement's affinity, which is the row rewrite upstream emits when the declared type changes.
    /// </summary>
    /// <remarks>
    /// As with the other typed alterations, the rewritten table the compiler carried is adopted when there
    /// is one: the coercion pass over every stored row belongs to the statement that validated it, not to a
    /// second pass here.
    /// </remarks>
    public void AlterColumn(
        int database,
        string table,
        int columnIndex,
        string columnDefinition,
        bool rename,
        bool quoteNewName)
    {
        RequireBoundDatabase(database, "AlterColumn");
        var target = RequireTable(table, "AlterColumn");
        var columnName = RequireColumnAt(target, columnIndex, "AlterColumn");
        if (TryAdoptPreparedReplacement(table, "AlterColumn"))
            return;

        _stage.Catalog.Tables[table] = rename
            ? target.CreateWithRenamedColumn(
                columnName,
                columnDefinition,
                quoteNewName,
                CancellationToken.None)
            : target.CreateWithAlteredColumn(
                columnName,
                ParseColumnDefinition(table, columnDefinition, "AlterColumn"),
                CancellationToken.None);
    }

    /// <summary>
    /// Adopts the replacement the compiler prepared for <paramref name="table"/>, when it carried one.
    /// </summary>
    /// <remarks>
    /// The stage takes a clone rather than the plan's own instance. The clone is what makes the adoption
    /// atomic in the sense the rest of the staging boundary relies on: nothing outside the stage can
    /// observe it, discarding the stage discards it, and the plan stays exactly as the statement built it
    /// so re-running the same program cannot see a table an earlier run mutated.
    /// </remarks>
    private bool TryAdoptPreparedReplacement(string table, string opcodeName)
    {
        if (!_pendingObjects.TryGetAlteredTable(table, out var prepared))
            return false;

        if (!string.Equals(prepared.Name, table, StringComparison.OrdinalIgnoreCase))
        {
            throw new VdbeSchemaExecutionException(
                $"{opcodeName} was given a prepared replacement named '{prepared.Name}' for table '{table}'.");
        }

        _stage.Catalog.Tables[table] = prepared.Clone();
        return true;
    }

    private void AdoptTableRow(
        ManagedSchemaRow row,
        Dictionary<string, EmbeddedTable> tables,
        Dictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        HashSet<string> candidateTables,
        List<EmbeddedDatabase.VirtualTableDefinition> adopted,
        List<EmbeddedDatabase.VirtualTableDefinition> displaced)
    {
        if (row.IsVirtualTable)
        {
            if (tables.ContainsKey(row.Name))
            {
                throw new VdbeSchemaExecutionException(
                    $"ParseSchema cannot adopt virtual table '{row.Name}' because an ordinary table already uses that name.");
            }

            // A virtual table the running program created is adopted as the instance VCreate produced.
            // Rebuilding it from the row would connect a second instance and leave the module's create,
            // sync and disconnect hooks unbalanced.
            if (_stagedVirtualTables.TryGetValue(row.Name, out var staged))
            {
                if (virtualTables.TryGetValue(row.Name, out var replaced) && !ReferenceEquals(replaced, staged))
                    displaced.Add(replaced);

                virtualTables[row.Name] = staged;
                return;
            }

            var definition = ManagedSchemaRowParser.ParseVirtualTable(row);
            adopted.Add(definition);
            if (virtualTables.TryGetValue(row.Name, out var previous))
                displaced.Add(previous);

            virtualTables[row.Name] = definition;
            return;
        }

        RequireKnownRoot(row);
        if (virtualTables.ContainsKey(row.Name))
        {
            throw new VdbeSchemaExecutionException(
                $"ParseSchema cannot adopt table '{row.Name}' because a virtual table already uses that name.");
        }

        // A table the compiler rewrote is adopted as the definition it computed: its foreign-key metadata
        // followed a rename that the stored text records but a reparse would have to re-derive.
        if (_pendingObjects.TryGetTable(row.Name, out var declared))
        {
            if (tables.TryGetValue(row.Name, out var replaced) && !ReferenceEquals(replaced, declared))
                declared.AdoptContentFrom(replaced);

            tables[row.Name] = declared;
            candidateTables.Add(row.Name);
            return;
        }

        var table = ManagedSchemaRowParser.ParseTable(row);
        if (tables.TryGetValue(row.Name, out var existing))
            table.AdoptContentFrom(existing);

        tables[row.Name] = table;
        candidateTables.Add(row.Name);
    }

    private void AdoptIndexRow(
        ManagedSchemaRow row,
        Dictionary<string, EmbeddedTable> tables,
        HashSet<string> candidateTables)
    {
        RequireKnownRoot(row);
        if (!tables.TryGetValue(row.TableName, out var table))
        {
            throw new VdbeSchemaExecutionException(
                $"ParseSchema cannot adopt index '{row.Name}' because its table '{row.TableName}' is not in the schema.");
        }

        if (row.IsImplicitIndex)
        {
            // An implicit constraint index is not declared by SQL; the table's own constraints produce it.
            // Reparsing therefore only asserts the row still corresponds to a constraint index the parsed
            // table created, instead of manufacturing an index the definition does not imply.
            var implicitIndex = table.Indexes.FirstOrDefault(index =>
                index.Origin != EmbeddedIndexOrigin.Explicit
                && string.Equals(index.Name, row.Name, StringComparison.OrdinalIgnoreCase));
            if (implicitIndex is null)
            {
                throw new VdbeSchemaExecutionException(
                    $"ParseSchema cannot adopt implicit index '{row.Name}' because table '{row.TableName}' "
                    + "declares no matching UNIQUE or PRIMARY KEY constraint.");
            }

            return;
        }

        var parsed = ManagedSchemaRowParser.ParseIndex(row, table);
        if (candidateTables.Add(row.TableName))
        {
            table = table.Clone();
            tables[row.TableName] = table;
        }

        table.Indexes.RemoveAll(index =>
            string.Equals(index.Name, row.Name, StringComparison.OrdinalIgnoreCase)
            && index.Origin == EmbeddedIndexOrigin.Explicit);
        table.Indexes.Add(parsed.Index);
        parsed.RestoreMethodState(row.TableName, table);
    }

    /// <summary>
    /// Adopts a <c>view</c> row, preferring the definition the compiler declared for it.
    /// </summary>
    /// <remarks>
    /// The stored SQL of a view is faithful, but reparsing it here would re-derive a definition the
    /// compiler already holds and would re-run the persistence-only checks the file store owns. Preferring
    /// the declared definition keeps <c>CREATE VIEW</c> behaving exactly as it did before it was lowered:
    /// a view over a bind parameter or a managed callback is legal in memory and refused at persist time.
    /// </remarks>
    private ViewDefinition AdoptViewRow(ManagedSchemaRow row)
        => _pendingObjects.TryGetView(row.Name, out var declared)
            ? declared
            : ManagedSchemaRowParser.ParseView(row, ManagedSchemaAdoptionMode.Reparse);

    private TriggerDefinition AdoptTriggerRow(
        ManagedSchemaRow row,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers)
    {
        // A rewritten trigger keeps its declaration order so firing order survives a reparse; a new one
        // lands after every trigger the schema already had.
        triggers.TryGetValue(row.Name, out var existing);
        var declarationOrder = existing is not null
            ? existing.DeclarationOrder
            : triggers.Count == 0
                ? 0
                : triggers.Values.Max(static value => value.DeclarationOrder) + 1;

        if (_pendingObjects.TryGetTrigger(row.Name, out var declared))
            return declared with { DeclarationOrder = declarationOrder };

        // Which schema owns a trigger, and which schema owns the table it watches, are routing facts the
        // connection decided; the stored SQL cannot express an unqualified TEMP trigger's foreign target.
        // A rewritten row therefore carries them over from the definition it replaces, which is what
        // upstream's ParseSchema does with trigger_target_database_id.
        return ManagedSchemaRowParser.ParseTrigger(
            row,
            tables,
            views,
            declarationOrder,
            ManagedSchemaAdoptionMode.Reparse,
            existing?.TargetSchema,
            existing?.Temporary ?? false);
    }

    private void RetargetTriggers(string from, string to)
    {
        foreach (var name in _stage.Catalog.Triggers.Keys.ToArray())
        {
            var trigger = _stage.Catalog.Triggers[name];
            if (string.Equals(trigger.TableName, from, StringComparison.OrdinalIgnoreCase))
                _stage.Catalog.Triggers[name] = trigger with { TableName = to };
        }
    }

    private void RequireAvailableName(string candidate, string current)
    {
        if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            return;

        var catalog = _stage.Catalog;
        if (catalog.Tables.ContainsKey(candidate)
            || catalog.VirtualTables.ContainsKey(candidate)
            || catalog.Views.ContainsKey(candidate)
            || catalog.Triggers.ContainsKey(candidate)
            || catalog.Tables.Values.Any(table => table.Indexes.Any(
                index => string.Equals(index.Name, candidate, StringComparison.OrdinalIgnoreCase))))
        {
            throw new VdbeSchemaExecutionException($"there is already an object named {candidate}");
        }
    }

    private EmbeddedTable RequireTable(string name, string opcodeName)
        => _stage.Catalog.Tables.TryGetValue(name, out var table)
            ? table
            : throw new VdbeSchemaExecutionException(
                $"{opcodeName} cannot alter '{name}' because the schema of '{_stage.DatabaseName}' has no such table.");

    private static string RequireColumnAt(EmbeddedTable table, int columnIndex, string opcodeName)
        => columnIndex >= 0 && columnIndex < table.Columns.Length
            ? table.Columns[columnIndex]
            : throw new VdbeSchemaExecutionException(
                $"{opcodeName} addresses column {columnIndex} of '{table.Name}', which has {table.Columns.Length} column(s).");

    private static EmbeddedColumn ParseColumnDefinition(string table, string definition, string opcodeName)
    {
        if (string.IsNullOrWhiteSpace(definition))
            throw new VdbeSchemaExecutionException($"{opcodeName} requires a column definition.");

        // Reuse the ordinary DDL parser rather than a second column grammar: a definition that ALTER TABLE
        // would reject must be rejected here too.
        var sql = $"ALTER TABLE \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ADD COLUMN {definition}";
        try
        {
            if (SqlParser.Parse(sql, SqlParameterMap.Parse(sql)) is AlterTableAddColumnStatement parsed)
                return parsed.Column;
        }
        catch (EmbeddedSqlException exception)
        {
            throw new VdbeSchemaExecutionException(
                $"{opcodeName} cannot parse the column definition \"{definition}\": {exception.Message}");
        }

        throw new VdbeSchemaExecutionException(
            $"{opcodeName} cannot parse the column definition \"{definition}\".");
    }

    private void RequireKnownRoot(ManagedSchemaRow row)
    {
        if (row.RootPage >= 2)
            return;

        throw new VdbeSchemaExecutionException(
            $"ParseSchema cannot adopt '{row.Name}' because its sqlite_schema row has rootpage {row.RootPage}.");
    }

    private static uint RequireRootPage(long rootPage, string opcodeName)
        => rootPage is >= 2 and <= uint.MaxValue
            ? (uint)rootPage
            : throw new VdbeSchemaExecutionException(
                $"{opcodeName} addresses root page {rootPage}, which is not an allocatable b-tree root.");

    private static int RequireInt32(VdbeSchemaCookie cookie, long value)
        => value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new VdbeSchemaExecutionException(
                $"SetCookie cannot store {value} in the 32-bit {cookie} cookie.");

    private void RequireFixedCookieUnchanged(VdbeSchemaCookie cookie, long value)
    {
        var current = ReadCookie(DatabaseIndex, cookie);
        if (current == value)
            return;

        throw new VdbeSchemaExecutionException(
            $"SetCookie cannot change the {cookie} cookie of '{_stage.DatabaseName}' from {current} to {value}: "
            + "the managed persistence adapter derives it from the writer and cannot publish a staged value.");
    }

    private static void Republish<TValue>(
        Dictionary<string, TValue> destination,
        Dictionary<string, TValue> source)
    {
        destination.Clear();
        foreach (var entry in source)
            destination[entry.Key] = entry.Value;
    }

    private static void TryDisconnect(EmbeddedDatabase.VirtualTableDefinition definition)
    {
        try
        {
            definition.Table.DisconnectInstance();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A module that fails to disconnect must not mask the schema failure that is unwinding.
        }
    }

    private static string DropOpcodeName(VdbeSchemaObjectKind kind) => kind switch
    {
        VdbeSchemaObjectKind.Table => "DropTable",
        VdbeSchemaObjectKind.View => "DropView",
        VdbeSchemaObjectKind.Index => "DropIndex",
        VdbeSchemaObjectKind.Trigger => "DropTrigger",
        _ => throw new VdbeSchemaExecutionException($"Unknown schema object kind {(int)kind}."),
    };

    private void RequireBoundDatabase(int database, string opcodeName)
    {
        if (database != DatabaseIndex)
        {
            throw new VdbeSchemaExecutionException(
                $"{opcodeName} addresses database {database}, but this schema binding serves database {DatabaseIndex} ('{_stage.DatabaseName}').");
        }
    }
}
