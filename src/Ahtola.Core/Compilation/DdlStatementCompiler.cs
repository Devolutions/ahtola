using Ahtola.Core.Execution;
using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core.Compilation;

/// <summary>
/// The connection-supplied facts and checks a DDL compilation needs that cannot be derived from the
/// statement or the catalog alone.
/// </summary>
/// <param name="Catalog">The schema the statement is resolved against.</param>
/// <param name="SchemaVersion">
/// The schema cookie currently in force. The compiled program stages <c>SchemaVersion + 1</c> through
/// <c>SetCookie</c>, which is the value the outer commit must publish.
/// </param>
/// <param name="ValidateCheckConstraintFunctions">
/// Resolves the functions named by a CHECK constraint. It depends on the connection's registered scalar
/// functions, so it cannot be a static rule.
/// </param>
/// <param name="EnforceMaxPageCount">
/// Rejects the statement when creating this many additional b-trees would exceed
/// <c>PRAGMA max_page_count</c>. <c>EXPLAIN</c> passes a no-op: describing a program must not consult a
/// runtime storage limit, and it never allocates a page.
/// </param>
/// <param name="Database">The routed database index every schema instruction addresses.</param>
/// <param name="HasCollation">
/// Reports whether a collation name resolves, on this connection, to a built-in or an already-registered
/// callback. Building an index orders its rows by every declared column's collation, so a name that can
/// never be honored has to fail the statement rather than publish an index that would silently compare
/// with BINARY.
/// </param>
/// <param name="IsRegisteredScalarFunction">
/// Reports whether a name/arity pair names an application-defined scalar function. SQLite forbids those
/// inside index expressions and partial-index WHERE clauses, because a later connection need not have
/// registered them.
/// </param>
/// <param name="IsMvccEnabled">
/// Whether the connection runs under MVCC. A managed index method must declare snapshot-safe storage
/// before it may be written under MVCC.
/// </param>
internal sealed record DdlCompilationContext(
    EmbeddedDatabase.SchemaCatalog Catalog,
    long SchemaVersion,
    Action<Expression> ValidateCheckConstraintFunctions,
    Action<int> EnforceMaxPageCount,
    int Database = 0,
    Func<string, bool>? HasCollation = null,
    Func<string, int, bool>? IsRegisteredScalarFunction = null,
    bool IsMvccEnabled = false);

/// <summary>
/// One row source a compiled schema program populates a freshly created table from.
/// </summary>
/// <param name="SourceCursor">The read cursor the loop scans.</param>
/// <param name="TargetCursor">The write cursor the loop inserts through.</param>
/// <param name="TargetTableName">The table the target cursor writes to.</param>
/// <param name="Rows">The rows the source cursor exposes.</param>
internal sealed record CompiledSchemaPopulation(
    Cursor SourceCursor,
    Cursor TargetCursor,
    string TargetTableName,
    IReadOnlyList<SqlValue[]> Rows);

/// <summary>
/// One stage-resident table a compiled schema program scans in order to delete rows from: the
/// <c>sqlite_sequence</c> watermark of a dropped AUTOINCREMENT table, or a change-capture version entry.
/// </summary>
/// <param name="Cursor">The cursor the scan walks and deletes through.</param>
/// <param name="TableName">The staged table the cursor is bound to.</param>
internal sealed record CompiledSchemaTableScan(Cursor Cursor, string TableName);

/// <summary>
/// One <c>sqlite_schema</c> row an <c>ALTER TABLE</c> program rewrites: the row named
/// <paramref name="CurrentName"/> is deleted where it stands and written back with this identity and text.
/// </summary>
/// <param name="EntryType">The row's <c>type</c> column, which an ALTER never changes.</param>
/// <param name="CurrentName">The stored spelling the delete scan searches for.</param>
/// <param name="Name">The <c>name</c> column the rewritten row carries.</param>
/// <param name="TableName">The <c>tbl_name</c> column the rewritten row carries.</param>
/// <param name="Sql">The <c>sql</c> column the rewritten row carries; null for an implicit index.</param>
/// <param name="OwnsRootPage">
/// Whether the object owns a b-tree whose <c>rootpage</c> has to survive the rewrite. Tables and indexes
/// keep the storage they already had across an ALTER; views, triggers and virtual tables are stored with
/// rootpage 0 and have nothing to carry.
/// </param>
internal sealed record CompiledSchemaRowRewrite(
    string EntryType,
    string CurrentName,
    string Name,
    string TableName,
    string? Sql,
    bool OwnsRootPage);

/// <summary>
/// One <c>sqlite_schema</c> index row an <c>ALTER TABLE</c> program brings into existence, together with
/// the b-tree the program must allocate for it.
/// </summary>
/// <remarks>
/// Only a constraint-backed index reaches this: an alteration that stops a table-level PRIMARY KEY from
/// being a rowid alias turns a constraint the rowid used to enforce into one that needs an index of its
/// own. The row is written exactly as <c>CREATE INDEX</c> writes one, so a reload cannot tell the two apart.
/// </remarks>
/// <param name="Name">The <c>name</c> column the new row carries.</param>
/// <param name="TableName">The <c>tbl_name</c> column the new row carries.</param>
/// <param name="Sql">The <c>sql</c> column; null for a constraint-backed index, which SQLite stores with none.</param>
internal sealed record CompiledSchemaIndexCreation(string Name, string TableName, string? Sql);

/// <summary>
/// Everything an <c>ALTER TABLE</c> program has to write that the statement alone cannot decide: the
/// <c>sqlite_schema</c> rows whose text or identity the alteration changes, and the dependent catalog
/// objects <c>ParseSchema</c> adopts instead of reparsing.
/// </summary>
/// <remarks>
/// <para>
/// Upstream computes the same facts inside <c>translate_alter_table</c> because its translator can reach
/// the whole schema. The managed port cannot: deciding whether an alteration is legal means resolving
/// every dependent view and trigger body against the schema the alteration would produce, which needs the
/// connection's evaluator. That decision therefore happens on the connection and arrives here as data,
/// exactly as the already-migrated virtual-table rename does — what the compiler owns is the bytecode.
/// </para>
/// <para>
/// A plan carrying no dependents is the shape <c>EXPLAIN</c> describes: it reports the alteration's own
/// row rewrites and typed opcode without evaluating a single dependent body.
/// </para>
/// </remarks>
internal sealed record CompiledAlterTablePlan
{
    /// <summary>The <c>sqlite_schema</c> rows the program rewrites, in emission order.</summary>
    public IReadOnlyList<CompiledSchemaRowRewrite> RowRewrites { get; init; } = [];

    /// <summary>
    /// The objects whose <c>sqlite_schema</c> row the program deletes outright, each with the b-tree its
    /// row records. Only the implicit AUTOINCREMENT backing table reaches this: clearing a rowid alias
    /// retires the watermark's storage with it.
    /// </summary>
    public IReadOnlyList<string> DroppedTables { get; init; } = [];

    /// <summary>
    /// The dependent tables whose rewritten definition <c>ParseSchema</c> adopts.
    /// </summary>
    public IReadOnlyDictionary<string, EmbeddedTable> Tables { get; init; } =
        new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The replacement the plan already built for the table the alteration rewrites, keyed by that table's
    /// stored name. The typed opcode adopts a stage-owned clone of it rather than deriving a second one.
    /// </summary>
    /// <remarks>
    /// This is not the same thing as <see cref="Tables"/>: those are the <em>dependent</em> tables a
    /// <c>ParseSchema</c> adopts because their stored text alone cannot carry their rewritten foreign-key
    /// metadata. This one is the altered table itself, and it exists so <c>AddColumn</c>,
    /// <c>DropColumn</c> and <c>AlterColumn</c> do not repeat the per-row projection the plan performed
    /// while deciding the alteration was legal. A plan carrying none — the shape <c>EXPLAIN</c>
    /// describes — still compiles to exactly the same program, because a described program never runs its
    /// typed opcode.
    /// </remarks>
    public IReadOnlyDictionary<string, EmbeddedTable> ReplacementTables { get; init; } =
        new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The indexes whose <c>sqlite_schema</c> row the program deletes and whose b-tree it retires. Only a
    /// constraint-backed index reaches this: rewriting the column a UNIQUE constraint was declared on can
    /// leave the implicit index it produced without a declaration to come from.
    /// </summary>
    public IReadOnlyList<string> DroppedIndexes { get; init; } = [];

    /// <summary>
    /// The constraint-backed indexes whose <c>sqlite_schema</c> row the program writes and whose b-tree it
    /// allocates. Only an alteration that retires a rowid alias while keeping the PRIMARY KEY it named
    /// reaches this: the constraint the rowid enforced now needs an index.
    /// </summary>
    public IReadOnlyList<CompiledSchemaIndexCreation> AddedIndexes { get; init; } = [];

    /// <summary>The dependent views whose rewritten definition <c>ParseSchema</c> adopts.</summary>
    public IReadOnlyDictionary<string, ViewDefinition> Views { get; init; } =
        new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The dependent triggers whose rewritten definition <c>ParseSchema</c> adopts.</summary>
    public IReadOnlyDictionary<string, TriggerDefinition> Triggers { get; init; } =
        new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>sqlite_sequence</c> table whose AUTOINCREMENT watermark rows the program deletes, when the
    /// alteration retires one.
    /// </summary>
    public string? ClearedSequenceTableName { get; init; }

