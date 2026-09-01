using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Core;

/// <summary>
/// The header cookies the managed persistence adapter can observe but cannot stage.
/// </summary>
/// <remarks>
/// <c>PersistFileCatalog</c> carries exactly three header integers forward —
/// <see cref="PragmaHeaderMetadata.SchemaVersion"/>, <see cref="PragmaHeaderMetadata.UserVersion"/> and
/// <see cref="PragmaHeaderMetadata.ApplicationId"/>. Everything else in the file header is decided by the
/// writer itself. Those cookies are therefore readable, and a <c>SetCookie</c> against one is accepted
/// only when it asserts the value already in force; changing it would stage something no commit could
/// publish, which is exactly the kind of success-shaped lie this seam exists to prevent.
/// </remarks>
internal readonly record struct ManagedSchemaFixedCookies(
    int DatabaseFormat,
    int DefaultPageCacheSize,
    uint LargestRootPageNumber,
    int DatabaseTextEncoding,
    int IncrementalVacuum)
{
    /// <summary>The cookies a managed database that has never been persisted reports.</summary>
    public static ManagedSchemaFixedCookies Default => new(
        DatabaseFormat: 4,
        DefaultPageCacheSize: 0,
        LargestRootPageNumber: 0,
        DatabaseTextEncoding: (int)SqliteTextEncoding.Utf8,
        IncrementalVacuum: 0);
}

/// <summary>
/// Everything one DDL program stages against a single routed database: the working
/// <see cref="EmbeddedDatabase.SchemaCatalog"/> clone it mutates, the ordered <c>sqlite_schema</c> rows
/// that describe that catalog, the header cookies it has set, and the root allocation/reclamation plan.
/// </summary>
/// <remarks>
/// <para>
/// The stage is the unit of atomicity for a schema program, and it is entirely transaction-local. It holds
/// a <em>clone</em> of the catalog, a <em>copy</em> of the schema rows and a value copy of the header
/// metadata, so discarding the stage discards every schema effect the program had — no storage was
/// touched, no live catalog was mutated, and no header was written. Publication stays where it already
/// lives: the outer <c>PersistFileCatalog</c>/<c>PublishCatalog</c> boundary, which takes
/// <see cref="Catalog"/> and <see cref="PragmaHeader"/> and commits them in one pager/WAL commit.
/// </para>
/// <para>
/// <see cref="Reset"/> exists for statement re-execution: it rebuilds the working catalog from the same
/// snapshot factory, restores the baseline rows and header, and clears the root plan, matching what
/// <c>ResumableStatement.Reset</c> does to interpreter state.
/// </para>
/// </remarks>
internal sealed class ManagedSchemaStage
{
    private readonly Func<EmbeddedDatabase.SchemaCatalog> _catalogFactory;
    private readonly ManagedSchemaRowSet _baselineRows;
    private readonly PragmaHeaderMetadata _baselinePragmaHeader;
    private readonly Dictionary<string, EmbeddedTable> _detachedTables =
        new(StringComparer.OrdinalIgnoreCase);
    private EmbeddedDatabase.SchemaCatalog _catalog;
    private ManagedSchemaRowSet _rows;
    private PragmaHeaderMetadata _pragmaHeader;

    private ManagedSchemaStage(
        string databaseName,
        Func<EmbeddedDatabase.SchemaCatalog> catalogFactory,
        EmbeddedDatabase.SchemaCatalog catalog,
        ManagedSchemaRowSet rows,
        PragmaHeaderMetadata pragmaHeader,
        ManagedSchemaFixedCookies fixedCookies,
        ManagedSchemaRootPlan rootPlan)
    {
        DatabaseName = databaseName;
        _catalogFactory = catalogFactory;
        _catalog = catalog;
        _rows = rows;
        _baselineRows = rows.Clone();
        _pragmaHeader = pragmaHeader;
        _baselinePragmaHeader = pragmaHeader;
        FixedCookies = fixedCookies;
        RootPlan = rootPlan;
    }

    /// <summary>The routed database this stage is bound to, used in diagnostics.</summary>
    public string DatabaseName { get; }

    /// <summary>The working catalog clone the program mutates.</summary>
    public EmbeddedDatabase.SchemaCatalog Catalog => _catalog;

    /// <summary>The transaction-local <c>sqlite_schema</c> rows, in schema order.</summary>
    public ManagedSchemaRowSet Rows => _rows;

    /// <summary>The staged header cookies the outer commit will publish.</summary>
    public PragmaHeaderMetadata PragmaHeader => _pragmaHeader;

    /// <summary>The header cookies this adapter can read but not change.</summary>
    public ManagedSchemaFixedCookies FixedCookies { get; }

    /// <summary>The root allocation and reclamation intents staged so far.</summary>
    public ManagedSchemaRootPlan RootPlan { get; }

    /// <summary>Whether anything has been staged that publication would have to carry.</summary>
    public bool HasStagedChanges
        => RootPlan.HasStagedChanges
            || _pragmaHeader != _baselinePragmaHeader
            || !RowsMatchBaseline();

    /// <summary>
    /// Builds a stage for <paramref name="databaseName"/>.
    /// </summary>
    /// <param name="databaseName">The routed database name, used in diagnostics.</param>
    /// <param name="catalogFactory">
    /// Produces a fresh working clone of the committed catalog. It is called once now and again on every
    /// <see cref="Reset"/>, so a re-run never inherits a half-mutated catalog.
    /// </param>
    /// <param name="pragmaHeader">The committed header metadata this program starts from.</param>
    /// <param name="fixedCookies">The header cookies the adapter can read but not stage.</param>
    /// <param name="tableRootPages">Physical table roots, when the database is file-backed.</param>
    /// <param name="indexRootPages">Physical index roots, when the database is file-backed.</param>
    /// <param name="firstLogicalRoot">
    /// The first logical root identifier. It must exceed every physical root and page the database uses;
    /// callers pass committed page count + 1 for a file-backed database and 2 for an in-memory one, whose
    /// objects have no physical roots at all.
    /// </param>
    public static ManagedSchemaStage Create(
        string databaseName,
        Func<EmbeddedDatabase.SchemaCatalog> catalogFactory,
        PragmaHeaderMetadata pragmaHeader,
        ManagedSchemaFixedCookies fixedCookies,
        IReadOnlyDictionary<string, uint>? tableRootPages = null,
        IReadOnlyDictionary<string, uint>? indexRootPages = null,
        uint firstLogicalRoot = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(catalogFactory);

        var rootPlan = new ManagedSchemaRootPlan(firstLogicalRoot);
        var catalog = catalogFactory()
            ?? throw new InvalidOperationException("The schema stage catalog factory returned null.");
        try
        {
            var rows = BuildRows(catalog, tableRootPages, indexRootPages, rootPlan);
            return new ManagedSchemaStage(
                databaseName,
                catalogFactory,
                catalog,
                rows,
                pragmaHeader,
                fixedCookies,
                rootPlan);
        }
        catch
        {
            catalog.DisconnectOwnedVirtualTables();
            throw;
        }
    }

    /// <summary>Stages a new value for one of the three publishable header cookies.</summary>
    public void StagePragmaHeader(PragmaHeaderMetadata pragmaHeader) => _pragmaHeader = pragmaHeader;

    /// <summary>
    /// Replaces the staged entry for <paramref name="tableName"/> with a private clone the running program
    /// may mutate, and returns it. Repeated calls reuse the same clone until something else — a
    /// <c>ParseSchema</c> adoption — replaces the entry.
    /// </summary>
    /// <remarks>
    /// The stage overlays the caller's catalog with fresh dictionaries but the <em>same</em>
    /// <see cref="EmbeddedTable"/> instances, so mutating one in place would be visible to the caller
    /// immediately and would survive a program that failed afterwards. Detaching is what keeps a row a
    /// program deletes — a retiring <c>sqlite_sequence</c> watermark, a change-capture version entry — and
    /// every mutation an index method makes to its own state transaction-local: discarding the stage simply
    /// drops the clone and the original table is untouched.
    /// </remarks>
    public bool TryDetachTable(string tableName, out EmbeddedTable detached)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (!_catalog.Tables.TryGetValue(tableName, out var current))
        {
            detached = null!;
            return false;
        }