    /// <summary>The table whose watermark rows <see cref="ClearedSequenceTableName"/> holds.</summary>
    public string? ClearedSequenceOwner { get; init; }

    /// <summary>An empty plan: the alteration touches only its own table.</summary>
    public static CompiledAlterTablePlan None { get; } = new();
}

/// <summary>
/// A lowered DDL statement: the typed program plus the cursor identities its runtime bindings must be
/// attached to.
/// </summary>
/// <remarks>
/// The program is complete and self-describing — <c>EXPLAIN</c> renders exactly this — but it is not yet
/// bound to anything. <see cref="CreateBindings"/> attaches it to a <see cref="ManagedSchemaStage"/> at
/// execution time, which is why describing a program can never mutate a schema.
/// </remarks>
internal sealed record CompiledSchemaProgram(
    VdbeProgram Program,
    Cursor SchemaCursor,
    IReadOnlyList<CompiledSchemaPopulation> Populations,
    long StagedSchemaVersion,
    bool IsNoOp)
{
    /// <summary>
    /// The definitions this program's <c>ParseSchema</c> adopts instead of rebuilding them from the rows
    /// it wrote. See <see cref="ManagedSchemaPendingObjects"/> for why the managed port needs them.
    /// </summary>
    public ManagedSchemaPendingObjects PendingObjects { get; init; } = ManagedSchemaPendingObjects.None;

    /// <summary>
    /// The virtual-table instances this program's <c>VDestroy</c>/<c>VRename</c> address, indexed by
    /// cursor. <c>VCreate</c> needs none: it produces its instance rather than acting on one.
    /// </summary>
    public IReadOnlyList<VdbeVirtualTableBinding?>? VirtualTableBindings { get; init; }

    /// <summary>
    /// The slot this program's deferred index-method bindings resolve through, when it has any.
    /// </summary>
    /// <remarks>
    /// A schema program's index-method operands live in the transaction-local catalog the program is
    /// building, which does not exist at compile time. The compiler therefore captures this slot in each
    /// deferred binding and <see cref="Bind"/> fills it once the program is attached to a stage. Leaving
    /// it empty is what makes <c>EXPLAIN</c> inert: nothing resolves, so no method is ever attached, and
    /// a described <c>CREATE VIRTUAL TABLE</c> can never publish a module instance.
    /// </remarks>
    public ManagedSchemaOperationsSlot? OperationsSlot { get; init; }

    /// <summary>
    /// The stage-resident tables this program deletes rows from through an ordinary write cursor, rather
    /// than through <c>sqlite_schema</c>.
    /// </summary>
    public IReadOnlyList<CompiledSchemaTableScan> TableScans { get; init; } = [];

    /// <summary>Attaches the operations a running program's deferred bindings resolve through.</summary>
    public void Bind(ManagedSchemaOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        OperationsSlot?.Bind(operations);
    }

    /// <summary>Builds the cursor sources and write targets this program's cursors need.</summary>
    /// <param name="stage">The transaction-local schema the program stages into.</param>
    /// <param name="cancellationToken">
    /// Cancels a population scan between rows, preserving the per-row cancellation the direct row copy had.
    /// </param>
    public (VdbeCursorSource?[] CursorSources, VdbeWriteTarget?[] WriteTargets) CreateBindings(
        ManagedSchemaStage stage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var cursorSources = new VdbeCursorSource?[Program.CursorCount];
        var writeTargets = new VdbeWriteTarget?[Program.CursorCount];

        cursorSources[SchemaCursor.Index] = ManagedSchemaProgramBindings.CreateSchemaCursorSource(stage);
        writeTargets[SchemaCursor.Index] = ManagedSchemaProgramBindings.CreateSchemaWriteTarget(stage);

        foreach (var population in Populations)
        {
            cursorSources[population.SourceCursor.Index] =
                ManagedSchemaProgramBindings.CreatePopulationCursorSource(population.Rows, cancellationToken);
            cursorSources[population.TargetCursor.Index] =
                ManagedSchemaProgramBindings.CreateTableCursorSource(stage, population.TargetTableName);
            writeTargets[population.TargetCursor.Index] =
                ManagedSchemaProgramBindings.CreateTableWriteTarget(stage, population.TargetTableName);
        }

        foreach (var scan in TableScans)
        {
            cursorSources[scan.Cursor.Index] =
                ManagedSchemaProgramBindings.CreateMutableTableCursorSource(stage, scan.TableName);
            writeTargets[scan.Cursor.Index] =
                ManagedSchemaProgramBindings.CreateMutableTableWriteTarget(stage, scan.TableName);
        }

        return (cursorSources, writeTargets);
    }
}

/// <summary>
/// Lowers the DDL statements Ahtola supports into typed <see cref="VdbeProgram"/>s, following Turso's
/// translators in <c>core/translate/schema.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Compilation performs every check that decides whether the statement is legal — reserved names, name
/// conflicts, <c>IF NOT EXISTS</c>, WITHOUT ROWID's mandatory primary key, CHECK-constraint function
/// resolution, and the internal AUTOINCREMENT tables' name availability — and then emits bytecode that
/// performs the effects. That split is upstream's: <c>translate_create_table</c> raises name conflicts
/// while translating, so a duplicate is reported before a single b-tree is allocated.
/// </para>
/// <para>
/// Nothing here touches a catalog, a stage, or storage. The compiler reads the catalog to resolve names
/// and to compute the table definition whose SQL and implicit indexes the schema rows describe, and
/// produces a program plus the cursor identities its bindings attach to. All schema effects happen when
/// the program runs.
/// </para>
/// </remarks>
internal static class DdlStatementCompiler
{
    /// <summary>
    /// Lowers <c>CREATE TABLE</c> and the materialized form of <c>CREATE TABLE AS SELECT</c>, following
    /// <c>translate_create_table</c> (schema.rs:1100).
    /// </summary>
    /// <remarks>
    /// A <c>CREATE TABLE IF NOT EXISTS</c> whose object already exists compiles to a program that only
    /// halts, matching upstream's early <c>return Ok(())</c>: there is nothing to do, and describing it
    /// still yields a real program rather than a special case.
    /// </remarks>
    public static CompiledSchemaProgram CompileCreateTable(
        CreateTableStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (EmbeddedDatabase.IsReservedObjectName(statement.Name))
            throw new EmbeddedSqlException($"object name reserved for internal use: {statement.Name}");
        if (catalog.Tables.ContainsKey(statement.Name) || catalog.VirtualTables.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"table {statement.Name} already exists");
        }
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a view named {statement.Name}");
        if (EmbeddedDatabase.TryFindIndex(catalog.Tables, statement.Name, out _, out _))
            throw new EmbeddedSqlException($"there is already an index named {statement.Name}");

        // A WITHOUT ROWID table has no hidden rowid to fall back on, so SQLite requires a
        // PRIMARY KEY; reject the table before it is registered when none is declared.
        if (statement.WithoutRowid
            && statement.PrimaryKeyColumns is null
            && !statement.Columns.Any(column => column.PrimaryKey))
        {
            throw new EmbeddedSqlException($"PRIMARY KEY missing on table {statement.Name}");
        }

        foreach (var check in statement.Columns
                     .SelectMany(column => column.CheckConstraints)
                     .Concat(statement.CheckConstraints ?? []))
        {
            context.ValidateCheckConstraintFunctions(check.Expression);
        }

        var isCreateTableAsSelect = statement.InitialRows is not null;
        var table = new EmbeddedTable(
            statement.Name,
            statement.Columns,
            statement.WithoutRowid,
            statement.PrimaryKeyColumns,
            statement.UniqueConstraints,
            statement.CheckConstraints,
            statement.PrimaryKeyConflictAlgorithm,
            statement.PrimaryKeyConstraintName,
            statement.PrimaryKeyDeclarationOrder,
            statement.TableForeignKeys,
            statement.Strict)
        {
            // CREATE TABLE AS SELECT stores its schema SQL in compact form (verified
            // against sqlite3); the catalog dump must reproduce that exact layout.
            SchemaSqlCompact = isCreateTableAsSelect,
        };
        var tableSql = isCreateTableAsSelect || statement.Sql is null
            ? EmbeddedDatabase.BuildCreateTableSql(statement.Name, table)
            : statement.Sql;

        var createsSequenceTable = false;
        var createsSequenceBackingTable = false;
        var sequenceBackingTableName = EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName(statement.Name);
        var requiredPages = 1;
        if (table.IsAutoIncrement)
        {
            createsSequenceTable = EmbeddedDatabase.RequiresSqliteSequenceTable(catalog);
            createsSequenceBackingTable = EmbeddedDatabase.RequiresAutoIncrementSequenceBackingTable(
                catalog,
                sequenceBackingTableName);
            if (createsSequenceTable)
                requiredPages++;
            if (createsSequenceBackingTable)
                requiredPages++;
        }

        context.EnforceMaxPageCount(requiredPages);

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));
        builder.EmitDatabaseFormatInitialization();

        if (createsSequenceTable)
        {
            var sequenceRoot = builder.EmitCreateBtree(VdbeCreateBtreeFlags.Table);
            builder.EmitSchemaEntry(
                schemaCursor,
                ManagedSchemaRow.TableType,
                EmbeddedDatabase.SqliteSequenceTableName,
                EmbeddedDatabase.SqliteSequenceTableName,
                sequenceRoot,
                EmbeddedDatabase.BuildCreateTableSql(
                    EmbeddedDatabase.SqliteSequenceTableName,
                    EmbeddedDatabase.CreateSqliteSequenceTable()));
        }

        var backingSeed = createsSequenceBackingTable
            ? EmbeddedDatabase.CreateAutoIncrementSequenceBackingTable(sequenceBackingTableName)
            : null;
        if (backingSeed is not null)
        {
            var backingRoot = builder.EmitCreateBtree(VdbeCreateBtreeFlags.Table);
            builder.EmitSchemaEntry(
                schemaCursor,
                ManagedSchemaRow.TableType,
                sequenceBackingTableName,
                sequenceBackingTableName,
                backingRoot,
                EmbeddedDatabase.BuildCreateTableSql(sequenceBackingTableName, backingSeed));
        }

        // A WITHOUT ROWID table's data lives in an index b-tree keyed by its primary key, so upstream
        // allocates one with CreateBTreeFlags::new_index (schema.rs:1324-1331).
        var tableRoot = builder.EmitCreateBtree(
            statement.WithoutRowid ? VdbeCreateBtreeFlags.Index : VdbeCreateBtreeFlags.Table);

        // Implicit UNIQUE/PRIMARY KEY indexes are not declared by SQL; the table definition produces them,
        // and each needs its own b-tree and schema row exactly as upstream's collect_autoindexes emits.
        var implicitIndexes = table.Indexes
            .Where(static index => index.Origin != EmbeddedIndexOrigin.Explicit)
            .ToArray();
        var implicitIndexRoots = new Register[implicitIndexes.Length];
        for (var index = 0; index < implicitIndexes.Length; index++)
            implicitIndexRoots[index] = builder.EmitCreateBtree(VdbeCreateBtreeFlags.Index);

        builder.EmitSchemaEntry(
            schemaCursor,
            ManagedSchemaRow.TableType,
            statement.Name,
            statement.Name,
            tableRoot,
            tableSql);
        for (var index = 0; index < implicitIndexes.Length; index++)
        {
            builder.EmitSchemaEntry(
                schemaCursor,
                ManagedSchemaRow.IndexType,
                implicitIndexes[index].Name,
                statement.Name,
                implicitIndexRoots[index],
                sql: null);
        }

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));

        // Upstream folds the sequence table into one ParseSchema by OR-ing its tbl_name into the clause.
        // The managed ParseSchema matcher accepts only AND-joined terms, so the same set of rows is
        // adopted by one instruction per object instead. Order matters: a table must be adopted before
        // anything writes rows into it.
        if (createsSequenceTable)
            builder.Emit(ParseSchemaFor(context.Database, EmbeddedDatabase.SqliteSequenceTableName));
        if (backingSeed is not null)
            builder.Emit(ParseSchemaFor(context.Database, sequenceBackingTableName));
        builder.Emit(ParseSchemaFor(context.Database, statement.Name));

        var populations = new List<CompiledSchemaPopulation>(2);
        if (backingSeed is not null)
        {
            populations.Add(EmitPopulation(
                builder,
                sequenceBackingTableName,
                [.. backingSeed.Rows],
                backingSeed.ColumnDefinitions.Length));
        }

        if (statement.InitialRows is { } initialRows)
        {
            populations.Add(EmitPopulation(
                builder,
                statement.Name,
                initialRows,
                table.ColumnDefinitions.Length));
        }

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            populations,
            stagedSchemaVersion,
            IsNoOp: false);
    }

    /// <summary>
    /// Lowers <c>CREATE INDEX</c>, following <c>translate_create_index</c> (index.rs:85).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that decides whether the statement is legal happens here, in upstream's order: reserved
    /// names, name conflicts, <c>IF NOT EXISTS</c>, the indexable-target rules, the index definition
    /// itself, application-defined functions in expressions and partial-index predicates, collation
    /// availability, and — for <c>USING method</c> — resolving the method, its column shape, its
    /// <c>WITH</c> options and its MVCC support. The emitted program then performs the effects.
    /// </para>
    /// <para>
    /// Two deliberate departures from upstream's instruction order, both forced by Ahtola's storage model:
    /// a b-tree root is allocated for a method index too, because the managed persistence adapter
    /// materializes a b-tree for every index row and <c>ParseSchema</c> refuses to adopt a rootpage-0
    /// index; and the refill runs <em>after</em> <c>ParseSchema</c>, because an index only becomes
    /// addressable in the managed catalog once that instruction has adopted it. Neither weakens
    /// atomicity: every effect is staged, so a failure at any point discards the whole program.
    /// </para>
    /// </remarks>
    public static CompiledSchemaProgram CompileCreateIndex(
        CreateIndexStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        var tables = catalog.Tables;
        if (EmbeddedDatabase.IsReservedObjectName(statement.Name))
            throw new EmbeddedSqlException($"object name reserved for internal use: {statement.Name}");
        if (tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a table named {statement.Name}");
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a view named {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a trigger named {statement.Name}");

        if (EmbeddedDatabase.TryFindIndex(tables, statement.Name, out _, out _))
        {
            if (statement.IfNotExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"index {statement.Name} already exists");
        }

        if (EmbeddedDatabase.IsSchemaTable(statement.TableName))
            throw new EmbeddedSqlException($"table {statement.TableName} may not be indexed");
        if (EmbeddedDatabase.IsSqliteSequenceTable(statement.TableName))
            throw new EmbeddedSqlException($"table {EmbeddedDatabase.SqliteSequenceTableName} may not be indexed");
        if (catalog.VirtualTables.ContainsKey(statement.TableName))
            throw new EmbeddedSqlException("virtual tables may not be indexed");
        if (catalog.Views.ContainsKey(statement.TableName))
            throw new EmbeddedSqlException("views may not be indexed");
        if (!tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var definition = BuildValidatedIndex(statement, table, context);
        context.EnforceMaxPageCount(1);

        var indexSql = definition.Sql ?? IndexSqlFormatter.BuildCreateIndexSql(statement.TableName, definition);
        var operationsSlot = definition.IsMethodIndex ? new ManagedSchemaOperationsSlot() : null;

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        var root = builder.EmitCreateBtree(VdbeCreateBtreeFlags.Index);
        builder.EmitSchemaEntry(
            schemaCursor,
            ManagedSchemaRow.IndexType,
            statement.Name,
            statement.TableName,
            root,
            indexSql);

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new ParseSchemaInstruction(context.Database, ParseSchemaClauseForIndex(statement.Name)));

        if (operationsSlot is not null)
        {
            // Upstream's refill for a method index feeds the method one base row at a time; the managed
            // methods derive their whole state from the base rows, so the lifecycle hook is the whole of
            // it. Resolving the attachment through the stage keeps the build detached: it lands on the
            // table clone ParseSchema just adopted, never on the instance the live catalog still holds.
            var methodCursor = builder.AllocateCursor();
            builder.Emit(new IndexMethodCreateInstruction(
                methodCursor,
                VdbeIndexMethodBinding.Deferred(
                    definition.Method!,
                    definition.Name,
                    () => operationsSlot.Require().ResolveMethodIndex(statement.TableName, statement.Name))));
        }
        else
        {
            builder.Emit(new IndexBuildInstruction(
                context.Database,
                statement.TableName,
                statement.Name,
                definition.Unique));
        }

        builder.Emit(new CloseCursorInstruction(schemaCursor));
        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            OperationsSlot = operationsSlot,
        };
    }

    /// <summary>
    /// Lowers <c>DROP INDEX</c>, following <c>translate_drop_index</c> (index.rs:1204).
    /// </summary>
    /// <remarks>
    /// The emitted program is upstream's: scan <c>sqlite_schema</c> for the row whose <c>name</c> and
    /// <c>type</c> match, delete it, bump the schema cookie, retire the index's storage — the b-tree
    /// through <c>Destroy</c>, or the method's own state through <c>IndexMethodDestroy</c> — and evict the
    /// index from the live schema with <c>DropIndex</c>. Ahtola assigns roots at commit rather than at
    /// creation, so the root the scan read out of the row travels to <c>Destroy</c> in a register instead
    /// of being a translate-time literal.
    /// </remarks>
    public static CompiledSchemaProgram CompileDropIndex(
        DropIndexStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (!EmbeddedDatabase.TryFindIndex(catalog.Tables, statement.Name, out var table, out var index))
        {
            if (statement.IfExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"no such index: {statement.Name}");
        }

        if (index.Origin != EmbeddedIndexOrigin.Explicit)
        {
            throw new EmbeddedSqlException(
                $"index associated with UNIQUE or PRIMARY KEY constraint cannot be dropped: {statement.Name}");
        }

        var tableName = catalog.Tables.First(entry => ReferenceEquals(entry.Value, table)).Key;
        var operationsSlot = index.IsMethodIndex ? new ManagedSchemaOperationsSlot() : null;

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        var rootRegister = builder.EmitSchemaRowDeleteScan(
            schemaCursor,
            ManagedSchemaRow.IndexType,
            index.Name);

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));

        if (operationsSlot is not null)
        {
            var methodCursor = builder.AllocateCursor();
            builder.Emit(new IndexMethodDestroyInstruction(
                methodCursor,
                VdbeIndexMethodBinding.Deferred(
                    index.Method!,
                    index.Name,
                    () => operationsSlot.Require().ResolveMethodIndex(tableName, index.Name))));
        }

        // A method index still owns a b-tree root in the managed catalog — every index row carries one —
        // so its storage is retired as well, exactly as an ordinary index's is.
        builder.Emit(new DestroyInstruction(
            context.Database,
            RootPage: 0,
            builder.AllocateRegister(),
            IsTemporary: false,
            rootRegister));
        builder.Emit(new DropIndexInstruction(context.Database, index.Name));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            OperationsSlot = operationsSlot,
        };
    }

    /// <summary>
    /// Lowers <c>CREATE VIEW</c>, following <c>translate_create_view</c> (view.rs:312).
    /// </summary>
    /// <remarks>
    /// Upstream's order is preserved: name conflicts and the reserved-prefix rule decide whether the
    /// statement is legal, then the program writes one rootpage-0 <c>view</c> row, adopts it with
    /// <c>ParseSchema</c>, and bumps the schema cookie. A view has no b-tree, so nothing is allocated and
    /// no page limit is consulted.
    /// </remarks>
    public static CompiledSchemaProgram CompileCreateView(
        CreateViewStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (EmbeddedDatabase.IsReservedObjectName(statement.Name))
            throw new EmbeddedSqlException($"object name reserved for internal use: {statement.Name}");
        if (catalog.Views.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"view {statement.Name} already exists");
        }
        if (catalog.Tables.ContainsKey(statement.Name) || catalog.VirtualTables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a table named {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a trigger named {statement.Name}");
        if (EmbeddedDatabase.TryFindIndex(catalog.Tables, statement.Name, out _, out _))
            throw new EmbeddedSqlException($"there is already an index named {statement.Name}");

        // Turso validates aggregate-internal ORDER BY while compiling a view, even though ordinary
        // queries retain the managed engine's supported aggregate ordering.
        if (EmbeddedDatabase.QueryContainsAggregateInternalOrderBy(statement.Query))
            throw new EmbeddedSqlException("ORDER BY clause is not supported yet in aggregate functions");

        // SQLite defers view-body validation to query time: base tables and views may be defined later
        // (forward references), and column arity / unknown columns, tables, or functions are reported when
        // the view is queried, not when it is created. Circular definitions are detected at query time by
        // EnterView. File-backed catalogs still reject runtime-only dependencies at persist time.
        var view = new ViewDefinition(statement.Name, statement.Columns, statement.Query, statement.Sql);

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        builder.EmitSchemaEntry(
            schemaCursor,
            ManagedSchemaRow.ViewType,
            statement.Name,
            statement.Name,
            rootRegister: null,
            statement.Sql);

        builder.Emit(new ParseSchemaInstruction(
            context.Database,
            ParseSchemaClauseFor(ManagedSchemaRow.ViewType, statement.Name)));

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            PendingObjects = new ManagedSchemaPendingObjects(
                Views: new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    [statement.Name] = view,
                }),
        };
    }

    /// <summary>
    /// Lowers <c>DROP VIEW</c>, following <c>translate_drop_view</c> (view.rs:438).
    /// </summary>
    /// <remarks>
    /// The emitted program is upstream's: scan <c>sqlite_schema</c> for the <c>view</c> row, delete it,
    /// bump the schema cookie, and evict the view with <c>DropView</c>. SQLite also drops the triggers that
    /// watched the view, so this program deletes their rows and evicts them in the same statement rather
    /// than leaving them pointing at an object that no longer exists.
    /// </remarks>
    public static CompiledSchemaProgram CompileDropView(
        DropViewStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (!catalog.Views.TryGetValue(statement.Name, out var view))
        {
            if (catalog.Tables.ContainsKey(statement.Name) || catalog.VirtualTables.ContainsKey(statement.Name))
                throw new EmbeddedSqlException($"use DROP TABLE to delete table {statement.Name}");
            if (statement.IfExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"no such view: {statement.Name}");
        }

        // Names resolve case-insensitively but the schema row stores the case the view was declared with,
        // and the scan compares the name column with BINARY semantics — so the program has to search for
        // the stored spelling, not the one this statement happened to use.
        var viewName = view.Name;
        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.ViewType, viewName);
        var orphanedTriggers = FindTriggersWatching(catalog, viewName);
        foreach (var trigger in orphanedTriggers)
            builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TriggerType, trigger);

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new DropViewInstruction(context.Database, viewName));
        foreach (var trigger in orphanedTriggers)
            builder.Emit(new DropTriggerInstruction(context.Database, trigger));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false);
    }

    /// <summary>
    /// Lowers <c>CREATE TRIGGER</c>, following <c>translate_create_trigger</c> (trigger.rs:88).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Triggers live in their own namespace: a trigger may share its name with a table, a view, or an
    /// index, and only trigger-vs-trigger collides — which is what upstream checks, consulting
    /// <c>get_trigger</c> alone. The timing rules follow: <c>INSTEAD OF</c> requires a view, a row trigger
    /// requires a table, system tables and virtual tables refuse triggers outright.
    /// </para>
    /// <para>
    /// A TEMP trigger whose target lives in another schema is validated by the owning connection before it
    /// is routed here, because the table is in a database this catalog cannot see. That routing decision —
    /// which schema owns the trigger, and which owns the table it watches — travels with the compiled
    /// definition, since the stored SQL of an unqualified TEMP trigger cannot express it. It is the same
    /// fact upstream carries as <c>ParseSchema.trigger_target_database_id</c>.
    /// </para>
    /// </remarks>
    public static CompiledSchemaProgram CompileCreateTrigger(
        CreateTriggerStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (EmbeddedDatabase.IsReservedObjectName(statement.Name))
            throw new EmbeddedSqlException($"object name reserved for internal use: {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"trigger {statement.Name} already exists");
        }

        var targetsTable = catalog.Tables.ContainsKey(statement.TableName);
        var targetsView = catalog.Views.ContainsKey(statement.TableName);
        if (catalog.VirtualTables.ContainsKey(statement.TableName))
            throw new EmbeddedSqlException("cannot create triggers on virtual tables");
        if (statement.TargetSchema is null)
        {
            if (EmbeddedDatabase.IsSqliteSequenceTable(statement.TableName) && targetsTable)
                throw new EmbeddedSqlException("cannot create trigger on system table");
            if (statement.Timing == TriggerTiming.InsteadOf)
            {
                if (targetsTable)
                    throw new EmbeddedSqlException($"cannot create INSTEAD OF trigger on table: {statement.TableName}");
                if (!targetsView)
                    throw new EmbeddedSqlException($"no such view: {statement.TableName}");
            }
            else
            {
                if (targetsView)
                {
                    throw new EmbeddedSqlException(
                        $"cannot create {statement.Timing.ToString().ToUpperInvariant()} trigger on view: {statement.TableName}");
                }
                if (!targetsTable)
                    throw new EmbeddedSqlException($"no such table: {statement.TableName}");
            }
        }

        var declarationOrder = catalog.Triggers.Count == 0
            ? 0
            : checked(catalog.Triggers.Values.Max(trigger => trigger.DeclarationOrder) + 1);
        var definition = new TriggerDefinition(
            statement.Name,
            statement.Timing,
            statement.Event,
            statement.UpdateOfColumns,
            statement.TableName,
            statement.When,
            statement.Body,
            statement.Sql,
            declarationOrder,
            statement.TargetSchema,
            statement.Temporary);

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        builder.EmitSchemaEntry(
            schemaCursor,
            ManagedSchemaRow.TriggerType,
            statement.Name,
            statement.TableName,
            rootRegister: null,
            statement.Sql);

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new ParseSchemaInstruction(
            context.Database,
            ParseSchemaClauseFor(ManagedSchemaRow.TriggerType, statement.Name)));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            PendingObjects = new ManagedSchemaPendingObjects(
                Triggers: new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    [statement.Name] = definition,
                }),
        };
    }

    /// <summary>
    /// Lowers <c>DROP TRIGGER</c>, following <c>translate_drop_trigger</c> (trigger.rs:487).
    /// </summary>
    public static CompiledSchemaProgram CompileDropTrigger(
        DropTriggerStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Catalog.Triggers.TryGetValue(statement.Name, out var trigger))
        {
            if (statement.IfExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"no such trigger: {statement.Name}");
        }

        // The scan matches the name column with BINARY semantics, so it has to search for the case the
        // trigger was declared with rather than the one this statement used.
        var triggerName = trigger.Name;
        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TriggerType, triggerName);

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new DropTriggerInstruction(context.Database, triggerName));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false);
    }

    /// <summary>
    /// Lowers <c>CREATE VIRTUAL TABLE</c>, following <c>translate_create_virtual_table</c>
    /// (schema.rs:1687).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's shape is preserved: <c>VCreate</c> instantiates the module's table first, then one
    /// rootpage-0 <c>table</c> row records it, the cookie is bumped, and <c>ParseSchema</c> publishes it.
    /// The instance <c>VCreate</c> produced is the one <c>ParseSchema</c> adopts, so a module's
    /// create/disconnect hooks stay balanced; rebuilding it from the row would connect a second one.
    /// </para>
    /// <para>
    /// The module is resolved and invoked only when the program runs, so describing this program cannot
    /// reach a module: the publish binding resolves through an operations slot that only a bound program
    /// ever fills.
    /// </para>
    /// </remarks>
    public static CompiledSchemaProgram CompileCreateVirtualTable(
        CreateVirtualTableStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (EmbeddedDatabase.IsReservedObjectName(statement.Name))
            throw new EmbeddedSqlException($"object name reserved for internal use: {statement.Name}");
        if (catalog.VirtualTables.ContainsKey(statement.Name))
        {
            if (statement.IfNotExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"table {statement.Name} already exists");
        }
        if (catalog.Tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a table named {statement.Name}");
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"there is already a view named {statement.Name}");
        if (catalog.Triggers.ContainsKey(statement.Name)
            || EmbeddedDatabase.TryFindIndex(catalog.Tables, statement.Name, out _, out _))
        {
            throw new EmbeddedSqlException($"there is already an object named {statement.Name}");
        }

        var arguments = statement.Arguments.ToArray();
        var operationsSlot = new ManagedSchemaOperationsSlot();
        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();

        builder.Emit(new VCreateInstruction(
            statement.ModuleName,
            new ManagedVirtualTableCreateContext(statement.Name, arguments),
            table => operationsSlot.Require().StageVirtualTable(
                statement.Name,
                statement.ModuleName,
                arguments,
                table)));
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));
        builder.EmitSchemaEntry(
            schemaCursor,
            ManagedSchemaRow.TableType,
            statement.Name,
            statement.Name,
            rootRegister: null,
            ManagedVirtualTableSchemaSql.BuildDeclaration(statement.Name, statement.ModuleName, arguments));

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(ParseSchemaFor(context.Database, statement.Name));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            OperationsSlot = operationsSlot,
        };
    }

    /// <summary>
    /// Lowers <c>DROP TABLE</c>, following <c>translate_drop_table</c> (schema.rs:1816).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's shape is preserved end to end: every <c>sqlite_schema</c> row the object owns is deleted
    /// through cursor bytecode, each index's storage is retired — the b-tree with <c>Destroy</c>, a managed
    /// method's own state with <c>IndexMethodDestroy</c> — then the table's own b-tree, then the
    /// <c>sqlite_sequence</c> watermark and the change-capture version entry are scanned out, the live
    /// schema is evicted with <c>DropTable</c>/<c>DropTrigger</c>, the implicit AUTOINCREMENT backing table
    /// is torn down the way <c>emit_drop_sequence_cleanup</c> tears down a sequence (sequence.rs:917), and
    /// one <c>SetCookie</c> — the statement's only cookie bump, exactly as upstream emits it last — closes
    /// the program.
    /// </para>
    /// <para>
    /// Two departures, both forced by Ahtola's storage model. Upstream knows every root as a translate-time
    /// literal because SQLite assigns a root page when a b-tree is created; Ahtola assigns roots at commit,
    /// so each retiring root travels from its schema row to <c>Destroy</c> in a register. And upstream
    /// deletes the table's rows with a single <c>tbl_name</c> scan, while the managed program emits one
    /// scan per object, because only a per-object scan can capture that object's root before its row is
    /// gone.
    /// </para>
    /// <para>
    /// Upstream's post-<c>Destroy</c> root-page fixup loop — which rewrites the schema row of whatever tree
    /// auto-vacuum moved into the hole — has no counterpart here: Ahtola's persistence rewrites the whole
    /// database on commit and assigns every root then, so retiring a tree never relocates another one.
    /// </para>
    /// <para>
    /// The virtual-table arm is upstream's <c>Table::Virtual</c> match arm and is dispatched to
    /// <see cref="CompileDropVirtualTable"/>, so one entry point lowers both.
    /// </para>
    /// </remarks>
    public static CompiledSchemaProgram CompileDropTable(
        DropTableStatement statement,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        var catalog = context.Catalog;
        if (EmbeddedDatabase.IsSqliteSequenceTable(statement.Name) && catalog.Tables.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"table {EmbeddedDatabase.SqliteSequenceTableName} may not be dropped");
        if (catalog.Views.ContainsKey(statement.Name))
            throw new EmbeddedSqlException($"use DROP VIEW to delete view {statement.Name}");
        if (catalog.VirtualTables.TryGetValue(statement.Name, out var virtualTable))
            return CompileDropVirtualTable(statement, virtualTable, context);
        if (!catalog.Tables.TryGetValue(statement.Name, out var table))
        {
            if (statement.IfExists)
                return CompileNoOp(context);

            throw new EmbeddedSqlException($"no such table: {statement.Name}");
        }

        // Names resolve case-insensitively but every scan compares the name column with BINARY semantics,
        // so the program has to search for the spelling the schema rows carry — the catalog's key — rather
        // than the one this statement happened to use.
        var tableName = ResolveCatalogName(catalog.Tables, statement.Name);
        var orphanedTriggers = FindTriggersWatching(catalog, tableName);
        var backingTableName = table.IsAutoIncrement
            ? ResolveAutoIncrementBackingTableName(catalog, table)
            : null;
        var sequenceTableName = table.IsAutoIncrement
            ? ResolveSqliteSequenceTableName(catalog, tableName)
            : null;
        var versionTableName = ResolveChangeDataCaptureVersionTableName(catalog, tableName);

        var operationsSlot = table.Indexes.Any(static index => index.IsMethodIndex)
            ? new ManagedSchemaOperationsSlot()
            : null;

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        // 1. Every schema row the table owns: its own, one per index, and one per trigger watching it.
        var tableRoot = builder.EmitSchemaRowDeleteScan(schemaCursor, ManagedSchemaRow.TableType, tableName);
        var indexRoots = new Register[table.Indexes.Count];
        for (var index = 0; index < table.Indexes.Count; index++)
        {
            indexRoots[index] = builder.EmitSchemaRowDeleteScan(
                schemaCursor,
                ManagedSchemaRow.IndexType,
                table.Indexes[index].Name);
        }

        foreach (var trigger in orphanedTriggers)
            builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TriggerType, trigger);

        // 2. Each index's storage. A managed method owns state the b-tree does not describe, so its
        //    Destroy hook runs first; the b-tree root is retired either way, because the managed
        //    persistence adapter materializes one for every index row.
        for (var index = 0; index < table.Indexes.Count; index++)
        {
            var definition = table.Indexes[index];
            if (definition.IsMethodIndex)
            {
                var methodCursor = builder.AllocateCursor();
                var indexName = definition.Name;
                builder.Emit(new IndexMethodDestroyInstruction(
                    methodCursor,
                    VdbeIndexMethodBinding.Deferred(
                        definition.Method!,
                        indexName,
                        () => operationsSlot!.Require().ResolveMethodIndex(tableName, indexName))));
            }

            builder.Emit(new DestroyInstruction(
                context.Database,
                RootPage: 0,
                builder.AllocateRegister(),
                IsTemporary: false,
                indexRoots[index]));
        }

        // 3. The table's own storage.
        builder.Emit(new DestroyInstruction(
            context.Database,
            RootPage: 0,
            builder.AllocateRegister(),
            IsTemporary: false,
            tableRoot));

        // 4. The rows that named the table in the engine's own bookkeeping tables. They are ordinary rows,
        //    not schema rows, so they are deleted through their own write cursors.
        var tableScans = new List<CompiledSchemaTableScan>(2);
        if (sequenceTableName is not null)
        {
            var sequenceCursor = builder.AllocateCursor();
            builder.Emit(new OpenWriteCursorInstruction(
                sequenceCursor,
                sequenceTableName,
                SqliteSequenceColumnCount));
            // The watermark is keyed by the table's declared name, which is the spelling the AUTOINCREMENT
            // writer records and the direct evaluator matched with ordinal comparison.
            builder.EmitTableRowDeleteScan(sequenceCursor, SqliteSequenceNameColumn, table.Name);
            builder.Emit(new CloseCursorInstruction(sequenceCursor));
            tableScans.Add(new CompiledSchemaTableScan(sequenceCursor, sequenceTableName));
        }

        if (versionTableName is not null)
        {
            var versionCursor = builder.AllocateCursor();
            builder.Emit(new OpenWriteCursorInstruction(
                versionCursor,
                versionTableName,
                ChangeDataCaptureVersionColumnCount));
            builder.EmitTableRowDeleteScan(versionCursor, ChangeDataCaptureVersionNameColumn, tableName);
            builder.Emit(new CloseCursorInstruction(versionCursor));
            tableScans.Add(new CompiledSchemaTableScan(versionCursor, versionTableName));
        }

        // 5. The live schema. Evicting the table takes its indexes with it, which is what upstream's
        //    DropTable does (execute.rs op_drop_table) and what the managed catalog's nesting gives.
        builder.Emit(new DropTableInstruction(context.Database, tableName));
        foreach (var trigger in orphanedTriggers)
            builder.Emit(new DropTriggerInstruction(context.Database, trigger));

        // 6. The implicit AUTOINCREMENT backing table, torn down exactly as a sequence is: schema row,
        //    b-tree, then the catalog entry. Leaving it behind would let a table recreated under the same
        //    name resume from the old watermark.
        if (backingTableName is not null)
        {
            var backingRoot = builder.EmitSchemaRowDeleteScan(
                schemaCursor,
                ManagedSchemaRow.TableType,
                backingTableName);
            builder.Emit(new DestroyInstruction(
                context.Database,
                RootPage: 0,
                builder.AllocateRegister(),
                IsTemporary: false,
                backingRoot));
            builder.Emit(new DropTableInstruction(context.Database, backingTableName));
        }

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            OperationsSlot = operationsSlot,
            TableScans = tableScans,
        };
    }

    /// <summary>
    /// Lowers the virtual-table branch of <c>DROP TABLE</c>, following the <c>Table::Virtual</c> arm of
    /// <c>translate_drop_table</c> (schema.rs:2100).
    /// </summary>
    /// <remarks>
    /// The schema rows go first, then the cookie, then <c>VDestroy</c> retires the module's own storage and
    /// <c>DropTable</c> evicts the entry — the same order the managed <c>DROP INDEX</c> program uses.
    /// <c>VDestroy</c> is the only thing that releases the instance; the <c>DropTable</c> that follows it
    /// only removes the catalog entry.
    /// </remarks>
    public static CompiledSchemaProgram CompileDropVirtualTable(
        DropTableStatement statement,
        EmbeddedDatabase.VirtualTableDefinition definition,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        var virtualCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        // The scan compares the name column with BINARY semantics, so it searches for the case the table
        // was declared with rather than the one this statement used.
        var tableName = definition.Name;
        builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TableType, tableName);
        var orphanedTriggers = FindTriggersWatching(context.Catalog, tableName);
        foreach (var trigger in orphanedTriggers)
            builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TriggerType, trigger);

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new VDestroyInstruction(virtualCursor, tableName));
        builder.Emit(new DropTableInstruction(context.Database, tableName));
        foreach (var trigger in orphanedTriggers)
            builder.Emit(new DropTriggerInstruction(context.Database, trigger));
        builder.Emit(new CloseCursorInstruction(schemaCursor));

        var program = builder.Build();
        return new CompiledSchemaProgram(
            program,
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            VirtualTableBindings = BindVirtualTable(program, virtualCursor, definition),
        };
    }

    /// <summary>
    /// Lowers the virtual-table branch of <c>ALTER TABLE … RENAME TO</c>, following
    /// <c>translate_rename_virtual_table</c> (alter.rs:2391).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>VRename</c> renames the module's table in place, the old <c>sqlite_schema</c> row is deleted and
    /// rewritten under the new name, the cookie is bumped, and <c>RenameTable</c> moves the catalog entry.
    /// Because the module keeps its instance, the definition moves with it rather than being rebuilt.
    /// </para>
    /// <para>
    /// Dependent views and triggers whose stored SQL names the table follow the rename the way SQLite
    /// rewrites them: their rows are rewritten with the new text and adopted by <c>ParseSchema</c>. The
    /// rewritten definitions are computed by the caller, which owns the dependent-schema validation the
    /// rename has to pass first.
    /// </para>
    /// </remarks>
    public static CompiledSchemaProgram CompileRenameVirtualTable(
        AlterTableRenameStatement statement,
        EmbeddedDatabase.VirtualTableDefinition definition,
        IReadOnlyDictionary<string, ViewDefinition> rewrittenViews,
        IReadOnlyDictionary<string, TriggerDefinition> rewrittenTriggers,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rewrittenViews);
        ArgumentNullException.ThrowIfNull(rewrittenTriggers);
        ArgumentNullException.ThrowIfNull(context);

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        var virtualCursor = builder.AllocateCursor();

        // The stored spelling is what the row scan and the catalog rename both have to name; the
        // statement may have addressed the table in any case.
        var currentName = definition.Name;
        var newName = builder.EmitConstant(SqlValue.Text(statement.NewName));
        builder.Emit(new VRenameInstruction(virtualCursor, newName));

        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));
        builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TableType, currentName);
        builder.EmitSchemaEntry(
            schemaCursor,
            ManagedSchemaRow.TableType,
            statement.NewName,
            statement.NewName,
            rootRegister: null,
            ManagedVirtualTableSchemaSql.BuildDeclaration(
                statement.NewName,
                definition.ModuleName,
                definition.Arguments));

        foreach (var view in rewrittenViews.Values)
        {
            builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.ViewType, view.Name);
            builder.EmitSchemaEntry(
                schemaCursor,
                ManagedSchemaRow.ViewType,
                view.Name,
                view.Name,
                rootRegister: null,
                view.Sql);
        }

        foreach (var trigger in rewrittenTriggers.Values)
        {
            builder.EmitSchemaRowDelete(schemaCursor, ManagedSchemaRow.TriggerType, trigger.Name);
            builder.EmitSchemaEntry(
                schemaCursor,
                ManagedSchemaRow.TriggerType,
                trigger.Name,
                trigger.TableName,
                rootRegister: null,
                trigger.Sql);
        }

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));
        builder.Emit(new RenameTableInstruction(context.Database, currentName, statement.NewName));

        // The rewritten dependents are adopted only after RenameTable has published the new name, because
        // a trigger's target has to resolve against the schema the rename produced.
        foreach (var view in rewrittenViews.Values)
        {
            builder.Emit(new ParseSchemaInstruction(
                context.Database,
                ParseSchemaClauseFor(ManagedSchemaRow.ViewType, view.Name)));
        }
        foreach (var trigger in rewrittenTriggers.Values)
        {
            builder.Emit(new ParseSchemaInstruction(
                context.Database,
                ParseSchemaClauseFor(ManagedSchemaRow.TriggerType, trigger.Name)));
        }

        builder.Emit(new CloseCursorInstruction(schemaCursor));

        var program = builder.Build();
        return new CompiledSchemaProgram(
            program,
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            PendingObjects = new ManagedSchemaPendingObjects(rewrittenViews, rewrittenTriggers),
            VirtualTableBindings = BindVirtualTable(program, virtualCursor, definition),
        };
    }

    /// <summary>
    /// Lowers the ordinary (b-tree) branch of <c>ALTER TABLE … RENAME TO</c>, following the
    /// <c>AlterTableBody::RenameTo</c> arm of <c>translate_alter_table</c> (alter.rs:1546).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream walks every <c>sqlite_schema</c> row through <c>AlterTableFunc::RenameTable</c> and writes
    /// each rewritten row back, then bumps the cookie and issues <c>RenameTable</c>. The managed program
    /// rewrites exactly the rows the rename changes — the table itself, every index on it, the AUTOINCREMENT
    /// backing table, every table whose foreign keys named the old parent, and every dependent view and
    /// trigger — because the connection has already decided which those are and what they say afterwards.
    /// </para>
    /// <para>
    /// <c>RenameTable</c> runs after the cookie exactly as upstream orders it, and moves the catalog entry
    /// with its rows: a rename never rebuilds a table. The dependents follow through <c>ParseSchema</c>,
    /// which adopts the definitions the plan carries rather than reparsing text a second time.
    /// </para>
    /// </remarks>
    public static CompiledSchemaProgram CompileRenameTable(
        AlterTableRenameStatement statement,
        string currentName,
        CompiledAlterTablePlan plan,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);

        return CompileAlterTable(
            plan,
            context,
            builder => builder.Emit(new RenameTableInstruction(
                context.Database,
                currentName,
                statement.NewName)));
    }

    /// <summary>
    /// Lowers <c>ALTER TABLE … ADD COLUMN</c>, following the <c>AlterTableBody::AddColumn</c> arm of
    /// <c>translate_alter_table</c> (alter.rs:1255).
    /// </summary>
    /// <remarks>
    /// Upstream rewrites the table's stored <c>CREATE TABLE</c> text and then issues <c>AddColumn</c>
    /// carrying the parsed column together with the constraints it contributes. The managed opcode carries
    /// the column's declaration text instead and reparses it with the ordinary DDL parser, so a definition
    /// the statement would reject is rejected identically when the schema is reloaded from
    /// <c>sqlite_schema</c>; the added column's own source text travels alongside it because the table's
    /// stored SQL is edited by insertion rather than regenerated.
    /// </remarks>
    public static CompiledSchemaProgram CompileAddColumn(
        AlterTableAddColumnStatement statement,
        string currentName,
        CompiledAlterTablePlan plan,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);

        return CompileAlterTable(
            plan,
            context,
            builder => builder.Emit(new AddColumnInstruction(
                context.Database,
                currentName,
                statement.Column.Name,
                statement.ColumnSql ?? statement.Column.Name,
                statement.ColumnSql)));
    }

    /// <summary>
    /// Lowers <c>ALTER TABLE … DROP COLUMN</c>, following the <c>AlterTableBody::DropColumn</c> arm of
    /// <c>translate_alter_table</c> (alter.rs:915).
    /// </summary>
    /// <remarks>
    /// Upstream rewrites the stored <c>CREATE TABLE</c> text, rewrites every stored row into the narrower
    /// layout, bumps the cookie and issues <c>DropColumn</c> with the dropped column's index. The managed
    /// <c>DropColumn</c> opcode performs the row projection against the staged table as part of the same
    /// step, which is what keeps a failed program from leaving half-projected rows behind: the projection
    /// lands on a table clone the stage owns and nothing outside it can observe.
    /// </remarks>
    public static CompiledSchemaProgram CompileDropColumn(
        AlterTableDropColumnStatement statement,
        string currentName,
        int columnIndex,
        CompiledAlterTablePlan plan,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);

        return CompileAlterTable(
            plan,
            context,
            builder => builder.Emit(new DropColumnInstruction(context.Database, currentName, columnIndex)));
    }

    /// <summary>
    /// Lowers <c>ALTER TABLE … RENAME COLUMN</c>, following the <c>RenameColumn</c> half of the shared
    /// <c>AlterColumn</c>/<c>RenameColumn</c> arm of <c>translate_alter_table</c> (alter.rs:1795).
    /// </summary>
    /// <remarks>
    /// Upstream builds a column definition holding nothing but the new name and issues
    /// <c>AlterColumn { rename: true }</c>; the managed opcode does the same and additionally carries
    /// whether SQLite has to quote the replacement, because the stored text rewrite is token-aware and the
    /// quoting decision belongs to the statement rather than to the schema.
    /// </remarks>
    public static CompiledSchemaProgram CompileRenameColumn(
        AlterTableRenameColumnStatement statement,
        string currentName,
        int columnIndex,
        CompiledAlterTablePlan plan,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);

        return CompileAlterTable(
            plan,
            context,
            builder => builder.Emit(new AlterColumnInstruction(
                context.Database,
                currentName,
                columnIndex,
                statement.NewName,
                Rename: true,
                statement.QuoteNewName)));
    }

    /// <summary>
    /// Lowers Turso's <c>ALTER TABLE … ALTER COLUMN</c> extension, following the <c>AlterColumn</c> half of
    /// the shared arm of <c>translate_alter_table</c> (alter.rs:1795).
    /// </summary>
    /// <remarks>
    /// The replacement definition travels as text and is reparsed by the opcode, so a definition that
    /// <c>ALTER COLUMN</c> would reject is rejected identically on reload. When the alteration retires a
    /// rowid alias of an AUTOINCREMENT table the program also clears the watermark and destroys the
    /// implicit backing table, which is upstream's <c>emit_delete_sqlite_sequence_entry</c> (alter.rs:368).
    /// </remarks>
    public static CompiledSchemaProgram CompileAlterColumn(
        AlterTableAlterColumnStatement statement,
        string currentName,
        int columnIndex,
        CompiledAlterTablePlan plan,
        DdlCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);

        return CompileAlterTable(
            plan,
            context,
            builder => builder.Emit(new AlterColumnInstruction(
                context.Database,
                currentName,
                columnIndex,
                statement.ColumnSql ?? statement.Column.Name,
                Rename: false)));
    }

    /// <summary>
    /// The shape every ordinary <c>ALTER TABLE</c> program has: retire the <c>sqlite_schema</c> rows and
    /// storage the alteration removes, rewrite the rows it changes, write the rows and b-trees it creates,
    /// bump the schema cookie exactly once, apply the typed schema effect, and adopt the dependents through
    /// <c>ParseSchema</c>.
    /// </summary>
    /// <remarks>
    /// The cookie precedes the typed effect because that is the order <c>translate_alter_table</c> emits in
    /// for every one of its arms; the dependent <c>ParseSchema</c>s follow it because a rewritten trigger's
    /// target has to resolve against the schema the alteration produced.
    /// </remarks>
    private static CompiledSchemaProgram CompileAlterTable(
        CompiledAlterTablePlan plan,
        DdlCompilationContext context,
        Action<SchemaProgramBuilder> emitSchemaEffect)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        builder.Emit(new OpenWriteCursorInstruction(
            schemaCursor,
            ManagedSchemaProgramBindings.SchemaTableName,
            ManagedSchemaProgramBindings.SchemaColumnCount));

        // A constraint-backed index the alteration leaves without a declaration loses its row and its
        // storage; the catalog entry goes with the table the typed effect rebuilds, so no DropIndex is
        // needed to evict it. Retiring those rows before the surviving rows are rewritten keeps the two
        // edits independent: a survivor may inherit the retired index's derived name, and rewriting it
        // first would leave the delete scan two rows to choose between.
        var droppedIndexRoots = new Register[plan.DroppedIndexes.Count];
        for (var index = 0; index < plan.DroppedIndexes.Count; index++)
        {
            droppedIndexRoots[index] = builder.EmitSchemaRowDeleteScan(
                schemaCursor,
                ManagedSchemaRow.IndexType,
                plan.DroppedIndexes[index]);
        }

        foreach (var rewrite in plan.RowRewrites)
        {
            builder.EmitSchemaRowRewrite(
                schemaCursor,
                rewrite.EntryType,
                rewrite.CurrentName,
                rewrite.Name,
                rewrite.TableName,
                rewrite.Sql,
                rewrite.OwnsRootPage);
        }

        // An index the alteration creates is written exactly as CREATE INDEX writes one: a fresh b-tree,
        // then the row that records it.
        foreach (var added in plan.AddedIndexes)
        {
            var root = builder.EmitCreateBtree(VdbeCreateBtreeFlags.Index);
            builder.EmitSchemaEntry(
                schemaCursor,
                ManagedSchemaRow.IndexType,
                added.Name,
                added.TableName,
                root,
                added.Sql);
        }

        // The implicit AUTOINCREMENT backing table is torn down exactly as DROP TABLE tears one down: its
        // row goes, then the b-tree that row recorded, then the catalog entry.
        var droppedRoots = new Register[plan.DroppedTables.Count];
        for (var index = 0; index < plan.DroppedTables.Count; index++)
        {
            droppedRoots[index] = builder.EmitSchemaRowDeleteScan(
                schemaCursor,
                ManagedSchemaRow.TableType,
                plan.DroppedTables[index]);
        }

        var tableScans = new List<CompiledSchemaTableScan>(1);
        if (plan.ClearedSequenceTableName is { } sequenceTableName)
        {
            var sequenceCursor = builder.AllocateCursor();
            builder.Emit(new OpenWriteCursorInstruction(
                sequenceCursor,
                sequenceTableName,
                SqliteSequenceColumnCount));
            builder.EmitTableRowDeleteScan(
                sequenceCursor,
                SqliteSequenceNameColumn,
                plan.ClearedSequenceOwner
                    ?? throw new InvalidOperationException(
                        "A cleared sqlite_sequence scan must name the table whose watermark it clears."));
            builder.Emit(new CloseCursorInstruction(sequenceCursor));
            tableScans.Add(new CompiledSchemaTableScan(sequenceCursor, sequenceTableName));
        }

        var stagedSchemaVersion = NextSchemaVersion(context.SchemaVersion);
        builder.Emit(new SetCookieInstruction(
            context.Database,
            VdbeSchemaCookie.SchemaVersion,
            checked((int)stagedSchemaVersion)));

        emitSchemaEffect(builder);

        for (var index = 0; index < plan.DroppedTables.Count; index++)
        {
            builder.Emit(new DestroyInstruction(
                context.Database,
                RootPage: 0,
                builder.AllocateRegister(),
                IsTemporary: false,
                droppedRoots[index]));
            builder.Emit(new DropTableInstruction(context.Database, plan.DroppedTables[index]));
        }

        for (var index = 0; index < plan.DroppedIndexes.Count; index++)
        {
            builder.Emit(new DestroyInstruction(
                context.Database,
                RootPage: 0,
                builder.AllocateRegister(),
                IsTemporary: false,
                droppedIndexRoots[index]));
        }

        foreach (var name in plan.Tables.Keys)
        {
            builder.Emit(new ParseSchemaInstruction(
                context.Database,
                ParseSchemaClauseFor(ManagedSchemaRow.TableType, name)));
        }
        foreach (var name in plan.Views.Keys)
        {
            builder.Emit(new ParseSchemaInstruction(
                context.Database,
                ParseSchemaClauseFor(ManagedSchemaRow.ViewType, name)));
        }
        foreach (var name in plan.Triggers.Keys)
        {
            builder.Emit(new ParseSchemaInstruction(
                context.Database,
                ParseSchemaClauseFor(ManagedSchemaRow.TriggerType, name)));
        }

        builder.Emit(new CloseCursorInstruction(schemaCursor));

        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            stagedSchemaVersion,
            IsNoOp: false)
        {
            PendingObjects = new ManagedSchemaPendingObjects(
                plan.Views,
                plan.Triggers,
                plan.Tables,
                plan.ReplacementTables),
            TableScans = tableScans,
        };
    }

    /// <summary>
    /// The triggers SQLite drops along with the object they watch: those declared in this schema against
    /// <paramref name="tableName"/>. A TEMP trigger reaching into another schema is stored elsewhere and
    /// is pruned by the owning connection instead.
    /// </summary>
    private static string[] FindTriggersWatching(EmbeddedDatabase.SchemaCatalog catalog, string tableName)
        => [.. catalog.Triggers.Values
            .Where(trigger => trigger.TargetSchema is null
                && string.Equals(trigger.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .Select(trigger => trigger.Name)];

    /// <summary>
    /// The spelling <paramref name="catalog"/> stores for <paramref name="name"/>. Catalog lookups are
    /// case-insensitive, but the <c>sqlite_schema</c> rows carry the stored spelling and every scan
    /// compares the name column with BINARY semantics, so a program must search for this one.
    /// </summary>
    private static string ResolveCatalogName<TValue>(
        IReadOnlyDictionary<string, TValue> catalog,
        string name)
        => catalog.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            ?? name;

    /// <summary>
    /// The <c>sqlite_sequence</c> table a dropped AUTOINCREMENT table's watermark lives in.
    /// </summary>
    /// <remarks>
    /// An AUTOINCREMENT table without <c>sqlite_sequence</c> is a malformed catalog, not a legal drop: the
    /// watermark it must clear has nowhere to live. The direct evaluator raised the same internal error.
    /// </remarks>
    private static string ResolveSqliteSequenceTableName(
        EmbeddedDatabase.SchemaCatalog catalog,
        string tableName)
    {
        if (!catalog.Tables.ContainsKey(EmbeddedDatabase.SqliteSequenceTableName))
        {
            throw new InvalidOperationException(
                $"The AUTOINCREMENT table '{tableName}' is missing {EmbeddedDatabase.SqliteSequenceTableName}.");
        }

        return ResolveCatalogName(catalog.Tables, EmbeddedDatabase.SqliteSequenceTableName);
    }

    /// <summary>
    /// The implicit AUTOINCREMENT backing table of <paramref name="table"/>, or <see langword="null"/> when
    /// the catalog has none — an AUTOINCREMENT table created before the backing table existed still drops.
    /// </summary>
    private static string? ResolveAutoIncrementBackingTableName(
        EmbeddedDatabase.SchemaCatalog catalog,
        EmbeddedTable table)
    {
        var backingTableName = EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName(table.Name);
        return catalog.Tables.ContainsKey(backingTableName)
            ? ResolveCatalogName(catalog.Tables, backingTableName)
            : null;
    }

    /// <summary>
    /// The change-capture version table whose entry for <paramref name="tableName"/> the drop retires, or
    /// <see langword="null"/> when there is none — including when the version table is itself the table
    /// being dropped, which takes its own rows with it.
    /// </summary>
    private static string? ResolveChangeDataCaptureVersionTableName(
        EmbeddedDatabase.SchemaCatalog catalog,
        string tableName)
    {
        if (string.Equals(
                tableName,
                ChangeDataCaptureConfiguration.VersionTableName,
                StringComparison.OrdinalIgnoreCase)
            || !catalog.Tables.ContainsKey(ChangeDataCaptureConfiguration.VersionTableName))
        {
            return null;
        }

        return ResolveCatalogName(catalog.Tables, ChangeDataCaptureConfiguration.VersionTableName);
    }

    private static VdbeVirtualTableBinding?[] BindVirtualTable(
        VdbeProgram program,
        Cursor cursor,
        EmbeddedDatabase.VirtualTableDefinition definition)
    {
        var bindings = new VdbeVirtualTableBinding?[program.CursorCount];
        bindings[cursor.Index] = new VdbeVirtualTableBinding(definition.Table);
        return bindings;
    }

    /// <summary>
    /// Builds the index definition and runs every check upstream performs while translating, in its order.
    /// </summary>
    /// <remarks>
    /// This is the decision half of what <c>ExecuteCreateIndex</c> used to do inline; the effects are now
    /// bytecode. It is shared so a caller that only needs the definition — <c>EXPLAIN</c>, or a future
    /// <c>REINDEX</c> translator — validates a statement exactly as executing it would.
    /// </remarks>
    private static EmbeddedIndex BuildValidatedIndex(
        CreateIndexStatement statement,
        EmbeddedTable table,
        DdlCompilationContext context)
    {
        var isRegisteredScalarFunction = context.IsRegisteredScalarFunction ?? NoRegisteredScalarFunctions;
        var definition = EmbeddedIndexFactory.Create(statement.TableName, table, statement);
        if (definition.Columns.Any(column =>
                column.Expression is not null
                && IndexExpressionSemantics.ContainsFunction(column.Expression, isRegisteredScalarFunction))
            || (definition.Where is not null
                && IndexExpressionSemantics.ContainsFunction(definition.Where, isRegisteredScalarFunction)))
        {
            throw new EmbeddedSqlException(
                "application-defined functions are prohibited in index expressions and partial index WHERE clauses");
        }

        var hasCollation = context.HasCollation ?? EveryCollationAvailable;
        foreach (var column in definition.Columns)
        {
            // Building an index requires ordering its rows by every declared column's collation. A name
            // that resolves to neither a SQLite built-in nor an already-registered application-defined
            // callback can never be honored, so CREATE INDEX fails closed here with the SQLite-style
            // message instead of publishing an index that would silently fall back to BINARY ordering
            // the first time it is planned or written to.
            var collationName = IndexExpressionSemantics.GetCollationName(table, column);
            if (!hasCollation(collationName))
                throw new EmbeddedSqlException($"no such collation sequence: {collationName}");
        }

        IndexExpressionSemantics.ValidateRoundTrip(statement.TableName, table, definition);

        if (definition.IsMethodIndex)
        {
            // Attaching resolves the method and validates its column shape and every WITH key, exactly as
            // upstream's index_module.attach does while translating, so an unknown method fails the
            // statement rather than the first query. The attachment is discarded: the one the program
            // acts on is resolved from the staged catalog, so nothing is published before the program
            // succeeds.
            var attachment = ManagedIndexMethodSemantics.CreateAttachment(statement.TableName, table, definition);
            ManagedIndexMethodMvcc.Ensure(attachment.Definition, context.IsMvccEnabled, forWrite: true);
        }

        return definition;
    }

    /// <summary>
    /// Emits the read cursor, write cursor and per-row loop that populate <paramref name="tableName"/>.
    /// </summary>
    private static CompiledSchemaPopulation EmitPopulation(
        SchemaProgramBuilder builder,
        string tableName,
        IReadOnlyList<SqlValue[]> rows,
        int columnCount)
    {
        var sourceCursor = builder.AllocateCursor();
        var targetCursor = builder.AllocateCursor();
        // The source cursor scans rows the query engine already produced; it is not a scan of any catalog
        // table, so it carries no table name. Upstream reads the same rows from a SELECT coroutine.
        builder.Emit(new OpenReadCursorInstruction(sourceCursor, TableName: null, columnCount));
        builder.Emit(new OpenWriteCursorInstruction(targetCursor, tableName, columnCount));
        builder.EmitPopulationLoop(sourceCursor, targetCursor, tableName, columnCount);
        return new CompiledSchemaPopulation(sourceCursor, targetCursor, tableName, rows);
    }

    /// <summary>
    /// The clause upstream builds for a created index: the row naming it, of type <c>index</c>
    /// (index.rs:304-305). The same name/type shape identifies a view or a trigger, whose namespaces the
    /// name alone would not separate.
    /// </summary>
    private static string ParseSchemaClauseForIndex(string indexName)
        => ParseSchemaClauseFor(ManagedSchemaRow.IndexType, indexName);

    private static string ParseSchemaClauseFor(string entryType, string name)
        => $"name = '{EscapeSqlStringLiteral(name)}' AND type = '{entryType}'";

    /// <summary>
    /// The fallback for a context that names no connection. It reports no application-defined function,
    /// which is the only safe default: a caller that cannot tell is one with no registrations to consult.
    /// </summary>
    private static bool NoRegisteredScalarFunctions(string name, int arity) => false;

    /// <summary>
    /// The fallback for a context that names no connection. It accepts every collation, because a caller
    /// with no collation registry cannot distinguish an unavailable name from a built-in one and must not
    /// invent a rejection.
    /// </summary>
    private static bool EveryCollationAvailable(string name) => true;

    /// <summary>
    /// The clause upstream builds for a created object: every non-trigger row naming it
    /// (schema.rs:1454-1456).
    /// </summary>
    private static ParseSchemaInstruction ParseSchemaFor(int database, string tableName)
        => new(
            database,
            $"tbl_name = '{EscapeSqlStringLiteral(tableName)}' AND type != '{ManagedSchemaRow.TriggerType}'");

    private static string EscapeSqlStringLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// The cookie value the statement stages. SQLite stores the schema cookie as a 32-bit header integer
    /// and wraps rather than failing, which is what the managed header does too.
    /// </summary>
    private static long NextSchemaVersion(long schemaVersion) => unchecked((int)schemaVersion + 1);

    /// <summary>
    /// The program a statement with nothing to do compiles to. It is a real program, so describing it and
    /// running it agree: both do nothing.
    /// </summary>
    private static CompiledSchemaProgram CompileNoOp(DdlCompilationContext context)
    {
        var builder = new SchemaProgramBuilder(context.Database);
        var schemaCursor = builder.AllocateCursor();
        return new CompiledSchemaProgram(
            builder.Build(),
            schemaCursor,
            [],
            context.SchemaVersion,
            IsNoOp: true);
    }

    /// <summary>The <c>sqlite_sequence(name, seq)</c> shape a drop scans for its watermark row.</summary>
    private const int SqliteSequenceColumnCount = 2;
    private const int SqliteSequenceNameColumn = 0;

    /// <summary>The change-capture version table's shape; its first column names the captured table.</summary>
    private const int ChangeDataCaptureVersionColumnCount = 2;
    private const int ChangeDataCaptureVersionNameColumn = 0;
}