        if (_detachedTables.TryGetValue(tableName, out var existing) && ReferenceEquals(existing, current))
        {
            detached = existing;
            return true;
        }

        var clone = current.Clone();
        _catalog.Tables[tableName] = clone;
        _detachedTables[tableName] = clone;
        detached = clone;
        return true;
    }

    /// <summary>
    /// Rewrites every staged row that still carries a logical root with the physical root
    /// <paramref name="resolvePhysicalRoot"/> assigns, retiring the logical identifier as it goes.
    /// </summary>
    /// <remarks>
    /// This is the publication step the root-plan invariant requires: the outer full-rewrite commit knows
    /// the page each b-tree landed on, and this is how that answer gets back into the rows before
    /// <see cref="ValidatePublishable"/> runs. Rows whose root is already physical, and the rootpage-0 rows
    /// of views, triggers and virtual tables, are left alone.
    /// </remarks>
    public void MapLogicalRoots(Func<ManagedSchemaRow, uint> resolvePhysicalRoot)
    {
        ArgumentNullException.ThrowIfNull(resolvePhysicalRoot);
        foreach (var row in _rows.Rows.ToArray())
        {
            if (!RootPlan.IsLogicalRoot(row.RootPage))
                continue;

            var physicalRoot = resolvePhysicalRoot(row);
            RootPlan.MapToPhysicalRoot(row.RootPage, physicalRoot);
            _rows.Replace(row with { RootPage = physicalRoot });
        }
    }

    /// <summary>
    /// Returns the stage to the state it was created in: a freshly cloned catalog, the baseline schema
    /// rows, the baseline header, and an empty root plan.
    /// </summary>
    public void Reset()
    {
        var replacement = _catalogFactory()
            ?? throw new InvalidOperationException("The schema stage catalog factory returned null.");
        var previous = _catalog;
        _catalog = replacement;
        _rows = _baselineRows.Clone();
        _pragmaHeader = _baselinePragmaHeader;
        _detachedTables.Clear();
        RootPlan.Reset();
        previous.DisconnectOwnedVirtualTables();
    }

    /// <summary>
    /// Releases the working catalog's owned virtual-table instances without rebuilding it. The owner calls
    /// this when the stage is being thrown away — a failed program, or the disposal of the statement that
    /// bound it — so a rolled-back <c>CREATE VIRTUAL TABLE</c> cannot leak a connection. Unlike
    /// <see cref="Reset"/> it does not produce a fresh catalog, because a discarded stage will never run
    /// again and the clone would itself connect instances nobody would release.
    /// </summary>
    public void Discard() => _catalog.DisconnectOwnedVirtualTables();

    /// <summary>
    /// Validates that the staged rows describe exactly the staged catalog. A program that mutated one
    /// side without the other fails closed here instead of handing the caller a catalog and a
    /// <c>sqlite_schema</c> that disagree.
    /// </summary>
    public void ValidateRowsDescribeCatalog()
    {
        var expected = new HashSet<string>(EnumerateCatalogIdentities(), StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(
            _rows.Rows.Select(static row => $"{row.Type}/{row.Name}/{row.TableName}"),
            StringComparer.OrdinalIgnoreCase);
        if (expected.SetEquals(actual))
            return;

        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToArray();
        throw new ManagedSchemaRowException(
            $"The staged sqlite_schema rows for '{DatabaseName}' do not describe the staged catalog. "
            + $"Missing rows: {Describe(missing)}. Unexpected rows: {Describe(extra)}.");
    }

    /// <summary>
    /// Validates that the staged rows describe exactly the staged catalog, and that no logical root would
    /// escape into durable storage. The outer publication boundary calls this after
    /// <see cref="MapLogicalRoots"/> and before it commits, so a program that mutated one side without the
    /// other fails closed instead of persisting a catalog and a <c>sqlite_schema</c> that disagree.
    /// </summary>
    public void ValidatePublishable()
    {
        _rows.ValidateNoLogicalRoots(RootPlan);
        ValidateRowsDescribeCatalog();
    }

    private IEnumerable<string> EnumerateCatalogIdentities()
    {
        foreach (var entry in _catalog.Tables)
        {
            yield return $"{ManagedSchemaRow.TableType}/{entry.Key}/{entry.Key}";
            foreach (var index in entry.Value.Indexes)
                yield return $"{ManagedSchemaRow.IndexType}/{index.Name}/{entry.Key}";
        }

        foreach (var name in _catalog.VirtualTables.Keys)
            yield return $"{ManagedSchemaRow.TableType}/{name}/{name}";
        foreach (var name in _catalog.Views.Keys)
            yield return $"{ManagedSchemaRow.ViewType}/{name}/{name}";
        foreach (var entry in _catalog.Triggers)
            yield return $"{ManagedSchemaRow.TriggerType}/{entry.Key}/{entry.Value.TableName}";
    }

    private bool RowsMatchBaseline()
    {
        if (_rows.Count != _baselineRows.Count)
            return false;

        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows.Rows[index] != _baselineRows.Rows[index])
                return false;
        }

        return true;
    }

    private static string Describe(IReadOnlyCollection<string> keys)
        => keys.Count == 0 ? "none" : string.Join(", ", keys);

    /// <summary>
    /// Projects a catalog into schema rows in the order the file store persists them: tables, virtual
    /// tables, indexes grouped by table, views by name, then triggers in declaration order.
    /// </summary>
    private static ManagedSchemaRowSet BuildRows(
        EmbeddedDatabase.SchemaCatalog catalog,
        IReadOnlyDictionary<string, uint>? tableRootPages,
        IReadOnlyDictionary<string, uint>? indexRootPages,
        ManagedSchemaRootPlan rootPlan)
    {
        var rows = new ManagedSchemaRowSet();
        foreach (var name in catalog.Tables.Keys)
        {
            rows.Add(ManagedSchemaRowFactory.ForTable(
                name,
                catalog.Tables[name],
                ResolveRoot(tableRootPages, name, ManagedSchemaRootKind.Table, rootPlan)));
        }

        foreach (var definition in catalog.VirtualTables.Values.OrderBy(
                     static value => value.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            // A stage holds the live instance, so its row carries the declaration rather than the
            // storage envelope: the payload only has to travel in a row that must stand on its own.
            rows.Add(ManagedSchemaRowFactory.ForVirtualTable(definition, ManagedSchemaSqlForm.Declared));
        }

        foreach (var tableName in catalog.Tables.Keys)
        {
            var table = catalog.Tables[tableName];
            foreach (var index in table.Indexes)
            {
                rows.Add(ManagedSchemaRowFactory.ForIndex(
                    tableName,
                    table,
                    index,
                    ResolveRoot(indexRootPages, index.Name, ManagedSchemaRootKind.Index, rootPlan),
                    ManagedSchemaSqlForm.Persisted));
            }
        }

        foreach (var name in catalog.Views.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            rows.Add(ManagedSchemaRowFactory.ForView(catalog.Views[name]));

        foreach (var trigger in catalog.Triggers.Values
                     .OrderBy(value => value.DeclarationOrder)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(ManagedSchemaRowFactory.ForTrigger(trigger));
        }

        return rows;
    }

    private static uint ResolveRoot(
        IReadOnlyDictionary<string, uint>? rootPages,
        string name,
        ManagedSchemaRootKind kind,
        ManagedSchemaRootPlan rootPlan)
        => rootPages is not null && rootPages.TryGetValue(name, out var rootPage) && rootPage >= 2
            ? rootPage
            : rootPlan.AssignBaselineRoot(kind);
}
