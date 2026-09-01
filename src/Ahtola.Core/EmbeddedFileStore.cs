using System.Runtime.ExceptionServices;
using System.Text;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;

namespace Ahtola.Core;

/// <summary>
/// The catalog reconstructed from a managed file-backed database.
/// </summary>
internal sealed record EmbeddedFileCatalog(
    Dictionary<string, EmbeddedTable> Tables,
    Dictionary<string, ViewDefinition> Views,
    Dictionary<string, TriggerDefinition> Triggers,
    Dictionary<string, EmbeddedDatabase.VirtualTableDefinition> VirtualTables);

internal sealed record EmbeddedFileIndexSeekRow(SqlValue[] Values, long? RowId);

internal sealed class EmbeddedFileIndexAccessor(
    Func<
        SqlValue[],
        Action?,
        Action?,
        Action?,
        IEnumerable<EmbeddedFileIndexSeekRow>> seek,
    Action open,
    Action close) : IDisposable
{
    public void Open() => open();

    public IEnumerable<EmbeddedFileIndexSeekRow> Seek(
        SqlValue[] prefix,
        Action? pageRead = null,
        Action? keyCompared = null,
        Action? tableRowFetched = null)
        => seek(prefix, pageRead, keyCompared, tableRowFetched);

    public void Dispose() => close();
}

/// <summary>
/// Durable full-ascending-order scan handle over an index's (or a WITHOUT ROWID table's own
/// primary-key b-tree's) entire committed b-tree — the "no known equality prefix" sibling of
/// <see cref="EmbeddedFileIndexAccessor"/>, used for ORDER BY elision / unconstrained MVCC merge
/// scans that would otherwise have to materialize and sort every row in memory.
/// </summary>
internal sealed class EmbeddedFileIndexFullScanAccessor(
    Func<Action?, Action?, IEnumerable<EmbeddedFileIndexSeekRow>> scan,
    Action open,
    Action close) : IDisposable
{
    public void Open() => open();

    public IEnumerable<EmbeddedFileIndexSeekRow> Scan(
        Action? pageRead = null,
        Action? tableRowFetched = null)
        => scan(pageRead, tableRowFetched);

    public void Dispose() => close();
}

internal sealed class EmbeddedFileBlobReadSource(
    SqliteTableBtreeCursor cursor,
    uint rootPage,
    long rowId,
    int columnIndex,
    int length,
    IDisposable snapshotLease) : IManagedIncrementalBlobReadSource
{
    private IDisposable? _snapshotLease = snapshotLease;

    public int Length { get; } = length;

    public int Read(int offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_snapshotLease is null, this);
        if (!cursor.TryReadColumn(rootPage, rowId, columnIndex, (ulong)offset, destination, out var bytesRead))
            throw new InvalidDataException($"SQLite rowid {rowId} disappeared from its pinned pager snapshot.");

        return bytesRead;
    }

    public void Dispose()
    {
        var lease = Interlocked.Exchange(ref _snapshotLease, null);
        lease?.Dispose();
    }
}

internal sealed class EmbeddedFileReadSnapshot(
    EmbeddedFileStore owner,
    SqlitePagerReadTransaction transaction,
    ISqliteBtreePageIo pageIo,
    IReadOnlyDictionary<string, uint> tableRootPages,
    IReadOnlyDictionary<string, uint> indexRootPages) : IDisposable
{
    private int _referenceCount = 1;
    private int _ownerReleased;

    internal EmbeddedFileStore Owner { get; } = owner;

    internal ISqliteBtreePageIo PageIo { get; } = pageIo;

    /// <summary>
    /// The table root-page mapping captured atomically with <see cref="PageIo"/>'s pager
    /// transaction (same locked section as the catalog clone — see
    /// <see cref="EmbeddedDatabase.CreateTransactionSnapshotWithPin"/>). A pinned classic
    /// transaction must resolve root pages from here, never from the store's live
    /// <c>_tableRootPages</c>, so a peer's later DDL — including a drop/recreate that reuses the
    /// same table name with a new root page — cannot redirect this snapshot's pager reads onto a
    /// page that has nothing to do with the table this snapshot was pinned against.
    /// </summary>
    internal IReadOnlyDictionary<string, uint> TableRootPages { get; } = tableRootPages;

    /// <summary>Same generation-bound guarantee as <see cref="TableRootPages"/>, for named secondary indexes.</summary>
    internal IReadOnlyDictionary<string, uint> IndexRootPages { get; } = indexRootPages;

    internal IDisposable Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            ObjectDisposedException.ThrowIf(current == 0, this);
            if (current == int.MaxValue)
                throw new InvalidOperationException("The pager read snapshot has too many active leases.");
            if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                return new EmbeddedFileReadSnapshotLease(this);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            Release();
    }

    private void Release()
    {
        var remaining = Interlocked.Decrement(ref _referenceCount);
        if (remaining == 0)
        {
            try
            {
                transaction.Dispose();
            }
            finally
            {
                Owner.ReleaseSnapshotReference();
            }
        }
    }

    private sealed class EmbeddedFileReadSnapshotLease(EmbeddedFileReadSnapshot snapshot) : IDisposable
    {
        private EmbeddedFileReadSnapshot? _snapshot = snapshot;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _snapshot, null);
            owner?.Release();
        }
    }
}

internal static class ManagedVirtualTableSchemaSql
{
    private const string PayloadMarker = "/*ahtola-managed-vtab:";
    private const string PayloadTerminator = "*/";

    public static string Build(EmbeddedDatabase.VirtualTableDefinition definition)
        => BuildDeclaration(definition.Name, definition.ModuleName, definition.Arguments)
            + " "
            + PayloadMarker
            + definition.PersistencePayload.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":"
            + Convert.ToBase64String(definition.PersistencePayload.Bytes.Span)
            + PayloadTerminator;

    /// <summary>
    /// The <c>CREATE VIRTUAL TABLE</c> text without the managed persistence envelope: the declaration a
    /// user wrote, and the form a transaction-local schema stage carries because it holds the live
    /// instance whose state the envelope would otherwise have to transport.
    /// </summary>
    public static string BuildDeclaration(string name, string moduleName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(arguments);
        var argumentList = arguments.Count == 0
            ? string.Empty
            : "(" + string.Join(", ", arguments) + ")";
        return "CREATE VIRTUAL TABLE "
            + QuoteIdentifier(name)
            + " USING "
            + QuoteIdentifier(moduleName)
            + argumentList;
    }

    public static (
        CreateVirtualTableStatement Declaration,
        ManagedVirtualTablePersistencePayload Payload) Parse(string sql)
    {
        var marker = sql.LastIndexOf(PayloadMarker, StringComparison.Ordinal);
        if (marker <= 0 || !sql.EndsWith(PayloadTerminator, StringComparison.Ordinal))
        {
            var payloadlessStatement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
            if (payloadlessStatement is CreateVirtualTableStatement payloadlessDeclaration
                && (payloadlessDeclaration.ModuleName.Equals("rtree", StringComparison.OrdinalIgnoreCase)
                    || payloadlessDeclaration.ModuleName.Equals("rtree_i32", StringComparison.OrdinalIgnoreCase)))
            {
                throw new EmbeddedSqlException(
                    "foreign SQLite R-Tree shadow-table layouts are not supported by the managed catalog; "
                    + "the CREATE VIRTUAL TABLE declaration is missing an Ahtola persistence payload");
            }
            throw new EmbeddedSqlException("managed virtual-table sqlite_schema SQL is missing its persistence payload");
        }

        var declarationSql = sql[..marker].TrimEnd();
        var statement = SqlParser.Parse(declarationSql, SqlParameterMap.Parse(declarationSql));
        if (statement is not CreateVirtualTableStatement declaration)
            throw new EmbeddedSqlException("managed virtual-table sqlite_schema SQL is not a CREATE VIRTUAL TABLE statement");

        var encoded = sql.AsSpan(marker + PayloadMarker.Length, sql.Length - marker - PayloadMarker.Length - PayloadTerminator.Length);
        var separator = encoded.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(
                encoded[..separator],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version)
            || version <= 0)
        {
            throw new EmbeddedSqlException("managed virtual-table persistence payload version is invalid");
        }

        byte[] bytes;
        try
        {
            bytes = encoded.Length == separator + 1
                ? []
                : Convert.FromBase64String(encoded[(separator + 1)..].ToString());
        }
        catch (FormatException exception)
        {
            throw new EmbeddedSqlException("managed virtual-table persistence payload is not valid base64", exception);
        }

        return (declaration, new ManagedVirtualTablePersistencePayload(version, bytes));
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Bridges the managed <see cref="EmbeddedDatabase"/> catalog to durable,
/// SQLite-format storage. It persists the schema on page 1 as a real
/// <c>sqlite_schema</c> table b-tree and stores each ordinary user table's
/// rows and metadata-ordered secondary indexes in recursively constructed
/// SQLite b-trees. WITHOUT ROWID tables use recursively constructed SQLite
/// index b-trees with composite ASC/DESC primary keys, built-in collations,
/// generated-column storage, and primary-key-suffixed secondary indexes.
/// Table and index records may use standard
/// SQLite overflow pages. All bytes are genuine SQLite page, cell, and record encodings;
/// nothing is a bespoke serialization format.
/// </summary>
/// <remarks>
/// This is a deliberately limited, honest engine. It only accepts schema and
/// data it can represent losslessly in real SQLite format and rejects everything
/// else up front so a persisted file stays a valid SQLite database. See the
/// reject rules in <see cref="ValidateTableRepresentable"/> and the documented
/// gaps on <c>OpenManagedDatabase</c>.
/// </remarks>
internal sealed class EmbeddedFileStore : IDisposable
{
    // sqlite_schema is: (type, name, tbl_name, rootpage, sql).
    private const int SchemaColumnCount = 5;
    private const uint SchemaRootPage = 1;

    private readonly IFileSystem _fileSystem;
    private readonly string _databasePath;
    private readonly string _walPath;
    private readonly EmbeddedDatabase _indexExpressionEvaluator = new();
    private int _pageSize;
    private int _usableSpace;
    private SqliteDatabaseHeader _header;
    private SqliteTextEncoding _textEncoding;

    private SqlitePager _pager;
    private Dictionary<string, uint> _tableRootPages = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, uint> _indexRootPages = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSchemaSignature = string.Empty;

    // Resolves an application-defined (non-built-in) collation name to a
    // ready-to-call comparison delegate, or null when no callback is
    // currently registered for that name. Attached by EmbeddedDatabase after
    // construction, because this store opens and loads the file catalog
    // before any owning database exists to register collation callbacks
    // against. A store with no resolver attached (the true cold-start path)
    // structurally validates custom-collation index pages/records but defers
    // ordering/content/uniqueness validation for them; see
    // ValidateIndexRepresentable and ValidateStoredIndex.
    private Func<string, Func<string, string, int>?>? _collationResolver;

    // The exact table dictionary whose contents this store last made durable.
    // Reference identity is the only admissible proof that a caller's "previous"
    // catalog really is the committed one, so an incremental write can compute
    // its page delta from memory instead of re-reading the database.
    private IReadOnlyDictionary<string, EmbeddedTable>? _committedTables;
    private Exception? _postCommitMaintenanceFailure;
    private SqliteSynchronousMode _synchronousMode = SqliteSynchronousMode.Full;
    private int _referenceCount = 1;
    private int _disposeRequested;
    private bool _disposed;

    private EmbeddedFileStore(IFileSystem fileSystem, string databasePath, string walPath, SqlitePager pager, SqliteDatabaseHeader header)
    {
        _fileSystem = fileSystem;
        _databasePath = databasePath;
        _walPath = walPath;
        _pager = pager;
        _header = header;
        _pageSize = header.PageSize;
        _usableSpace = header.UsableSpace;
        _textEncoding = header.TextEncoding == SqliteTextEncoding.Unset
            ? SqliteTextEncoding.Utf8
            : header.TextEncoding;
    }

    /// <summary>
    /// Attaches (or clears, when <paramref name="resolver"/> is
    /// <see langword="null"/>) the runtime collation resolver used to bind
    /// application-defined collation names to comparison delegates for
    /// index build/write/seek. The owning <c>EmbeddedDatabase</c> calls this
    /// immediately after construction and again whenever a collation
    /// callback is registered, replaced, or unregistered.
    /// </summary>
    /// <remarks>
    /// Also forwards the same resolver to <see cref="_indexExpressionEvaluator"/> — a private,
    /// throwaway <c>EmbeddedDatabase</c> this store owns purely to evaluate partial-index
    /// predicates and index expressions, which never receives the application's
    /// <c>RegisterCollation</c> calls directly. Without this, a partial predicate or expression
    /// index key referencing a custom COLLATE would fail "no such collation sequence" while
    /// evaluating through that private instance even though the exact same collation is fully
    /// registered and active on the owning connection. A missing callback still fails closed,
    /// but only lazily: the first time the affected expression/predicate is actually evaluated,
    /// never eagerly at store-open time.
    /// </remarks>
    internal void SetCollationResolver(Func<string, Func<string, string, int>?>? resolver)
    {
        _collationResolver = resolver;
        _indexExpressionEvaluator.SetExternalCollationResolver(resolver);
    }

    /// <summary>
    /// Opens (or creates) the managed file database and reconstructs its catalog
    /// from the committed SQLite pages.
    /// </summary>
    public static EmbeddedFileStore Open(
        string path,
        IFileSystem fileSystem,
        out EmbeddedFileCatalog catalog,
        AhtolaEncryptionOptions? encryption = null,
        bool readOnly = false,
        int? initialPageSize = null,
        SqliteTextEncoding? initialTextEncoding = null,
        bool foreignReadOnly = false,
        IPageCodec? pageCodec = null,
        bool createRollbackJournalMode = false,
        Func<string, Func<string, string, int>?>? collationResolver = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(fileSystem);
        PageCodecSupport.RejectCombinedTransforms(encryption, pageCodec);

        var walPath = path + "-wal";
        var databaseExists = fileSystem.FileExists(path);
        var walExists = fileSystem.FileExists(walPath);
        if (initialPageSize is { } requestedPageSize)
            _ = SqlitePageSize.Encode(requestedPageSize);

        SqlitePager pager;
        if (!databaseExists)
        {
            if (readOnly)
            {
                throw new EmbeddedSqlException(
                    $"Cannot open managed database '{path}' read-only because its database file does not exist.");
            }

            // The main database file is absent. A lingering write-ahead log is
            // orphaned — its frames reference a database that was deleted (for
            // example by EFCore's EnsureDeleted, which removes only the main
            // file). Native SQLite discards the orphaned WAL and creates a
            // fresh database; match that so delete/reopen cycles do not fault
            // with "missing its main database file".
            if (walExists)
                fileSystem.DeleteFile(walPath);

            var header = SqliteDatabaseHeader.CreateDefault() with
            {
                PageSize = initialPageSize ?? SqlitePageSize.Default,
                TextEncoding = initialTextEncoding ?? SqliteTextEncoding.Utf8,
            };
            if (createRollbackJournalMode)
            {
                pager = SqlitePager.CreateRollbackJournal(
                    fileSystem,
                    path,
                    walPath,
                    header,
                    encryption: encryption,
                    pageCodec: pageCodec);
            }
            else
            {
                var walHeader = SqliteWalHeader.Create(
                    header.PageSize,
                    unchecked((uint)Random.Shared.Next()),
                    unchecked((uint)Random.Shared.Next()));
                pager = SqlitePager.Create(
                    fileSystem,
                    path,
                    walPath,
                    walHeader,
                    header,
                    encryption: encryption,
                    pageCodec: pageCodec);
            }
        }
        else
        {
            if (initialPageSize is not null || initialTextEncoding is not null)
            {
                throw new InvalidOperationException(
                    "Initial page size and text encoding can be specified only when creating a database.");
            }
            pager = SqlitePager.Open(
                fileSystem,
                path,
                walPath,
                readOnly,
                encryption: encryption,
                foreignReadOnly: foreignReadOnly,
                pageCodec: pageCodec);
        }

        try
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(SchemaRootPage));
            var store = new EmbeddedFileStore(fileSystem, path, walPath, pager, header);
            // Attaching the resolver before the catalog load lets a reopen on an
            // existing connection (which already knows its registered
            // collations) validate custom-collation index order/content/
            // uniqueness immediately instead of deferring; the true cold-start
            // path (a brand-new connection with no owning EmbeddedDatabase yet)
            // passes null here and defers structurally, per
            // ValidateIndexRepresentable/ValidateStoredIndex.
            store.SetCollationResolver(collationResolver);
            catalog = store.Load();
            return store;
        }
        catch
        {
            pager.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Forces a committed-view rescan and captures the token identifying the
    /// durable view this store currently sees. Used by foreign read-only
    /// connections to detect owner commits between statements.
    /// </summary>
    internal SqlitePagerViewToken CaptureCommittedViewToken() => _pager.CaptureCommittedViewToken();

    internal void SetSynchronousMode(SqliteSynchronousMode synchronousMode)
    {
        synchronousMode.Validate(nameof(synchronousMode));
        _synchronousMode = synchronousMode;
    }

    /// <summary>
    /// The shared per-file storage generation (see <see cref="SqlitePager.CommittedViewGeneration"/>).
    /// A cheap race-free signal that the committed view may have changed; never rescans the WAL.
    /// </summary>
    internal long CommittedViewGeneration => _pager.CommittedViewGeneration;

    /// <summary>
    /// Answers "can the pager physically seek this table's committed durable b-tree", either for
    /// a named secondary index (<paramref name="index"/> non-null, rowid or WITHOUT ROWID) or, when
    /// <paramref name="index"/> is <see langword="null"/>, for a WITHOUT ROWID table's own primary-key
    /// b-tree (the table's root page, which the managed engine already stores as an index b-tree keyed
    /// by <see cref="EmbeddedTable.PrimaryKeySchema"/>).
    /// </summary>
    internal bool CanOpenIndexAccessor(
        EmbeddedTable table,
        EmbeddedIndex? index,
        bool requireCommittedTableIdentity = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        // A classic transaction's QueryContext.Tables is built from a cloned SchemaCatalog, so its
        // EmbeddedTable is never the same object as the one this store committed even when nothing
        // schema-relevant changed. The transaction-overlay caller passes false here and instead
        // relies on the transaction's own "no schema changes this transaction" gate for safety, so
        // relaxing this check to a name-based existence test is sound: everything downstream already
        // matches indexes/tables by name (see the persistedIndex lookup below), not by reference.
        var committed = !_disposed
            && _committedTables is not null
            && (requireCommittedTableIdentity
                ? _committedTables.TryGetValue(table.Name, out var committedTable) && ReferenceEquals(committedTable, table)
                : _committedTables.ContainsKey(table.Name))
            && _tableRootPages.ContainsKey(table.Name);
        if (index is null)
        {
            return committed
                && table.WithoutRowid
                && table.PrimaryKeySchema is { Terms.Count: > 0 };
        }

        // Partial and expression index columns are both safe to seek here: the persisted b-tree
        // holds whatever rows/values were computed when the row was written (see
        // TryBuildIndexRecord), so a seek never has to re-evaluate a predicate or an expression at
        // read time. Query-level safety (partial-predicate implication, expression-operand
        // matching, keeping expression indexes non-covering) is enforced by the caller before it
        // ever asks to open an accessor; this method only answers "can the pager physically seek
        // this index's committed b-tree". WITHOUT ROWID secondary indexes are just as physically
        // seekable as rowid ones: their records always carry the table's primary-key columns
        // (see GetWithoutRowidIndexStorageColumns), so the PK-driven table-root lookup they need
        // for a non-covering fetch is always possible.
        return committed
            && !index.IsMethodIndex
            && index.Columns.Count > 0
            && _indexRootPages.ContainsKey(index.Name)
            && (!table.WithoutRowid || table.PrimaryKeySchema is { Terms.Count: > 0 });
    }

    internal bool TryOpenReadSnapshot(
        EmbeddedTable table,
        out EmbeddedFileReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(table);
        snapshot = null!;
        if (_disposed
            || _committedTables is null
            || !_committedTables.TryGetValue(table.Name, out var committed)
            || !ReferenceEquals(committed, table)
            || !_tableRootPages.ContainsKey(table.Name))
        {
            return false;
        }

        return TryOpenReadSnapshot(out snapshot);
    }

    /// <summary>
    /// Opens a pager read snapshot independent of any single table, for pinning at the start of a
    /// classic (non-MVCC) transaction. The snapshot is coupled to whatever catalog/version was
    /// cloned in the same locked section (see <see cref="EmbeddedDatabase.CreateTransactionSnapshotWithPin"/>)
    /// and, once opened, is unaffected by any later peer commit until it is disposed.
    /// </summary>
    internal bool TryOpenReadSnapshot(out EmbeddedFileReadSnapshot snapshot)
    {
        snapshot = null!;
        if (_disposed || _committedTables is null)
            return false;

        RetainForSnapshot();
        SqlitePagerReadTransaction? transaction = null;
        try
        {
            transaction = _pager.BeginReadTransaction();
            var pageIo = new SqliteStagedBtreePageIo(
                transaction.ReadPage,
                transaction.PageCount,
                _pageSize,
                _usableSpace);
            // Capturing the field references (not a defensive copy) is sound: every writer
            // replaces _tableRootPages/_indexRootPages wholesale with a brand-new dictionary
            // rather than mutating one in place (see RefreshCommittedCatalog and the VACUUM
            // rewrite path), so the dictionary this snapshot now holds a reference to can never
            // change out from under it once captured.
            snapshot = new EmbeddedFileReadSnapshot(this, transaction, pageIo, _tableRootPages, _indexRootPages);
            return true;
        }
        catch
        {
            transaction?.Dispose();
            Release();
            throw;
        }
    }

    private void RetainForSnapshot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            ObjectDisposedException.ThrowIf(current == 0, this);
            if (current == int.MaxValue)
                throw new InvalidOperationException("The file store has too many active snapshots.");
            if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) != current)
                continue;

            if (Volatile.Read(ref _disposeRequested) == 0)
                return;

            Release();
            throw new ObjectDisposedException(nameof(EmbeddedFileStore));
        }
    }

    internal void ReleaseSnapshotReference() => Release();

    private void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0)
            _pager.Dispose();
    }

    internal bool TryOpenBlobReadSource(
        EmbeddedTable table,
        long rowId,
        int physicalColumnIndex,
        EmbeddedFileReadSnapshot? sharedSnapshot,
        bool requireCommittedTableIdentity,
        out IManagedIncrementalBlobReadSource source,
        out SqliteRecordColumnLocation location)
    {
        ArgumentNullException.ThrowIfNull(table);
        source = null!;
        location = default;
        if (_disposed || table.WithoutRowid)
            return false;
        if (sharedSnapshot is not null && !ReferenceEquals(sharedSnapshot.Owner, this))
            throw new ArgumentException("The shared read snapshot belongs to another file store.", nameof(sharedSnapshot));

        var tableRootPages = sharedSnapshot?.TableRootPages ?? _tableRootPages;
        if (_committedTables is null
            || (requireCommittedTableIdentity
                ? !_committedTables.TryGetValue(table.Name, out var committed) || !ReferenceEquals(committed, table)
                : !_committedTables.ContainsKey(table.Name))
            || !tableRootPages.TryGetValue(table.Name, out var rootPage))
        {
            return false;
        }

        EmbeddedFileReadSnapshot snapshot;
        IDisposable snapshotLease;
        if (sharedSnapshot is null)
        {
            if (!TryOpenReadSnapshot(table, out snapshot))
                return false;

            snapshotLease = snapshot;
        }
        else
        {
            snapshot = sharedSnapshot;
            snapshotLease = snapshot.Retain();
        }

        try
        {
            var cursor = new SqliteTableBtreeCursor(snapshot.PageIo);
            if (!cursor.TryGetColumnLocation(rootPage, rowId, physicalColumnIndex, out location))
                return false;
            if (location.Length > int.MaxValue)
                throw new InvalidDataException("SQLite incremental BLOB length exceeds the managed address space.");

            source = new EmbeddedFileBlobReadSource(
                cursor,
                rootPage,
                rowId,
                physicalColumnIndex,
                checked((int)location.Length),
                snapshotLease);
            snapshotLease = null!;
            return true;
        }
        finally
        {
            snapshotLease?.Dispose();
        }
    }

    /// <summary>
    /// Overwrites bytes inside one committed BLOB/TEXT column without materializing
    /// the value or rewriting sibling rows. Same-size only; the overflow chain is
    /// reused in place.
    /// </summary>
    internal bool TryWriteBlobColumn(
        EmbeddedTable table,
        long rowId,
        int physicalColumnIndex,
        ulong offset,
        ReadOnlySpan<byte> source)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_disposed || table.WithoutRowid || source.IsEmpty)
            return false;
        if (_committedTables is null
            || !_committedTables.ContainsKey(table.Name)
            || !_tableRootPages.TryGetValue(table.Name, out var rootPage)
            || rootPage < 2
            || rootPage > _pager.CommittedPageCount)
        {
            return false;
        }

        var currentHeader = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        if (currentHeader.PageSize != _pageSize
            || currentHeader.UsableSpace != _usableSpace
            || currentHeader.VersionValidFor != currentHeader.ChangeCounter
            || currentHeader.DatabaseSizeInPages != _pager.CommittedPageCount
            || currentHeader.LargestRootBtreePage != 0
            || currentHeader.IncrementalVacuumEnabled != 0)
        {
            return false;
        }

        var pageIo = new SqliteStagedBtreePageIo(
            pageNumber => _pager.ReadCommittedPage(pageNumber),
            _pager.CommittedPageCount,
            _pageSize,
            _usableSpace,
            currentHeader.FirstFreelistTrunkPage,
            currentHeader.FreelistPageCount);
        var cursor = new SqliteTableBtreeCursor(pageIo);
        if (!cursor.TryWriteColumn(rootPage, rowId, physicalColumnIndex, offset, source))
            return false;
        if (pageIo.StagedPages.Count == 0 || pageIo.StagedPages.ContainsKey(SchemaRootPage))
            return false;

        var target = pageIo.PageCount;
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            DatabaseSizeInPages = target,
            FirstFreelistTrunkPage = pageIo.FirstFreelistTrunkPage,
            FreelistPageCount = pageIo.FreelistPageCount,
        };
        var pageOne = _pager.ReadCommittedPage(SchemaRootPage);
        newHeader.WriteTo(pageOne);

        using (var transaction = _pager.BeginTransaction(target))
        {
            foreach (var (pageNumber, image) in pageIo.StagedPages)
                transaction.WritePage(pageNumber, image);

            transaction.WritePage(SchemaRootPage, pageOne);
            transaction.Commit(_synchronousMode);
        }

        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    internal bool TryOpenIndexAccessor(
        EmbeddedTable table,
        EmbeddedIndex? index,
        int prefixLength,
        bool covering,
        EmbeddedFileReadSnapshot? sharedSnapshot,
        out EmbeddedFileIndexAccessor accessor,
        bool requireCommittedTableIdentity = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        accessor = null!;
        if (!CanOpenIndexAccessor(table, index, requireCommittedTableIdentity) || prefixLength < 1)
            return false;

        if (index is null)
            return TryOpenPrimaryKeyIndexAccessor(table, prefixLength, sharedSnapshot, out accessor);

        // A shared (pinned) snapshot resolves roots from the mapping it captured atomically with
        // its pager transaction, never from the store's live _tableRootPages/_indexRootPages: a
        // peer's DDL committed after this snapshot was pinned — including a drop/recreate that
        // reuses the same name with a new root page — must not redirect this snapshot's reads
        // onto a page number that has nothing to do with what it was pinned against.
        var tableRootPages = sharedSnapshot?.TableRootPages ?? _tableRootPages;
        var indexRootPages = sharedSnapshot?.IndexRootPages ?? _indexRootPages;
        if (prefixLength > index.Columns.Count
            || !tableRootPages.TryGetValue(table.Name, out var tableRootPage)
            || !indexRootPages.TryGetValue(index.Name, out var indexRootPage))
        {
            return false;
        }

        var persistedIndex = table.Indexes.FirstOrDefault(candidate =>
            ReferenceEquals(candidate, index)
            || string.Equals(candidate.Name, index.Name, StringComparison.OrdinalIgnoreCase));
        if (persistedIndex is null)
            return false;

        var comparer = CreateIndexComparer(table, persistedIndex);
        var (getPageIo, openSnapshot, closeSnapshot) = CreateIndexAccessorSnapshotAdapter(sharedSnapshot);

        if (table.WithoutRowid)
        {
            var primaryKeySchema = table.PrimaryKeySchema
                ?? throw new InvalidDataException(
                    $"WITHOUT ROWID table '{table.Name}' is missing its primary-key metadata.");
            var storageColumns = GetWithoutRowidIndexStorageColumns(table, persistedIndex);
            var primaryKeyComparer = CreatePrimaryKeyComparer(primaryKeySchema);
            accessor = new EmbeddedFileIndexAccessor(
                (prefix, pageRead, keyCompared, tableRowFetched) => SeekCommittedWithoutRowidIndex(
                    table,
                    persistedIndex,
                    storageColumns,
                    primaryKeySchema,
                    tableRootPage,
                    indexRootPage,
                    prefixLength,
                    covering,
                    getPageIo(),
                    comparer,
                    primaryKeyComparer,
                    prefix,
                    pageRead,
                    keyCompared,
                    tableRowFetched),
                openSnapshot,
                closeSnapshot);
            return true;
        }

        accessor = new EmbeddedFileIndexAccessor(
            (prefix, pageRead, keyCompared, tableRowFetched) => SeekCommittedRowidIndex(
                table,
                persistedIndex,
                tableRootPage,
                indexRootPage,
                prefixLength,
                covering,
                getPageIo(),
                comparer,
                prefix,
                pageRead,
                keyCompared,
                tableRowFetched),
            openSnapshot,
            closeSnapshot);
        return true;
    }

    private bool TryOpenPrimaryKeyIndexAccessor(
        EmbeddedTable table,
        int prefixLength,
        EmbeddedFileReadSnapshot? sharedSnapshot,
        out EmbeddedFileIndexAccessor accessor)
    {
        accessor = null!;
        var primaryKeySchema = table.PrimaryKeySchema;
        // See the identical comment in TryOpenIndexAccessor: a shared snapshot's own captured
        // root-page mapping must win over the store's live one.
        var tableRootPages = sharedSnapshot?.TableRootPages ?? _tableRootPages;
        if (primaryKeySchema is null
            || prefixLength > primaryKeySchema.Terms.Count
            || !tableRootPages.TryGetValue(table.Name, out var tableRootPage))
        {
            return false;
        }

        var comparer = CreatePrimaryKeyComparer(primaryKeySchema);
        var (getPageIo, openSnapshot, closeSnapshot) = CreateIndexAccessorSnapshotAdapter(sharedSnapshot);
        accessor = new EmbeddedFileIndexAccessor(
            (prefix, pageRead, keyCompared, _) => SeekCommittedPrimaryKeyIndex(
                table,
                primaryKeySchema,
                tableRootPage,
                prefixLength,
                getPageIo(),
                comparer,
                prefix,
                pageRead,
                keyCompared),
            openSnapshot,
            closeSnapshot);
        return true;
    }

    /// <summary>
    /// Opens a durable full-ascending-order scan accessor over an index's (or a WITHOUT ROWID
    /// table's own primary-key b-tree's) entire committed b-tree — the "no known equality prefix"
    /// counterpart of <see cref="TryOpenIndexAccessor"/>. A caller with no seek prefix (e.g. an
    /// ORDER BY-elision full scan, or an MVCC merge that must visit every base row) uses this to
    /// stream the base table's natural key order lazily instead of materializing and sorting every
    /// row in memory first.
    /// </summary>
    internal bool TryOpenIndexFullScanAccessor(
        EmbeddedTable table,
        EmbeddedIndex? index,
        bool covering,
        EmbeddedFileReadSnapshot? sharedSnapshot,
        out EmbeddedFileIndexFullScanAccessor accessor,
        bool requireCommittedTableIdentity = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        accessor = null!;
        if (!CanOpenIndexAccessor(table, index, requireCommittedTableIdentity))
            return false;

        if (index is null)
            return TryOpenPrimaryKeyIndexFullScanAccessor(table, sharedSnapshot, out accessor);

        // See the identical comment in TryOpenIndexAccessor: a shared snapshot's own captured
        // root-page mapping must win over the store's live one.
        var tableRootPages = sharedSnapshot?.TableRootPages ?? _tableRootPages;
        var indexRootPages = sharedSnapshot?.IndexRootPages ?? _indexRootPages;
        if (!tableRootPages.TryGetValue(table.Name, out var tableRootPage)
            || !indexRootPages.TryGetValue(index.Name, out var indexRootPage))
        {
            return false;
        }

        var persistedIndex = table.Indexes.FirstOrDefault(candidate =>
            ReferenceEquals(candidate, index)
            || string.Equals(candidate.Name, index.Name, StringComparison.OrdinalIgnoreCase));
        if (persistedIndex is null)
            return false;

        var comparer = CreateIndexComparer(table, persistedIndex);
        var (getPageIo, openSnapshot, closeSnapshot) = CreateIndexAccessorSnapshotAdapter(sharedSnapshot);

        if (table.WithoutRowid)
        {
            var primaryKeySchema = table.PrimaryKeySchema
                ?? throw new InvalidDataException(
                    $"WITHOUT ROWID table '{table.Name}' is missing its primary-key metadata.");
            var storageColumns = GetWithoutRowidIndexStorageColumns(table, persistedIndex);
            var primaryKeyComparer = CreatePrimaryKeyComparer(primaryKeySchema);
            accessor = new EmbeddedFileIndexFullScanAccessor(
                (pageRead, tableRowFetched) => ScanCommittedWithoutRowidIndexAscending(
                    table,
                    persistedIndex,
                    storageColumns,
                    primaryKeySchema,
                    tableRootPage,
                    indexRootPage,
                    covering,
                    getPageIo(),
                    comparer,
                    primaryKeyComparer,
                    pageRead,
                    tableRowFetched),
                openSnapshot,
                closeSnapshot);
            return true;
        }

        accessor = new EmbeddedFileIndexFullScanAccessor(
            (pageRead, tableRowFetched) => ScanCommittedRowidIndexAscending(
                table,
                persistedIndex,
                tableRootPage,
                indexRootPage,
                covering,
                getPageIo(),
                comparer,
                pageRead,
                tableRowFetched),
            openSnapshot,
            closeSnapshot);
        return true;
    }

    private bool TryOpenPrimaryKeyIndexFullScanAccessor(
        EmbeddedTable table,
        EmbeddedFileReadSnapshot? sharedSnapshot,
        out EmbeddedFileIndexFullScanAccessor accessor)
    {
        accessor = null!;
        var primaryKeySchema = table.PrimaryKeySchema;
        // See the identical comment in TryOpenIndexAccessor: a shared snapshot's own captured
        // root-page mapping must win over the store's live one.
        var tableRootPages = sharedSnapshot?.TableRootPages ?? _tableRootPages;
        if (primaryKeySchema is null || !tableRootPages.TryGetValue(table.Name, out var tableRootPage))
            return false;

        var comparer = CreatePrimaryKeyComparer(primaryKeySchema);
        var (getPageIo, openSnapshot, closeSnapshot) = CreateIndexAccessorSnapshotAdapter(sharedSnapshot);
        accessor = new EmbeddedFileIndexFullScanAccessor(
            (pageRead, _) => ScanCommittedPrimaryKeyIndexAscending(
                table,
                primaryKeySchema,
                tableRootPage,
                getPageIo(),
                comparer,
                pageRead),
            openSnapshot,
            closeSnapshot);
        return true;
    }

    /// <summary>
    /// Builds the shared lazy-open/close committed-page-snapshot plumbing used by every durable
    /// index accessor (secondary index, or WITHOUT ROWID primary-key b-tree). When
    /// <paramref name="sharedSnapshot"/> is supplied the accessor reuses its page I/O and never
    /// opens or closes a transaction of its own; otherwise a fresh read transaction is opened
    /// lazily on first use and disposed when the accessor is disposed.
    /// </summary>
    private (Func<ISqliteBtreePageIo> GetPageIo, Action Open, Action Close) CreateIndexAccessorSnapshotAdapter(
        EmbeddedFileReadSnapshot? sharedSnapshot)
    {
        var snapshotGate = new object();
        SqlitePagerReadTransaction? snapshot = null;
        ISqliteBtreePageIo? pageIo = null;
        if (sharedSnapshot is not null)
        {
            if (!ReferenceEquals(sharedSnapshot.Owner, this))
                throw new ArgumentException("The shared read snapshot belongs to another file store.", nameof(sharedSnapshot));
            pageIo = sharedSnapshot.PageIo;
        }

        ISqliteBtreePageIo GetPageIo()
        {
            lock (snapshotGate)
            {
                if (pageIo is not null)
                    return pageIo;
                snapshot = _pager.BeginReadTransaction();
                pageIo = new SqliteStagedBtreePageIo(
                    snapshot.ReadPage,
                    snapshot.PageCount,
                    _pageSize,
                    _usableSpace);
                return pageIo;
            }
        }

        void CloseSnapshot()
        {
            if (sharedSnapshot is not null)
                return;
            lock (snapshotGate)
            {
                snapshot?.Dispose();
                snapshot = null;
                pageIo = null;
            }
        }

        return (
            GetPageIo,
            sharedSnapshot is null ? () => _ = GetPageIo() : static () => { },
            CloseSnapshot);
    }

    private IEnumerable<EmbeddedFileIndexSeekRow> SeekCommittedRowidIndex(
        EmbeddedTable table,
        EmbeddedIndex index,
        uint tableRootPage,
        uint indexRootPage,
        int prefixLength,
        bool covering,
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        SqlValue[] prefix,
        Action? pageRead,
        Action? keyCompared,
        Action? tableRowFetched)
    {
        if (prefix.Length != prefixLength)
            throw new ArgumentException("The index seek key does not match the planned prefix length.", nameof(prefix));

        var indexCursor = new SqliteIndexBtreeCursor(pageIo, comparer, _textEncoding);
        var tableCursor = covering ? null : new SqliteTableBtreeCursor(pageIo);
        foreach (var record in indexCursor.SeekPrefix(indexRootPage, prefix, pageRead, keyCompared))
        {
            var indexValues = SqliteRecordCodec.Decode(record, _textEncoding);
            if (indexValues.Length <= index.Columns.Count
                || indexValues[^1].Kind != SqlValueKind.Integer)
            {
                throw new InvalidDataException(
                    $"SQLite index '{index.Name}' record is missing its rowid suffix.");
            }

            var rowId = indexValues[^1].AsInteger();
            SqlValue[] row;
            if (covering)
            {
                row = new SqlValue[table.Columns.Length];
                Array.Fill(row, SqlValue.Null);
                for (var position = 0; position < index.Columns.Count; position++)
                    row[index.Columns[position].ColumnIndex] = indexValues[position];
            }
            else
            {
                if (!tableCursor!.TrySeek(tableRootPage, rowId, out var tableRecord))
                {
                    throw new InvalidDataException(
                        $"SQLite index '{index.Name}' references missing rowid {rowId} in table '{table.Name}'.");
                }

                tableRowFetched?.Invoke();
                row = RestoreRowidTableRecord(table, SqliteRecordCodec.Decode(tableRecord, _textEncoding));
            }

            if (table.RowidAliasColumnIndex >= 0)
                row[table.RowidAliasColumnIndex] = SqlValue.Integer(rowId);
            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, row);
            yield return new EmbeddedFileIndexSeekRow(row, rowId);
        }
    }

    /// <summary>
    /// Seeks a WITHOUT ROWID table's secondary index. Every persisted record for such an index
    /// always carries the table's full primary-key columns as a locator suffix (see
    /// <see cref="GetWithoutRowidIndexStorageColumns"/>), never a rowid: a non-covering match
    /// re-seeks the table's own root page — which the managed engine stores as an index b-tree
    /// keyed by the declared primary key — using that locator instead of <see cref="SqliteTableBtreeCursor"/>.
    /// </summary>
    private IEnumerable<EmbeddedFileIndexSeekRow> SeekCommittedWithoutRowidIndex(
        EmbeddedTable table,
        EmbeddedIndex index,
        IReadOnlyList<EmbeddedIndexColumn> storageColumns,
        SqlitePrimaryKeySchema primaryKeySchema,
        uint tableRootPage,
        uint indexRootPage,
        int prefixLength,
        bool covering,
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        SqliteIndexRecordComparer primaryKeyComparer,
        SqlValue[] prefix,
        Action? pageRead,
        Action? keyCompared,
        Action? tableRowFetched)
    {
        if (prefix.Length != prefixLength)
            throw new ArgumentException("The index seek key does not match the planned prefix length.", nameof(prefix));

        var indexCursor = new SqliteIndexBtreeCursor(pageIo, comparer, _textEncoding);
        var tableCursor = covering ? null : new SqliteIndexBtreeCursor(pageIo, primaryKeyComparer, _textEncoding);
        foreach (var record in indexCursor.SeekPrefix(indexRootPage, prefix, pageRead, keyCompared))
        {
            var indexValues = SqliteRecordCodec.Decode(record, _textEncoding);
            if (indexValues.Length != storageColumns.Count)
            {
                throw new InvalidDataException(
                    $"SQLite index '{index.Name}' record does not carry its declared storage columns.");
            }

            // Scatter every non-expression storage column (the declared index columns plus the
            // always-present primary-key suffix) into a partial row. Expression-index columns
            // (ColumnIndex < 0) key the index but are never real table columns, and a covering
            // plan never includes one (see CanOpenIndexAccessor); they contribute to neither the
            // covering projection nor primary-key reconstruction.
            var partialRow = new SqlValue[table.Columns.Length];
            Array.Fill(partialRow, SqlValue.Null);
            for (var position = 0; position < storageColumns.Count; position++)
            {
                var columnIndex = storageColumns[position].ColumnIndex;
                if (columnIndex >= 0)
                    partialRow[columnIndex] = indexValues[position];
            }

            SqlValue[] row;
            if (covering)
            {
                row = partialRow;
            }
            else
            {
                var primaryKey = primaryKeySchema.ProjectKey(partialRow);
                byte[]? tableRecord = null;
                foreach (var candidate in tableCursor!.SeekPrefix(tableRootPage, primaryKey, pageRead, keyCompared))
                {
                    tableRecord = candidate;
                    break;
                }

                if (tableRecord is null)
                {
                    throw new InvalidDataException(
                        $"SQLite index '{index.Name}' references a missing primary key in table '{table.Name}'.");
                }

                tableRowFetched?.Invoke();
                row = RestoreWithoutRowidRecord(
                    table.Name,
                    table,
                    primaryKeySchema,
                    SqliteRecordCodec.Decode(tableRecord, _textEncoding));
            }

            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, row);
            yield return new EmbeddedFileIndexSeekRow(row, RowId: null);
        }
    }

    /// <summary>
    /// Seeks a WITHOUT ROWID table's own primary-key b-tree (its root page) directly, driving an
    /// equality join lookup on the table's leading primary-key term(s) without materializing an
    /// index view. The primary key already carries the full row, so this is always covering.
    /// </summary>
    private IEnumerable<EmbeddedFileIndexSeekRow> SeekCommittedPrimaryKeyIndex(
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        uint tableRootPage,
        int prefixLength,
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        SqlValue[] prefix,
        Action? pageRead,
        Action? keyCompared)
    {
        if (prefix.Length != prefixLength)
            throw new ArgumentException("The index seek key does not match the planned prefix length.", nameof(prefix));

        var cursor = new SqliteIndexBtreeCursor(pageIo, comparer, _textEncoding);
        foreach (var record in cursor.SeekPrefix(tableRootPage, prefix, pageRead, keyCompared))
        {
            var storedValues = SqliteRecordCodec.Decode(record, _textEncoding);
            var row = RestoreWithoutRowidRecord(table.Name, table, primaryKeySchema, storedValues);
            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, row);
            yield return new EmbeddedFileIndexSeekRow(row, RowId: null);
        }
    }

    /// <summary>
    /// Ascending-order sibling of <see cref="SeekCommittedRowidIndex"/>: materializes every row in
    /// the index's committed b-tree, in the index's own key order, instead of only the rows
    /// matching an equality prefix. Used by ORDER BY-elision and MVCC merge scans that have no
    /// known seek prefix.
    /// </summary>
    private IEnumerable<EmbeddedFileIndexSeekRow> ScanCommittedRowidIndexAscending(
        EmbeddedTable table,
        EmbeddedIndex index,
        uint tableRootPage,
        uint indexRootPage,
        bool covering,
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        Action? pageRead,
        Action? tableRowFetched)
    {
        var indexCursor = new SqliteIndexBtreeCursor(pageIo, comparer, _textEncoding);
        var tableCursor = covering ? null : new SqliteTableBtreeCursor(pageIo);
        foreach (var record in indexCursor.ScanAscending(indexRootPage, pageRead))
        {
            var indexValues = SqliteRecordCodec.Decode(record, _textEncoding);
            if (indexValues.Length <= index.Columns.Count
                || indexValues[^1].Kind != SqlValueKind.Integer)
            {
                throw new InvalidDataException(
                    $"SQLite index '{index.Name}' record is missing its rowid suffix.");
            }

            var rowId = indexValues[^1].AsInteger();
            SqlValue[] row;
            if (covering)
            {
                row = new SqlValue[table.Columns.Length];
                Array.Fill(row, SqlValue.Null);
                for (var position = 0; position < index.Columns.Count; position++)
                    row[index.Columns[position].ColumnIndex] = indexValues[position];
            }
            else
            {
                if (!tableCursor!.TrySeek(tableRootPage, rowId, out var tableRecord))
                {
                    throw new InvalidDataException(
                        $"SQLite index '{index.Name}' references missing rowid {rowId} in table '{table.Name}'.");
                }

                tableRowFetched?.Invoke();
                row = RestoreRowidTableRecord(table, SqliteRecordCodec.Decode(tableRecord, _textEncoding));
            }

            if (table.RowidAliasColumnIndex >= 0)
                row[table.RowidAliasColumnIndex] = SqlValue.Integer(rowId);
            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, row);
            yield return new EmbeddedFileIndexSeekRow(row, rowId);
        }
    }

    /// <summary>
    /// Ascending-order sibling of <see cref="SeekCommittedWithoutRowidIndex"/>. See that method's
    /// doc comment for the record shape; this variant walks the whole index instead of an equality
    /// prefix range.
    /// </summary>
    private IEnumerable<EmbeddedFileIndexSeekRow> ScanCommittedWithoutRowidIndexAscending(
        EmbeddedTable table,
        EmbeddedIndex index,
        IReadOnlyList<EmbeddedIndexColumn> storageColumns,
        SqlitePrimaryKeySchema primaryKeySchema,
        uint tableRootPage,
        uint indexRootPage,
        bool covering,
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        SqliteIndexRecordComparer primaryKeyComparer,
        Action? pageRead,
        Action? tableRowFetched)
    {
        var indexCursor = new SqliteIndexBtreeCursor(pageIo, comparer, _textEncoding);
        var tableCursor = covering ? null : new SqliteIndexBtreeCursor(pageIo, primaryKeyComparer, _textEncoding);
        foreach (var record in indexCursor.ScanAscending(indexRootPage, pageRead))
        {
            var indexValues = SqliteRecordCodec.Decode(record, _textEncoding);
            if (indexValues.Length != storageColumns.Count)
            {
                throw new InvalidDataException(
                    $"SQLite index '{index.Name}' record does not carry its declared storage columns.");
            }

            var partialRow = new SqlValue[table.Columns.Length];
            Array.Fill(partialRow, SqlValue.Null);
            for (var position = 0; position < storageColumns.Count; position++)
            {
                var columnIndex = storageColumns[position].ColumnIndex;
                if (columnIndex >= 0)
                    partialRow[columnIndex] = indexValues[position];
            }

            SqlValue[] row;
            if (covering)
            {
                row = partialRow;
            }
            else
            {
                var primaryKey = primaryKeySchema.ProjectKey(partialRow);
                byte[]? tableRecord = null;
                foreach (var candidate in tableCursor!.SeekPrefix(tableRootPage, primaryKey, pageRead, keyCompared: null))
                {
                    tableRecord = candidate;
                    break;
                }

                if (tableRecord is null)
                {
                    throw new InvalidDataException(
                        $"SQLite index '{index.Name}' references a missing primary key in table '{table.Name}'.");
                }

                tableRowFetched?.Invoke();
                row = RestoreWithoutRowidRecord(
                    table.Name,
                    table,
                    primaryKeySchema,
                    SqliteRecordCodec.Decode(tableRecord, _textEncoding));
            }

            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, row);
            yield return new EmbeddedFileIndexSeekRow(row, RowId: null);
        }
    }

    /// <summary>
    /// Ascending-order sibling of <see cref="SeekCommittedPrimaryKeyIndex"/>: walks a WITHOUT
    /// ROWID table's own primary-key b-tree in full instead of seeking an equality prefix.
    /// </summary>
    private IEnumerable<EmbeddedFileIndexSeekRow> ScanCommittedPrimaryKeyIndexAscending(
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        uint tableRootPage,
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        Action? pageRead)
    {
        var cursor = new SqliteIndexBtreeCursor(pageIo, comparer, _textEncoding);
        foreach (var record in cursor.ScanAscending(tableRootPage, pageRead))
        {
            var storedValues = SqliteRecordCodec.Decode(record, _textEncoding);
            var row = RestoreWithoutRowidRecord(table.Name, table, primaryKeySchema, storedValues);
            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, row);
            yield return new EmbeddedFileIndexSeekRow(row, RowId: null);
        }
    }

    private EmbeddedFileCatalog Load()
    {
        var tables = new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase);
        var views = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        var triggers = new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase);
        var virtualTables = new Dictionary<string, EmbeddedDatabase.VirtualTableDefinition>(
            StringComparer.OrdinalIgnoreCase);
        var rootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var indexRootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var loadedIndexes = new Dictionary<string, EmbeddedIndex>(StringComparer.OrdinalIgnoreCase);
        long triggerDeclarationOrder = 0;

        var schemaEntries = ReadSchemaEntries();
        ValidateSchemaEntries(schemaEntries);

        var occupiedBtreePages = new HashSet<uint>(
            schemaEntries
                .Where(entry => entry.Type is "table" or "index" && entry.RootPage != 0)
                .Select(entry => entry.RootPage));

        // Materialize tables first so views and triggers can be parsed afterwards.
        foreach (var entry in schemaEntries)
        {
            if (!string.Equals(entry.Type, "table", StringComparison.Ordinal))
                continue;

            if (entry.RootPage == 0)
            {
                virtualTables.Add(entry.Name, ManagedSchemaRowParser.ParseVirtualTable(entry));
                continue;
            }

            var table = ManagedSchemaRowParser.ParseTable(entry);
            LoadTableRows(entry.Name, table, entry.RootPage, occupiedBtreePages);
            tables[entry.Name] = table;
            rootPages[entry.Name] = entry.RootPage;
        }

        foreach (var entry in schemaEntries)
        {
            if (!string.Equals(entry.Type, "index", StringComparison.Ordinal))
                continue;

            if (!tables.TryGetValue(entry.TableName, out var table))
            {
                throw new EmbeddedSqlException(
                    $"Stored index '{entry.Name}' references missing table '{entry.TableName}'.");
            }
            if (entry.Sql is null)
            {
                var candidates = new List<EmbeddedIndex>();
                var currentIndex = table.Indexes.SingleOrDefault(index =>
                    index.Origin != EmbeddedIndexOrigin.Explicit
                    && string.Equals(index.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (currentIndex is not null)
                    candidates.Add(currentIndex);
                if (table.TryGetLegacyImplicitIndex(entry.Name, out var legacyIndex)
                    && legacyIndex is not null)
                {
                    candidates.Add(legacyIndex);
                }
                if (candidates.Count == 0)
                {
                    throw new EmbeddedSqlException(
                        $"Stored implicit index '{entry.Name}' does not match a UNIQUE or PRIMARY KEY constraint on table '{entry.TableName}'.");
                }

                EmbeddedIndex? validatedIndex = null;
                EmbeddedSqlException? validationFailure = null;
                foreach (var candidate in candidates)
                {
                    var candidatePages = new HashSet<uint>(occupiedBtreePages);
                    try
                    {
                        ValidateIndexRepresentable(entry.TableName, table, candidate, structuralOnly: _collationResolver is null);
                        ValidateStoredIndex(entry, table, candidate, candidatePages, structuralOnly: _collationResolver is null);
                        occupiedBtreePages.UnionWith(candidatePages);
                        validatedIndex = candidate;
                        break;
                    }
                    catch (EmbeddedSqlException exception)
                    {
                        validationFailure ??= exception;
                    }
                }
                if (validatedIndex is null)
                {
                    throw validationFailure
                        ?? new EmbeddedSqlException(
                            $"Stored implicit index '{entry.Name}' does not match table '{entry.TableName}'.");
                }

                indexRootPages.Add(entry.Name, entry.RootPage);
                loadedIndexes.Add(entry.Name, validatedIndex);
                continue;
            }

            var parsed = ManagedSchemaRowParser.ParseIndex(entry, table);
            var index = parsed.Index;
            ValidateIndexRepresentable(entry.TableName, table, index, structuralOnly: _collationResolver is null);
            ValidateStoredIndex(entry, table, index, occupiedBtreePages, structuralOnly: _collationResolver is null);
            parsed.RestoreMethodState(entry.TableName, table);

            table.Indexes.Add(index);
            indexRootPages.Add(entry.Name, entry.RootPage);
            loadedIndexes.Add(entry.Name, index);
        }

        EmbeddedDatabase.ValidateSqliteSequenceCatalog(tables);
        ValidateAllocationMap(schemaEntries, tables, loadedIndexes, virtualTables);

        foreach (var entry in schemaEntries)
        {
            if (entry.Type is "table" or "index")
                continue;

            switch (entry.Type)
            {
                case "view":
                    views[entry.Name] = ManagedSchemaRowParser.ParseView(entry);
                    break;
                case "trigger":
                    triggers[entry.Name] = ManagedSchemaRowParser.ParseTrigger(
                        entry,
                        tables,
                        views,
                        triggerDeclarationOrder++);
                    break;
                default:
                    throw new EmbeddedSqlException($"Stored schema entry '{entry.Name}' has an unsupported type '{entry.Type}'.");
            }
        }

        _tableRootPages = rootPages;
        _indexRootPages = indexRootPages;
        _lastSchemaSignature = ComputeSchemaSignature(schemaEntries);
        _committedTables = tables;
        return new EmbeddedFileCatalog(tables, views, triggers, virtualTables);
    }

    private void ValidateAllocationMap(
        IReadOnlyList<ManagedSchemaRow> schemaEntries,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, EmbeddedIndex> loadedIndexes,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables)
    {
        try
        {
            var pageCount = _pager.CommittedPageCount;
            var freelist = SqliteFreelist.Read(
                _header,
                pageCount,
                _pager.ReadCommittedPage);
            var activePages = new HashSet<uint>();
            var overflowReader = new SqliteOverflowChainReader(_pager, _header);

            AddOwnedPage(activePages, SchemaRootPage, pageCount, "sqlite_schema");
            _ = CollectTableTreeNodePages(
                "sqlite_schema",
                SchemaRootPage,
                _pager.ReadCommittedPage(SchemaRootPage),
                activePages,
                pageCount,
                overflowReader,
                "root",
                isFirstPage: true);

            foreach (var entry in schemaEntries)
            {
                switch (entry.Type)
                {
                    case "table":
                        if (entry.RootPage == 0)
                        {
                            if (!virtualTables.ContainsKey(entry.Name))
                            {
                                throw new InvalidDataException(
                                    $"Managed file database is missing virtual table '{entry.Name}'.");
                            }
                            break;
                        }
                        if (!tables.TryGetValue(entry.Name, out var table))
                        {
                            throw new InvalidDataException(
                                $"Managed file database is missing the loaded definition for table '{entry.Name}'.");
                        }
                        CollectTableTreePages(entry, table, activePages, pageCount, overflowReader);
                        break;
                    case "index":
                        if (!tables.TryGetValue(entry.TableName, out var indexedTable))
                        {
                            throw new InvalidDataException(
                                $"Managed file database is missing table '{entry.TableName}' for index '{entry.Name}'.");
                        }
                        if (!loadedIndexes.TryGetValue(entry.Name, out var index))
                        {
                            throw new InvalidDataException(
                                $"Managed file database is missing the loaded definition for index '{entry.Name}'.");
                        }
                        CollectIndexTreePages(
                            entry,
                            activePages,
                            pageCount,
                            overflowReader,
                            // ValidateAllocationMap only runs from Load(), before EmbeddedDatabase
                            // wires up a runtime collation resolver, so a not-yet-resolvable custom
                            // collation must defer here exactly like the other Load() validation
                            // calls above (structural page-reachability only, never ordering).
                            CreateIndexComparer(indexedTable, index, allowDeferredCustomCollation: _collationResolver is null));
                        break;
                }
            }

            foreach (var activePage in activePages)
            {
                if (freelist.PageNumbers.Contains(activePage))
                {
                    throw new InvalidDataException(
                        $"SQLite page {activePage} is both reachable and present in the freelist.");
                }
            }

            var accountedPageCount = checked(activePages.Count + freelist.PageNumbers.Count);
            if (accountedPageCount != pageCount)
            {
                throw new InvalidDataException(
                    $"SQLite allocation map accounts for {accountedPageCount} page(s), but the database has {pageCount}.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new EmbeddedSqlException(
                "Managed file database has an invalid SQLite page allocation map.",
                exception);
        }
    }

    private void CollectTableTreePages(
        ManagedSchemaRow entry,
        EmbeddedTable table,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader)
    {
        if (table.WithoutRowid)
        {
            CollectWithoutRowidTableTreePages(entry, table, activePages, pageCount, overflowReader);
            return;
        }

        AddOwnedPage(activePages, entry.RootPage, pageCount, $"table '{entry.Name}' root");
        _ = CollectTableTreeNodePages(
            entry.Name,
            entry.RootPage,
            _pager.ReadCommittedPage(entry.RootPage),
            activePages,
            pageCount,
            overflowReader,
            "root");
    }

    private int CollectTableTreeNodePages(
        string tableName,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner,
        bool isFirstPage = false)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage, isFirstPage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                CollectTableLeafOverflowPages(
                    SqliteTableLeafPageView.Parse(pageImage, _usableSpace, isFirstPage),
                    activePages,
                    pageCount,
                    overflowReader,
                    $"table '{tableName}' {owner}");
                return 0;
            case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace, isFirstPage);
                    int? childHeight = null;
                    foreach (var childPage in interior.Cells
                                 .Select(cell => cell.Cell.LeftChildPage)
                                 .Append(interior.Header.RightMostChildPage))
                    {
                        AddOwnedPage(
                            activePages,
                            childPage,
                            pageCount,
                            $"table '{tableName}' interior child {pageNumber}");
                        var height = CollectTableTreeNodePages(
                            tableName,
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            activePages,
                            pageCount,
                            overflowReader,
                            $"interior child {pageNumber}");
                        if (childHeight is { } expectedHeight && height != expectedHeight)
                        {
                            throw new InvalidDataException(
                                $"Stored table '{tableName}' interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
                        }

                        childHeight = height;
                    }

                    return checked((childHeight ?? throw new InvalidDataException(
                        $"Stored table '{tableName}' has an empty interior page {pageNumber}.")) + 1);
                }
            default:
                throw new InvalidDataException(
                    $"Stored table '{tableName}' {owner} page {pageNumber} has unsupported type {header.PageType}.");
        }
    }

    private void CollectWithoutRowidTableTreePages(
        ManagedSchemaRow entry,
        EmbeddedTable table,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader)
    {
        AddOwnedPage(activePages, entry.RootPage, pageCount, $"WITHOUT ROWID table '{entry.Name}' root");
        var rootPage = _pager.ReadCommittedPage(entry.RootPage);
        var rootHeader = SqliteBtreePageHeader.Parse(rootPage);
        if (rootHeader.PageType is not (SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior))
        {
            throw new InvalidDataException(
                $"Stored WITHOUT ROWID table '{entry.Name}' root page has unsupported type {rootHeader.PageType}.");
        }

        _ = CollectIndexTreeNodePages(
            $"WITHOUT ROWID table '{entry.Name}'",
            entry.RootPage,
            rootPage,
            activePages,
            pageCount,
            overflowReader,
            "root",
            CreatePrimaryKeyComparer(
                table.PrimaryKeySchema
                    ?? throw new InvalidDataException(
                        $"Stored WITHOUT ROWID table '{entry.Name}' is missing primary-key metadata.")));
    }

    private void CollectIndexTreePages(
        ManagedSchemaRow entry,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        SqliteIndexRecordComparer comparer)
    {
        AddOwnedPage(activePages, entry.RootPage, pageCount, $"index '{entry.Name}' root");
        _ = CollectIndexTreeNodePages(
            $"index '{entry.Name}'",
            entry.RootPage,
            _pager.ReadCommittedPage(entry.RootPage),
            activePages,
            pageCount,
            overflowReader,
            "root",
            comparer);
    }

    private int CollectIndexTreeNodePages(
        string treeDescription,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner,
        SqliteIndexRecordComparer? comparer = null)
    {
        comparer ??= new SqliteIndexRecordComparer(_textEncoding);
        var header = SqliteBtreePageHeader.Parse(pageImage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                CollectIndexLeafOverflowPages(
                    SqliteIndexLeafPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader,
                        recordComparer: comparer),
                    activePages,
                    pageCount,
                    overflowReader,
                    $"{treeDescription} {owner}");
                return 0;
            case SqliteBtreePageType.IndexInterior:
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader,
                        recordComparer: comparer);
                    foreach (var cell in interior.Cells)
                    {
                        CollectIndexOverflowPages(
                            cell.Cell.Key,
                            activePages,
                            pageCount,
                            overflowReader,
                            $"{treeDescription} interior separator");
                    }

                    int? childHeight = null;
                    foreach (var childPage in interior.Cells
                                 .Select(cell => cell.Cell.LeftChildPage)
                                 .Append(interior.Header.RightMostChildPage))
                    {
                        AddOwnedPage(
                            activePages,
                            childPage,
                            pageCount,
                            $"{treeDescription} interior child {pageNumber}");
                        var height = CollectIndexTreeNodePages(
                            treeDescription,
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            activePages,
                            pageCount,
                            overflowReader,
                            $"interior child {pageNumber}",
                            comparer);
                        if (childHeight is { } expectedHeight && height != expectedHeight)
                        {
                            throw new InvalidDataException(
                                $"Stored {treeDescription} interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
                        }

                        childHeight = height;
                    }

                    return checked((childHeight ?? throw new InvalidDataException(
                        $"Stored {treeDescription} has an empty interior page {pageNumber}.")) + 1);
                }
            default:
                throw new InvalidDataException(
                    $"Stored {treeDescription} {owner} page {pageNumber} has unsupported type {header.PageType}.");
        }
    }

    /// <summary>
    /// Collects every page one index's b-tree currently occupies EXCEPT its
    /// root — interior, leaf, and overflow pages — reading through
    /// <paramref name="pageIo"/> rather than the committed pager directly, so
    /// this is safe to call against a <see cref="SqliteStagedBtreePageIo"/>
    /// mid-transaction as well as against committed state.
    /// </summary>
    /// <remarks>
    /// A targeted REINDEX frees the pages this collects and then writes the
    /// index's replacement tree, reusing the same root page number so the
    /// schema catalog's root-page pointer never has to change. <paramref
    /// name="comparer"/> should be built with
    /// <c>allowDeferredCustomCollation: true</c> AND
    /// <c>forceDeferCustomCollation: true</c>: this traversal only needs to
    /// prove page/record shape, never real ordering, so an index whose
    /// custom collation callback is not currently registered — or whose
    /// currently-registered callback is a replacement that no longer agrees
    /// with the tree's existing physical order, which is precisely why a
    /// REINDEX is often requested in the first place — can still have its
    /// stale tree freed without ever binary-comparing its keys under either
    /// the old or the new semantics.
    /// </remarks>
    private void CollectIndexTreeNonRootPagesForFree(
        uint rootPage,
        ISqliteBtreePageIo pageIo,
        SqliteOverflowChainReader overflowReader,
        SqliteIndexRecordComparer comparer,
        ISet<uint> pages)
    {
        CollectIndexTreeNodePagesForFree(rootPage, pageIo.ReadPage(rootPage), pageIo, overflowReader, comparer, pages);
    }

    private void CollectIndexTreeNodePagesForFree(
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISqliteBtreePageIo pageIo,
        SqliteOverflowChainReader overflowReader,
        SqliteIndexRecordComparer comparer,
        ISet<uint> pages)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                CollectIndexLeafOverflowPages(
                    SqliteIndexLeafPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader,
                        recordComparer: comparer),
                    pages,
                    pageIo.PageCount,
                    overflowReader,
                    $"stale index page {pageNumber}");
                return;
            case SqliteBtreePageType.IndexInterior:
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader,
                        recordComparer: comparer);
                    foreach (var cell in interior.Cells)
                    {
                        CollectIndexOverflowPages(
                            cell.Cell.Key,
                            pages,
                            pageIo.PageCount,
                            overflowReader,
                            $"stale index page {pageNumber} separator");
                    }

                    foreach (var childPage in interior.Cells
                                 .Select(cell => cell.Cell.LeftChildPage)
                                 .Append(interior.Header.RightMostChildPage))
                    {
                        AddOwnedPage(pages, childPage, pageIo.PageCount, $"stale index page {pageNumber} child");
                        CollectIndexTreeNodePagesForFree(
                            childPage,
                            pageIo.ReadPage(childPage),
                            pageIo,
                            overflowReader,
                            comparer,
                            pages);
                    }

                    return;
                }
            default:
                throw new InvalidDataException(
                    $"Stale index page {pageNumber} has unsupported type {header.PageType}.");
        }
    }

    /// <summary>
    /// Finds indexes whose collation is currently unavailable — nothing has registered a
    /// callback for a custom name, or an application override is bound but this store cannot
    /// prove it still agrees with the tree's existing physical order — but whose owning table
    /// has not changed since the previous commit, and clones their existing committed b-tree
    /// byte-for-byte so a full rewrite never has to call <see cref="BuildIndexTree"/> — which
    /// unconditionally requires a working comparator — for them. This is what lets an unrelated
    /// <c>CREATE TABLE</c> or a sibling <c>CREATE INDEX</c> succeed even while another index
    /// depends on a collation nothing has registered right now.
    /// </summary>
    /// <remarks>
    /// Only ever engaged for a plain full rewrite (see the <c>!reclaimTrailingPages</c> caller
    /// in <see cref="PersistCore"/>): VACUUM / page-size migration compute their target page
    /// count from <see cref="RebuildPageAllocator.HighestAllocatedPage"/>, which never accounts
    /// for a page this method asks the allocator to leave untouched, so a preserved index's
    /// existing (possibly high-numbered) page could then land outside the shrunk target range
    /// and fail <see cref="ValidateRewritePlan"/>.
    /// </remarks>
    private Dictionary<string, PreservedIndexTree> TryPreserveUnavailableIndexTrees(
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables,
        SqliteDatabaseHeader currentHeader)
    {
        var preserved = new Dictionary<string, PreservedIndexTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in indexes)
        {
            if (definition.Index.IsMethodIndex)
                continue;
            if (!IsIndexUnchangedFromPrevious(definition.TableName, definition.Table, definition.Index, previousTables))
                continue;
            if (!_indexRootPages.TryGetValue(definition.Index.Name, out var rootPage)
                || rootPage < 2
                || rootPage > _pager.CommittedPageCount)
            {
                continue;
            }

            // A comparer built with no deferred flags is exactly this store's
            // "is this index's collation fully usable right now" probe (see
            // CreateIndexComparer / CreateIndexComparerOrThrowNoSuchCollation).
            // When it succeeds there is nothing to preserve: the ordinary
            // BuildIndexTree path below already handles this index correctly —
            // and, unlike this preserving path, also re-proves its order —
            // so only genuinely unavailable collations reach the clone below.
            // That probe only ever looks at indexed-term (column-level)
            // collations, though: a partial index's WHERE predicate or an
            // expression index's column body can embed its own COLLATE
            // reference that CreateIndexComparer never sees at all (it is
            // never handed to the SqliteIndexRecordComparer constructor).
            // TryFindUnresolvedEmbeddedIndexCollation is the same check
            // ValidateStoredIndex uses to defer judgement on those, so an
            // index unavailable for that reason must be preserved here too —
            // otherwise BuildIndexRecords below would call into the
            // predicate/expression evaluator and fail hard with "no such
            // collation sequence", turning an unrelated CREATE TABLE / CREATE
            // INDEX into a hard failure instead of leaving this index alone.
            if (!TryFindUnresolvedEmbeddedIndexCollation(definition.Index, out _))
            {
                try
                {
                    CreateIndexComparer(definition.Table, definition.Index);
                    continue;
                }
                catch (EmbeddedSqlException)
                {
                    // Falls through to attempt preservation.
                }
            }

            try
            {
                var structuralComparer = CreateIndexComparer(
                    definition.Table,
                    definition.Index,
                    allowDeferredCustomCollation: true,
                    forceDeferCustomCollation: true);
                var tree = ClonePreservedIndexTree(rootPage, structuralComparer, currentHeader);
                preserved[definition.Index.Name] = new PreservedIndexTree(rootPage, tree);
            }
            catch (InvalidDataException)
            {
                // The committed tree itself is not safely traversable
                // (physically corrupted, or truncated). Fall back to the
                // ordinary rebuild path, which will then correctly throw the
                // same no-such-collation-sequence failure BuildIndexTree
                // already produces for an index whose collation truly is
                // unavailable: preservation is only ever a byte-for-byte
                // optimization, never a way to paper over real corruption.
            }
        }

        return preserved;
    }

    /// <summary>
    /// Reads one index's complete committed b-tree — root, every interior/leaf/overflow page —
    /// into a <see cref="PreparedIndexTree"/> without ever trusting <paramref
    /// name="structuralComparer"/> to compare key ordering (it must be built with
    /// <c>forceDeferCustomCollation: true</c>). Applies the same duplicate/out-of-range page
    /// checks <see cref="CollectIndexTreeNodePagesForFree"/> applies when freeing a stale tree,
    /// so a physically corrupted committed image is rejected here exactly as it would be there,
    /// rather than silently copied forward.
    /// </summary>
    private PreparedIndexTree ClonePreservedIndexTree(
        uint rootPage,
        SqliteIndexRecordComparer structuralComparer,
        SqliteDatabaseHeader currentHeader)
    {
        var overflowReader = new SqliteOverflowChainReader(_pager, currentHeader);
        var visited = new HashSet<uint> { rootPage };
        var interiorPages = new List<PageImage>();
        var leafPages = new List<PageImage>();
        var overflowPages = new List<PageImage>();
        var rootImage = _pager.ReadCommittedPage(rootPage);
        ClonePreservedIndexNode(
            rootPage,
            rootImage,
            isRoot: true,
            overflowReader,
            structuralComparer,
            visited,
            interiorPages,
            leafPages,
            overflowPages);
        return new PreparedIndexTree(rootImage, interiorPages, leafPages, overflowPages);
    }

    private void ClonePreservedIndexNode(
        uint pageNumber,
        byte[] pageImage,
        bool isRoot,
        SqliteOverflowChainReader overflowReader,
        SqliteIndexRecordComparer comparer,
        HashSet<uint> visited,
        List<PageImage> interiorPages,
        List<PageImage> leafPages,
        List<PageImage> overflowPages)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                {
                    if (!isRoot)
                        leafPages.Add(new PageImage(pageNumber, pageImage));

                    var leaf = SqliteIndexLeafPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader,
                        recordComparer: comparer);
                    foreach (var cell in leaf.Cells)
                    {
                        CloneIndexOverflowPages(
                            cell.Cell,
                            overflowReader,
                            visited,
                            overflowPages,
                            $"preserved index page {pageNumber}");
                    }

                    return;
                }
            case SqliteBtreePageType.IndexInterior:
                {
                    if (!isRoot)
                        interiorPages.Add(new PageImage(pageNumber, pageImage));

                    var interior = SqliteIndexInteriorPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader,
                        recordComparer: comparer);
                    foreach (var cell in interior.Cells)
                    {
                        CloneIndexOverflowPages(
                            cell.Cell.Key,
                            overflowReader,
                            visited,
                            overflowPages,
                            $"preserved index page {pageNumber} separator");
                    }

                    foreach (var childPage in interior.Cells
                                 .Select(cell => cell.Cell.LeftChildPage)
                                 .Append(interior.Header.RightMostChildPage))
                    {
                        AddOwnedPage(visited, childPage, _pager.CommittedPageCount, $"preserved index page {pageNumber} child");
                        ClonePreservedIndexNode(
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            isRoot: false,
                            overflowReader,
                            comparer,
                            visited,
                            interiorPages,
                            leafPages,
                            overflowPages);
                    }

                    return;
                }
            default:
                throw new InvalidDataException(
                    $"Stored preserved index page {pageNumber} has unsupported type {header.PageType}.");
        }
    }

    private void CloneIndexOverflowPages(
        SqliteIndexLeafCell cell,
        SqliteOverflowChainReader overflowReader,
        HashSet<uint> visited,
        List<PageImage> overflowPages,
        string owner)
    {
        var overflowLength = GetOverflowLength(cell.PayloadLength, cell.LocalPayload.Length, owner);
        if (overflowLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException($"SQLite {owner} cell has an unnecessary overflow page.");
            return;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException($"SQLite {owner} cell is missing its overflow page.");

        foreach (var overflowPage in overflowReader.Traverse(firstOverflowPage, overflowLength))
        {
            AddOwnedPage(visited, overflowPage, _pager.CommittedPageCount, $"{owner} overflow");
            overflowPages.Add(new PageImage(overflowPage, _pager.ReadCommittedPage(overflowPage)));
        }
    }

    /// <summary>
    /// Unions every root/interior/leaf/overflow page number a set of preserved index trees
    /// occupies, so <see cref="RebuildPageAllocator"/> can be told to leave all of them alone.
    /// Also re-validates there is no collision ACROSS different preserved indexes: each tree's
    /// own clone already guarantees no duplicate WITHIN itself (see the <c>visited</c> set in
    /// <see cref="ClonePreservedIndexTree"/>), but two indexes should never legitimately share a
    /// page, so a collision here would mean the committed catalog itself is corrupted.
    /// </summary>
    private static HashSet<uint> CollectPreservedIndexTreePages(
        IReadOnlyDictionary<string, PreservedIndexTree> preservedIndexTrees)
    {
        var pages = new HashSet<uint>();
        foreach (var preserved in preservedIndexTrees.Values)
        {
            AddOwnedPage(pages, preserved.RootPage, uint.MaxValue, "preserved index root");
            foreach (var page in preserved.Tree.InteriorPages)
                AddOwnedPage(pages, page.PageNumber, uint.MaxValue, "preserved index page");
            foreach (var page in preserved.Tree.LeafPages)
                AddOwnedPage(pages, page.PageNumber, uint.MaxValue, "preserved index page");
            foreach (var page in preserved.Tree.OverflowPages)
                AddOwnedPage(pages, page.PageNumber, uint.MaxValue, "preserved index overflow page");
        }

        return pages;
    }

    private static void CollectTableLeafOverflowPages(
        SqliteTableLeafPageView leaf,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        foreach (var cell in leaf.Cells)
        {
            CollectTableOverflowPages(
                cell.Cell,
                activePages,
                pageCount,
                overflowReader,
                owner);
        }
    }

    private static void CollectIndexLeafOverflowPages(
        SqliteIndexLeafPageView leaf,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        foreach (var cell in leaf.Cells)
        {
            CollectIndexOverflowPages(
                cell.Cell,
                activePages,
                pageCount,
                overflowReader,
                owner);
        }
    }

    private static void CollectTableOverflowPages(
        SqliteTableLeafCell cell,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        var overflowLength = GetOverflowLength(cell.PayloadLength, cell.LocalPayload.Length, owner);
        if (overflowLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException($"SQLite {owner} cell has an unnecessary overflow page.");
            return;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException($"SQLite {owner} cell is missing its overflow page.");

        foreach (var overflowPage in overflowReader.Traverse(firstOverflowPage, overflowLength))
            AddOwnedPage(activePages, overflowPage, pageCount, $"{owner} overflow");
    }

    private static void CollectIndexOverflowPages(
        SqliteIndexLeafCell cell,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        var overflowLength = GetOverflowLength(cell.PayloadLength, cell.LocalPayload.Length, owner);
        if (overflowLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException($"SQLite {owner} cell has an unnecessary overflow page.");
            return;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException($"SQLite {owner} cell is missing its overflow page.");

        foreach (var overflowPage in overflowReader.Traverse(firstOverflowPage, overflowLength))
            AddOwnedPage(activePages, overflowPage, pageCount, $"{owner} overflow");
    }

    private static ulong GetOverflowLength(ulong payloadLength, int localPayloadLength, string owner)
    {
        if (localPayloadLength < 0 || (ulong)localPayloadLength > payloadLength)
            throw new InvalidDataException($"SQLite {owner} cell local payload exceeds its logical payload.");

        return payloadLength - (ulong)localPayloadLength;
    }

    private static void AddOwnedPage(ISet<uint> activePages, uint pageNumber, uint pageCount, string owner)
    {
        if (pageNumber == 0 || pageNumber > pageCount)
            throw new InvalidDataException($"SQLite {owner} references invalid page {pageNumber}.");
        if (!activePages.Add(pageNumber))
            throw new InvalidDataException($"SQLite {owner} reuses page {pageNumber}.");
    }

    private List<ManagedSchemaRow> ReadSchemaEntries()
    {
        try
        {
            var entries = new List<ManagedSchemaRow>();
            var occupiedPages = new HashSet<uint> { SchemaRootPage };
            long? previousRowId = null;
            _ = ReadSchemaTreeNodeEntries(
                SchemaRootPage,
                _pager.ReadCommittedPage(SchemaRootPage),
                isRoot: true,
                occupiedPages,
                ref previousRowId,
                entries);
            return entries;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or ArgumentOutOfRangeException
                or OverflowException
                or NotSupportedException)
        {
            throw new EmbeddedSqlException(
                "Managed file database has an invalid sqlite_schema b-tree.",
                exception);
        }
    }

    private SchemaTreeReadResult ReadSchemaTreeNodeEntries(
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        bool isRoot,
        ISet<uint> occupiedPages,
        ref long? previousRowId,
        ICollection<ManagedSchemaRow> entries)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage, isFirstPage: isRoot);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    var leaf = SqliteTableLeafPageView.Parse(
                        pageImage,
                        _usableSpace,
                        isFirstPage: isRoot);
                    if (leaf.Cells.Count == 0)
                    {
                        if (isRoot)
                            return new SchemaTreeReadResult(null, null, 0);
                        throw new EmbeddedSqlException("Managed file database sqlite_schema has an empty leaf child page.");
                    }

                    foreach (var cell in leaf.Cells)
                    {
                        if (previousRowId is { } previous && cell.Cell.RowId <= previous)
                        {
                            throw new EmbeddedSqlException(
                                "Managed file database sqlite_schema rows are not globally ordered by rowid.");
                        }

                        var values = DecodeCellRecord(cell.Cell);
                        if (values.Length != SchemaColumnCount)
                            throw new EmbeddedSqlException("Managed file database has a malformed sqlite_schema row.");

                        entries.Add(new ManagedSchemaRow(
                            cell.Cell.RowId,
                            RequireText(values[0], "type"),
                            RequireText(values[1], "name"),
                            RequireText(values[2], "tbl_name"),
                            checked((uint)RequireInteger(values[3], "rootpage")),
                            RequireNullableText(values[4], "sql")));
                        previousRowId = cell.Cell.RowId;
                    }

                    return new SchemaTreeReadResult(
                        leaf.Cells[0].Cell.RowId,
                        leaf.Cells[^1].Cell.RowId,
                        0);
                }
            case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(
                        pageImage,
                        _usableSpace,
                        isFirstPage: isRoot);
                    if (interior.Cells.Count == 0 && !isRoot)
                        throw new EmbeddedSqlException("Managed file database sqlite_schema has an empty interior page.");

                    int? childHeight = null;
                    long? minimumRowId = null;
                    long? maximumRowId = null;
                    for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
                    {
                        var childPage = childIndex == interior.Cells.Count
                            ? interior.Header.RightMostChildPage
                            : interior.Cells[childIndex].Cell.LeftChildPage;
                        if (childPage < 2 || childPage > _pager.CommittedPageCount)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema references invalid child page {childPage}.");
                        }
                        if (!occupiedPages.Add(childPage))
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema reuses child page {childPage}.");
                        }

                        var child = ReadSchemaTreeNodeEntries(
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            isRoot: false,
                            occupiedPages,
                            ref previousRowId,
                            entries);
                        if (child.MinimumRowId is not { } childMinimumRowId
                            || child.MaximumRowId is not { } childMaximumRowId)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema has an empty child page {childPage}.");
                        }
                        if (childHeight is { } expectedHeight && child.Height != expectedHeight)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema interior page {pageNumber} has children with inconsistent heights.");
                        }
                        if (childIndex > 0
                            && childMinimumRowId <= interior.Cells[childIndex - 1].Cell.RowId)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema interior page {pageNumber} separator {childIndex - 1} is not below minimum rowid on child page {childPage}.");
                        }
                        // SQLite preserves table-interior separators as upper bounds
                        // when deleting the current maximum rowid from a left child.
                        if (childIndex < interior.Cells.Count
                            && childMaximumRowId > interior.Cells[childIndex].Cell.RowId)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema interior page {pageNumber} separator {childIndex} is below maximum rowid on child page {childPage}.");
                        }

                        childHeight = child.Height;
                        minimumRowId ??= childMinimumRowId;
                        maximumRowId = childMaximumRowId;
                    }

                    return new SchemaTreeReadResult(
                        minimumRowId ?? throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema has an empty interior page {pageNumber}."),
                        maximumRowId ?? throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema has an empty interior page {pageNumber}."),
                        checked((childHeight ?? throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema has an empty interior page {pageNumber}.")) + 1));
                }
            default:
                throw new EmbeddedSqlException(
                    $"Managed file database sqlite_schema root has unsupported SQLite page type {header.PageType}.");
        }
    }

    private void LoadTableRows(
        string tableName,
        EmbeddedTable table,
        uint rootPage,
        ISet<uint> occupiedBtreePages)
    {
        if (rootPage < 2)
            throw new EmbeddedSqlException($"Managed file database references an invalid rootpage {rootPage}.");

        if (table.WithoutRowid)
        {
            LoadWithoutRowidTableRows(tableName, table, rootPage, occupiedBtreePages);
            return;
        }

        var page = _pager.ReadCommittedPage(rootPage);
        var header = SqliteBtreePageHeader.Parse(page);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    var view = SqliteTableLeafPageView.Parse(page, _usableSpace, isFirstPage: false);
                    LoadTableLeafRows(table, view, previousRowId: null);
                    return;
                }
            case SqliteBtreePageType.TableInterior:
                LoadTableInteriorRows(table, rootPage, page, occupiedBtreePages);
                return;
            default:
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} has unsupported SQLite page type {header.PageType}.");
        }
    }

    private void LoadTableInteriorRows(
        EmbeddedTable table,
        uint rootPage,
        ReadOnlySpan<byte> rootPageImage,
        ISet<uint> occupiedBtreePages)
    {
        long? previousRowId = null;
        _ = LoadTableTreeNodeRows(
            table,
            rootPage,
            rootPage,
            rootPageImage,
            occupiedBtreePages,
            ref previousRowId,
            isRoot: true);
    }

    private TableTreeReadResult LoadTableTreeNodeRows(
        EmbeddedTable table,
        uint rootPage,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISet<uint> occupiedBtreePages,
        ref long? previousRowId,
        bool isRoot)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    SqliteTableLeafPageView leaf;
                    try
                    {
                        leaf = SqliteTableLeafPageView.Parse(pageImage, _usableSpace, isFirstPage: false);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table rootpage {rootPage} has non-leaf child page {pageNumber}.",
                            exception);
                    }

                    if (!isRoot && leaf.Cells.Count == 0)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table rootpage {rootPage} has an empty leaf child page {pageNumber}.");
                    }

                    var leafMaximumRowId = LoadTableLeafRows(table, leaf, previousRowId);
                    if (leafMaximumRowId is null)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table rootpage {rootPage} has an empty leaf child page {pageNumber}.");
                    }

                    previousRowId = leafMaximumRowId;
                    return new TableTreeReadResult(
                        leaf.Cells[0].Cell.RowId,
                        leafMaximumRowId.Value,
                        0);
                }
            case SqliteBtreePageType.TableInterior:
                break;
            default:
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} has unsupported page type {header.PageType} at page {pageNumber}.");
        }

        var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace);
        SqliteBtreePageType? directChildType = null;
        foreach (var childPage in interior.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(interior.Header.RightMostChildPage))
        {
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references invalid child page {childPage}.");
            }

            var currentChildType = SqliteBtreePageHeader.Parse(_pager.ReadCommittedPage(childPage)).PageType;
            if (currentChildType is not (SqliteBtreePageType.TableLeaf or SqliteBtreePageType.TableInterior))
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references unsupported child type {currentChildType}.");
            }
            if (directChildType is { } expectedChildType && currentChildType != expectedChildType)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
            }

            directChildType = currentChildType;
        }

        int? childHeight = null;
        long? minimumRowId = null;
        long? maximumRowId = null;
        SqliteBtreePageType? childType = null;
        for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
        {
            var childPage = childIndex == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[childIndex].Cell.LeftChildPage;
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references invalid child page {childPage}.");
            }
            if (!occupiedBtreePages.Add(childPage))
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} reuses b-tree page {childPage} as a child.");
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            var currentChildType = SqliteBtreePageHeader.Parse(childPageImage).PageType;
            if (currentChildType is not (SqliteBtreePageType.TableLeaf or SqliteBtreePageType.TableInterior))
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references unsupported child type {currentChildType}.");
            }
            if (childType is { } expectedChildType && currentChildType != expectedChildType)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
            }

            childType = currentChildType;
            var childResult = LoadTableTreeNodeRows(
                table,
                rootPage,
                childPage,
                childPageImage,
                occupiedBtreePages,
                ref previousRowId,
                isRoot: false);
            if (childHeight is { } expectedHeight && childResult.Height != expectedHeight)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
            }

            childHeight = childResult.Height;
            if (childIndex > 0
                && childResult.MinimumRowId <= interior.Cells[childIndex - 1].Cell.RowId)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} separator {childIndex - 1} is not below minimum rowid on child page {childPage}.");
            }
            // SQLite table-interior separators are search upper bounds for the left
            // child, not a maintained copy of that child's live maximum rowid.
            // After deletes, the separator can stay strictly greater than every
            // remaining left-child rowid (stale separator). Equality is therefore
            // too strong; only reject keys that would violate the bound.
            if (childIndex < interior.Cells.Count
                && childResult.MaximumRowId > interior.Cells[childIndex].Cell.RowId)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} separator {childIndex} is below maximum rowid on child page {childPage}.");
            }

            minimumRowId ??= childResult.MinimumRowId;
            maximumRowId = childResult.MaximumRowId;
        }

        return new TableTreeReadResult(
            minimumRowId ?? throw new EmbeddedSqlException(
                $"Managed file database table rootpage {rootPage} has an empty interior page {pageNumber}."),
            maximumRowId ?? throw new EmbeddedSqlException(
                $"Managed file database table rootpage {rootPage} has an empty interior page {pageNumber}."),
            checked((childHeight ?? throw new EmbeddedSqlException(
                $"Managed file database table rootpage {rootPage} has an empty interior page {pageNumber}.")) + 1));
    }

    private long? LoadTableLeafRows(
        EmbeddedTable table,
        SqliteTableLeafPageView view,
        long? previousRowId)
    {
        var aliasIndex = table.RowidAliasColumnIndex;
        foreach (var cell in view.Cells)
        {
            if (previousRowId is { } previous && cell.Cell.RowId <= previous)
            {
                throw new EmbeddedSqlException(
                    "Managed file database table leaves are not globally ordered by rowid.");
            }

            var values = RestoreRowidTableRecord(table, DecodeCellRecord(cell.Cell));

            if (aliasIndex >= 0)
                values[aliasIndex] = SqlValue.Integer(cell.Cell.RowId);
            EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, table.Name, values);

            // Preserve the on-disk rowid so both alias and hidden-rowid tables keep their
            // identity across reopen, exactly as SQLite does.
            table.Rows.Add(values);
            table.RowIds.Add(cell.Cell.RowId);
            previousRowId = cell.Cell.RowId;
        }

        return previousRowId;
    }

    private void LoadWithoutRowidTableRows(
        string tableName,
        EmbeddedTable table,
        uint rootPage,
        ISet<uint> occupiedBtreePages)
    {
        var primaryKeySchema = ValidateWithoutRowidTableRepresentable(tableName, table);
        try
        {
            var rootPageImage = _pager.ReadCommittedPage(rootPage);
            var overflowReader = new SqliteOverflowChainReader(_pager, _header);
            var rootHeader = SqliteBtreePageHeader.Parse(rootPageImage);
            var comparer = CreatePrimaryKeyComparer(primaryKeySchema);
            var records = rootHeader.PageType switch
            {
                SqliteBtreePageType.IndexLeaf => ReadIndexLeafRecords(rootPageImage, overflowReader, comparer),
                SqliteBtreePageType.IndexInterior => ReadIndexInteriorRecords(
                    new ManagedSchemaRow("table", tableName, tableName, rootPage, string.Empty),
                    rootPageImage,
                    overflowReader,
                    occupiedBtreePages,
                    comparer),
                _ => throw new InvalidDataException(
                    $"Stored WITHOUT ROWID table '{tableName}' root page has unsupported type {rootHeader.PageType}."),
            };
            SqlValue[]? previousKey = null;
            var syntheticRowId = 0L;

            foreach (var record in records)
            {
                var storedValues = SqliteRecordCodec.Decode(record, _textEncoding);
                var row = RestoreWithoutRowidRecord(tableName, table, primaryKeySchema, storedValues);
                EmbeddedDatabase.RecomputeVirtualGeneratedColumns(table, tableName, row);
                var key = primaryKeySchema.ProjectKey(row);
                if (key.Any(value => value.Kind == SqlValueKind.Null))
                {
                    throw new InvalidDataException(
                        $"Stored WITHOUT ROWID table '{tableName}' contains a NULL primary-key value.");
                }
                if (previousKey is not null && comparer.Compare(previousKey, key) >= 0)
                {
                    throw new InvalidDataException(
                        $"Stored WITHOUT ROWID table '{tableName}' primary keys are not strictly increasing in declared key order.");
                }

                table.Rows.Add(row);
                table.RowIds.Add(checked(++syntheticRowId));
                previousKey = key;
            }

            table.ValidateRows(tableName, table.Rows);
        }
        catch (EmbeddedSqlException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new EmbeddedSqlException(
                $"Stored WITHOUT ROWID table '{tableName}' is not a valid supported SQLite index b-tree "
                + $"for primary key ({string.Join(", ", primaryKeySchema.Terms.Select(term =>
                    $"{term.ColumnName} {term.Collation.Name} {term.SortOrder}"))}).",
                exception);
        }
    }

    private SqlValue[] DecodeCellRecord(SqliteTableLeafCell cell)
    {
        var payload = cell.FirstOverflowPage is null
            ? cell.LocalPayload.ToArray()
            : new SqliteOverflowChainReader(_pager, _header).ReadPayload(cell);
        return SqliteRecordCodec.Decode(payload, _textEncoding);
    }

    internal static SqlValue[] RestoreRowidTableRecord(
        EmbeddedTable table,
        IReadOnlyList<SqlValue> storedValues)
    {
        var storedColumnCount = table.ColumnDefinitions.Count(
            column => !column.IsGenerated || column.GeneratedStored);
        if (storedValues.Count > storedColumnCount)
        {
            throw new EmbeddedSqlException(
                $"Managed file database row for table has {storedValues.Count} stored column(s), "
                + $"but the schema requires {storedColumnCount}.");
        }

        var row = new SqlValue[table.ColumnDefinitions.Length];
        var source = 0;
        for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
        {
            var column = table.ColumnDefinitions[columnIndex];
            if (column.IsGenerated && !column.GeneratedStored)
            {
                row[columnIndex] = SqlValue.Null;
                continue;
            }

            // ALTER TABLE ADD COLUMN does not rewrite existing records, so a stored
            // row can be shorter than the schema. SQLite reads each missing trailing
            // column as its declared default, which ADD COLUMN constrains to a constant.
            row[columnIndex] = source < storedValues.Count
                ? storedValues[source]
                : column.DefaultValue ?? SqlValue.Null;
            source++;
        }

        return row;
    }

    /// <summary>
    /// Validates and durably persists the full managed catalog as SQLite pages in
    /// a single atomic WAL transaction. Any unsupported schema or data is rejected
    /// before a byte is written so the on-disk database is never left invalid.
    /// </summary>
    public FileCatalogVersion Persist(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        PragmaHeaderMetadata? pragmaHeader = null,
        bool forceFullRewrite = false,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null,
        IReadOnlyList<(string TableName, EmbeddedTable Table, EmbeddedIndex Index)>? targetedIndexRebuild = null)
        => PersistCore(
            tables,
            views,
            triggers,
            virtualTables,
            reclaimTrailingPages: false,
            incrementSchemaCookie: false,
            pragmaHeader,
            forceFullRewrite,
            previousTables,
            targetedIndexRebuild: targetedIndexRebuild);

    /// <summary>
    /// Materializes an MVCC checkpoint into pager WAL pages without reclaiming
    /// those frames. The checkpoint state machine must first backfill the main
    /// store and retire the logical log before it resets the WAL.
    /// </summary>
    internal FileCatalogVersion PersistForMvccCheckpoint(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        PragmaHeaderMetadata? pragmaHeader = null,
        bool forceFullRewrite = false,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null)
        => PersistCore(
            tables,
            views,
            triggers,
            virtualTables,
            reclaimTrailingPages: false,
            incrementSchemaCookie: false,
            pragmaHeader,
            forceFullRewrite,
            previousTables,
            checkpointAfterCommit: false);

    /// <summary>
    /// Builds a private, not-yet-published VACUUM destination image in one
    /// transaction: the source header is folded into the same commit and no
    /// intermediate checkpoint runs.
    /// </summary>
    /// <remarks>
    /// This mirrors SQLite's <c>vacuum.c</c> (and Turso's
    /// <c>core/vdbe/vacuum.rs</c>) target-build policy: the target is a private
    /// temporary file, so intermediate durability buys nothing — a crash simply
    /// discards it. Durability is established once, by the caller's final
    /// journal-mode fold, before the image is atomically published.
    /// </remarks>
    private FileCatalogVersion PersistForVacuumTarget(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        SqliteDatabaseHeader vacuumSourceHeader)
        => PersistCore(
            tables,
            views,
            triggers,
            virtualTables,
            reclaimTrailingPages: false,
            incrementSchemaCookie: false,
            pragmaHeader: null,
            forceFullRewrite: false,
            previousTables: null,
            checkpointAfterCommit: false,
            vacuumSourceHeader: vacuumSourceHeader);

    internal FileCatalogVersion CommittedCatalogVersion => FileCatalogVersion.FromHeader(_header);

    /// <summary>
    /// Rebuilds the current managed catalog into the smallest complete page image
    /// the managed writer can represent, then checkpoints and physically removes
    /// its retired suffix.
    /// </summary>
    internal void Compact()
    {
        ThrowIfDisposed();
        var catalog = Load();
        _ = PersistCore(
            catalog.Tables,
            catalog.Views,
            catalog.Triggers,
            catalog.VirtualTables,
            reclaimTrailingPages: true,
            incrementSchemaCookie: true,
            pragmaHeader: null,
            forceFullRewrite: true);
    }

    internal FileCatalogVersion UpdatePragmaHeader(PragmaHeaderMetadata metadata)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();

        var pageOne = _pager.ReadCommittedPage(SchemaRootPage);
        var current = SqliteDatabaseHeader.Parse(pageOne);
        if (unchecked((int)current.SchemaCookie) == metadata.SchemaVersion
            && current.UserVersion == metadata.UserVersion
            && current.ApplicationId == metadata.ApplicationId)
        {
            return CommittedCatalogVersion;
        }

        var changeCounter = unchecked(current.ChangeCounter + 1);
        var updated = current with
        {
            ChangeCounter = changeCounter,
            VersionValidFor = changeCounter,
            SchemaCookie = unchecked((uint)metadata.SchemaVersion),
            UserVersion = metadata.UserVersion,
            ApplicationId = metadata.ApplicationId,
        };
        updated.WriteTo(pageOne);
        using (var transaction = _pager.BeginTransaction(_pager.CommittedPageCount))
        {
            transaction.WritePage(SchemaRootPage, pageOne);
            transaction.Commit(_synchronousMode);
        }

        _header = updated;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return CommittedCatalogVersion;
    }

    internal SqliteJournalMode JournalMode => _pager.JournalMode;

    internal bool IsExclusiveLockingMode => _pager.IsExclusiveLockingMode;

    internal bool SetExclusiveLockingMode(bool exclusive, TimeSpan busyTimeout)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        return _pager.SetExclusiveLockingMode(exclusive, busyTimeout);
    }

    /// <summary>Backfills the WAL pages materialized by an MVCC checkpoint.</summary>
    internal SqliteCheckpointResult BackfillMvccCheckpoint(TimeSpan busyTimeout)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        return _pager.CheckpointToMainStore(busyTimeout, _synchronousMode);
    }

    /// <summary>
    /// Reclaims a previously backfilled MVCC checkpoint WAL only after the
    /// logical log has been durably retired.
    /// </summary>
    internal SqliteCheckpointResult ResetMvccCheckpointWal(TimeSpan busyTimeout)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        return _pager.CheckpointToMainStoreAndResetWal(busyTimeout, _synchronousMode);
    }

    internal SqliteJournalMode SwitchJournalMode(SqliteJournalMode journalMode)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        var result = _pager.SwitchJournalMode(
            journalMode,
            synchronousMode: _synchronousMode);
        _header = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        return result;
    }

    internal void MigratePageSize(
        int pageSize,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        _ = SqlitePageSize.Encode(pageSize);
        if (pageSize == _pageSize)
            return;
        if (_pager.JournalMode != SqliteJournalMode.Delete)
        {
            throw new EmbeddedSqlException(
                "cannot change page size while in WAL mode; set PRAGMA journal_mode=DELETE first");
        }

        var temporaryPath = _databasePath + $".page-size-{Guid.NewGuid():N}.tmp";
        var temporaryWalPath = temporaryPath + "-wal";
        var temporaryJournalPath = temporaryPath + "-journal";
        try
        {
            using (var replacement = Open(
                       temporaryPath,
                       _fileSystem,
                       out _,
                       initialPageSize: pageSize,
                       initialTextEncoding: _textEncoding))
            {
                replacement.Persist(tables, views, triggers, virtualTables);
                replacement.SwitchJournalMode(SqliteJournalMode.Delete);
                replacement.RewriteVacuumHeader(_header);
            }

            _pager.ReplaceDatabaseFile(temporaryPath);
            _pager.Dispose();
            _pager = SqlitePager.Open(_fileSystem, _databasePath, _walPath);
            _header = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
            _pageSize = _header.PageSize;
            _usableSpace = _header.UsableSpace;
            _textEncoding = _header.TextEncoding == SqliteTextEncoding.Unset
                ? SqliteTextEncoding.Utf8
                : _header.TextEncoding;
            _ = Load();
        }
        finally
        {
            TryDeleteArtifact(temporaryJournalPath);
            TryDeleteArtifact(temporaryWalPath);
            TryDeleteArtifact(temporaryPath);
        }
    }

    internal void VacuumInto(
        string destinationPath,
        int pageSize,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        _ = SqlitePageSize.Encode(pageSize);

        var atomicFileSystem = AhtolaEncryptionFileSystem.Unwrap(_fileSystem) as IAtomicFileSystem
            ?? throw new EmbeddedSqlException(
                "VACUUM INTO requires a file system with atomic replacement support.");
        var replaceEmptyDestination = _fileSystem.FileExists(destinationPath);
        if (replaceEmptyDestination)
        {
            using var destination = _fileSystem.OpenFile(
                destinationPath,
                FileOpenMode.OpenExisting,
                readOnly: true);
            if (destination.Length != 0)
                throw new EmbeddedSqlException("output file already exists");
        }
        foreach (var suffix in new[] { "-wal", "-journal" })
        {
            if (_fileSystem.FileExists(destinationPath + suffix))
                throw new EmbeddedSqlException("output file already exists");
        }
        var destinationShmPath = destinationPath + "-shm";
        var replaceEmptyDestinationShm = _fileSystem.FileExists(destinationShmPath);
        if (replaceEmptyDestinationShm)
        {
            using var destinationShm = _fileSystem.OpenFile(
                destinationShmPath,
                FileOpenMode.OpenExisting,
                readOnly: true);
            if (destinationShm.Length != 0)
                throw new EmbeddedSqlException("output file already exists");
        }

        var temporaryPath = destinationPath + $".vacuum-{Guid.NewGuid():N}.tmp";
        var temporaryWalPath = temporaryPath + "-wal";
        var temporaryJournalPath = temporaryPath + "-journal";
        var temporaryShmPath = temporaryPath + "-shm";
        try
        {
            using (var replacement = Open(
                       temporaryPath,
                       _fileSystem,
                       out _,
                       initialPageSize: pageSize,
                       initialTextEncoding: _textEncoding,
                       createRollbackJournalMode: true))
            {
                // The private target is created directly in DELETE mode and its
                // final image is written straight into the main file: nothing can
                // observe it until it is atomically published, so an intermediate
                // WAL copy plus checkpoint buys no recoverability.
                replacement.PersistForVacuumTarget(tables, views, triggers, virtualTables, _header);
                replacement.ValidateVacuumTarget();
            }

            try
            {
                atomicFileSystem.ReplaceFileAtomically(
                    temporaryPath,
                    destinationPath,
                    replaceEmptyDestination);
            }
            catch (IOException exception) when (exception.Message == "output file already exists")
            {
                throw new EmbeddedSqlException("output file already exists", exception);
            }

            if (_fileSystem.FileExists(temporaryShmPath))
            {
                atomicFileSystem.ReplaceFileAtomically(
                    temporaryShmPath,
                    destinationShmPath,
                    replaceEmptyDestinationShm);
            }
        }
        finally
        {
            TryDeleteArtifact(temporaryJournalPath);
            TryDeleteArtifact(temporaryWalPath);
            TryDeleteArtifact(temporaryShmPath);
            TryDeleteArtifact(temporaryPath);
        }
    }

    /// <summary>
    /// Proves a freshly folded VACUUM destination is self-contained before it is
    /// published: the WAL is gone, the header is authoritative, and the complete
    /// catalog re-reads and re-validates from the main image alone.
    /// </summary>
    private void ValidateVacuumTarget()
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();
        if (_pager.JournalMode != SqliteJournalMode.Delete)
        {
            throw new EmbeddedSqlException(
                "VACUUM INTO could not fold its destination into a self-contained database image.");
        }

        var header = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        if (header.DatabaseSizeInPages != _pager.CommittedPageCount
            || header.VersionValidFor != header.ChangeCounter)
        {
            throw new EmbeddedSqlException(
                "VACUUM INTO produced a destination whose header does not describe its own image.");
        }

        _header = header;
    }

    private void RewriteVacuumHeader(SqliteDatabaseHeader sourceHeader)
    {
        var pageOne = _pager.ReadCommittedPage(SchemaRootPage);
        var current = SqliteDatabaseHeader.Parse(pageOne);
        var changeCounter = unchecked(sourceHeader.ChangeCounter + 1);
        var migrated = current with
        {
            ChangeCounter = changeCounter,
            VersionValidFor = changeCounter,
            DatabaseSizeInPages = _pager.CommittedPageCount,
            SchemaCookie = unchecked(sourceHeader.SchemaCookie + 1),
            DefaultPageCacheSize = sourceHeader.DefaultPageCacheSize,
            TextEncoding = sourceHeader.TextEncoding,
            UserVersion = sourceHeader.UserVersion,
            ApplicationId = sourceHeader.ApplicationId,
            SqliteVersion = sourceHeader.SqliteVersion,
        };
        migrated.WriteTo(pageOne);
        using var transaction = _pager.BeginTransaction(_pager.CommittedPageCount);
        transaction.WritePage(SchemaRootPage, pageOne);
        transaction.Commit(_synchronousMode);
        _header = migrated;
    }

    private void TryDeleteArtifact(string path)
    {
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch
        {
            // Migration is already committed; stale temporary artifacts are non-authoritative.
        }
    }

    private FileCatalogVersion PersistCore(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        bool reclaimTrailingPages,
        bool incrementSchemaCookie,
        PragmaHeaderMetadata? pragmaHeader,
        bool forceFullRewrite,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null,
        bool checkpointAfterCommit = true,
        SqliteDatabaseHeader? vacuumSourceHeader = null,
        IReadOnlyList<(string TableName, EmbeddedTable Table, EmbeddedIndex Index)>? targetedIndexRebuild = null)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();

        // Validate first: a reject must leave the existing database untouched.
        foreach (var (name, table) in tables)
            ValidateTableRepresentable(name, table, previousTables);
        EmbeddedDatabase.ValidateSqliteSequenceCatalog(tables);
        ValidateSchemaDefinitions(tables, views, triggers, virtualTables);

        if (!forceFullRewrite
            && !reclaimTrailingPages
            && pragmaHeader is null)
        {
            if (previousTables is not null
                && ReferenceEquals(previousTables, _committedTables)
                && TryPersistIncrementalRowMutation(
                    tables,
                    views,
                    triggers,
                    virtualTables,
                    previousTables,
                    checkpointAfterCommit))
            {
                _committedTables = tables;
                return CommittedCatalogVersion;
            }

            if (checkpointAfterCommit
                && TryPersistBoundedTableLeafMutation(
                    tables,
                    views,
                    triggers,
                    virtualTables,
                    checkpointAfterCommit))
            {
                _committedTables = tables;
                return CommittedCatalogVersion;
            }
        }

        // A targeted REINDEX only ever needs to rebuild the selected ordinary index
        // trees in place: everything else — including a table whose index uses a
        // custom collation with no callback registered right now — must remain
        // byte-for-byte untouched. This has to be attempted here, at the same point
        // every other mutation is actually committed to the pager/WAL, rather than
        // eagerly inside statement execution: the statement only records which
        // targets to rebuild, and ForceFullCatalogRewrite is still requested as the
        // guaranteed fallback in case the rebuild is not eligible (schema changed
        // concurrently, a pragma header write is also pending) or fails structurally
        // (for example the old tree's on-disk image cannot be traversed because it
        // is itself corrupted, which the full rewrite already knows how to repair).
        if (forceFullRewrite
            && !reclaimTrailingPages
            && pragmaHeader is null
            && targetedIndexRebuild is { Count: > 0 }
            && TryPersistTargetedIndexRebuild(
                tables,
                views,
                triggers,
                virtualTables,
                targetedIndexRebuild,
                checkpointAfterCommit,
                previousTables))
        {
            _committedTables = tables;
            return CommittedCatalogVersion;
        }

        var currentHeader = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        var currentFreelist = SqliteFreelist.Read(
            currentHeader,
            _pager.CommittedPageCount,
            _pager.ReadCommittedPage);
        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        // previousTables lets GetIndexDefinitions defer validation for an index
        // whose own row/tree has not changed since the previous commit even while
        // this rewrite's connection has some OTHER custom collation resolver
        // attached (see IsIndexUnchangedFromPrevious / structuralOnly below);
        // omitting it here used to silently force full validation of every
        // index on every full rewrite, defeating that deferral exactly when an
        // unrelated CREATE TABLE / CREATE INDEX needed it most.
        var indexes = GetIndexDefinitions(tableNames, tables, views, triggers, virtualTables, previousTables);

        // An index whose custom collation nothing has registered right now
        // must never block an unrelated schema change as long as its own
        // table has not changed since the previous commit: its existing
        // committed b-tree is cloned byte-for-byte below instead of being
        // rebuilt through BuildIndexTree, which unconditionally requires a
        // working comparator. Gated to a plain full rewrite (never VACUUM /
        // page-size migration): those compute their target page count from
        // the allocator's own high-water mark, which would not account for a
        // page this preserves without ever handing out.
        var preservedIndexTrees = reclaimTrailingPages
            ? new Dictionary<string, PreservedIndexTree>(StringComparer.OrdinalIgnoreCase)
            : TryPreserveUnavailableIndexTrees(indexes, previousTables, currentHeader);
        var reservedPages = preservedIndexTrees.Count == 0
            ? null
            : CollectPreservedIndexTreePages(preservedIndexTrees);

        // Only a fully rebuilt page map can safely repurpose existing freelist
        // pages: all new data, trunks, leaves, and page 1 are one WAL commit.
        var allocator = new RebuildPageAllocator(
            _pager.CommittedPageCount,
            currentFreelist.LeafPageNumbers,
            reclaimTrailingPages,
            reservedPages);
        var rootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var indexRootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tableNames)
            rootPages[name] = allocator.ReservePage();
        foreach (var definition in indexes)
        {
            indexRootPages[definition.Index.Name] = preservedIndexTrees.TryGetValue(
                definition.Index.Name,
                out var preservedRoot)
                ? preservedRoot.RootPage
                : allocator.ReservePage();
        }

        // Build every page image up front so a build failure also rejects cleanly.
        var tablePages = new Dictionary<uint, PreparedTableTree>();
        var indexPages = new Dictionary<uint, PreparedIndexTree>();
        foreach (var name in tableNames)
        {
            var table = tables[name];
            tablePages[rootPages[name]] = table.WithoutRowid
                ? BuildWithoutRowidTableTree(name, table, allocator)
                : BuildTableTree(name, table, allocator);
        }
        foreach (var definition in indexes)
        {
            indexPages[indexRootPages[definition.Index.Name]] = preservedIndexTrees.TryGetValue(
                definition.Index.Name,
                out var preservedTree)
                ? preservedTree.Tree
                : BuildIndexTree(
                    definition.TableName,
                    definition.Table,
                    definition.Index,
                    allocator);
        }

        var schemaEntries = BuildSchemaEntries(
            tables,
            views,
            triggers,
            virtualTables,
            rootPages,
            indexRootPages);
        var schemaTree = BuildSchemaTree(schemaEntries, allocator);
        var activePages = CollectRewriteActivePages(
            schemaTree,
            tableNames,
            rootPages,
            tablePages,
            indexes,
            indexRootPages,
            indexPages);
        var target = reclaimTrailingPages
            ? allocator.HighestAllocatedPage
            : Math.Max(_pager.CommittedPageCount, allocator.HighestAllocatedPage);
        var freelist = SqliteFreelist.CreateFromFreePages(
            target,
            EnumerateFreePages(target, activePages),
            _pageSize,
            _usableSpace);

        var signature = ComputeSchemaSignature(schemaEntries);
        var schemaChanged = !string.Equals(signature, _lastSchemaSignature, StringComparison.Ordinal);

        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            DatabaseSizeInPages = target,
            FirstFreelistTrunkPage = freelist.FirstTrunkPage,
            FreelistPageCount = freelist.PageCount,
            SchemaCookie = pragmaHeader is { } metadata
                ? unchecked((uint)metadata.SchemaVersion)
                : schemaChanged || incrementSchemaCookie
                    ? unchecked(currentHeader.SchemaCookie + 1)
                    : currentHeader.SchemaCookie,
            UserVersion = pragmaHeader?.UserVersion ?? currentHeader.UserVersion,
            ApplicationId = pragmaHeader?.ApplicationId ?? currentHeader.ApplicationId,
        };
        if (vacuumSourceHeader is { } vacuumSource)
        {
            // Fold VACUUM's destination header into the single rebuild commit
            // instead of paying a second transaction to rewrite page one.
            var vacuumChangeCounter = unchecked(vacuumSource.ChangeCounter + 1);
            newHeader = newHeader with
            {
                ChangeCounter = vacuumChangeCounter,
                VersionValidFor = vacuumChangeCounter,
                SchemaCookie = unchecked(vacuumSource.SchemaCookie + 1),
                DefaultPageCacheSize = vacuumSource.DefaultPageCacheSize,
                TextEncoding = vacuumSource.TextEncoding,
                UserVersion = vacuumSource.UserVersion,
                ApplicationId = vacuumSource.ApplicationId,
                SqliteVersion = vacuumSource.SqliteVersion,
            };
        }

        newHeader.WriteTo(schemaTree.RootPage);
        ValidateRewritePlan(
            target,
            schemaTree,
            tableNames,
            rootPages,
            tablePages,
            indexes,
            indexRootPages,
            indexPages,
            freelist);

        if (vacuumSourceHeader is not null)
        {
            // Private, unpublished destination: write the final image straight
            // into the main file instead of through WAL + checkpoint.
            var image = new Dictionary<uint, ReadOnlyMemory<byte>>(checked((int)target));
            CollectRewritePlanPages(
                image,
                tableNames,
                rootPages,
                tablePages,
                indexes,
                indexRootPages,
                indexPages,
                schemaTree,
                freelist);
            for (var pageNumber = 1U; pageNumber <= target; pageNumber++)
            {
                if (!image.ContainsKey(pageNumber))
                {
                    throw new EmbeddedSqlException(
                        $"VACUUM INTO could not build a complete destination image: page {pageNumber} is missing.");
                }
            }

            _pager.WriteUnpublishedVacuumImage(target, pageNumber => image[pageNumber]);
            _header = newHeader;
            _tableRootPages = rootPages;
            _indexRootPages = indexRootPages;
            _lastSchemaSignature = signature;
            _committedTables = tables;
            return CommittedCatalogVersion;
        }

        using (var transaction = reclaimTrailingPages
                   ? _pager.BeginExclusiveRewriteTransaction(target)
                   : _pager.BeginTransaction(target))
        {
            foreach (var name in tableNames)
            {
                var tablePage = tablePages[rootPages[name]];
                transaction.WritePage(rootPages[name], tablePage.RootPage);
                foreach (var interiorPage in tablePage.InteriorPages)
                    transaction.WritePage(interiorPage.PageNumber, interiorPage.Page);
                foreach (var leafPage in tablePage.LeafPages)
                    transaction.WritePage(leafPage.PageNumber, leafPage.Page);
                foreach (var overflowPage in tablePage.OverflowPages)
                    transaction.WritePage(overflowPage.PageNumber, overflowPage.Page);
            }
            foreach (var definition in indexes)
            {
                var indexPage = indexPages[indexRootPages[definition.Index.Name]];
                transaction.WritePage(indexRootPages[definition.Index.Name], indexPage.RootPage);
                foreach (var interiorPage in indexPage.InteriorPages)
                    transaction.WritePage(interiorPage.PageNumber, interiorPage.Page);
                foreach (var leafPage in indexPage.LeafPages)
                    transaction.WritePage(leafPage.PageNumber, leafPage.Page);
                foreach (var overflowPage in indexPage.OverflowPages)
                    transaction.WritePage(overflowPage.PageNumber, overflowPage.Page);
            }

            foreach (var interiorPage in schemaTree.InteriorPages)
                transaction.WritePage(interiorPage.PageNumber, interiorPage.Page);
            foreach (var leafPage in schemaTree.LeafPages)
                transaction.WritePage(leafPage.PageNumber, leafPage.Page);
            foreach (var overflowPage in schemaTree.OverflowPages)
                transaction.WritePage(overflowPage.PageNumber, overflowPage.Page);
            foreach (var freelistPage in freelist.PageImages)
                transaction.WritePage(freelistPage.PageNumber, freelistPage.Page.Span);

            // Page one carries the authoritative size and catalog routing, so it
            // must be the final frame that makes every replacement page visible.
            transaction.WritePage(SchemaRootPage, schemaTree.RootPage);
            transaction.Commit(_synchronousMode);
        }

        // A full catalog rewrite rewrites every managed page. Once its exclusive
        // checkpoint has durably installed that view, retain neither its WAL frames
        // nor overlay so later rewrites do not rescan an unbounded history.
        _header = newHeader;
        if (checkpointAfterCommit)
            CheckpointCommittedMutation(reclaimTrailingPages);
        _tableRootPages = rootPages;
        _indexRootPages = indexRootPages;
        _lastSchemaSignature = signature;
        _committedTables = tables;
        return CommittedCatalogVersion;
    }

    /// <summary>
    /// Gathers every page of a completed rewrite plan by final page number, in
    /// the same order the transaction path writes them.
    /// </summary>
    private static void CollectRewritePlanPages(
        Dictionary<uint, ReadOnlyMemory<byte>> image,
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, uint> rootPages,
        IReadOnlyDictionary<uint, PreparedTableTree> tablePages,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, uint> indexRootPages,
        IReadOnlyDictionary<uint, PreparedIndexTree> indexPages,
        PreparedSchemaTree schemaTree,
        SqliteFreelist freelist)
    {
        foreach (var name in tableNames)
        {
            var tablePage = tablePages[rootPages[name]];
            image[rootPages[name]] = tablePage.RootPage;
            foreach (var interiorPage in tablePage.InteriorPages)
                image[interiorPage.PageNumber] = interiorPage.Page;
            foreach (var leafPage in tablePage.LeafPages)
                image[leafPage.PageNumber] = leafPage.Page;
            foreach (var overflowPage in tablePage.OverflowPages)
                image[overflowPage.PageNumber] = overflowPage.Page;
        }

        foreach (var definition in indexes)
        {
            var indexPage = indexPages[indexRootPages[definition.Index.Name]];
            image[indexRootPages[definition.Index.Name]] = indexPage.RootPage;
            foreach (var interiorPage in indexPage.InteriorPages)
                image[interiorPage.PageNumber] = interiorPage.Page;
            foreach (var leafPage in indexPage.LeafPages)
                image[leafPage.PageNumber] = leafPage.Page;
            foreach (var overflowPage in indexPage.OverflowPages)
                image[overflowPage.PageNumber] = overflowPage.Page;
        }

        foreach (var interiorPage in schemaTree.InteriorPages)
            image[interiorPage.PageNumber] = interiorPage.Page;
        foreach (var leafPage in schemaTree.LeafPages)
            image[leafPage.PageNumber] = leafPage.Page;
        foreach (var overflowPage in schemaTree.OverflowPages)
            image[overflowPage.PageNumber] = overflowPage.Page;
        foreach (var freelistPage in freelist.PageImages)
            image[freelistPage.PageNumber] = freelistPage.Page;

        // Page one carries the authoritative size and catalog routing.
        image[SchemaRootPage] = schemaTree.RootPage;
    }

    /// <summary>The number of changed rows above which a complete rewrite is preferred.</summary>
    /// <remarks>
    /// A bulk change touches most of the database anyway, and one rewrite packs
    /// its pages far more densely than a long sequence of incremental splits.
    /// </remarks>
    private const int MaximumIncrementalChangedRows = 256;

    /// <summary>
    /// Declares that <paramref name="tables"/> is content-identical to what this
    /// store last made durable, so later writes may compute their page delta
    /// against it.
    /// </summary>
    /// <remarks>
    /// VACUUM and page-size migration republish the durable catalog from freshly
    /// loaded objects, which loses the reference identity the incremental writer
    /// relies on. The caller's dictionary is adopted only after proving it holds
    /// the same rows, so a mismatch disables incremental writes instead of
    /// trusting an unverified baseline.
    /// </remarks>
    internal void AdoptCommittedTables(IReadOnlyDictionary<string, EmbeddedTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var committed = _committedTables;
        if (committed is null || committed.Count != tables.Count)
        {
            _committedTables = null;
            return;
        }

        foreach (var (name, table) in tables)
        {
            if (!committed.TryGetValue(name, out var persisted) || !HaveSameRows(table, persisted))
            {
                _committedTables = null;
                return;
            }
        }

        _committedTables = tables;
    }

    /// <summary>
    /// Applies the row-level difference between the committed catalog and
    /// <paramref name="tables"/> by descending each affected b-tree and
    /// rewriting only the pages on the search paths it touches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the incremental write path. Its cost is proportional to the number
    /// of changed rows and the height of the trees they live in, not to the size
    /// of the database, so it never reads or rebuilds pages the mutation does not
    /// reach. Every page it reads, dirties, or allocates crosses
    /// <see cref="ISqliteBtreePageIo"/>, and only the dirtied pages become WAL
    /// frames.
    /// </para>
    /// <para>
    /// It performs ordinary b-tree split, sibling redistribution, merge,
    /// root-collapse, overflow cleanup, and freelist reuse. It refuses page shapes
    /// outside those bounded operations and anything whose committed contents it
    /// cannot prove it knows. Every refusal falls through to the complete catalog
    /// rewrite, which can always represent the mutation.
    /// </para>
    /// </remarks>
    private bool TryPersistIncrementalRowMutation(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        IReadOnlyDictionary<string, EmbeddedTable> previousTables,
        bool checkpointAfterCommit)
    {
        if (!HasCurrentSchemaShape(tables, views, triggers, virtualTables, previousTables))
            return false;

        var currentHeader = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        if (currentHeader.PageSize != _pageSize
            || currentHeader.UsableSpace != _usableSpace
            || currentHeader.VersionValidFor != currentHeader.ChangeCounter
            || currentHeader.DatabaseSizeInPages != _pager.CommittedPageCount
            || currentHeader.LargestRootBtreePage != 0
            || currentHeader.IncrementalVacuumEnabled != 0)
        {
            return false;
        }

        // Incremental allocation prefers freelist leaves/trunks before appending.
        // A non-empty freelist is therefore safe here and no longer forces a full
        // rewrite solely to avoid stranding free pages.
        if (!TryCollectRowDeltas(tables, previousTables, out var deltas) || deltas.Count == 0)
            return false;

        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexesByTable = GetIndexDefinitions(tableNames, tables, views, triggers, virtualTables, previousTables)
            .GroupBy(definition => definition.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IndexDefinition>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var pageIo = new SqliteStagedBtreePageIo(
            pageNumber => _pager.ReadCommittedPage(pageNumber),
            _pager.CommittedPageCount,
            _pageSize,
            _usableSpace,
            currentHeader.FirstFreelistTrunkPage,
            currentHeader.FreelistPageCount);
        try
        {
            foreach (var delta in deltas)
            {
                if (!_tableRootPages.TryGetValue(delta.TableName, out var rootPage)
                    || rootPage < 2
                    || rootPage > _pager.CommittedPageCount)
                {
                    return false;
                }

                if (!ApplyIncrementalTableDelta(
                        pageIo,
                        delta,
                        rootPage,
                        indexesByTable.TryGetValue(delta.TableName, out var indexes) ? indexes : []))
                {
                    return false;
                }
            }
        }
        catch (SqliteBtreeMaintenanceRequiredException)
        {
            return false;
        }

        if (pageIo.StagedPages.Count == 0 || pageIo.StagedPages.ContainsKey(SchemaRootPage))
            return false;

        var target = pageIo.PageCount;
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            DatabaseSizeInPages = target,
            FirstFreelistTrunkPage = pageIo.FirstFreelistTrunkPage,
            FreelistPageCount = pageIo.FreelistPageCount,
        };
        var pageOne = _pager.ReadCommittedPage(SchemaRootPage);
        newHeader.WriteTo(pageOne);

        using (var transaction = _pager.BeginTransaction(target))
        {
            foreach (var (pageNumber, image) in pageIo.StagedPages)
                transaction.WritePage(pageNumber, image);

            // Page one publishes the new database size, so it must be the frame
            // that makes every page this mutation allocated reachable.
            transaction.WritePage(SchemaRootPage, pageOne);
            transaction.Commit(_synchronousMode);
        }

        _header = newHeader;
        if (checkpointAfterCommit)
            CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    /// <summary>
    /// Rebuilds only the b-tree of each targeted, ordinary index in place —
    /// reusing its existing root page number and freeing its old
    /// interior/leaf/overflow pages — in one pager/WAL transaction, without
    /// touching any other table or index tree and without requiring any
    /// collation other than each targeted index's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the persistence path for a targeted <c>REINDEX
    /// index|table|collation</c>. Every targeted index's replacement tree is
    /// written to the SAME root page number it already occupies — real SQLite
    /// roots change shape between leaf and interior in place as a tree grows
    /// or shrinks, so this mirrors ordinary b-tree behavior rather than
    /// inventing a new one — which means the schema catalog's root-page
    /// pointer, and therefore the schema tree itself, never needs to change.
    /// </para>
    /// <para>
    /// Every OTHER index's pages — including one whose custom collation is
    /// not currently registered — are never read, freed, or rewritten by this
    /// method, so they are never binary-compared and never require their
    /// callback. A caller must not include a method index in
    /// <paramref name="targets"/>: method indexes are rebuilt through their
    /// cursor, not through this file-store b-tree path.
    /// </para>
    /// </remarks>
    internal bool TryPersistTargetedIndexRebuild(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        IReadOnlyList<(string TableName, EmbeddedTable Table, EmbeddedIndex Index)> targets,
        bool checkpointAfterCommit = true,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null)
    {
        if (targets.Count == 0)
            return true;
        if (targets.Any(target => target.Index.IsMethodIndex))
            return false;
        if (!HasCurrentSchemaShape(tables, views, triggers, virtualTables, previousTables))
            return false;

        var distinctTargets = targets
            .GroupBy(target => target.Index.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var rootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, index) in distinctTargets)
        {
            if (!_indexRootPages.TryGetValue(index.Name, out var rootPage)
                || rootPage < 2
                || rootPage > _pager.CommittedPageCount)
            {
                return false;
            }

            rootPages[index.Name] = rootPage;
        }

        var currentHeader = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        if (currentHeader.PageSize != _pageSize
            || currentHeader.UsableSpace != _usableSpace
            || currentHeader.VersionValidFor != currentHeader.ChangeCounter
            || currentHeader.DatabaseSizeInPages != _pager.CommittedPageCount
            || currentHeader.LargestRootBtreePage != 0
            || currentHeader.IncrementalVacuumEnabled != 0)
        {
            return false;
        }

        var pageIo = new SqliteStagedBtreePageIo(
            pageNumber => _pager.ReadCommittedPage(pageNumber),
            _pager.CommittedPageCount,
            _pageSize,
            _usableSpace,
            currentHeader.FirstFreelistTrunkPage,
            currentHeader.FreelistPageCount);
        var overflowReader = new SqliteOverflowChainReader(pageIo);

        try
        {
            // Free every non-root page each targeted index's OLD tree
            // occupies before rebuilding, so the replacement tree below can
            // reuse them. forceDeferCustomCollation keeps this a pure
            // structural traversal: it proves page/record shape only, never
            // ordering, regardless of whether the target's own callback
            // happens to be registered right now. A REINDEX is frequently
            // triggered precisely because a replacement callback no longer
            // agrees with the OLD tree's physical order (that mismatch is
            // exactly what makes the index stale) — trusting the currently
            // bound delegate to validate the old tree's order would either
            // throw on a false "corruption" or, worse, silently misjudge
            // which pages are stale, so this pass must never binary-compare
            // keys under any delegate, old or new.
            var freedPages = new HashSet<uint>();
            foreach (var (_, table, index) in distinctTargets)
            {
                var freeComparer = CreateIndexComparer(
                    table,
                    index,
                    allowDeferredCustomCollation: true,
                    forceDeferCustomCollation: true);
                CollectIndexTreeNonRootPagesForFree(rootPages[index.Name], pageIo, overflowReader, freeComparer, freedPages);
            }

            foreach (var pageNumber in freedPages.Order())
                pageIo.FreePage(pageNumber);

            var allocator = new StagedIndexRebuildPageAllocator(pageIo);
            foreach (var (tableName, table, index) in distinctTargets)
            {
                // Resolving this comparer requires only the targeted index's
                // own collation — never any other index's — so an unrelated
                // index whose custom collation is not registered is untouched
                // by this call.
                var tree = BuildIndexTree(tableName, table, index, allocator);
                var rootPage = rootPages[index.Name];
                pageIo.WritePage(rootPage, tree.RootPage);
                foreach (var page in tree.InteriorPages)
                    pageIo.WritePage(page.PageNumber, page.Page);
                foreach (var page in tree.LeafPages)
                    pageIo.WritePage(page.PageNumber, page.Page);
                foreach (var page in tree.OverflowPages)
                    pageIo.WritePage(page.PageNumber, page.Page);
            }
        }
        catch (SqliteBtreeMaintenanceRequiredException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            // Free-page collection walks the OLD tree to discover its stale
            // pages, which requires that tree's stored bytes to still parse as
            // a well-formed b-tree. REINDEX must also repair a physically
            // corrupted index, so a target whose on-disk image is not safely
            // traversable falls back to the full-catalog rewrite, which never
            // reads the old tree and always succeeds by reallocating fresh.
            return false;
        }

        if (pageIo.StagedPages.Count == 0 || pageIo.StagedPages.ContainsKey(SchemaRootPage))
            return false;

        var target = pageIo.PageCount;
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            DatabaseSizeInPages = target,
            FirstFreelistTrunkPage = pageIo.FirstFreelistTrunkPage,
            FreelistPageCount = pageIo.FreelistPageCount,
        };
        var pageOne = _pager.ReadCommittedPage(SchemaRootPage);
        newHeader.WriteTo(pageOne);

        using (var transaction = _pager.BeginTransaction(target))
        {
            foreach (var (pageNumber, image) in pageIo.StagedPages)
                transaction.WritePage(pageNumber, image);

            transaction.WritePage(SchemaRootPage, pageOne);
            transaction.Commit(_synchronousMode);
        }

        _header = newHeader;
        if (checkpointAfterCommit)
            CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool ApplyIncrementalTableDelta(
        ISqliteBtreePageIo pageIo,
        TableRowDelta delta,
        uint rootPage,
        IReadOnlyList<IndexDefinition> indexes)
    {
        var table = delta.Table;
        var indexPlans = new List<IncrementalIndexPlan>(indexes.Count);
        foreach (var definition in indexes)
        {
            if (!_indexRootPages.TryGetValue(definition.Index.Name, out var indexRootPage)
                || indexRootPage < 2
                || indexRootPage > _pager.CommittedPageCount)
            {
                return false;
            }

            var comparer = CreateIndexComparer(table, definition.Index);
            indexPlans.Add(new IncrementalIndexPlan(
                definition.Index,
                indexRootPage,
                comparer,
                new SqliteIncrementalIndexBtree(pageIo, comparer, _textEncoding)));
        }

        var indexDeletes = new List<(IncrementalIndexPlan Plan, byte[] Record)>();
        var indexInserts = new List<(IncrementalIndexPlan Plan, byte[] Record)>();
        foreach (var plan in indexPlans)
        {
            foreach (var change in delta.Changes)
            {
                var before = change.Before is null
                    ? null
                    : TryBuildIndexRecord(delta.PreviousTable, plan.Index, change.Before, change.RowId, plan.Comparer);
                var after = change.After is null
                    ? null
                    : TryBuildIndexRecord(table, plan.Index, change.After, change.RowId, plan.Comparer);
                if (before is not null && after is not null && before.AsSpan().SequenceEqual(after))
                    continue;

                if (before is not null)
                    indexDeletes.Add((plan, before));
                if (after is not null)
                    indexInserts.Add((plan, after));
            }
        }

        // Every removal precedes every addition so a mutation that moves a key
        // between rows never transiently duplicates it.
        foreach (var (plan, record) in indexDeletes)
            plan.Tree.Delete(plan.RootPage, record);

        var tableTree = new SqliteIncrementalTableBtree(pageIo);
        foreach (var change in delta.Changes)
        {
            if (change.After is null)
                tableTree.Delete(rootPage, change.RowId);
        }
        foreach (var change in delta.Changes)
        {
            if (change.Before is not null && change.After is not null)
                tableTree.Update(rootPage, change.RowId, BuildTableRecord(table, change.After));
        }
        foreach (var change in delta.Changes)
        {
            if (change.Before is null && change.After is not null)
                tableTree.Insert(rootPage, change.RowId, BuildTableRecord(table, change.After));
        }

        foreach (var (plan, record) in indexInserts)
            plan.Tree.Insert(plan.RootPage, record);

        return true;
    }

    /// <summary>
    /// Computes the per-table row difference between the committed catalog and
    /// the catalog about to be persisted.
    /// </summary>
    private static bool TryCollectRowDeltas(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, EmbeddedTable> previousTables,
        out List<TableRowDelta> deltas)
    {
        deltas = [];
        if (tables.Count != previousTables.Count)
            return false;

        var changedRowBudget = MaximumIncrementalChangedRows;
        foreach (var (name, table) in tables)
        {
            if (!previousTables.TryGetValue(name, out var previous))
                return false;
            if (HaveSameRows(table, previous))
                continue;

            // A WITHOUT ROWID table is stored as an index b-tree keyed by its
            // primary key, which this rowid-oriented delta cannot express.
            if (table.WithoutRowid
                || table.Rows.Count != table.RowIds.Count
                || previous.Rows.Count != previous.RowIds.Count)
            {
                return false;
            }

            var before = new Dictionary<long, SqlValue[]>(previous.Rows.Count);
            for (var index = 0; index < previous.Rows.Count; index++)
            {
                if (!before.TryAdd(previous.RowIds[index], previous.Rows[index]))
                    return false;
            }

            var changes = new List<RowChange>();
            for (var index = 0; index < table.Rows.Count; index++)
            {
                var rowId = table.RowIds[index];
                var row = table.Rows[index];
                if (!before.Remove(rowId, out var previousRow))
                    changes.Add(new RowChange(rowId, null, row));
                else if (!previousRow.AsSpan().SequenceEqual(row))
                    changes.Add(new RowChange(rowId, previousRow, row));

                if (changes.Count > changedRowBudget)
                    return false;
            }

            foreach (var (rowId, previousRow) in before)
            {
                changes.Add(new RowChange(rowId, previousRow, null));
                if (changes.Count > changedRowBudget)
                    return false;
            }

            if (changes.Count == 0)
                return false;

            changedRowBudget -= changes.Count;
            deltas.Add(new TableRowDelta(name, table, previous, changes));
        }

        return true;
    }

    /// <summary>
    /// Encodes the complete SQLite index key for one row, or returns
    /// <see langword="null"/> when a partial index excludes it.
    /// </summary>
    private byte[]? TryBuildIndexRecord(
        EmbeddedTable table,
        EmbeddedIndex index,
        SqlValue[] row,
        long rowId,
        SqliteIndexRecordComparer comparer)
    {
        if (row.Length != table.ColumnDefinitions.Length)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist index '{index.Name}' because a row has an invalid column count.");
        }

        if (!IndexExpressionSemantics.Qualifies(
                index,
                table,
                row,
                rowId,
                _indexExpressionEvaluator.EvaluateIndexExpression))
        {
            return null;
        }

        var key = IndexExpressionSemantics.ProjectKey(
            index,
            table,
            row,
            rowId,
            _indexExpressionEvaluator.EvaluateIndexExpression);
        var values = new SqlValue[index.Columns.Count + 1];
        Array.Copy(key, values, key.Length);
        values[^1] = SqlValue.Integer(rowId);
        var record = SqliteRecordCodec.Encode(values, _textEncoding);
        comparer.Validate(record);
        return record;
    }

    private sealed record RowChange(long RowId, SqlValue[]? Before, SqlValue[]? After);

    private sealed record TableRowDelta(
        string TableName,
        EmbeddedTable Table,
        EmbeddedTable PreviousTable,
        IReadOnlyList<RowChange> Changes);

    private sealed record IncrementalIndexPlan(
        EmbeddedIndex Index,
        uint RootPage,
        SqliteIndexRecordComparer Comparer,
        SqliteIncrementalIndexBtree Tree);

    /// <summary>
    /// Replaces one existing ordinary-table leaf and every compatible
    /// secondary-index root leaf, promotes compatible secondary-index roots
    /// into interior roots with append-only leaves, or splits the right-most
    /// child leaf of one compatible secondary-index interior root. It can delete
    /// a record from a compatible direct child or retire a singleton child after
    /// moving that child's adjacent separator into its surviving sibling.
    /// </summary>
    /// <remarks>
    /// This intentionally accepts only an unchanged schema, exactly one changed rowid
    /// table, and fully local replacements that fit in its current
    /// table and ASC BINARY index root leaves. Existing freelist pages remain
    /// untouched; new split children are always appended after the committed page
    /// count. In addition to compatible root
    /// promotions, it can replace one non-overflow record or delete one
    /// non-empty child-leaf record beneath a one-level unindexed table root.
    /// A deletion can also collapse an exactly-two-child root back into its
    /// catalog-root leaf when every surviving record fits there, returning both
    /// retired children to a new exact freelist. It can remove an empty child
    /// from a compatible one-level root with at least three children, returning
    /// that retired child to a new exact freelist. A deletion that changes a
    /// non-rightmost child's maximum rowid replaces its parent separator in the
    /// same transaction. In an unindexed tree, a validated ancestor path updates
    /// the nearest separator that owns the changed subtree maximum at arbitrary
    /// depth. At arbitrary depth it can insert one fully local row before a
    /// leaf's current maximum without changing a separator or requiring a
    /// rebalance. It can also append one
    /// maximum-rowid record to the right-most table leaf and split that leaf
    /// when its parent has room for the new separator. When that one-level
    /// unindexed table root is full, it can promote the root while splitting
    /// its right-most leaf, provided every child is a non-overflow leaf and
    /// the mutation is a strict right-most append. For exactly one
    /// ASC BINARY secondary index, it can likewise split the right-most
    /// non-overflow index leaf under a one-level root when the target adds one
    /// maximum complete key, or split a full non-rightmost child when one new
    /// complete key fits strictly between its adjacent parent separators and the
    /// parent can accept the promoted separator. It can also atomically insert
    /// one non-separator key from a fully validated leaf below at least two index
    /// interior levels when the replacement fits without a rebalance. It can insert or delete
    /// one record in direct middle children, or append one strict maximum record to
    /// direct right-most children, of multiple compatible one-level index roots when
    /// no parent separator changes, and it can insert one record into
    /// the left-most direct children of multiple compatible one-level index roots.
    /// It can delete a singleton direct
    /// child when its parent retains at least two separators, transferring the
    /// removed child's adjacent separator into its surviving sibling before
    /// freelisting the retired page.
    /// All routing changes retain their catalog rootpage and are
    /// published with the replacement table page in one WAL transaction whose
    /// final frame is page one. Any overflow ownership change, unproven
    /// topology, rebalance, unsupported index coordination,
    /// root-type change, multi-table mutation, or an unvalidated freelist
    /// partition returns false before a write so the complete catalog rewrite
    /// remains the safe fallback.
    /// </remarks>
    private bool TryPersistBoundedTableLeafMutation(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        bool checkpointAfterCommit)
    {
        var schemaPage = _pager.ReadCommittedPage(SchemaRootPage);
        var currentHeader = SqliteDatabaseHeader.Parse(schemaPage);

        if (!HasCurrentSchemaShape(tables, views, triggers, virtualTables))
            return false;

        var persisted = Load();
        if (!HasCurrentSchemaShape(tables, views, triggers, virtualTables, persisted.Tables)
            || !TryGetSingleChangedTable(tables, persisted.Tables, out var tableName, out var table))
        {
            return false;
        }

        if (table.WithoutRowid
            || table.Rows.Count != table.RowIds.Count
            || !_tableRootPages.TryGetValue(tableName, out var rootPage)
            || rootPage < 2
            || rootPage > _pager.CommittedPageCount)
        {
            return false;
        }

        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexes = GetIndexDefinitions(tableNames, tables, views, triggers, virtualTables, persisted.Tables)
            .Where(index => string.Equals(index.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (indexes.Length != table.Indexes.Count
            || indexes.Any(index => !IsBoundedIndexLeafMutationCompatible(index)))
            return false;

        var mutationRootPages = new HashSet<uint> { SchemaRootPage, rootPage };
        var affectedIndexNames = new HashSet<string>(
            indexes.Select(index => index.Index.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes)
        {
            if (!_indexRootPages.TryGetValue(index.Index.Name, out var indexRootPage)
                || indexRootPage < 2
                || indexRootPage > _pager.CommittedPageCount
                || !mutationRootPages.Add(indexRootPage))
            {
                return false;
            }
        }

        if (_tableRootPages.Any(entry =>
                !string.Equals(entry.Key, tableName, StringComparison.OrdinalIgnoreCase)
                && mutationRootPages.Contains(entry.Value))
            || _indexRootPages.Any(entry =>
                !affectedIndexNames.Contains(entry.Key)
                && mutationRootPages.Contains(entry.Value)))
        {
            return false;
        }

        if (currentHeader.PageSize != _pageSize
            || currentHeader.VersionValidFor != currentHeader.ChangeCounter
            || currentHeader.DatabaseSizeInPages != _pager.CommittedPageCount
            || currentHeader.LargestRootBtreePage != 0
            || currentHeader.IncrementalVacuumEnabled != 0)
        {
            return false;
        }

        if (SqliteBtreePageHeader.Parse(schemaPage, isFirstPage: true).PageType
            is not (SqliteBtreePageType.TableLeaf or SqliteBtreePageType.TableInterior))
            return false;

        var existingPage = _pager.ReadCommittedPage(rootPage);
        var existingPageType = SqliteBtreePageHeader.Parse(existingPage).PageType;
        if (existingPageType == SqliteBtreePageType.TableInterior)
        {
            if (currentHeader.FreelistPageCount != 0
                || currentHeader.FirstFreelistTrunkPage != 0)
            {
                return false;
            }

            return indexes.Length == 0
                   && (TryPersistBoundedTableInteriorNestedLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorThirdLevelLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistValidatedTableInteriorArbitraryDepthLeafInsertion(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistValidatedTableInteriorArbitraryDepthLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorRootDirectLeafInsertion(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorRootSingleLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorRootRightLeafAppend(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                        || TryPersistBoundedTableInteriorRootRightLeafAppendWithRootPromotion(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader));
        }
        if (existingPageType != SqliteBtreePageType.TableLeaf)
        {
            throw new InvalidDataException(
                $"Managed file database table '{tableName}' rootpage {rootPage} has unsupported page type {existingPageType}.");
        }

        var existingLeaf = SqliteTableLeafPageView.Parse(existingPage, _usableSpace, isFirstPage: false);
        if (existingLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            return false;

        if (indexes.Length == 0
            && (currentHeader.FreelistPageCount != 0
                || currentHeader.FirstFreelistTrunkPage != 0))
        {
            return false;
        }

        if (!TryBuildBoundedTableLeafImage(tableName, table, existingPage, out var replacementPage))
        {
            return indexes.Length == 0
                   && TryPersistBoundedTableRootLeafPromotion(
                       tableName,
                       table,
                       rootPage,
                       schemaPage,
                       existingPage,
                       currentHeader);
        }

        var indexRootImages = indexes
            .Select(index =>
            {
                var indexRootPage = _indexRootPages[index.Index.Name];
                return (Definition: index, RootPage: indexRootPage, SourcePage: _pager.ReadCommittedPage(indexRootPage));
            })
            .ToArray();
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return indexRootImages.Length == 1
                   && SqliteBtreePageHeader.Parse(indexRootImages[0].SourcePage).PageType
                       == SqliteBtreePageType.IndexInterior
                   && TryPersistValidatedSecondaryIndexNestedLeafInsertion(
                       table,
                       persisted.Tables[tableName],
                       indexRootImages[0].Definition,
                       rootPage,
                       replacementPage,
                       indexRootImages[0].RootPage,
                       indexRootImages[0].SourcePage,
                       schemaPage,
                       existingPage,
                       currentHeader);
        }

        if (indexRootImages.Length > 1
            && indexRootImages.All(index =>
                SqliteBtreePageHeader.Parse(index.SourcePage).PageType == SqliteBtreePageType.IndexInterior))
        {
            return TryPersistBoundedSecondaryIndexInteriorRootLeafInsertions(
                       table,
                       persisted.Tables[tableName],
                       rootPage,
                       replacementPage,
                       indexRootImages,
                       schemaPage,
                       existingPage,
                       currentHeader)
                   || TryPersistBoundedSecondaryIndexInteriorRootLeafDeletions(
                       table,
                       persisted.Tables[tableName],
                       rootPage,
                       replacementPage,
                       indexRootImages,
                       schemaPage,
                       existingPage,
                       currentHeader);
        }

        var indexReplacementPages = new List<(uint PageNumber, byte[] SourcePage, byte[] ReplacementPage)>(
            indexes.Length);
        var indexRootPromotions = new List<BoundedIndexRootLeafPromotion>();
        foreach (var index in indexes)
        {
            var indexRootPage = _indexRootPages[index.Index.Name];
            var existingIndexPage = indexRootImages
                .Single(image => string.Equals(
                    image.Definition.Index.Name,
                    index.Index.Name,
                    StringComparison.OrdinalIgnoreCase))
                .SourcePage;
            var existingIndexPageType = SqliteBtreePageHeader.Parse(existingIndexPage).PageType;
            if (existingIndexPageType == SqliteBtreePageType.IndexInterior)
            {
                return indexes.Length == 1
                       && (TryPersistBoundedSecondaryIndexInteriorRootLeafDeletion(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader)
                           || TryPersistValidatedSecondaryIndexNestedLeafInsertion(
                               table,
                               persisted.Tables[tableName],
                               index,
                               rootPage,
                               replacementPage,
                               indexRootPage,
                               existingIndexPage,
                               schemaPage,
                               existingPage,
                               currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootLeafInsertion(
                               table,
                               persisted.Tables[tableName],
                               index,
                               rootPage,
                               replacementPage,
                               indexRootPage,
                               existingIndexPage,
                               schemaPage,
                               existingPage,
                               currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootLeftmostLeafSplit(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootMiddleLeafSplit(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootRightLeafSplit(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader));
            }
            if (existingIndexPageType != SqliteBtreePageType.IndexLeaf)
            {
                throw new InvalidDataException(
                    $"Managed file database index '{index.Index.Name}' rootpage {indexRootPage} has unsupported page type {existingIndexPageType}.");
            }

            var existingIndexLeaf = SqliteIndexLeafPageView.Parse(
                existingIndexPage,
                _usableSpace,
                _textEncoding);
            if (existingIndexLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
                return false;

            if (!TryBuildBoundedIndexLeafImage(index, existingIndexPage, out var replacementIndexPage))
            {
                var comparer = new SqliteIndexRecordComparer(_textEncoding);
                var records = BuildIndexRecords(
                    index.TableName,
                    index.Table,
                    index.Index,
                    comparer);
                ValidateBoundedUniqueIndexRecords(index, records, comparer);
                if (!TryBuildBoundedIndexRootLeafSplitImages(
                        records,
                        out var leftPage,
                        out var rightPage,
                        out var separatorRecord))
                {
                    return false;
                }

                indexRootPromotions.Add(new BoundedIndexRootLeafPromotion(
                    indexRootPage,
                    existingIndexPage,
                    leftPage,
                    rightPage,
                    separatorRecord));
                continue;
            }

            indexReplacementPages.Add((indexRootPage, existingIndexPage, replacementIndexPage));
        }

        if (indexRootPromotions.Count != 0)
        {
            return TryPersistBoundedSecondaryIndexRootLeafPromotions(
                rootPage,
                replacementPage,
                indexReplacementPages,
                indexRootPromotions,
                schemaPage,
                existingPage,
                currentHeader);
        }

        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(schemaPage);

        using (var transaction = _pager.BeginTransaction(_pager.CommittedPageCount))
        {
            transaction.WritePage(rootPage, replacementPage);
            foreach (var (pageNumber, _, replacementIndexPage) in indexReplacementPages)
                transaction.WritePage(pageNumber, replacementIndexPage);
            transaction.WritePage(SchemaRootPage, schemaPage);
            transaction.Commit(_synchronousMode);
        }

        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootRightLeafAppend(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue
            || !TryGetBoundedStrictAppendCell(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var appendedCell))
        {
            return false;
        }

        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length == 0)
            return false;

        foreach (var childPage in childPages)
        {
            if (childPage < 2
                || childPage == rootPage
                || childPage > sourcePageCount)
            {
                return false;
            }

            var childImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childImage).PageType != SqliteBtreePageType.TableLeaf
                || SqliteTableLeafPageView.Parse(childImage, _usableSpace).Cells
                    .Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }
        }

        var targetLeafPage = parent.Header.RightMostChildPage;
        var sourceLeafPage = _pager.ReadCommittedPage(targetLeafPage);
        var sourceLeaf = SqliteTableLeafPageView.Parse(sourceLeafPage, _usableSpace);
        if (sourceLeaf.Cells.Count == 0
            || sourceLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
            || sourceLeaf.Cells.Count > persistedCells.Count)
        {
            return false;
        }

        var sourceLeafStart = persistedCells.Count - sourceLeaf.Cells.Count;
        for (var index = 0; index < sourceLeaf.Cells.Count; index++)
        {
            if (sourceLeaf.Cells[index].Cell.RowId != persistedCells[sourceLeafStart + index].RowId)
                return false;
        }

        var targetCells = new List<SqliteTableLeafCell>(sourceLeaf.Cells.Count + 1);
        targetCells.AddRange(sourceLeaf.Cells.Select(cell => cell.Cell));
        targetCells.Add(appendedCell);
        if (TryBuildBoundedTableLeafPage(targetCells, 0, targetCells.Count, out var replacementLeafPage))
        {
            return CommitBoundedTableInteriorRootLeafMutation(
                targetLeafPage,
                sourceLeafPage,
                replacementLeafPage,
                schemaPage,
                currentHeader);
        }

        if (!TryBuildBoundedTableLeafSplitImages(
                targetCells,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRowId))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementRootPage;
        try
        {
            var parentBuilder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                appendedPage);
            foreach (var cell in parent.Cells)
                parentBuilder.Append(cell.Cell);
            parentBuilder.Append(SqliteTableInteriorCell.Create(targetLeafPage, separatorRowId));
            replacementRootPage = existingRootPage.ToArray();
            parentBuilder.WriteTo(replacementRootPage);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(rootPage, existingRootPage),
                new SqlitePageImage(targetLeafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(targetLeafPage, leftLeafPage),
                new SqlitePageImage(rootPage, replacementRootPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootRightLeafAppendWithRootPromotion(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages > uint.MaxValue - 3
            || !TryGetBoundedStrictAppendCell(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var appendedCell))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length < 2)
            return false;

        var persistedCellIndex = 0;
        SqliteTableLeafPageView? sourceRightLeaf = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage == rootPage
                || childPage > sourcePageCount)
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.TableLeaf)
                return false;

            var child = SqliteTableLeafPageView.Parse(childPageImage, _usableSpace);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var childMaximumRowId = child.Cells[^1].Cell.RowId;
            if (!parent.IsChildRangeValid(
                    childIndex,
                    child.Cells[0].Cell.RowId,
                    childMaximumRowId))
            {
                return false;
            }

            foreach (var cell in child.Cells)
            {
                if (persistedCellIndex >= persistedCells.Count
                    || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                    || !cell.Cell.LocalPayload.Span.SequenceEqual(persistedCells[persistedCellIndex].Record))
                {
                    return false;
                }

                persistedCellIndex++;
            }

            if (childIndex == childPages.Length - 1)
                sourceRightLeaf = child;
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceRightLeaf is null)
        {
            return false;
        }

        var targetCells = new List<SqliteTableLeafCell>(sourceRightLeaf.Cells.Count + 1);
        targetCells.AddRange(sourceRightLeaf.Cells.Select(cell => cell.Cell));
        targetCells.Add(appendedCell);
        if (TryBuildBoundedTableLeafPage(targetCells, 0, targetCells.Count, out _))
            return false;

        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = sourcePageCount + 3,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitWriter(
                _pager,
                new SqliteAppendOnlyPageAllocator(sourcePageCount))
            .PrepareTableInteriorRootRightmostLeafSplit(
                rootPage,
                appendedCell,
                targetSchemaPage);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootRightLeafSplit(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue)
            return false;

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var existingRecords = new List<byte[]>();
        List<byte[]>? sourceRightLeafRecords = null;
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var childRecords = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                existingRecords.Add(record);
                childRecords.Add(record);
                previousRecord = record;
            }

            if (childIndex < parent.Cells.Count)
            {
                var separator = parent.GetRecord(childIndex);
                if (comparer.Compare(previousRecord!, separator) >= 0)
                    return false;

                existingRecords.Add(separator);
                previousRecord = separator;
            }
            else
            {
                sourceRightLeafRecords = childRecords;
            }
        }

        if (sourceRightLeafRecords is null
            || existingRecords.Count == 0
            || previousRecord is null)
        {
            return false;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex])
                || !targetRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
            {
                return false;
            }
        }

        var appendedRecord = targetRecords[^1];
        if (comparer.Compare(previousRecord, appendedRecord) >= 0
            || SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)appendedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var targetRightLeafRecords = new List<byte[]>(sourceRightLeafRecords.Count + 1);
        targetRightLeafRecords.AddRange(sourceRightLeafRecords);
        targetRightLeafRecords.Add(appendedRecord);
        if (TryBuildBoundedIndexLeafPage(
                targetRightLeafRecords,
                0,
                targetRightLeafRecords.Count,
                out _)
            || !TryBuildBoundedIndexRootLeafSplitImages(
                targetRightLeafRecords,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRecord))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementIndexRootPage;
        try
        {
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                appendedPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
                parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            parentBuilder.Append(
                SqliteIndexInteriorCell.Create(
                    parent.Header.RightMostChildPage,
                    separatorRecord,
                    _usableSpace),
                separatorRecord);
            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(parent.Header.RightMostChildPage, _pager.ReadCommittedPage(parent.Header.RightMostChildPage)),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(parent.Header.RightMostChildPage, leftLeafPage),
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistValidatedSecondaryIndexNestedLeafInsertion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        SqliteFreelist freelist;
        try
        {
            freelist = SqliteFreelist.Read(
                currentHeader,
                currentHeader.DatabaseSizeInPages,
                _pager.ReadCommittedPage);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage };
        var sourceTreePages = new List<SqlitePageImage>();
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        int? leafDepth = null;
        var overflowReader = new SqliteOverflowChainReader(_pager, currentHeader);

        bool AddRecord(byte[] record)
        {
            if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                return false;

            existingRecords.Add(record);
            previousRecord = record;
            return true;
        }

        bool AddOverflowPages(SqliteIndexLeafCell cell)
        {
            ulong overflowLength;
            try
            {
                overflowLength = GetOverflowLength(
                    cell.PayloadLength,
                    cell.LocalPayload.Length,
                    "nested secondary-index");
            }
            catch (InvalidDataException)
            {
                return false;
            }

            if (overflowLength == 0)
                return cell.FirstOverflowPage is null;
            if (cell.FirstOverflowPage is not { } firstOverflowPage)
                return false;

            try
            {
                foreach (var overflowPage in overflowReader.Traverse(firstOverflowPage, overflowLength))
                {
                    if (!ownedPages.Add(overflowPage))
                        return false;

                    sourceTreePages.Add(new SqlitePageImage(
                        overflowPage,
                        _pager.ReadCommittedPage(overflowPage)));
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }

            return true;
        }

        bool Visit(uint pageNumber, ReadOnlySpan<byte> pageImage, int depth, out byte[] maximumRecord)
        {
            maximumRecord = null!;
            if (pageNumber < 2
                || pageNumber > sourcePageCount
                || !ownedPages.Add(pageNumber))
            {
                return false;
            }

            var sourcePage = pageImage.ToArray();
            sourceTreePages.Add(new SqlitePageImage(pageNumber, sourcePage));
            switch (SqliteBtreePageHeader.Parse(sourcePage).PageType)
            {
                case SqliteBtreePageType.IndexLeaf:
                    {
                        var leaf = SqliteIndexLeafPageView.Parse(
                            sourcePage,
                            _usableSpace,
                            _textEncoding,
                            overflowReader: overflowReader);
                        if (leaf.Cells.Count == 0
                            || (leafDepth is { } expectedDepth && expectedDepth != depth))
                        {
                            return false;
                        }

                        leafDepth ??= depth;
                        for (var recordIndex = 0; recordIndex < leaf.Cells.Count; recordIndex++)
                        {
                            if (!AddOverflowPages(leaf.Cells[recordIndex].Cell))
                                return false;

                            var record = leaf.GetRecord(recordIndex);
                            if (!AddRecord(record))
                                return false;
                            maximumRecord = record;
                        }

                        return true;
                    }

                case SqliteBtreePageType.IndexInterior:
                    {
                        var interior = SqliteIndexInteriorPageView.Parse(
                            sourcePage,
                            _usableSpace,
                            _textEncoding,
                            overflowReader: overflowReader);
                        if (interior.Cells.Count == 0)
                        {
                            return false;
                        }

                        var childPages = interior.Cells
                            .Select(cell => cell.Cell.LeftChildPage)
                            .Append(interior.Header.RightMostChildPage)
                            .ToArray();
                        if (childPages.Length != interior.Cells.Count + 1)
                            return false;

                        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                        {
                            if (!Visit(
                                    childPages[childIndex],
                                    _pager.ReadCommittedPage(childPages[childIndex]),
                                    depth + 1,
                                    out var childMaximum))
                            {
                                return false;
                            }

                            if (childIndex < interior.Cells.Count)
                            {
                                if (!AddOverflowPages(interior.Cells[childIndex].Cell.Key))
                                    return false;

                                var separator = interior.GetRecord(childIndex);
                                if (comparer.Compare(childMaximum, separator) >= 0
                                    || !AddRecord(separator))
                                {
                                    return false;
                                }
                            }
                            else
                            {
                                maximumRecord = childMaximum;
                            }
                        }

                        return maximumRecord is not null;
                    }

                default:
                    return false;
            }
        }

        if (!Visit(indexRootPage, sourceIndexRootPage, depth: 1, out _)
            || leafDepth is null
            || leafDepth.Value < 3
            || existingRecords.Count == 0)
        {
            return false;
        }

        foreach (var freelistPage in freelist.PageNumbers)
        {
            if (!ownedPages.Add(freelistPage))
                return false;

            sourceTreePages.Add(new SqlitePageImage(
                freelistPage,
                _pager.ReadCommittedPage(freelistPage)));
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var routedPage = indexRootPage;
        var interiorDepth = 0;
        byte[]? sourceLeafPage = null;
        SqliteIndexLeafPageView? sourceLeaf = null;
        while (true)
        {
            var sourcePage = sourceTreePages
                .SingleOrDefault(image => image.PageNumber == routedPage)
                ?.ToArray();
            if (sourcePage is null)
                return false;

            switch (SqliteBtreePageHeader.Parse(sourcePage).PageType)
            {
                case SqliteBtreePageType.IndexInterior:
                    {
                        interiorDepth++;
                        var interior = SqliteIndexInteriorPageView.Parse(
                            sourcePage,
                            _usableSpace,
                            _textEncoding,
                            overflowReader: overflowReader);
                        var route = interior.SearchChild(addedRecord);
                        if (route.IsSeparatorKey || route.ChildPage == 0)
                            return false;

                        routedPage = route.ChildPage;
                        break;
                    }

                case SqliteBtreePageType.IndexLeaf:
                    sourceLeafPage = sourcePage;
                    sourceLeaf = SqliteIndexLeafPageView.Parse(
                        sourcePage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader);
                    goto RoutedToLeaf;

                default:
                    return false;
            }
        }

    RoutedToLeaf:
        if (interiorDepth < 2
            || sourceLeafPage is null
            || sourceLeaf is null)
        {
            return false;
        }

        var insertion = sourceLeaf.Search(addedRecord);
        if (insertion.IsExact
            || sourceLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            return false;

        var replacementRecords = new List<byte[]>(sourceLeaf.Cells.Count + 1);
        for (var recordIndex = 0; recordIndex < sourceLeaf.Cells.Count; recordIndex++)
        {
            if (recordIndex == insertion.Index)
                replacementRecords.Add(addedRecord);
            replacementRecords.Add(sourceLeaf.GetRecord(recordIndex));
        }
        if (insertion.Index == sourceLeaf.Cells.Count)
            replacementRecords.Add(addedRecord);

        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceLeafPage,
                out var replacementLeafPage))
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(checked(2 + sourceTreePages.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, sourceTablePage),
        };
        sourcePages.AddRange(sourceTreePages);
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            [
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(routedPage, replacementLeafPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafInsertion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1 || childPages.Length < 2)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex < 0
            || route.ChildIndex >= parent.Cells.Count
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || (route.ChildIndex > 0
                && comparer.Compare(parent.GetRecord(route.ChildIndex - 1), addedRecord) >= 0)
            || (route.ChildIndex < parent.Cells.Count
                && comparer.Compare(addedRecord, parent.GetRecord(route.ChildIndex)) >= 0))
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        var sourceLeafPage = _pager.ReadCommittedPage(route.ChildPage);
        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceLeafPage,
                out var replacementLeafPage))
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(route.ChildPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(route.ChildPage, replacementLeafPage),
                // Page one publishes the revised table and index leaf last.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeftmostLeafSplit(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue
            || currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex != 0
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || comparer.Compare(addedRecord, parent.GetRecord(0)) >= 0)
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        if (TryBuildBoundedIndexLeafPage(
                replacementRecords,
                0,
                replacementRecords.Count,
                out _)
            || !TryBuildBoundedIndexRootLeafSplitImages(
                replacementRecords,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRecord))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementIndexRootPage;
        try
        {
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                parent.Header.RightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex == 0)
                {
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            route.ChildPage,
                            separatorRecord,
                            _usableSpace),
                        separatorRecord);
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            appendedPage,
                            parent.GetRecord(cellIndex),
                            _usableSpace),
                        parent.GetRecord(cellIndex));
                    continue;
                }

                parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            }

            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(route.ChildPage, sourceChildPages[route.ChildIndex]),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(route.ChildPage, leftLeafPage),
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                // The catalog root makes the new left-most child reachable only
                // after every dependent image has been written to the WAL.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootMiddleLeafSplit(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue
            || currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count < 2
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex <= 0
            || route.ChildIndex >= parent.Cells.Count
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || comparer.Compare(parent.GetRecord(route.ChildIndex - 1), addedRecord) >= 0
            || comparer.Compare(addedRecord, parent.GetRecord(route.ChildIndex)) >= 0)
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        if (TryBuildBoundedIndexLeafPage(
                replacementRecords,
                0,
                replacementRecords.Count,
                out _)
            || !TryBuildBoundedIndexRootLeafSplitImages(
                replacementRecords,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRecord))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementIndexRootPage;
        try
        {
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                parent.Header.RightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex == route.ChildIndex)
                {
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            route.ChildPage,
                            separatorRecord,
                            _usableSpace),
                        separatorRecord);
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            appendedPage,
                            parent.GetRecord(cellIndex),
                            _usableSpace),
                        parent.GetRecord(cellIndex));
                    continue;
                }

                parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            }

            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(route.ChildPage, sourceChildPages[route.ChildIndex]),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(route.ChildPage, leftLeafPage),
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                // The catalog root makes the new child reachable only after every
                // dependent image has been written to the WAL.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafInsertions(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        IReadOnlyList<(IndexDefinition Definition, uint RootPage, byte[] SourcePage)> indexes,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0
            || indexes.Count < 2)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage };
        var insertions = new List<BoundedSecondaryIndexInteriorLeafInsertion>(indexes.Count);
        foreach (var (definition, indexRootPage, sourceIndexRootPage) in indexes)
        {
            if (!TryPrepareBoundedSecondaryIndexInteriorRootLeafInsertion(
                    table,
                    persistedTable,
                    definition,
                    indexRootPage,
                    sourceIndexRootPage,
                    currentHeader,
                    out var insertion))
            {
                return false;
            }

            foreach (var sourcePage in insertion.SourceTreePages)
            {
                if (!ownedPages.Add(sourcePage.PageNumber))
                    return false;
            }

            insertions.Add(insertion);
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(checked(2 + ownedPages.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, sourceTablePage),
        };
        foreach (var insertion in insertions)
            sourcePages.AddRange(insertion.SourceTreePages);

        var writeImages = new List<SqlitePageImage>(checked(2 + insertions.Count))
        {
            new(tableRootPage, replacementTablePage),
        };
        writeImages.AddRange(insertions
            .OrderBy(insertion => insertion.LeafPageNumber)
            .Select(insertion => new SqlitePageImage(
                insertion.LeafPageNumber,
                insertion.ReplacementLeafPage)));
        // Page one publishes all table and index leaf replacements only after
        // every routed page image is present in the WAL.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPrepareBoundedSecondaryIndexInteriorRootLeafInsertion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        SqliteDatabaseHeader currentHeader,
        out BoundedSecondaryIndexInteriorLeafInsertion insertion)
    {
        insertion = null!;
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (indexRootPage < 2
            || indexRootPage > sourcePageCount
            || SqliteBtreePageHeader.Parse(sourceIndexRootPage).PageType != SqliteBtreePageType.IndexInterior)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { indexRootPage };
        var sourceTreePages = new List<SqlitePageImage>(childPages.Length + 1)
        {
            new(indexRootPage, sourceIndexRootPage),
        };
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceTreePages.Add(new SqlitePageImage(childPage, childPageImage));
            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex < 0
            || route.ChildIndex >= childPages.Length
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var isRightmostChild = route.ChildIndex == parent.Cells.Count;
        if (isRightmostChild
            && (addedRecordIndex != targetRecords.Count - 1
                || comparer.Compare(previousRecord!, addedRecord) >= 0))
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((isRightmostChild && insertionIndex != routedRecords.Count)
            || (insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || (route.ChildIndex > 0
                && comparer.Compare(parent.GetRecord(route.ChildIndex - 1), addedRecord) >= 0)
            || (route.ChildIndex < parent.Cells.Count
                && comparer.Compare(addedRecord, parent.GetRecord(route.ChildIndex)) >= 0))
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceChildPages[route.ChildIndex],
                out var replacementLeafPage))
        {
            return false;
        }

        insertion = new BoundedSecondaryIndexInteriorLeafInsertion(
            route.ChildPage,
            replacementLeafPage,
            sourceTreePages);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafDeletions(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        IReadOnlyList<(IndexDefinition Definition, uint RootPage, byte[] SourcePage)> indexes,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0
            || indexes.Count < 2)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage };
        var deletions = new List<BoundedSecondaryIndexInteriorLeafDeletion>(indexes.Count);
        foreach (var (definition, indexRootPage, sourceIndexRootPage) in indexes)
        {
            if (!TryPrepareBoundedSecondaryIndexInteriorRootLeafDeletion(
                    table,
                    persistedTable,
                    definition,
                    indexRootPage,
                    sourceIndexRootPage,
                    currentHeader,
                    out var deletion))
            {
                return false;
            }

            foreach (var sourcePage in deletion.SourceTreePages)
            {
                if (!ownedPages.Add(sourcePage.PageNumber))
                    return false;
            }

            deletions.Add(deletion);
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(checked(2 + ownedPages.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, sourceTablePage),
        };
        foreach (var deletion in deletions)
            sourcePages.AddRange(deletion.SourceTreePages);

        var writeImages = new List<SqlitePageImage>(checked(2 + deletions.Count))
        {
            new(tableRootPage, replacementTablePage),
        };
        writeImages.AddRange(deletions
            .OrderBy(deletion => deletion.LeafPageNumber)
            .Select(deletion => new SqlitePageImage(
                deletion.LeafPageNumber,
                deletion.ReplacementLeafPage)));
        // The catalog root publishes all table and index leaf replacements only
        // after every routed page image is present in the WAL.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPrepareBoundedSecondaryIndexInteriorRootLeafDeletion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        SqliteDatabaseHeader currentHeader,
        out BoundedSecondaryIndexInteriorLeafDeletion deletion)
    {
        deletion = null!;
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (indexRootPage < 2
            || indexRootPage > sourcePageCount
            || SqliteBtreePageHeader.Parse(sourceIndexRootPage).PageType != SqliteBtreePageType.IndexInterior)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1 || childPages.Length < 2)
            return false;

        var ownedPages = new HashSet<uint> { indexRootPage };
        var sourceTreePages = new List<SqlitePageImage>(childPages.Length + 1)
        {
            new(indexRootPage, sourceIndexRootPage),
        };
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceTreePages.Add(new SqlitePageImage(childPage, childPageImage));
            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        if (existingRecords.Count == 0)
            return false;

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count - 1)
        {
            return false;
        }

        var removedRecordIndex = -1;
        var targetRecordIndex = 0;
        for (var existingRecordIndex = 0;
             existingRecordIndex < existingRecords.Count;
             existingRecordIndex++)
        {
            if (targetRecordIndex < targetRecords.Count
                && targetRecords[targetRecordIndex].AsSpan().SequenceEqual(existingRecords[existingRecordIndex]))
            {
                targetRecordIndex++;
                continue;
            }

            if (removedRecordIndex >= 0)
                return false;

            removedRecordIndex = existingRecordIndex;
        }

        if (removedRecordIndex < 0 || targetRecordIndex != targetRecords.Count)
            return false;

        var removedRecord = existingRecords[removedRecordIndex];
        var removedChildIndex = -1;
        var removedChildRecordIndex = -1;
        for (var childIndex = 0; childIndex < childRecords.Count; childIndex++)
        {
            var recordIndex = childRecords[childIndex].FindIndex(
                record => record.AsSpan().SequenceEqual(removedRecord));
            if (recordIndex < 0)
                continue;

            if (removedChildIndex >= 0)
                return false;

            removedChildIndex = childIndex;
            removedChildRecordIndex = recordIndex;
        }

        // A separator is the maximum key of its left child. Rewriting that child
        // would require parent propagation, so this path never attempts it.
        if (removedChildIndex < 0
            || childRecords[removedChildIndex].Count <= 1
            || (removedChildIndex < parent.Cells.Count
                && removedChildRecordIndex == childRecords[removedChildIndex].Count - 1))
        {
            return false;
        }

        var replacementRecords = childRecords[removedChildIndex]
            .Where(record => !record.AsSpan().SequenceEqual(removedRecord))
            .ToArray();
        if (replacementRecords.Length != childRecords[removedChildIndex].Count - 1
            || !TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceChildPages[removedChildIndex],
                out var replacementLeafPage))
        {
            return false;
        }

        deletion = new BoundedSecondaryIndexInteriorLeafDeletion(
            childPages[removedChildIndex],
            replacementLeafPage,
            sourceTreePages);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafDeletion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1 || childPages.Length < 2)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        if (existingRecords.Count == 0)
            return false;

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count - 1)
        {
            return false;
        }

        var removedRecordIndex = -1;
        var targetRecordIndex = 0;
        for (var existingRecordIndex = 0;
             existingRecordIndex < existingRecords.Count;
             existingRecordIndex++)
        {
            if (targetRecordIndex < targetRecords.Count
                && targetRecords[targetRecordIndex].AsSpan().SequenceEqual(existingRecords[existingRecordIndex]))
            {
                targetRecordIndex++;
                continue;
            }

            if (removedRecordIndex >= 0)
                return false;

            removedRecordIndex = existingRecordIndex;
        }

        if (removedRecordIndex < 0 || targetRecordIndex != targetRecords.Count)
            return false;

        var removedRecord = existingRecords[removedRecordIndex];
        var removedChildIndex = -1;
        for (var childIndex = 0; childIndex < childRecords.Count; childIndex++)
        {
            if (childRecords[childIndex].Any(record => record.AsSpan().SequenceEqual(removedRecord)))
            {
                removedChildIndex = childIndex;
                break;
            }
        }

        if (removedChildIndex < 0)
            return false;

        if (childRecords[removedChildIndex].Count > 1)
        {
            var replacementRecords = childRecords[removedChildIndex]
                .Where(record => !record.AsSpan().SequenceEqual(removedRecord))
                .ToArray();
            if (replacementRecords.Length != childRecords[removedChildIndex].Count - 1
                || !TryBuildBoundedIndexLeafReplacementPage(
                    replacementRecords,
                    sourceChildPages[removedChildIndex],
                    out var replacementLeafPage))
            {
                return false;
            }

            var leafSourceSchemaPage = schemaPage.ToArray();
            var leafTargetSchemaPage = schemaPage.ToArray();
            var leafChangeCounter = currentHeader.ChangeCounter + 1;
            var leafHeader = currentHeader with
            {
                ChangeCounter = leafChangeCounter,
                VersionValidFor = leafChangeCounter,
            };
            leafHeader.WriteTo(leafTargetSchemaPage);

            var leafMutation = new SqliteBtreeSplitMutation(
                sourcePageCount,
                sourcePageCount,
                _pageSize,
                [
                    new SqlitePageImage(SchemaRootPage, leafSourceSchemaPage),
                    new SqlitePageImage(tableRootPage, sourceTablePage),
                    new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                    new SqlitePageImage(
                        childPages[removedChildIndex],
                        sourceChildPages[removedChildIndex]),
                ],
                [
                    new SqlitePageImage(tableRootPage, replacementTablePage),
                    new SqlitePageImage(childPages[removedChildIndex], replacementLeafPage),
                    // Page one publishes the revised table and index leaves last.
                    new SqlitePageImage(SchemaRootPage, leafTargetSchemaPage),
                ]);
            leafMutation.CommitTo(_pager);
            _header = leafHeader;
            CheckpointCommittedMutation(reclaimTrailingPages: false);
            return true;
        }

        if (parent.Cells.Count < 2 || childPages.Length < 3)
            return false;

        var removedSeparatorIndex = removedChildIndex == parent.Cells.Count
            ? parent.Cells.Count - 1
            : removedChildIndex;
        var siblingChildIndex = removedChildIndex == parent.Cells.Count
            ? removedChildIndex - 1
            : removedChildIndex + 1;
        var transferredSeparator = parent.GetRecord(removedSeparatorIndex);
        var replacementSiblingRecords = new List<byte[]>(
            childRecords[siblingChildIndex].Count + 1);
        if (siblingChildIndex < removedChildIndex)
        {
            replacementSiblingRecords.AddRange(childRecords[siblingChildIndex]);
            replacementSiblingRecords.Add(transferredSeparator);
        }
        else
        {
            replacementSiblingRecords.Add(transferredSeparator);
            replacementSiblingRecords.AddRange(childRecords[siblingChildIndex]);
        }

        var siblingPage = childPages[siblingChildIndex];
        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementSiblingRecords,
                sourceChildPages[siblingChildIndex],
                out var replacementSiblingPage))
        {
            return false;
        }

        byte[] replacementIndexRootPage;
        try
        {
            var rightMostChildPage = removedChildIndex == parent.Cells.Count
                ? parent.Cells[^1].Cell.LeftChildPage
                : parent.Header.RightMostChildPage;
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                rightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex != removedSeparatorIndex)
                    parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            }

            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        SqliteFreelist retiredChild;
        try
        {
            retiredChild = SqliteFreelist.CreateFromFreePages(
                sourcePageCount,
                [childPages[removedChildIndex]],
                _pageSize,
                _usableSpace);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (retiredChild.PageCount != 1
            || retiredChild.FirstTrunkPage != childPages[removedChildIndex]
            || retiredChild.PageNumbers.Count != 1
            || retiredChild.PageNumbers[0] != childPages[removedChildIndex]
            || retiredChild.TrunkPageNumbers.Count != 1
            || retiredChild.TrunkPageNumbers[0] != childPages[removedChildIndex]
            || retiredChild.PageImages.Count != 1
            || retiredChild.PageImages[0].PageNumber != childPages[removedChildIndex])
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            FirstFreelistTrunkPage = retiredChild.FirstTrunkPage,
            FreelistPageCount = retiredChild.PageCount,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(childPages[removedChildIndex], sourceChildPages[removedChildIndex]),
                new SqlitePageImage(siblingPage, sourceChildPages[siblingChildIndex]),
            ],
            [
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(siblingPage, replacementSiblingPage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                retiredChild.PageImages[0],
                // Page one exposes both the revised tree and its retired child.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedTableInteriorNestedLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var root = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        if (root.Cells.Count == 0)
            return false;

        var rootChildPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .ToArray();
        if (rootChildPages.Length != root.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, rootPage };
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        byte[]? sourceParentPage = null;
        SqliteTableInteriorPageView? targetParent = null;
        uint targetParentPage = 0;
        var targetParentIndex = -1;
        var targetLeafIndex = -1;

        for (var parentIndex = 0; parentIndex < rootChildPages.Length; parentIndex++)
        {
            var parentPage = rootChildPages[parentIndex];
            if (parentPage < 2
                || parentPage > sourcePageCount
                || !ownedPages.Add(parentPage))
            {
                return false;
            }

            var parentImage = _pager.ReadCommittedPage(parentPage);
            if (SqliteBtreePageHeader.Parse(parentImage).PageType != SqliteBtreePageType.TableInterior)
                return false;

            var parent = SqliteTableInteriorPageView.Parse(parentImage, _usableSpace);
            if (parent.Cells.Count == 0)
                return false;

            var leafPages = parent.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(parent.Header.RightMostChildPage)
                .ToArray();
            if (leafPages.Length != parent.Cells.Count + 1)
                return false;

            long? parentMinimumRowId = null;
            long? parentMaximumRowId = null;
            for (var leafIndex = 0; leafIndex < leafPages.Length; leafIndex++)
            {
                var leafPage = leafPages[leafIndex];
                if (leafPage < 2
                    || leafPage > sourcePageCount
                    || !ownedPages.Add(leafPage))
                {
                    return false;
                }

                var leafImage = _pager.ReadCommittedPage(leafPage);
                if (SqliteBtreePageHeader.Parse(leafImage).PageType != SqliteBtreePageType.TableLeaf)
                    return false;

                var leaf = SqliteTableLeafPageView.Parse(leafImage, _usableSpace);
                if (leaf.Cells.Count == 0
                    || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                    || (previousMaximumRowId is { } maximumRowId
                        && leaf.Cells[0].Cell.RowId <= maximumRowId)
                    || !parent.IsChildRangeValid(
                        leafIndex,
                        leaf.Cells[0].Cell.RowId,
                        leaf.Cells[^1].Cell.RowId))
                {
                    return false;
                }

                foreach (var cell in leaf.Cells)
                {
                    if (persistedCellIndex >= persistedCells.Count
                        || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                        || !cell.Cell.LocalPayload.Span.SequenceEqual(
                            persistedCells[persistedCellIndex].Record))
                    {
                        return false;
                    }

                    persistedCellIndex++;
                }

                if (leaf.Search(change.RowId).IsExact)
                {
                    if (sourceLeaf is not null)
                        return false;

                    sourceLeaf = leaf;
                    sourceLeafPage = leafImage;
                    targetLeafPage = leafPage;
                    sourceParentPage = parentImage;
                    targetParent = parent;
                    targetParentPage = parentPage;
                    targetParentIndex = parentIndex;
                    targetLeafIndex = leafIndex;
                }

                parentMinimumRowId ??= leaf.Cells[0].Cell.RowId;
                parentMaximumRowId = leaf.Cells[^1].Cell.RowId;
                previousMaximumRowId = parentMaximumRowId;
            }

            if (parentMinimumRowId is null
                || parentMaximumRowId is null
                || !root.IsChildRangeValid(
                    parentIndex,
                    parentMinimumRowId.Value,
                    parentMaximumRowId.Value))
            {
                return false;
            }
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || sourceParentPage is null
            || targetParent is null
            || targetLeafPage == 0
            || targetParentPage == 0
            || targetParentIndex < 0
            || targetLeafIndex < 0)
        {
            return false;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementParentPage = null;
        byte[]? replacementRootPage = null;
        var boundaryChanged = change.IsDelete
            && sourceLeaf.Cells[^1].Cell.RowId == change.RowId;
        if (boundaryChanged && targetLeafIndex < targetParent.Cells.Count)
        {
            try
            {
                var parentBuilder = new SqliteTableInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    targetParent.Header.RightMostChildPage);
                for (var cellIndex = 0; cellIndex < targetParent.Cells.Count; cellIndex++)
                {
                    var cell = targetParent.Cells[cellIndex].Cell;
                    parentBuilder.Append(cellIndex == targetLeafIndex
                        ? SqliteTableInteriorCell.Create(targetLeafPage, replacementCells[^1].RowId)
                        : cell);
                }

                replacementParentPage = sourceParentPage.ToArray();
                parentBuilder.WriteTo(replacementParentPage);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        else if (boundaryChanged && targetParentIndex < root.Cells.Count)
        {
            try
            {
                var rootBuilder = new SqliteTableInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    root.Header.RightMostChildPage);
                for (var cellIndex = 0; cellIndex < root.Cells.Count; cellIndex++)
                {
                    var cell = root.Cells[cellIndex].Cell;
                    rootBuilder.Append(cellIndex == targetParentIndex
                        ? SqliteTableInteriorCell.Create(targetParentPage, replacementCells[^1].RowId)
                        : cell);
                }

                replacementRootPage = existingRootPage.ToArray();
                rootBuilder.WriteTo(replacementRootPage);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (replacementParentPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(targetParentPage, sourceParentPage));
            writeImages.Add(new SqlitePageImage(targetParentPage, replacementParentPage));
        }
        if (replacementRootPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(rootPage, existingRootPage));
            writeImages.Add(new SqlitePageImage(rootPage, replacementRootPage));
        }

        // Page one remains the WAL commit frame, after every routing image.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedTableInteriorThirdLevelLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var root = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        if (root.Cells.Count == 0)
            return false;

        var rootChildPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .ToArray();
        if (rootChildPages.Length != root.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, rootPage };
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        byte[]? sourceParentPage = null;
        SqliteTableInteriorPageView? targetParent = null;
        uint targetParentPage = 0;
        var targetLeafIndex = -1;
        byte[]? sourceGrandparentPage = null;
        SqliteTableInteriorPageView? targetGrandparent = null;
        uint targetGrandparentPage = 0;
        var targetParentIndex = -1;
        var targetGrandparentIndex = -1;

        for (var grandparentIndex = 0;
             grandparentIndex < rootChildPages.Length;
             grandparentIndex++)
        {
            var grandparentPage = rootChildPages[grandparentIndex];
            if (grandparentPage < 2
                || grandparentPage > sourcePageCount
                || !ownedPages.Add(grandparentPage))
            {
                return false;
            }

            var grandparentImage = _pager.ReadCommittedPage(grandparentPage);
            if (SqliteBtreePageHeader.Parse(grandparentImage).PageType
                != SqliteBtreePageType.TableInterior)
            {
                return false;
            }

            var grandparent = SqliteTableInteriorPageView.Parse(grandparentImage, _usableSpace);
            if (grandparent.Cells.Count == 0)
                return false;

            var parentPages = grandparent.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(grandparent.Header.RightMostChildPage)
                .ToArray();
            if (parentPages.Length != grandparent.Cells.Count + 1)
                return false;

            long? grandparentMinimumRowId = null;
            long? grandparentMaximumRowId = null;
            for (var parentIndex = 0; parentIndex < parentPages.Length; parentIndex++)
            {
                var parentPage = parentPages[parentIndex];
                if (parentPage < 2
                    || parentPage > sourcePageCount
                    || !ownedPages.Add(parentPage))
                {
                    return false;
                }

                var parentImage = _pager.ReadCommittedPage(parentPage);
                if (SqliteBtreePageHeader.Parse(parentImage).PageType
                    != SqliteBtreePageType.TableInterior)
                {
                    return false;
                }

                var parent = SqliteTableInteriorPageView.Parse(parentImage, _usableSpace);
                if (parent.Cells.Count == 0)
                    return false;

                var leafPages = parent.Cells
                    .Select(cell => cell.Cell.LeftChildPage)
                    .Append(parent.Header.RightMostChildPage)
                    .ToArray();
                if (leafPages.Length != parent.Cells.Count + 1)
                    return false;

                long? parentMinimumRowId = null;
                long? parentMaximumRowId = null;
                for (var leafIndex = 0; leafIndex < leafPages.Length; leafIndex++)
                {
                    var leafPage = leafPages[leafIndex];
                    if (leafPage < 2
                        || leafPage > sourcePageCount
                        || !ownedPages.Add(leafPage))
                    {
                        return false;
                    }

                    var leafImage = _pager.ReadCommittedPage(leafPage);
                    if (SqliteBtreePageHeader.Parse(leafImage).PageType
                        != SqliteBtreePageType.TableLeaf)
                    {
                        return false;
                    }

                    var leaf = SqliteTableLeafPageView.Parse(leafImage, _usableSpace);
                    if (leaf.Cells.Count == 0
                        || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                        || (previousMaximumRowId is { } maximumRowId
                            && leaf.Cells[0].Cell.RowId <= maximumRowId)
                        || !parent.IsChildRangeValid(
                            leafIndex,
                            leaf.Cells[0].Cell.RowId,
                            leaf.Cells[^1].Cell.RowId))
                    {
                        return false;
                    }

                    foreach (var cell in leaf.Cells)
                    {
                        if (persistedCellIndex >= persistedCells.Count
                            || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                            || !cell.Cell.LocalPayload.Span.SequenceEqual(
                                persistedCells[persistedCellIndex].Record))
                        {
                            return false;
                        }

                        persistedCellIndex++;
                    }

                    if (leaf.Search(change.RowId).IsExact)
                    {
                        if (sourceLeaf is not null)
                            return false;

                        sourceLeaf = leaf;
                        sourceLeafPage = leafImage;
                        targetLeafPage = leafPage;
                        sourceParentPage = parentImage;
                        targetParent = parent;
                        targetParentPage = parentPage;
                        targetLeafIndex = leafIndex;
                        sourceGrandparentPage = grandparentImage;
                        targetGrandparent = grandparent;
                        targetGrandparentPage = grandparentPage;
                        targetParentIndex = parentIndex;
                        targetGrandparentIndex = grandparentIndex;
                    }

                    parentMinimumRowId ??= leaf.Cells[0].Cell.RowId;
                    parentMaximumRowId = leaf.Cells[^1].Cell.RowId;
                    previousMaximumRowId = parentMaximumRowId;
                }

                if (parentMinimumRowId is null
                    || parentMaximumRowId is null
                    || !grandparent.IsChildRangeValid(
                        parentIndex,
                        parentMinimumRowId.Value,
                        parentMaximumRowId.Value))
                {
                    return false;
                }

                grandparentMinimumRowId ??= parentMinimumRowId.Value;
                grandparentMaximumRowId = parentMaximumRowId;
            }

            if (grandparentMinimumRowId is null
                || grandparentMaximumRowId is null
                || !root.IsChildRangeValid(
                    grandparentIndex,
                    grandparentMinimumRowId.Value,
                    grandparentMaximumRowId.Value))
            {
                return false;
            }
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || sourceParentPage is null
            || targetParent is null
            || targetLeafPage == 0
            || targetParentPage == 0
            || targetLeafIndex < 0
            || sourceGrandparentPage is null
            || targetGrandparent is null
            || targetGrandparentPage == 0
            || targetParentIndex < 0
            || targetGrandparentIndex < 0)
        {
            return false;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementParentPage = null;
        byte[]? replacementGrandparentPage = null;
        byte[]? replacementRootPage = null;
        var boundaryChanged = change.IsDelete
            && sourceLeaf.Cells[^1].Cell.RowId == change.RowId;
        if (boundaryChanged)
        {
            if (targetLeafIndex < targetParent.Cells.Count)
            {
                if (!TryReplaceTableInteriorSeparator(
                        sourceParentPage,
                        targetParent,
                        targetLeafIndex,
                        targetLeafPage,
                        replacementCells[^1].RowId,
                        out replacementParentPage))
                {
                    return false;
                }
            }
            else if (targetParentIndex < targetGrandparent.Cells.Count)
            {
                if (!TryReplaceTableInteriorSeparator(
                        sourceGrandparentPage,
                        targetGrandparent,
                        targetParentIndex,
                        targetParentPage,
                        replacementCells[^1].RowId,
                        out replacementGrandparentPage))
                {
                    return false;
                }
            }
            else if (targetGrandparentIndex < root.Cells.Count
                     && !TryReplaceTableInteriorSeparator(
                         existingRootPage,
                         root,
                         targetGrandparentIndex,
                         targetGrandparentPage,
                         replacementCells[^1].RowId,
                         out replacementRootPage))
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (replacementParentPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(targetParentPage, sourceParentPage));
            writeImages.Add(new SqlitePageImage(targetParentPage, replacementParentPage));
        }
        if (replacementGrandparentPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(targetGrandparentPage, sourceGrandparentPage));
            writeImages.Add(new SqlitePageImage(targetGrandparentPage, replacementGrandparentPage));
        }
        if (replacementRootPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(rootPage, existingRootPage));
            writeImages.Add(new SqlitePageImage(rootPage, replacementRootPage));
        }

        // The catalog page carries the WAL commit marker after every reachable page.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistValidatedTableInteriorArbitraryDepthLeafInsertion(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleInsertionCell(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var insertedCell))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage };
        if (rootPage < 2 || rootPage > sourcePageCount || !ownedPages.Add(rootPage))
            return false;

        var persistedCellIndex = 0;
        long? previousRowId = null;
        int? leafDepth = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        var targetInsertionIndex = -1;
        var targetInteriorDepth = 0;

        bool Visit(
            uint pageNumber,
            byte[] pageImage,
            int depth,
            bool containsInsertionRoute,
            out long minimumRowId,
            out long maximumRowId)
        {
            minimumRowId = 0;
            maximumRowId = 0;
            switch (SqliteBtreePageHeader.Parse(pageImage).PageType)
            {
                case SqliteBtreePageType.TableLeaf:
                    {
                        var leaf = SqliteTableLeafPageView.Parse(pageImage, _usableSpace);
                        if (leaf.Cells.Count == 0
                            || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                            || (leafDepth is { } expectedDepth && expectedDepth != depth))
                        {
                            return false;
                        }

                        leafDepth ??= depth;
                        foreach (var cell in leaf.Cells)
                        {
                            if (persistedCellIndex >= persistedCells.Count
                                || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                                || !cell.Cell.LocalPayload.Span.SequenceEqual(
                                    persistedCells[persistedCellIndex].Record)
                                || (previousRowId is { } previous && cell.Cell.RowId <= previous))
                            {
                                return false;
                            }

                            persistedCellIndex++;
                            previousRowId = cell.Cell.RowId;
                        }

                        var insertion = leaf.Search(insertedCell.RowId);
                        if (containsInsertionRoute
                            && !insertion.IsExact
                            && insertion.Index < leaf.Cells.Count
                            && insertedCell.RowId < leaf.Cells[^1].Cell.RowId)
                        {
                            if (sourceLeaf is not null)
                                return false;

                            sourceLeaf = leaf;
                            sourceLeafPage = pageImage;
                            targetLeafPage = pageNumber;
                            targetInsertionIndex = insertion.Index;
                            targetInteriorDepth = depth - 1;
                        }

                        minimumRowId = leaf.Cells[0].Cell.RowId;
                        maximumRowId = leaf.Cells[^1].Cell.RowId;
                        return true;
                    }

                case SqliteBtreePageType.TableInterior:
                    {
                        var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace);
                        if (interior.Cells.Count == 0)
                            return false;

                        var childPages = interior.Cells
                            .Select(cell => cell.Cell.LeftChildPage)
                            .Append(interior.Header.RightMostChildPage)
                            .ToArray();
                        if (childPages.Length != interior.Cells.Count + 1)
                            return false;

                        var insertionChildIndex = containsInsertionRoute
                            ? interior.SearchChild(insertedCell.RowId).ChildIndex
                            : -1;
                        long? childMinimumRowId = null;
                        long? childMaximumRowId = null;
                        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                        {
                            var childPage = childPages[childIndex];
                            if (childPage < 2
                                || childPage > sourcePageCount
                                || !ownedPages.Add(childPage))
                            {
                                return false;
                            }

                            if (!Visit(
                                    childPage,
                                    _pager.ReadCommittedPage(childPage),
                                    depth + 1,
                                    containsInsertionRoute && childIndex == insertionChildIndex,
                                    out var childMinimum,
                                    out var childMaximum))
                            {
                                return false;
                            }
                            if (!interior.IsChildRangeValid(
                                    childIndex,
                                    childMinimum,
                                    childMaximum))
                            {
                                return false;
                            }

                            childMinimumRowId ??= childMinimum;
                            childMaximumRowId = childMaximum;
                        }

                        if (childMinimumRowId is null || childMaximumRowId is null)
                            return false;

                        minimumRowId = childMinimumRowId.Value;
                        maximumRowId = childMaximumRowId.Value;
                        return true;
                    }

                default:
                    return false;
            }
        }

        if (!Visit(
                rootPage,
                existingRootPage.ToArray(),
                depth: 1,
                containsInsertionRoute: true,
                out _,
                out _)
            || persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || targetLeafPage == 0
            || targetInsertionIndex < 0
            || targetInteriorDepth <= 3)
        {
            return false;
        }

        var replacementCells = new List<SqliteTableLeafCell>(sourceLeaf.Cells.Count + 1);
        for (var cellIndex = 0; cellIndex < sourceLeaf.Cells.Count; cellIndex++)
        {
            if (cellIndex == targetInsertionIndex)
                replacementCells.Add(insertedCell);
            replacementCells.Add(sourceLeaf.Cells[cellIndex].Cell);
        }
        if (targetInsertionIndex == sourceLeaf.Cells.Count)
            return false;

        if (!TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(targetLeafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(targetLeafPage, replacementLeafPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistValidatedTableInteriorArbitraryDepthLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage };
        if (rootPage < 2 || rootPage > sourcePageCount || !ownedPages.Add(rootPage))
            return false;

        var persistedCellIndex = 0;
        long? previousRowId = null;
        int? leafDepth = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        BoundedTableInteriorPathEntry[] targetPath = [];

        bool Visit(
            uint pageNumber,
            byte[] pageImage,
            List<BoundedTableInteriorPathEntry> path,
            int depth,
            out long minimumRowId,
            out long maximumRowId)
        {
            minimumRowId = 0;
            maximumRowId = 0;
            switch (SqliteBtreePageHeader.Parse(pageImage).PageType)
            {
                case SqliteBtreePageType.TableLeaf:
                    {
                        var leaf = SqliteTableLeafPageView.Parse(pageImage, _usableSpace);
                        if (leaf.Cells.Count == 0
                            || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                            || (leafDepth is { } expectedDepth && expectedDepth != depth))
                        {
                            return false;
                        }

                        leafDepth ??= depth;
                        foreach (var cell in leaf.Cells)
                        {
                            if (persistedCellIndex >= persistedCells.Count
                                || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                                || !cell.Cell.LocalPayload.Span.SequenceEqual(
                                    persistedCells[persistedCellIndex].Record)
                                || (previousRowId is { } previous && cell.Cell.RowId <= previous))
                            {
                                return false;
                            }

                            persistedCellIndex++;
                            previousRowId = cell.Cell.RowId;
                        }

                        if (leaf.Search(change.RowId).IsExact)
                        {
                            if (sourceLeaf is not null)
                                return false;

                            sourceLeaf = leaf;
                            sourceLeafPage = pageImage;
                            targetLeafPage = pageNumber;
                            targetPath = path.ToArray();
                        }

                        minimumRowId = leaf.Cells[0].Cell.RowId;
                        maximumRowId = leaf.Cells[^1].Cell.RowId;
                        return true;
                    }

                case SqliteBtreePageType.TableInterior:
                    {
                        var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace);
                        if (interior.Cells.Count == 0)
                            return false;

                        var childPages = interior.Cells
                            .Select(cell => cell.Cell.LeftChildPage)
                            .Append(interior.Header.RightMostChildPage)
                            .ToArray();
                        if (childPages.Length != interior.Cells.Count + 1)
                            return false;

                        long? childMinimumRowId = null;
                        long? childMaximumRowId = null;
                        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                        {
                            var childPage = childPages[childIndex];
                            if (childPage < 2
                                || childPage > sourcePageCount
                                || !ownedPages.Add(childPage))
                            {
                                return false;
                            }

                            var childImage = _pager.ReadCommittedPage(childPage);
                            path.Add(new BoundedTableInteriorPathEntry(
                                pageNumber,
                                pageImage,
                                interior,
                                childIndex,
                                childPage));
                            if (!Visit(
                                    childPage,
                                    childImage,
                                    path,
                                    depth + 1,
                                    out var childMinimum,
                                    out var childMaximum))
                            {
                                path.RemoveAt(path.Count - 1);
                                return false;
                            }

                            path.RemoveAt(path.Count - 1);
                            if (!interior.IsChildRangeValid(
                                    childIndex,
                                    childMinimum,
                                    childMaximum))
                            {
                                return false;
                            }

                            childMinimumRowId ??= childMinimum;
                            childMaximumRowId = childMaximum;
                        }

                        if (childMinimumRowId is null || childMaximumRowId is null)
                            return false;

                        minimumRowId = childMinimumRowId.Value;
                        maximumRowId = childMaximumRowId.Value;
                        return true;
                    }

                default:
                    return false;
            }
        }

        if (!Visit(rootPage, existingRootPage.ToArray(), [], depth: 1, out _, out _)
            || persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || targetLeafPage == 0
            // The narrower paths above own roots with up to three interior levels.
            || targetPath.Length <= 3)
        {
            return false;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementSeparatorOwnerPage = null;
        BoundedTableInteriorPathEntry? separatorOwner = null;
        if (change.IsDelete && sourceLeaf.Cells[^1].Cell.RowId == change.RowId)
        {
            if (!TryFindValidatedTableInteriorSeparatorOwner(targetPath, out separatorOwner))
                return false;

            if (separatorOwner is not null
                && !TryReplaceTableInteriorSeparator(
                    separatorOwner.SourcePage,
                    separatorOwner.Page,
                    separatorOwner.ChildIndex,
                    separatorOwner.ChildPage,
                    replacementCells[^1].RowId,
                    out replacementSeparatorOwnerPage))
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (separatorOwner is not null)
        {
            sourcePages.Add(new SqlitePageImage(
                separatorOwner.PageNumber,
                separatorOwner.SourcePage));
            writeImages.Add(new SqlitePageImage(
                separatorOwner.PageNumber,
                replacementSeparatorOwnerPage!));
        }

        // Page one is the final WAL frame after every changed tree page.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private static bool TryFindValidatedTableInteriorSeparatorOwner(
        IReadOnlyList<BoundedTableInteriorPathEntry> path,
        out BoundedTableInteriorPathEntry? separatorOwner)
    {
        separatorOwner = null;
        for (var pathIndex = path.Count - 1; pathIndex >= 0; pathIndex--)
        {
            var candidate = path[pathIndex];
            if (candidate.ChildIndex < 0 || candidate.ChildIndex > candidate.Page.Cells.Count)
                return false;

            if (candidate.ChildIndex == candidate.Page.Cells.Count)
            {
                if (candidate.Page.Header.RightMostChildPage != candidate.ChildPage)
                    return false;

                continue;
            }

            if (candidate.Page.Cells[candidate.ChildIndex].Cell.LeftChildPage != candidate.ChildPage)
                return false;

            separatorOwner = candidate;
            return true;
        }

        return true;
    }

    private bool TryReplaceTableInteriorSeparator(
        ReadOnlySpan<byte> sourcePage,
        SqliteTableInteriorPageView parent,
        int separatorIndex,
        uint leftChildPage,
        long separatorRowId,
        out byte[] replacementPage)
    {
        replacementPage = null!;
        if (separatorIndex < 0
            || separatorIndex >= parent.Cells.Count
            || parent.Cells[separatorIndex].Cell.LeftChildPage != leftChildPage)
        {
            return false;
        }

        try
        {
            var builder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                parent.Header.RightMostChildPage);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                var cell = parent.Cells[cellIndex].Cell;
                builder.Append(cellIndex == separatorIndex
                    ? SqliteTableInteriorCell.Create(leftChildPage, separatorRowId)
                    : cell);
            }

            replacementPage = sourcePage.ToArray();
            builder.WriteTo(replacementPage);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryPersistBoundedTableInteriorRootDirectLeafInsertion(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleInsertionCell(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var insertedCell))
        {
            return false;
        }

        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length == 0)
            return false;

        var ownedPages = new HashSet<uint> { rootPage };
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        var insertedChildIndex = parent.SearchChild(insertedCell.RowId).ChildIndex;
        SqliteTableLeafPageView? sourceLeaf = null;
        byte[]? sourceLeafPage = null;
        uint targetLeafPage = 0;

        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childImage).PageType != SqliteBtreePageType.TableLeaf)
                return false;

            var childLeaf = SqliteTableLeafPageView.Parse(childImage, _usableSpace);
            if (childLeaf.Cells.Count == 0
                || childLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                || (previousMaximumRowId is { } previousMaximum
                    && childLeaf.Cells[0].Cell.RowId <= previousMaximum)
                || !parent.IsChildRangeValid(
                    childIndex,
                    childLeaf.Cells[0].Cell.RowId,
                    childLeaf.Cells[^1].Cell.RowId))
            {
                return false;
            }

            var childMaximumRowId = childLeaf.Cells[^1].Cell.RowId;

            foreach (var cell in childLeaf.Cells)
            {
                if (persistedCellIndex >= persistedCells.Count
                    || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                    || !cell.Cell.LocalPayload.Span.SequenceEqual(
                        persistedCells[persistedCellIndex].Record))
                {
                    return false;
                }

                persistedCellIndex++;
            }

            if (childIndex == insertedChildIndex)
            {
                if (sourceLeaf is not null)
                    return false;

                sourceLeaf = childLeaf;
                sourceLeafPage = childImage;
                targetLeafPage = childPage;
            }

            previousMaximumRowId = childMaximumRowId;
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || targetLeafPage == 0)
        {
            return false;
        }

        var insertion = sourceLeaf.Search(insertedCell.RowId);
        if (insertion.IsExact)
            return false;

        var replacementCells = sourceLeaf.Cells.Select(cell => cell.Cell).ToList();
        replacementCells.Insert(insertion.Index, insertedCell);
        if (!TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        return CommitBoundedTableInteriorRootLeafMutation(
            targetLeafPage,
            sourceLeafPage,
            replacementLeafPage,
            schemaPage,
            currentHeader);
    }

    private bool TryPersistBoundedTableInteriorRootSingleLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length == 0)
            return false;

        SqliteTableLeafPageView? sourceLeaf = null;
        byte[]? sourceLeafPage = null;
        uint targetLeafPage = 0;
        var targetChildIndex = -1;
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage == rootPage
                || childPage > sourcePageCount)
            {
                return false;
            }

            var childImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childImage).PageType != SqliteBtreePageType.TableLeaf)
                return false;

            var childLeaf = SqliteTableLeafPageView.Parse(childImage, _usableSpace);
            if (childLeaf.Cells.Count == 0
                || childLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                || (previousMaximumRowId is { } maximumRowId
                    && childLeaf.Cells[0].Cell.RowId <= maximumRowId)
                || !parent.IsChildRangeValid(
                    childIndex,
                    childLeaf.Cells[0].Cell.RowId,
                    childLeaf.Cells[^1].Cell.RowId))
            {
                return false;
            }

            foreach (var cell in childLeaf.Cells)
            {
                if (persistedCellIndex >= persistedCells.Count
                    || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                    || !cell.Cell.LocalPayload.Span.SequenceEqual(
                        persistedCells[persistedCellIndex].Record))
                {
                    return false;
                }

                persistedCellIndex++;
            }

            if (childLeaf.Search(change.RowId).IsExact)
            {
                if (sourceLeaf is not null)
                    return false;

                sourceLeaf = childLeaf;
                sourceLeafPage = childImage;
                targetLeafPage = childPage;
                targetChildIndex = childIndex;
            }

            previousMaximumRowId = childLeaf.Cells[^1].Cell.RowId;
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || targetLeafPage == 0
            || targetChildIndex < 0)
        {
            return false;
        }

        if (change.IsDelete
            && TryPersistBoundedTableInteriorRootCollapse(
                rootPage,
                schemaPage,
                existingRootPage,
                currentHeader,
                parent,
                childPages,
                persistedCells,
                change))
        {
            return true;
        }

        if (change.IsDelete
            && sourceLeaf.Cells.Count == 1
            && TryPersistBoundedTableInteriorRootEmptyChildRemoval(
                rootPage,
                schemaPage,
                existingRootPage,
                currentHeader,
                parent,
                childPages,
                targetChildIndex,
                targetLeafPage,
                sourceLeafPage))
        {
            return true;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementRootPage = null;
        if (change.IsDelete
            && targetChildIndex < parent.Cells.Count
            && parent.Cells[targetChildIndex].Cell.RowId == change.RowId)
        {
            try
            {
                var parentBuilder = new SqliteTableInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    parent.Header.RightMostChildPage);
                for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
                {
                    var cell = parent.Cells[cellIndex].Cell;
                    parentBuilder.Append(cellIndex == targetChildIndex
                        ? SqliteTableInteriorCell.Create(targetLeafPage, replacementCells[^1].RowId)
                        : cell);
                }

                replacementRootPage = existingRootPage.ToArray();
                parentBuilder.WriteTo(replacementRootPage);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (replacementRootPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(rootPage, existingRootPage));
            writeImages.Add(new SqlitePageImage(rootPage, replacementRootPage));
        }

        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootEmptyChildRemoval(
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader,
        SqliteTableInteriorPageView parent,
        IReadOnlyList<uint> childPages,
        int targetChildIndex,
        uint targetLeafPage,
        ReadOnlySpan<byte> sourceLeafPage)
    {
        if (parent.Cells.Count < 2
            || childPages.Count != parent.Cells.Count + 1
            || childPages.Count < 3
            || targetChildIndex < 0
            || targetChildIndex >= childPages.Count
            || childPages[targetChildIndex] != targetLeafPage)
        {
            return false;
        }

        byte[] replacementRootPage;
        try
        {
            var removedSeparatorIndex = targetChildIndex == parent.Cells.Count
                ? parent.Cells.Count - 1
                : targetChildIndex;
            var rightMostChildPage = targetChildIndex == parent.Cells.Count
                ? parent.Cells[^1].Cell.LeftChildPage
                : parent.Header.RightMostChildPage;
            var parentBuilder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                rightMostChildPage);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex != removedSeparatorIndex)
                    parentBuilder.Append(parent.Cells[cellIndex].Cell);
            }

            replacementRootPage = existingRootPage.ToArray();
            parentBuilder.WriteTo(replacementRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        SqliteFreelist retiredChild;
        try
        {
            retiredChild = SqliteFreelist.CreateFromFreePages(
                currentHeader.DatabaseSizeInPages,
                [targetLeafPage],
                _pageSize,
                _usableSpace);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (retiredChild.PageCount != 1
            || retiredChild.FirstTrunkPage != targetLeafPage
            || retiredChild.PageNumbers.Count != 1
            || retiredChild.PageNumbers[0] != targetLeafPage
            || retiredChild.TrunkPageNumbers.Count != 1
            || retiredChild.TrunkPageNumbers[0] != targetLeafPage
            || retiredChild.PageImages.Count != 1
            || retiredChild.PageImages[0].PageNumber != targetLeafPage)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            FirstFreelistTrunkPage = retiredChild.FirstTrunkPage,
            FreelistPageCount = retiredChild.PageCount,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            currentHeader.DatabaseSizeInPages,
            currentHeader.DatabaseSizeInPages,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(rootPage, existingRootPage),
                new SqlitePageImage(targetLeafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(rootPage, replacementRootPage),
                retiredChild.PageImages[0],
                // Page one publishes both the replacement root and retired child.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootCollapse(
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader,
        SqliteTableInteriorPageView parent,
        IReadOnlyList<uint> childPages,
        IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        BoundedSingleLeafChange change)
    {
        if (!change.IsDelete
            || parent.Cells.Count != 1
            || childPages.Count != 2
            || persistedCells.Count < 2)
        {
            return false;
        }

        var replacementCells = new List<SqliteTableLeafCell>(persistedCells.Count - 1);
        foreach (var (rowId, record) in persistedCells)
        {
            if (rowId == change.RowId)
                continue;

            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
                return false;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(rowId, record, _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count != persistedCells.Count - 1
            || replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementRootPage))
        {
            return false;
        }

        SqliteFreelist retiredChildren;
        try
        {
            retiredChildren = SqliteFreelist.CreateFromFreePages(
                currentHeader.DatabaseSizeInPages,
                childPages,
                _pageSize,
                _usableSpace);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (retiredChildren.PageCount != childPages.Count
            || !retiredChildren.PageNumbers.SequenceEqual(childPages.Order())
            || retiredChildren.PageImages.Count != childPages.Count)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            FirstFreelistTrunkPage = retiredChildren.FirstTrunkPage,
            FreelistPageCount = retiredChildren.PageCount,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(2 + childPages.Count)
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(rootPage, existingRootPage),
        };
        sourcePages.AddRange(childPages.Select(
            childPage => new SqlitePageImage(childPage, _pager.ReadCommittedPage(childPage))));

        var writeImages = new List<SqlitePageImage>(2 + retiredChildren.PageImages.Count)
        {
            new(rootPage, replacementRootPage),
        };
        writeImages.AddRange(retiredChildren.PageImages.OrderBy(image => image.PageNumber));
        // Page one publishes both the leaf root and its retired-child freelist.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            currentHeader.DatabaseSizeInPages,
            currentHeader.DatabaseSizeInPages,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool CommitBoundedTableInteriorRootLeafMutation(
        uint leafPage,
        ReadOnlySpan<byte> sourceLeafPage,
        ReadOnlySpan<byte> replacementLeafPage,
        ReadOnlySpan<byte> schemaPage,
        SqliteDatabaseHeader currentHeader)
    {
        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            currentHeader.DatabaseSizeInPages,
            currentHeader.DatabaseSizeInPages,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(leafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(leafPage, replacementLeafPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryGetBoundedSingleLeafChange(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        out IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        out BoundedSingleLeafChange change)
    {
        persistedCells = EnumerateRowCells(tableName, persistedTable).ToArray();
        change = null!;
        var targetCells = EnumerateRowCells(tableName, table).ToArray();
        if (targetCells.Length == persistedCells.Count)
        {
            for (var index = 0; index < persistedCells.Count; index++)
            {
                if (targetCells[index].RowId != persistedCells[index].RowId)
                    return false;
                if (targetCells[index].Record.AsSpan().SequenceEqual(persistedCells[index].Record))
                    continue;
                if (change is not null)
                    return false;

                var layout = SqlitePayloadLayout.Calculate(
                    SqliteBtreePageType.TableLeaf,
                    checked((ulong)targetCells[index].Record.Length),
                    _usableSpace);
                if (layout.UsesOverflow)
                    return false;

                change = new BoundedSingleLeafChange(
                    targetCells[index].RowId,
                    targetCells[index].Record,
                    IsDelete: false);
            }

            return change is not null;
        }

        if (targetCells.Length != persistedCells.Count - 1)
            return false;

        var persistedIndex = 0;
        for (var targetIndex = 0; targetIndex < targetCells.Length; targetIndex++, persistedIndex++)
        {
            if (targetCells[targetIndex].RowId == persistedCells[persistedIndex].RowId)
            {
                if (!targetCells[targetIndex].Record.AsSpan().SequenceEqual(persistedCells[persistedIndex].Record))
                    return false;
                continue;
            }

            if (change is not null
                || targetCells[targetIndex].RowId <= persistedCells[persistedIndex].RowId)
            {
                return false;
            }

            change = new BoundedSingleLeafChange(
                persistedCells[persistedIndex].RowId,
                ReplacementRecord: null,
                IsDelete: true);
            persistedIndex++;
            if (persistedIndex >= persistedCells.Count
                || targetCells[targetIndex].RowId != persistedCells[persistedIndex].RowId
                || !targetCells[targetIndex].Record.AsSpan().SequenceEqual(persistedCells[persistedIndex].Record))
            {
                return false;
            }
        }

        if (persistedIndex < persistedCells.Count)
        {
            if (change is not null || persistedIndex != persistedCells.Count - 1)
                return false;

            change = new BoundedSingleLeafChange(
                persistedCells[persistedIndex].RowId,
                ReplacementRecord: null,
                IsDelete: true);
            persistedIndex++;
        }

        return persistedIndex == persistedCells.Count && change is not null;
    }

    private bool TryGetBoundedSingleInsertionCell(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        out IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        out SqliteTableLeafCell insertedCell)
    {
        persistedCells = EnumerateRowCells(tableName, persistedTable).ToArray();
        insertedCell = null!;
        var targetCells = EnumerateRowCells(tableName, table).ToArray();
        if (targetCells.Length != persistedCells.Count + 1)
            return false;

        var persistedIndex = 0;
        (long RowId, byte[] Record)? insertion = null;
        for (var targetIndex = 0; targetIndex < targetCells.Length; targetIndex++)
        {
            if (persistedIndex < persistedCells.Count
                && targetCells[targetIndex].RowId == persistedCells[persistedIndex].RowId)
            {
                if (!targetCells[targetIndex].Record.AsSpan().SequenceEqual(
                        persistedCells[persistedIndex].Record))
                {
                    return false;
                }

                persistedIndex++;
                continue;
            }

            if (insertion is not null
                || (persistedIndex < persistedCells.Count
                    && targetCells[targetIndex].RowId > persistedCells[persistedIndex].RowId))
            {
                return false;
            }

            insertion = targetCells[targetIndex];
        }

        if (persistedIndex != persistedCells.Count || insertion is null)
            return false;

        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)insertion.Value.Record.Length),
            _usableSpace);
        if (layout.UsesOverflow)
            return false;

        try
        {
            insertedCell = SqliteTableLeafCell.Create(
                insertion.Value.RowId,
                insertion.Value.Record,
                _usableSpace);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryGetBoundedStrictAppendCell(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        out IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        out SqliteTableLeafCell appendedCell)
    {
        persistedCells = EnumerateRowCells(tableName, persistedTable).ToArray();
        appendedCell = null!;
        var targetCells = EnumerateRowCells(tableName, table).ToArray();
        if (targetCells.Length != persistedCells.Count + 1)
            return false;

        for (var index = 0; index < persistedCells.Count; index++)
        {
            if (targetCells[index].RowId != persistedCells[index].RowId
                || !targetCells[index].Record.AsSpan().SequenceEqual(persistedCells[index].Record))
            {
                return false;
            }
        }

        if (persistedCells.Count != 0
            && targetCells[^1].RowId <= persistedCells[^1].RowId)
        {
            return false;
        }

        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)targetCells[^1].Record.Length),
            _usableSpace);
        if (layout.UsesOverflow)
            return false;

        try
        {
            appendedCell = SqliteTableLeafCell.Create(
                targetCells[^1].RowId,
                targetCells[^1].Record,
                _usableSpace);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryPersistBoundedTableRootLeafPromotion(
        string tableName,
        EmbeddedTable table,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages > uint.MaxValue - 3)
            return false;

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var leftChildPage = sourcePageCount + 1;
        var rightChildPage = sourcePageCount + 2;
        if (!TryBuildBoundedTableLeafSplitImages(
                tableName,
                table,
                out var leftPage,
                out var rightPage,
                out var separatorRowId))
        {
            return false;
        }

        byte[] promotedRootPage;
        try
        {
            var rootBuilder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                rightChildPage);
            rootBuilder.Append(SqliteTableInteriorCell.Create(leftChildPage, separatorRowId));
            promotedRootPage = existingRootPage.ToArray();
            rootBuilder.WriteTo(promotedRootPage);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = rightChildPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            rightChildPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(rootPage, existingRootPage),
            ],
            [
                new SqlitePageImage(leftChildPage, leftPage),
                new SqlitePageImage(rightChildPage, rightPage),
                new SqlitePageImage(rootPage, promotedRootPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexRootLeafPromotions(
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        IReadOnlyList<(uint PageNumber, byte[] SourcePage, byte[] ReplacementPage)> indexReplacementPages,
        IReadOnlyList<BoundedIndexRootLeafPromotion> indexRootPromotions,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (indexRootPromotions.Count == 0)
            throw new ArgumentException("At least one index root must be promoted.", nameof(indexRootPromotions));

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var promotionCount = checked((uint)indexRootPromotions.Count);
        if (promotionCount > (uint.MaxValue - sourcePageCount) / 2)
            return false;

        var targetPageCount = checked(sourcePageCount + (promotionCount * 2));
        var appendedLeafPages = new List<SqlitePageImage>(checked(indexRootPromotions.Count * 2));
        var promotedRootPages = new List<SqlitePageImage>(indexRootPromotions.Count);
        for (var promotionIndex = 0; promotionIndex < indexRootPromotions.Count; promotionIndex++)
        {
            var promotion = indexRootPromotions[promotionIndex];
            var leftChildPage = checked(sourcePageCount + ((uint)promotionIndex * 2) + 1);
            var rightChildPage = checked(leftChildPage + 1);
            try
            {
                var comparer = new SqliteIndexRecordComparer(_textEncoding);
                var rootBuilder = new SqliteIndexInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    rightChildPage,
                    comparer);
                rootBuilder.Append(
                    SqliteIndexInteriorCell.Create(
                        leftChildPage,
                        promotion.SeparatorRecord,
                        _usableSpace),
                    promotion.SeparatorRecord);
                var promotedRootPage = promotion.SourceRootPage.ToArray();
                rootBuilder.WriteTo(promotedRootPage);
                appendedLeafPages.Add(new SqlitePageImage(leftChildPage, promotion.LeftPage));
                appendedLeafPages.Add(new SqlitePageImage(rightChildPage, promotion.RightPage));
                promotedRootPages.Add(new SqlitePageImage(promotion.RootPage, promotedRootPage));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = targetPageCount,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(
            checked(2 + indexReplacementPages.Count + indexRootPromotions.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, existingTablePage),
        };
        sourcePages.AddRange(indexReplacementPages.Select(
            replacement => new SqlitePageImage(replacement.PageNumber, replacement.SourcePage)));
        sourcePages.AddRange(indexRootPromotions.Select(
            promotion => new SqlitePageImage(promotion.RootPage, promotion.SourceRootPage)));

        var writeImages = new List<SqlitePageImage>(
            checked(appendedLeafPages.Count + 2 + indexReplacementPages.Count + promotedRootPages.Count));
        writeImages.AddRange(appendedLeafPages);
        writeImages.Add(new SqlitePageImage(tableRootPage, replacementTablePage));
        writeImages.AddRange(indexReplacementPages
            .OrderBy(replacement => replacement.PageNumber)
            .Select(replacement => new SqlitePageImage(
                replacement.PageNumber,
                replacement.ReplacementPage)));
        writeImages.AddRange(promotedRootPages.OrderBy(image => image.PageNumber));
        // Page one is the final routing/catalog image and therefore carries the
        // transaction's WAL commit marker after every tree image is durable.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            targetPageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        _header = newHeader;
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        return true;
    }

    private sealed record BoundedIndexRootLeafPromotion(
        uint RootPage,
        byte[] SourceRootPage,
        byte[] LeftPage,
        byte[] RightPage,
        byte[] SeparatorRecord);

    private sealed record BoundedSecondaryIndexInteriorLeafDeletion(
        uint LeafPageNumber,
        byte[] ReplacementLeafPage,
        IReadOnlyList<SqlitePageImage> SourceTreePages);

    private sealed record BoundedSecondaryIndexInteriorLeafInsertion(
        uint LeafPageNumber,
        byte[] ReplacementLeafPage,
        IReadOnlyList<SqlitePageImage> SourceTreePages);

    private sealed record BoundedSingleLeafChange(
        long RowId,
        byte[]? ReplacementRecord,
        bool IsDelete);

    private sealed record BoundedTableInteriorPathEntry(
        uint PageNumber,
        byte[] SourcePage,
        SqliteTableInteriorPageView Page,
        int ChildIndex,
        uint ChildPage);

    private static bool IsBoundedIndexLeafMutationCompatible(IndexDefinition definition)
        => definition.Index.Columns.Count != 0
           && !definition.Index.IsPartial
           && definition.Index.Columns.All(column =>
               !column.IsExpression
               && !column.Descending
               && (column.Collation is null
                   || string.Equals(column.Collation, "BINARY", StringComparison.OrdinalIgnoreCase)));

    private bool HasCurrentSchemaShape(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null)
    {
        if (tables.Count != _tableRootPages.Count
            || tables.Keys.Any(name => !_tableRootPages.ContainsKey(name)))
        {
            return false;
        }

        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexes = GetIndexDefinitions(tableNames, tables, views, triggers, virtualTables, previousTables);
        if (indexes.Count != _indexRootPages.Count
            || indexes.Any(index => !_indexRootPages.ContainsKey(index.Index.Name)))
        {
            return false;
        }

        var entries = BuildSchemaEntries(
            tables,
            views,
            triggers,
            virtualTables,
            _tableRootPages,
            _indexRootPages);
        return string.Equals(
            ComputeSchemaSignature(entries),
            _lastSchemaSignature,
            StringComparison.Ordinal);
    }

    private static bool TryGetSingleChangedTable(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, EmbeddedTable> persistedTables,
        out string tableName,
        out EmbeddedTable table)
    {
        tableName = string.Empty;
        table = null!;
        if (tables.Count != persistedTables.Count)
            return false;

        foreach (var (name, candidate) in tables)
        {
            if (!persistedTables.TryGetValue(name, out var persisted))
                return false;
            if (HaveSameRows(candidate, persisted))
                continue;
            if (table is not null)
                return false;

            tableName = name;
            table = candidate;
        }

        return table is not null;
    }

    private static bool HaveSameRows(EmbeddedTable left, EmbeddedTable right)
    {
        if (left.Rows.Count != right.Rows.Count || left.RowIds.Count != right.RowIds.Count)
            return false;

        for (var rowIndex = 0; rowIndex < left.Rows.Count; rowIndex++)
        {
            if (left.RowIds[rowIndex] != right.RowIds[rowIndex]
                || !left.Rows[rowIndex].AsSpan().SequenceEqual(right.Rows[rowIndex]))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryBuildBoundedTableLeafImage(
        string tableName,
        EmbeddedTable table,
        ReadOnlySpan<byte> existingPage,
        out byte[] replacementPage)
    {
        var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage: false);
        foreach (var (rowId, record) in EnumerateRowCells(tableName, table))
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
            {
                replacementPage = null!;
                return false;
            }

            try
            {
                builder.Append(SqliteTableLeafCell.Create(rowId, record, _usableSpace));
            }
            catch (InvalidOperationException)
            {
                replacementPage = null!;
                return false;
            }
        }

        replacementPage = existingPage.ToArray();
        builder.WriteTo(replacementPage);
        return true;
    }

    private bool TryBuildBoundedTableLeafSplitImages(
        string tableName,
        EmbeddedTable table,
        out byte[] leftPage,
        out byte[] rightPage,
        out long separatorRowId)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRowId = 0;
        var cells = new List<SqliteTableLeafCell>(table.Rows.Count);
        foreach (var (rowId, record) in EnumerateRowCells(tableName, table))
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
                return false;

            try
            {
                cells.Add(SqliteTableLeafCell.Create(rowId, record, _usableSpace));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return TryBuildBoundedTableLeafSplitImages(
            cells,
            out leftPage,
            out rightPage,
            out separatorRowId);
    }

    private bool TryBuildBoundedTableLeafSplitImages(
        IReadOnlyList<SqliteTableLeafCell> cells,
        out byte[] leftPage,
        out byte[] rightPage,
        out long separatorRowId)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRowId = 0;
        if (cells.Count < 2)
            return false;

        for (var leftCellCount = 1; leftCellCount < cells.Count; leftCellCount++)
        {
            if (!TryBuildBoundedTableLeafPage(cells, 0, leftCellCount, out leftPage)
                || !TryBuildBoundedTableLeafPage(
                    cells,
                    leftCellCount,
                    cells.Count - leftCellCount,
                    out rightPage))
            {
                continue;
            }

            separatorRowId = cells[leftCellCount - 1].RowId;
            return true;
        }

        leftPage = null!;
        rightPage = null!;
        return false;
    }

    private bool TryBuildBoundedTableLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        int start,
        int count,
        out byte[] page)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(
                _pageSize,
                _usableSpace,
                isFirstPage: false);
            for (var index = start; index < start + count; index++)
                builder.Append(cells[index]);

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private bool TryBuildBoundedIndexLeafImage(
        IndexDefinition definition,
        ReadOnlySpan<byte> existingPage,
        out byte[] replacementPage)
    {
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var records = BuildIndexRecords(
            definition.TableName,
            definition.Table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, records, comparer);

        var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
        foreach (var record in records)
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
            {
                replacementPage = null!;
                return false;
            }

            try
            {
                builder.Append(SqliteIndexLeafCell.Create(record, _usableSpace), record);
            }
            catch (InvalidOperationException)
            {
                replacementPage = null!;
                return false;
            }
        }

        replacementPage = existingPage.ToArray();
        builder.WriteTo(replacementPage);
        return true;
    }

    private bool TryBuildBoundedIndexRootLeafSplitImages(
        IReadOnlyList<byte[]> records,
        out byte[] leftPage,
        out byte[] rightPage,
        out byte[] separatorRecord)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRecord = null!;
        if (records.Count < 3)
            return false;

        foreach (var record in records)
        {
            if (SqlitePayloadLayout.Calculate(
                    SqliteBtreePageType.IndexLeaf,
                    checked((ulong)record.Length),
                    _usableSpace).UsesOverflow)
            {
                return false;
            }
        }

        var middle = records.Count / 2;
        for (var distance = 0; distance < records.Count; distance++)
        {
            var beforeMiddle = middle - distance;
            if (TryBuildBoundedIndexRootLeafSplitAt(
                    records,
                    beforeMiddle,
                    out leftPage,
                    out rightPage,
                    out separatorRecord))
            {
                return true;
            }

            var afterMiddle = middle + distance;
            if (afterMiddle != beforeMiddle
                && TryBuildBoundedIndexRootLeafSplitAt(
                    records,
                    afterMiddle,
                    out leftPage,
                    out rightPage,
                    out separatorRecord))
            {
                return true;
            }
        }

        leftPage = null!;
        rightPage = null!;
        separatorRecord = null!;
        return false;
    }

    private bool TryBuildBoundedIndexRootLeafSplitAt(
        IReadOnlyList<byte[]> records,
        int separatorIndex,
        out byte[] leftPage,
        out byte[] rightPage,
        out byte[] separatorRecord)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRecord = null!;
        if (separatorIndex <= 0 || separatorIndex >= records.Count - 1
            || !TryBuildBoundedIndexLeafPage(records, 0, separatorIndex, out leftPage)
            || !TryBuildBoundedIndexLeafPage(
                records,
                separatorIndex + 1,
                records.Count - separatorIndex - 1,
                out rightPage))
        {
            return false;
        }

        separatorRecord = records[separatorIndex].ToArray();
        return true;
    }

    private bool TryBuildBoundedIndexLeafPage(
        IReadOnlyList<byte[]> records,
        int start,
        int count,
        out byte[] page)
    {
        try
        {
            var comparer = new SqliteIndexRecordComparer(_textEncoding);
            var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
            for (var index = start; index < start + count; index++)
            {
                var record = records[index];
                builder.Append(SqliteIndexLeafCell.Create(record, _usableSpace), record);
            }

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private bool TryBuildBoundedIndexLeafReplacementPage(
        IReadOnlyList<byte[]> records,
        ReadOnlySpan<byte> sourcePage,
        out byte[] replacementPage)
    {
        try
        {
            var comparer = new SqliteIndexRecordComparer(_textEncoding);
            var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
            foreach (var record in records)
            {
                if (SqlitePayloadLayout.Calculate(
                        SqliteBtreePageType.IndexLeaf,
                        checked((ulong)record.Length),
                        _usableSpace).UsesOverflow)
                {
                    replacementPage = null!;
                    return false;
                }

                builder.Append(SqliteIndexLeafCell.Create(record, _usableSpace), record);
            }

            replacementPage = sourcePage.ToArray();
            builder.WriteTo(replacementPage);
            return true;
        }
        catch (ArgumentException)
        {
            replacementPage = null!;
            return false;
        }
        catch (InvalidOperationException)
        {
            replacementPage = null!;
            return false;
        }
    }

    private void ValidateBoundedUniqueIndexRecords(
        IndexDefinition definition,
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer)
    {
        if (!definition.Index.Unique)
            return;

        SqlValue[]? previousNonNullKey = null;
        foreach (var record in records)
        {
            var values = SqliteRecordCodec.Decode(record, _textEncoding);
            if (values.Length != definition.Index.Columns.Count + 1)
            {
                throw new InvalidDataException(
                    $"SQLite index '{definition.Index.Name}' record has an unexpected column count.");
            }

            var key = values[..^1];
            if (key.Any(value => value.Kind == SqlValueKind.Null))
                continue;

            if (previousNonNullKey is not null
                && comparer.Compare(previousNonNullKey, key) == 0)
            {
                var columns = definition.Index.Columns.Select(
                    column => $"{definition.TableName}.{column.Name}");
                throw new EmbeddedSqlException($"UNIQUE constraint failed: {string.Join(", ", columns)}");
            }

            previousNonNullKey = key;
        }
    }

    private void CheckpointCommittedMutation(bool reclaimTrailingPages)
    {
        try
        {
            _pager.CheckpointToMainStoreAndResetWal(synchronousMode: _synchronousMode);
        }
        catch (SqlitePagerBusyException)
        {
            // A peer connection's pinned durable transaction snapshot (or another
            // in-flight checkpoint) is legitimately holding a WAL read mark right now.
            // This is ordinary SQLite PASSIVE-checkpoint contention, not a maintenance
            // failure: the mutation above is already durable, and the WAL simply keeps
            // the frames around for the next opportunistic checkpoint to reclaim. Do
            // not escalate this to EmbeddedPostCommitMaintenanceException regardless of
            // reclaimTrailingPages, since surfacing it would make an unrelated reader's
            // still-open transaction fail an otherwise-successful peer commit.
        }
        catch (IOException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (UnauthorizedAccessException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (InvalidDataException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (InvalidOperationException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (NotSupportedException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
    }

    private static void ValidateRewritePlan(
        uint targetPageCount,
        PreparedSchemaTree schemaTree,
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, uint> tableRootPages,
        IReadOnlyDictionary<uint, PreparedTableTree> tablePages,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, uint> indexRootPages,
        IReadOnlyDictionary<uint, PreparedIndexTree> indexPages,
        SqliteFreelist freelist)
    {
        var activePages = CollectRewriteActivePages(
            schemaTree,
            tableNames,
            tableRootPages,
            tablePages,
            indexes,
            indexRootPages,
            indexPages);

        foreach (var activePage in activePages)
        {
            if (activePage == 0 || activePage > targetPageCount)
            {
                throw new InvalidOperationException(
                    $"Managed file rewrite active page {activePage} is outside its target range 1..{targetPageCount}.");
            }
        }

        var accountedPages = new HashSet<uint>(activePages);
        foreach (var freePage in freelist.PageNumbers)
        {
            if (freePage < 2 || freePage > targetPageCount)
            {
                throw new InvalidOperationException(
                    $"Managed file rewrite freelist page {freePage} is outside its target range 2..{targetPageCount}.");
            }
            if (!accountedPages.Add(freePage))
                throw new InvalidOperationException($"Managed file rewrite assigns page {freePage} more than once.");
        }

        if (accountedPages.Count != targetPageCount)
        {
            throw new InvalidOperationException(
                $"Managed file rewrite accounts for {accountedPages.Count} pages, but its committed size is {targetPageCount}.");
        }

        var imagePages = new HashSet<uint>();
        foreach (var image in freelist.PageImages)
        {
            if (!imagePages.Add(image.PageNumber)
                || !freelist.PageNumbers.Contains(image.PageNumber))
            {
                throw new InvalidOperationException(
                    $"Managed file rewrite freelist image for page {image.PageNumber} is invalid.");
            }
        }
        if (imagePages.Count != freelist.PageCount)
            throw new InvalidOperationException("Managed file rewrite did not materialize every freelist page.");
    }

    private static HashSet<uint> CollectRewriteActivePages(
        PreparedSchemaTree schemaTree,
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, uint> tableRootPages,
        IReadOnlyDictionary<uint, PreparedTableTree> tablePages,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, uint> indexRootPages,
        IReadOnlyDictionary<uint, PreparedIndexTree> indexPages)
    {
        var activePages = new HashSet<uint> { SchemaRootPage };
        foreach (var interiorPage in schemaTree.InteriorPages)
            AddActivePage(activePages, interiorPage.PageNumber);
        foreach (var leafPage in schemaTree.LeafPages)
            AddActivePage(activePages, leafPage.PageNumber);
        foreach (var overflowPage in schemaTree.OverflowPages)
            AddActivePage(activePages, overflowPage.PageNumber);
        foreach (var name in tableNames)
        {
            var rootPage = tableRootPages[name];
            AddActivePage(activePages, rootPage);
            var tree = tablePages[rootPage];
            foreach (var interiorPage in tree.InteriorPages)
                AddActivePage(activePages, interiorPage.PageNumber);
            foreach (var leafPage in tree.LeafPages)
                AddActivePage(activePages, leafPage.PageNumber);
            foreach (var overflowPage in tree.OverflowPages)
                AddActivePage(activePages, overflowPage.PageNumber);
        }

        foreach (var definition in indexes)
        {
            var rootPage = indexRootPages[definition.Index.Name];
            AddActivePage(activePages, rootPage);
            var tree = indexPages[rootPage];
            foreach (var interiorPage in tree.InteriorPages)
                AddActivePage(activePages, interiorPage.PageNumber);
            foreach (var leafPage in tree.LeafPages)
                AddActivePage(activePages, leafPage.PageNumber);
            foreach (var overflowPage in tree.OverflowPages)
                AddActivePage(activePages, overflowPage.PageNumber);
        }

        return activePages;
    }

    private static IEnumerable<uint> EnumerateFreePages(uint targetPageCount, ISet<uint> activePages)
    {
        for (var pageNumber = 2U; pageNumber <= targetPageCount; pageNumber++)
        {
            if (!activePages.Contains(pageNumber))
                yield return pageNumber;
            if (pageNumber == uint.MaxValue)
                yield break;
        }
    }

    private static void AddActivePage(ISet<uint> activePages, uint pageNumber)
    {
        if (pageNumber == 0)
            throw new InvalidOperationException("Managed file rewrite cannot assign SQLite page zero.");
        if (!activePages.Add(pageNumber))
            throw new InvalidOperationException($"Managed file rewrite assigns active page {pageNumber} more than once.");
    }

    /// <summary>Flushes the committed view into the main file and releases resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        _disposed = true;
        Release();
    }

    private EmbeddedPostCommitMaintenanceException RecordPostCommitMaintenanceFailure(Exception exception)
    {
        _postCommitMaintenanceFailure = exception;
        return new EmbeddedPostCommitMaintenanceException(exception);
    }

    private void ThrowIfPostCommitMaintenanceFaulted()
    {
        if (_postCommitMaintenanceFailure is not null)
        {
            throw new InvalidOperationException(
                "A prior managed database mutation committed successfully, but post-commit checkpoint maintenance failed. "
                + "Dispose and reopen the database before another write.",
                _postCommitMaintenanceFailure);
        }
    }

    /// <summary>
    /// Reports whether <paramref name="table"/>'s row storage — <see cref="RowStore.LineageId"/>
    /// and <see cref="RowStore.Revision"/> — is unchanged relative to the entry recorded for
    /// <paramref name="name"/> in <paramref name="previousTables"/>, and returns that previous
    /// entry via <paramref name="previous"/> for a caller to then check individual indexes
    /// against (see <see cref="IsIndexUnchangedFromPrevious(EmbeddedIndex,EmbeddedTable?)"/>).
    /// Deliberately says nothing about the table's current index *list*: a sibling
    /// <c>CREATE INDEX</c>/<c>DROP INDEX</c> only ever appends to or removes an entry from
    /// <see cref="EmbeddedTable.Indexes"/> without touching the table's row storage, so gating
    /// this proof on the index list being identical (as an earlier version of this method did)
    /// would defeat every pre-existing index's preservation/deferred-validation exemption the
    /// moment any one index on the same table was added or dropped — forcing an unrelated
    /// <c>CREATE INDEX new_binary ON t(...)</c> to rebuild (and therefore require a working
    /// comparator for) every other index on <c>t</c>, including one whose collation is not
    /// currently available.
    /// </summary>
    /// <remarks>
    /// The working catalog for an ordinary statement is built via
    /// <c>SchemaCatalog.Clone()</c>, which unconditionally deep-clones every table (so a
    /// failed/rolled-back statement never mutates the published catalog) — even tables
    /// the statement never touches. That means <paramref name="table"/> is essentially
    /// never reference-identical to the previously-recorded entry even when nothing about
    /// it changed, so a plain reference check alone would defeat this exemption for every
    /// commit. <see cref="EmbeddedTable.Clone"/> preserves the source's exact
    /// <see cref="RowStore.Revision"/> *and* <see cref="RowStore.LineageId"/> (rather than
    /// rebuilding the row store row by row), so comparing those cheap, already-tracked signals
    /// detects "row storage genuinely untouched since the previous commit" without an
    /// O(row count) content diff.
    /// <para>
    /// Revision equality alone is not collision-resistant: a full table rebuild (e.g.
    /// <c>EmbeddedTable.CreateWithAlteredColumn</c> for <c>ALTER TABLE ... ALTER COLUMN</c>,
    /// or DROP/ADD COLUMN) constructs a brand-new <see cref="RowStore"/> and re-adds every
    /// existing row into it via <see cref="RowStore.Add"/> — so a table with N rows and no
    /// prior updates/deletes ends up with the identical Revision (== N) both before and after
    /// the rebuild, even though every row's bytes may have changed under a column's new
    /// affinity/type. Requiring <see cref="RowStore.LineageId"/> equality as well closes that
    /// gap: it can only ever match when the exact same in-memory RowStore was carried forward
    /// (Clone), never across a rebuild that constructs a new one.
    /// </para>
    /// </remarks>
    private static bool IsTableRowStorageUnchangedFromPrevious(
        string name,
        EmbeddedTable table,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables,
        out EmbeddedTable? previous)
    {
        if (previousTables is null || !previousTables.TryGetValue(name, out previous))
        {
            previous = null;
            return false;
        }

        return ReferenceEquals(previous, table)
            || (table.Rows.LineageId == previous.Rows.LineageId
                && table.Rows.Revision == previous.Rows.Revision);
    }

    /// <summary>
    /// Reports whether <paramref name="index"/> — one specific index, not its owning table's
    /// entire index list — is the exact same <see cref="EmbeddedIndex"/> instance carried
    /// forward on <paramref name="previous"/> (the table entry recorded at the previous
    /// commit; see <see cref="IsTableRowStorageUnchangedFromPrevious"/>). A brand-new index
    /// added by this statement's own <c>CREATE INDEX</c> (<c>table.Indexes.Add(definition)</c>
    /// in EmbeddedDatabase.cs) is never present by reference in <paramref name="previous"/>'s
    /// list and so correctly reports "changed" here even while every pre-existing index on the
    /// same table — carried forward unmodified — still reports "unchanged". Combined with
    /// row-storage-unchanged, this is what proves a specific index's already-committed b-tree
    /// is still exactly what was durably written for it, independent of whether some other
    /// index on the same table was just added or dropped.
    /// </summary>
    private static bool IsIndexUnchangedFromPrevious(EmbeddedIndex index, EmbeddedTable? previous)
        => previous is not null
            && previous.Indexes.Contains(index, ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Combines <see cref="IsTableRowStorageUnchangedFromPrevious"/> and
    /// <see cref="IsIndexUnchangedFromPrevious(EmbeddedIndex,EmbeddedTable?)"/> into the single
    /// per-index "is this index's committed b-tree still trustworthy/preservable" proof callers
    /// need. See both for why this is scoped to one index rather than the table as a whole.
    /// </summary>
    private static bool IsIndexUnchangedFromPrevious(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables)
        => IsTableRowStorageUnchangedFromPrevious(tableName, table, previousTables, out var previous)
            && IsIndexUnchangedFromPrevious(index, previous);

    private void ValidateTableRepresentable(
        string name,
        EmbeddedTable table,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null)
    {
        if (table.WithoutRowid)
        {
            _ = ValidateWithoutRowidTableRepresentable(name, table, previousTables);
            return;
        }

        if (table.TableLevelPrimaryKey is not null && !table.HasRowidAlias)
        {
            ValidatePrimaryKeyIndexPrerequisites(
                name,
                table,
                "a table-level PRIMARY KEY",
                allowDescending: true,
                allowBuiltInCollations: true);
        }

        var columns = table.ColumnDefinitions;
        var primaryKeyCount = 0;
        var primaryKeyIndex = -1;
        for (var index = 0; index < columns.Length; index++)
        {
            if (columns[index].PrimaryKey)
            {
                primaryKeyCount++;
                primaryKeyIndex = index;
            }
        }

        if (primaryKeyCount > 1)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{name}' because its catalog marks multiple columns as PRIMARY KEY, which is an inconsistent table definition.");
        }

        // A single non-rowid-alias PRIMARY KEY (TEXT, composite-capable, or INTEGER ...
        // DESC) is persisted through the implicit sqlite_autoindex index that the
        // catalog already materializes in table.Indexes; the generic index validation
        // below covers it. Only a rowid-alias INTEGER PRIMARY KEY, which has no backing
        // index, needs its values checked directly here.
        if (primaryKeyCount == 1 && table.RowidAliasColumnIndex >= 0)
        {
            var seen = new HashSet<long>();
            foreach (var row in table.Rows)
            {
                var value = row[primaryKeyIndex];
                if (value.Kind != SqlValueKind.Integer)
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because its INTEGER PRIMARY KEY column '{columns[primaryKeyIndex].Name}' contains a non-integer value; rowid aliases must be distinct non-null integers.");
                }

                if (!seen.Add(value.AsInteger()))
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because its INTEGER PRIMARY KEY column '{columns[primaryKeyIndex].Name}' contains duplicate values.");
                }
            }
        }

        foreach (var index in table.Indexes)
        {
            var structuralOnly = _collationResolver is null
                || IsIndexUnchangedFromPrevious(name, table, index, previousTables);
            ValidateIndexRepresentable(name, table, index, structuralOnly: structuralOnly);
        }
    }

    private static void ValidatePrimaryKeyIndexPrerequisites(
        string tableName,
        EmbeddedTable table,
        string primaryKeyKind,
        bool allowDescending = false,
        bool allowBuiltInCollations = false)
    {
        var primaryKeySchema = table.PrimaryKeySchema;
        if (primaryKeySchema is null)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{tableName}' because its primary-key descriptor is missing.");
        }

        try
        {
            if (allowBuiltInCollations)
                primaryKeySchema.EnsureSupportedByManagedIndexWriter(allowDescending);
            else if (allowDescending)
                primaryKeySchema.EnsureSupportedByBinaryIndexWriter();
            else
                primaryKeySchema.EnsureSupportedByBinaryAscendingIndexWriter();
        }
        catch (ArgumentException exception)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{tableName}' because its primary-key metadata is inconsistent.",
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist {primaryKeyKind} table '{tableName}' because {exception.Message} "
                + (allowBuiltInCollations
                    ? "The managed primary-key index writer supports ASC/DESC terms with BINARY, NOCASE, and RTRIM collation."
                    : "The managed primary-key index writer supports only BINARY terms"
                        + (allowDescending ? "." : " in ascending order.")),
                exception);
        }
    }

    private SqlitePrimaryKeySchema ValidateWithoutRowidTableRepresentable(
        string tableName,
        EmbeddedTable table,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null)
    {
        ValidatePrimaryKeyIndexPrerequisites(
            tableName,
            table,
            "WITHOUT ROWID",
            allowDescending: true,
            allowBuiltInCollations: true);
        var primaryKeySchema = table.PrimaryKeySchema
            ?? throw new InvalidOperationException("Validated WITHOUT ROWID table is missing its primary-key schema.");
        if (primaryKeySchema.Terms.Count != table.PrimaryKeyColumns.Count)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary-key metadata is inconsistent.");
        }

        for (var position = 0; position < primaryKeySchema.Terms.Count; position++)
        {
            var term = primaryKeySchema.Terms[position];
            var primaryKeyColumn = table.PrimaryKeyColumns[position];
            if (term.ColumnIndex != primaryKeyColumn.Index
                || (term.SortOrder == SqliteKeySortOrder.Descending) != primaryKeyColumn.Descending
                || term.ColumnIndex < 0
                || term.ColumnIndex >= table.ColumnDefinitions.Length
                || !string.Equals(
                    term.ColumnName,
                    table.ColumnDefinitions[term.ColumnIndex].Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary-key metadata is inconsistent.");
            }
        }

        foreach (var index in table.Indexes)
        {
            var structuralOnly = _collationResolver is null
                || IsIndexUnchangedFromPrevious(tableName, table, index, previousTables);
            ValidateIndexRepresentable(tableName, table, index, structuralOnly: structuralOnly);
        }

        return primaryKeySchema;
    }

    private static void ValidateSchemaDefinitions(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables)
    {
        foreach (var (catalogName, definition) in virtualTables)
        {
            if (!string.Equals(catalogName, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist virtual table '{catalogName}' because its catalog key and definition name differ.");
            }

            _ = ManagedVirtualTableSchemaSql.Parse(ManagedVirtualTableSchemaSql.Build(definition));
        }

        foreach (var (catalogName, view) in views)
            ValidateViewDefinition(catalogName, view);

        foreach (var (catalogName, trigger) in triggers)
            ValidateTriggerDefinition(catalogName, trigger, tables, views);
    }

    private static void ValidateViewDefinition(string catalogName, ViewDefinition view)
    {
        if (!string.Equals(catalogName, view.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist view '{catalogName}' because its catalog key and definition name differ.");
        }

        var statement = SqlParser.Parse(view.Sql, SqlParameterMap.Parse(view.Sql));
        if (statement is not CreateViewStatement persisted
            || !string.Equals(persisted.Name, view.Name, StringComparison.OrdinalIgnoreCase)
            || !SameColumnList(persisted.Columns, view.Columns))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist view '{catalogName}' because its SQL cannot reconstruct its catalog definition.");
        }

        ValidateRuntimeIndependentQuery("view", catalogName, view.Query);
        ValidateRuntimeIndependentQuery("view", catalogName, persisted.Query);
    }

    private static string LocalTableName(string name)
        => ManagedSchemaName.TrySplit(name, out _, out var local) ? local : name;

    private static void ValidateTriggerDefinition(
        string catalogName,
        TriggerDefinition trigger,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views)
    {
        if (!string.Equals(catalogName, trigger.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{catalogName}' because its catalog key and definition name differ.");
        }
        var targetExists = trigger.Timing == TriggerTiming.InsteadOf
            ? views.ContainsKey(trigger.TableName)
            : tables.ContainsKey(trigger.TableName);
        if (!targetExists)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{catalogName}' because its target '{trigger.TableName}' does not exist.");
        }

        var statement = SqlParser.Parse(trigger.Sql, SqlParameterMap.Parse(trigger.Sql));
        if (statement is not CreateTriggerStatement persisted
            || !string.Equals(persisted.Name, trigger.Name, StringComparison.OrdinalIgnoreCase)
            || persisted.Timing != trigger.Timing
            || persisted.Event != trigger.Event
            || !SameColumnList(persisted.UpdateOfColumns, trigger.UpdateOfColumns)
            || !string.Equals(LocalTableName(persisted.TableName), trigger.TableName, StringComparison.OrdinalIgnoreCase)
            || (persisted.When is null) != (trigger.When is null)
            || persisted.Body.Count != trigger.Body.Count
            || !HaveSameStatementKinds(persisted.Body, trigger.Body))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{catalogName}' because its SQL cannot reconstruct its statement-level definition.");
        }

        ValidateRuntimeIndependentTriggerBody(catalogName, trigger.When, trigger.Body);
        ValidateRuntimeIndependentTriggerBody(catalogName, persisted.When, persisted.Body);
        ValidateTriggerCollationDependencies(catalogName, trigger, tables, views);
    }


    internal static void ValidateTriggerCollationDependencies(
        string name,
        TriggerDefinition trigger,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views)
    {
        var referencedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            trigger.TableName,
        };
        foreach (var statement in trigger.Body)
            CollectTriggerReferencedTables(statement, referencedTables);
        if (views.TryGetValue(trigger.TableName, out var targetView))
            CollectTriggerReferencedTables(targetView.Query, referencedTables);

        foreach (var tableName in referencedTables)
        {
            if (!tables.TryGetValue(tableName, out var table))
                continue;
            var collation = table.ColumnDefinitions
                .Select(column => column.Collation)
                .FirstOrDefault(value => value is not null && !IsBuiltInCollation(value));
            if (collation is not null)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist trigger '{name}' because table "
                    + $"'{tableName}' uses custom collation '{collation}'.");
            }
        }
    }

    private static bool IsBuiltInCollation(string collation)
        => collation.Equals("BINARY", StringComparison.OrdinalIgnoreCase)
            || collation.Equals("NOCASE", StringComparison.OrdinalIgnoreCase)
            || collation.Equals("RTRIM", StringComparison.OrdinalIgnoreCase);

    private static void CollectTriggerReferencedTables(
        ParsedStatement statement,
        ISet<string> tables)
    {
        switch (statement)
        {
            case InsertStatement insert:
                tables.Add(insert.TableName);
                if (insert.Source is not null)
                    CollectTriggerReferencedTables(insert.Source, tables);
                foreach (var expression in insert.Rows.SelectMany(row => row))
                    CollectTriggerReferencedTables(expression, tables);
                foreach (var upsert in insert.Upsert?.Clauses() ?? [])
                {
                    foreach (var target in upsert.Target)
                        CollectTriggerReferencedTables(target.Expression, tables);
                    CollectTriggerReferencedTables(upsert.TargetWhere, tables);
                    if (upsert.Action is DoUpdateUpsertAction upsertUpdate)
                    {
                        foreach (var assignment in upsertUpdate.Assignments)
                            CollectTriggerReferencedTables(assignment.Value, tables);
                        CollectTriggerReferencedTables(upsertUpdate.Where, tables);
                    }
                }
                break;
            case UpdateStatement update:
                tables.Add(update.TableName);
                CollectTriggerReferencedTables(update.From, tables);
                foreach (var assignment in update.Assignments)
                    CollectTriggerReferencedTables(assignment.Value, tables);
                CollectTriggerReferencedTables(update.Where, tables);
                foreach (var term in update.EffectiveOrderBy)
                    CollectTriggerReferencedTables(term.Expression, tables);
                CollectTriggerReferencedTables(update.Limit, tables);
                break;
            case DeleteStatement delete:
                tables.Add(delete.TableName);
                CollectTriggerReferencedTables(delete.Where, tables);
                foreach (var term in delete.EffectiveOrderBy)
                    CollectTriggerReferencedTables(term.Expression, tables);
                CollectTriggerReferencedTables(delete.Limit, tables);
                break;
            case WithDmlStatement with:
                foreach (var commonTableExpression in with.CommonTableExpressions)
                    CollectTriggerReferencedTables(commonTableExpression.Body, tables);
                CollectTriggerReferencedTables(with.Dml, tables);
                break;
            case QueryStatement query:
                CollectTriggerReferencedTables(query, tables);
                break;
        }
    }

    private static void CollectTriggerReferencedTables(
        QueryStatement query,
        ISet<string> tables)
    {
        switch (query)
        {
            case SelectStatement select:
                CollectTriggerReferencedTables(select.Source, tables);
                foreach (var expression in select.Projections.Select(projection => projection.Expression)
                             .Append(select.Where)
                             .Concat(select.GroupBy)
                             .Append(select.Having)
                             .Concat(select.OrderBy.Select(term => term.Expression))
                             .Append(select.Limit)
                             .Append(select.Offset))
                {
                    CollectTriggerReferencedTables(expression, tables);
                }
                foreach (var window in select.NamedWindows)
                {
                    foreach (var expression in window.Specification.PartitionBy
                                 .Concat(window.Specification.OrderBy.Select(term => term.Expression))
                                 .Append(window.Specification.Frame?.Start.Offset)
                                 .Append(window.Specification.Frame?.End.Offset))
                    {
                        CollectTriggerReferencedTables(expression, tables);
                    }
                }
                break;
            case ValuesClause values:
                foreach (var expression in values.Rows.SelectMany(row => row))
                    CollectTriggerReferencedTables(expression, tables);
                break;
            case CompoundSelectStatement compound:
                foreach (var term in compound.Terms)
                    CollectTriggerReferencedTables(term, tables);
                foreach (var expression in compound.OrderBy.Select(term => term.Expression)
                             .Append(compound.Limit)
                             .Append(compound.Offset))
                {
                    CollectTriggerReferencedTables(expression, tables);
                }
                break;
            case WithSelectStatement with:
                foreach (var commonTableExpression in with.CommonTableExpressions)
                    CollectTriggerReferencedTables(commonTableExpression.Body, tables);
                CollectTriggerReferencedTables(with.Query, tables);
                break;
        }
    }

    private static void CollectTriggerReferencedTables(
        TableSource? source,
        ISet<string> tables)
    {
        switch (source)
        {
            case NamedTableSource named:
                tables.Add(named.Name);
                break;
            case DerivedTableSource derived:
                CollectTriggerReferencedTables(derived.Query, tables);
                break;
            case JoinTableSource join:
                CollectTriggerReferencedTables(join.Left, tables);
                CollectTriggerReferencedTables(join.Right, tables);
                CollectTriggerReferencedTables(join.Condition, tables);
                break;
            case TableValuedFunctionSource function:
                foreach (var argument in function.Arguments)
                    CollectTriggerReferencedTables(argument, tables);
                break;
        }
    }

    private static void CollectTriggerReferencedTables(
        Expression? expression,
        ISet<string> tables)
    {
        switch (expression)
        {
            case null:
                return;
            case ScalarSubqueryExpression scalar:
                CollectTriggerReferencedTables(scalar.Query, tables);
                return;
            case ExistsExpression exists:
                CollectTriggerReferencedTables(exists.Query, tables);
                return;
            case InSubqueryExpression @in:
                CollectTriggerReferencedTables(@in.Value, tables);
                CollectTriggerReferencedTables(@in.Query, tables);
                return;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    CollectTriggerReferencedTables(argument, tables);
                CollectTriggerReferencedTables(function.Filter, tables);
                if (function.Window is not null)
                {
                    foreach (var child in function.Window.PartitionBy
                                 .Concat(function.Window.OrderBy.Select(term => term.Expression))
                                 .Append(function.Window.Frame?.Start.Offset)
                                 .Append(function.Window.Frame?.End.Offset))
                    {
                        CollectTriggerReferencedTables(child, tables);
                    }
                }
                return;
            case RowValueExpression rowValue:
                foreach (var value in rowValue.Values)
                    CollectTriggerReferencedTables(value, tables);
                return;
            case CollationExpression collation:
                CollectTriggerReferencedTables(collation.Expression, tables);
                return;
            case CastExpression cast:
                CollectTriggerReferencedTables(cast.Expression, tables);
                return;
            case CaseExpression @case:
                CollectTriggerReferencedTables(@case.Operand, tables);
                foreach (var clause in @case.Clauses)
                {
                    CollectTriggerReferencedTables(clause.When, tables);
                    CollectTriggerReferencedTables(clause.Then, tables);
                }
                CollectTriggerReferencedTables(@case.Else, tables);
                return;
            case LikeExpression like:
                CollectTriggerReferencedTables(like.Value, tables);
                CollectTriggerReferencedTables(like.Pattern, tables);
                CollectTriggerReferencedTables(like.Escape, tables);
                return;
            case GlobExpression glob:
                CollectTriggerReferencedTables(glob.Value, tables);
                CollectTriggerReferencedTables(glob.Pattern, tables);
                return;
            case InExpression @in:
                CollectTriggerReferencedTables(@in.Value, tables);
                foreach (var value in @in.Values)
                    CollectTriggerReferencedTables(value, tables);
                return;
            case BetweenExpression between:
                CollectTriggerReferencedTables(between.Value, tables);
                CollectTriggerReferencedTables(between.Lower, tables);
                CollectTriggerReferencedTables(between.Upper, tables);
                return;
            case UnaryExpression unary:
                CollectTriggerReferencedTables(unary.Operand, tables);
                return;
            case BinaryExpression binary:
                CollectTriggerReferencedTables(binary.Left, tables);
                CollectTriggerReferencedTables(binary.Right, tables);
                return;
        }
    }

    private static bool SameColumnList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => left is null || right is null
            ? left is null && right is null
            : left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private static bool HaveSameStatementKinds(
        IReadOnlyList<ParsedStatement> left,
        IReadOnlyList<ParsedStatement> right)
    {
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].GetType() != right[index].GetType())
                return false;
        }

        return true;
    }

    internal static void ValidateRuntimeIndependentQuery(string objectType, string name, QueryStatement query)
    {
        var dependency = FindRuntimeDependency(query);
        if (dependency is not null)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist {objectType} '{name}' because it uses {dependency}. "
                + "File-backed schema definitions cannot retain bind parameters, managed callbacks, or custom collations across reopen.");
        }
    }

    internal static void ValidateRuntimeIndependentTriggerBody(
        string name,
        Expression? when,
        IReadOnlyList<ParsedStatement> statements)
    {
        var whenDependency = FindRuntimeDependency(when);
        if (whenDependency is not null)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{name}' because it uses {whenDependency}. "
                + "File-backed schema definitions cannot retain bind parameters, managed callbacks, or custom collations across reopen.");
        }

        foreach (var statement in statements)
        {
            var dependency = FindRuntimeDependency(statement);
            if (dependency is not null)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist trigger '{name}' because it uses {dependency}. "
                    + "File-backed schema definitions cannot retain bind parameters, managed callbacks, or custom collations across reopen.");
            }
        }
    }

    private static string? FindRuntimeDependency(QueryStatement query)
    {
        return query switch
        {
            SelectStatement select => FirstRuntimeDependency(
                FindRuntimeDependency(select.Projections),
                FindRuntimeDependency(select.Source),
                FindRuntimeDependency(select.Where),
                FindRuntimeDependency(select.GroupBy),
                FindRuntimeDependency(select.Having),
                FindRuntimeDependency(select.NamedWindows),
                FindRuntimeDependency(select.OrderBy),
                FindRuntimeDependency(select.Limit),
                FindRuntimeDependency(select.Offset)),
            ValuesClause values => FindRuntimeDependency(values.Rows),
            CompoundSelectStatement compound => FirstRuntimeDependency(
                FindRuntimeDependency(compound.Terms),
                FindRuntimeDependency(compound.OrderBy),
                FindRuntimeDependency(compound.Limit),
                FindRuntimeDependency(compound.Offset)),
            WithSelectStatement with => FirstRuntimeDependency(
                FindRuntimeDependency(with.CommonTableExpressions),
                FindRuntimeDependency(with.Query)),
            _ => $"unsupported query type {query.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(TableSource? source)
    {
        return source switch
        {
            null => null,
            NamedTableSource => null,
            TableValuedFunctionSource function => FirstRuntimeDependency(
                [.. function.Arguments.Select(argument => FindRuntimeDependency(argument))]),
            DerivedTableSource derived => FindRuntimeDependency(derived.Query),
            JoinTableSource join => FirstRuntimeDependency(
                FindRuntimeDependency(join.Left),
                FindRuntimeDependency(join.Right),
                FindRuntimeDependency(join.Condition)),
            _ => $"unsupported table source {source.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(ParsedStatement statement)
    {
        return statement switch
        {
            InsertStatement insert => FirstRuntimeDependency(
                FindRuntimeDependency(insert.Rows),
                insert.Source is null ? null : FindRuntimeDependency(insert.Source),
                FindRuntimeDependency(insert.Returning),
                FindRuntimeDependency(insert.Upsert)),
            UpdateStatement update => FirstRuntimeDependency(
                FindRuntimeDependency(update.Assignments),
                FindRuntimeDependency(update.From),
                FindRuntimeDependency(update.Where),
                FindRuntimeDependency(update.EffectiveOrderBy),
                FindRuntimeDependency(update.Limit),
                FindRuntimeDependency(update.Returning)),
            DeleteStatement delete => FirstRuntimeDependency(
                FindRuntimeDependency(delete.Where),
                FindRuntimeDependency(delete.EffectiveOrderBy),
                FindRuntimeDependency(delete.Limit),
                FindRuntimeDependency(delete.Returning)),
            WithDmlStatement with => FirstRuntimeDependency(
                FindRuntimeDependency(with.CommonTableExpressions),
                FindRuntimeDependency(with.Dml)),
            QueryStatement query => FindRuntimeDependency(query),
            _ => $"unsupported trigger body statement {statement.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(Expression? expression)
    {
        return expression switch
        {
            null or LiteralExpression or CurrentTimeExpression or ColumnExpression or RaiseExpression
                or StarExpression or QualifiedStarExpression => null,
            ParameterExpression => "a bind parameter",
            RowValueExpression rowValue => FindRuntimeDependency(rowValue.Values),
            // Only engine built-ins may appear in persisted schema: their implementations exist on
            // every connection, so the stored SQL re-resolves identically after reopen. Unknown
            // names may be connection-registered managed callbacks, which cannot survive reopen.
            // Arguments, FILTER, and window parts are still walked for runtime dependencies.
            FunctionExpression function => FirstRuntimeDependency(
                SqliteBuiltinFunctions.Contains(function.Name) ? null : $"function {function.Name}()",
                FindRuntimeDependency(function.Arguments),
                FindRuntimeDependency(function.Filter),
                function.Window is null ? null : FindRuntimeDependency(function.Window)),
            ScalarSubqueryExpression subquery => FindRuntimeDependency(subquery.Query),
            ExistsExpression exists => FindRuntimeDependency(exists.Query),
            CollationExpression collation => IsBuiltInCollation(collation.Name)
                ? FindRuntimeDependency(collation.Expression)
                : $"explicit collation '{collation.Name}'",
            CastExpression cast => FindRuntimeDependency(cast.Expression),
            CaseExpression @case => FirstRuntimeDependency(
                FindRuntimeDependency(@case.Operand),
                FindRuntimeDependency(@case.Clauses),
                FindRuntimeDependency(@case.Else)),
            LikeExpression like => FirstRuntimeDependency(
                FindRuntimeDependency(like.Value),
                FindRuntimeDependency(like.Pattern),
                FindRuntimeDependency(like.Escape)),
            InExpression @in => FirstRuntimeDependency(
                FindRuntimeDependency(@in.Value),
                FindRuntimeDependency(@in.Values)),
            InSubqueryExpression @in => FirstRuntimeDependency(
                FindRuntimeDependency(@in.Value),
                FindRuntimeDependency(@in.Query)),
            BetweenExpression between => FirstRuntimeDependency(
                FindRuntimeDependency(between.Value),
                FindRuntimeDependency(between.Lower),
                FindRuntimeDependency(between.Upper)),
            UnaryExpression unary => FindRuntimeDependency(unary.Operand),
            GlobExpression glob => FirstRuntimeDependency(
                FindRuntimeDependency(glob.Value),
                FindRuntimeDependency(glob.Pattern)),
            BinaryExpression binary => FirstRuntimeDependency(
                FindRuntimeDependency(binary.Left),
                FindRuntimeDependency(binary.Right)),
            _ => $"unsupported expression {expression.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(IEnumerable<Projection>? projections)
    {
        if (projections is null)
            return null;

        foreach (var projection in projections)
        {
            var dependency = FindRuntimeDependency(projection.Expression);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(UpsertClause? upsert)
    {
        foreach (var clause in upsert?.Clauses() ?? [])
        {
            var collation = clause.Target
                .Select(column => column.Collation)
                .FirstOrDefault(name => name is not null && !IsBuiltInCollation(name));
            if (collation is not null)
                return $"explicit collation '{collation}'";
            var targetDependency = FirstRuntimeDependency(
                FirstRuntimeDependency(clause.Target.Select(target =>
                    FindRuntimeDependency(target.Expression)).ToArray()),
                FindRuntimeDependency(clause.TargetWhere));
            var dependency = clause.Action is DoUpdateUpsertAction update
                ? FirstRuntimeDependency(
                    targetDependency,
                    FindRuntimeDependency(update.Assignments),
                    FindRuntimeDependency(update.Where))
                : targetDependency;
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<OrderByTerm> terms)
    {
        foreach (var term in terms)
        {
            var dependency = FindRuntimeDependency(term.Expression);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<NamedWindowDefinition> windows)
    {
        foreach (var window in windows)
        {
            var dependency = FindRuntimeDependency(window.Specification);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(WindowSpecification window)
    {
        return FirstRuntimeDependency(
            FindRuntimeDependency(window.PartitionBy),
            FindRuntimeDependency(window.OrderBy),
            FindRuntimeDependency(window.Frame?.Start.Offset),
            FindRuntimeDependency(window.Frame?.End.Offset));
    }

    private static string? FindRuntimeDependency(IEnumerable<Expression> expressions)
    {
        foreach (var expression in expressions)
        {
            var dependency = FindRuntimeDependency(expression);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(
        IReadOnlyList<IReadOnlyList<Expression>> rows)
    {
        foreach (var row in rows)
        {
            var dependency = FindRuntimeDependency(row);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<QueryStatement> queries)
    {
        foreach (var query in queries)
        {
            var dependency = FindRuntimeDependency(query);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<CommonTableExpression> expressions)
    {
        foreach (var expression in expressions)
        {
            var dependency = FindRuntimeDependency(expression.Body);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<CaseClause> clauses)
    {
        foreach (var clause in clauses)
        {
            var dependency = FirstRuntimeDependency(
                FindRuntimeDependency(clause.When),
                FindRuntimeDependency(clause.Then));
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<ColumnAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            var dependency = FindRuntimeDependency(assignment.Value);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FirstRuntimeDependency(params string?[] dependencies)
        => dependencies.FirstOrDefault(static dependency => dependency is not null);

    private PreparedTableTree BuildWithoutRowidTableTree(
        string name,
        EmbeddedTable table,
        RebuildPageAllocator allocator)
    {
        var primaryKeySchema = ValidateWithoutRowidTableRepresentable(name, table);
        var comparer = CreatePrimaryKeyComparer(primaryKeySchema);
        var records = BuildWithoutRowidTableRecords(name, table, primaryKeySchema, comparer);
        try
        {
            var treeDescription = $"WITHOUT ROWID table '{name}'";
            var indexTree = BuildIndexTreeFromLeafGroups(
                treeDescription,
                PartitionIndexLeafRecords(treeDescription, records, comparer),
                comparer,
                allocator);
            return new PreparedTableTree(
                indexTree.RootPage,
                indexTree.InteriorPages,
                indexTree.LeafPages,
                indexTree.OverflowPages);
        }
        catch (InvalidOperationException exception)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{name}' because its primary-key index cannot be represented as a valid SQLite index b-tree.",
                exception);
        }
    }

    private PreparedTableTree BuildTableTree(
        string name,
        EmbeddedTable table,
        RebuildPageAllocator allocator)
    {
        var leafGroups = PartitionTableLeafCells(name, table);
        var overflowPages = new List<PageImage>();
        if (leafGroups.Count == 1)
        {
            return new PreparedTableTree(
                BuildTableLeafPage(leafGroups[0], allocator, overflowPages),
                Array.Empty<PageImage>(),
                Array.Empty<PageImage>(),
                overflowPages);
        }

        var leafPageNumbers = new uint[leafGroups.Count];
        for (var leafIndex = 0; leafIndex < leafPageNumbers.Length; leafIndex++)
            leafPageNumbers[leafIndex] = allocator.ReservePage();

        var leafPages = new List<PageImage>(leafGroups.Count);
        for (var leafIndex = 0; leafIndex < leafGroups.Count; leafIndex++)
        {
            leafPages.Add(new PageImage(
                leafPageNumbers[leafIndex],
                BuildTableLeafPage(leafGroups[leafIndex], allocator, overflowPages)));
        }

        var leafChildren = leafGroups
            .Select((group, index) => new TableTreeChild(
                leafPageNumbers[index],
                group[^1].RowId))
            .ToArray();
        var interiorPages = new List<PageImage>();
        IReadOnlyList<TableTreeChild> levelChildren = leafChildren;
        byte[] root;
        while (!TryBuildTableInteriorPage(levelChildren, out root))
        {
            var groups = PartitionTableInteriorChildren(levelChildren);
            var parentChildren = new TableTreeChild[groups.Count];
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var pageNumber = allocator.ReservePage();
                interiorPages.Add(new PageImage(
                    pageNumber,
                    BuildTableInteriorPage(group)));
                parentChildren[groupIndex] = new TableTreeChild(pageNumber, group[^1].MaximumRowId);
            }

            levelChildren = parentChildren;
        }

        return new PreparedTableTree(root, interiorPages, leafPages, overflowPages);
    }

    private List<List<TableTreeChild>> PartitionTableInteriorChildren(
        IReadOnlyList<TableTreeChild> children)
    {
        if (children.Count < 2)
            throw new ArgumentException("A table-interior partition requires at least two children.", nameof(children));

        var groups = new List<List<TableTreeChild>> { new() };
        foreach (var child in children)
        {
            var currentGroup = groups[^1];
            currentGroup.Add(child);
            if (TryBuildTableInteriorPage(currentGroup, out _))
                continue;

            currentGroup.RemoveAt(currentGroup.Count - 1);
            if (currentGroup.Count == 0)
            {
                throw new InvalidOperationException(
                    "A SQLite table-interior page cannot contain one child.");
            }

            groups.Add(new List<TableTreeChild> { child });
        }

        if (groups.Count > 1 && groups[^1].Count == 1)
        {
            var previousGroup = groups[^2];
            var movedChild = previousGroup[^1];
            previousGroup.RemoveAt(previousGroup.Count - 1);
            groups[^1].Insert(0, movedChild);
            if (previousGroup.Count == 0
                || !TryBuildTableInteriorPage(previousGroup, out _)
                || !TryBuildTableInteriorPage(groups[^1], out _))
            {
                throw new InvalidOperationException(
                    "SQLite table-interior child partitioning cannot preserve non-empty child pages.");
            }
        }

        return groups;
    }

    private byte[] BuildTableInteriorPage(IReadOnlyList<TableTreeChild> children)
    {
        if (!TryBuildTableInteriorPage(children, out var page))
        {
            throw new InvalidOperationException(
                "SQLite table-interior cells and their pointer array do not fit in the page's usable space.");
        }

        return page;
    }

    private bool TryBuildTableInteriorPage(
        IReadOnlyList<TableTreeChild> children,
        out byte[] page)
        => TryBuildTableInteriorPage(children, isFirstPage: false, out page);

    private bool TryBuildTableInteriorPage(
        IReadOnlyList<TableTreeChild> children,
        bool isFirstPage,
        out byte[] page)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count == 0)
            throw new ArgumentException("A table-interior page requires at least one child.", nameof(children));

        try
        {
            var builder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                children[^1].PageNumber,
                isFirstPage);
            for (var childIndex = 0; childIndex < children.Count - 1; childIndex++)
            {
                var child = children[childIndex];
                builder.Append(SqliteTableInteriorCell.Create(child.PageNumber, child.MaximumRowId));
            }

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = [];
            return false;
        }
    }

    private List<List<PendingTableCell>> PartitionTableLeafCells(string name, EmbeddedTable table)
    {
        // Planning only needs each cell's encoded length to place page
        // boundaries, and rowids are already strictly increasing here, so a
        // throwaway page builder (which copies every payload into a planning
        // cell and signals "full" by throwing) was pure duplicated work. The fit
        // arithmetic mirrors SqliteTableLeafPageBuilder.EnsureFits exactly so
        // page boundaries are unchanged.
        const int HeaderOffset = 0;
        var leafGroups = new List<List<PendingTableCell>> { new() };
        var cellBytes = 0;
        var cellCount = 0;
        foreach (var (rowId, record) in EnumerateRowCells(name, table))
        {
            var encodedLength = SqliteTableLeafCell.CalculateEncodedLength(
                rowId,
                checked((ulong)record.Length),
                _usableSpace);
            if (!TableLeafCellFits(HeaderOffset, cellBytes, cellCount, encodedLength))
            {
                if (cellCount == 0 || leafGroups[^1].Count == 0)
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because rowid {rowId} cannot fit in a SQLite table leaf.");
                }

                leafGroups.Add([]);
                cellBytes = 0;
                cellCount = 0;
                if (!TableLeafCellFits(HeaderOffset, cellBytes, cellCount, encodedLength))
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because rowid {rowId} cannot fit in a SQLite table leaf.");
                }
            }

            cellBytes = checked(cellBytes + encodedLength);
            cellCount++;
            leafGroups[^1].Add(new PendingTableCell(rowId, record));
        }

        return leafGroups;
    }

    /// <summary>
    /// Mirrors <c>SqliteTableLeafPageBuilder.EnsureFits</c>: whether one more
    /// cell and its pointer still fit in the leaf's usable space.
    /// </summary>
    private bool TableLeafCellFits(int headerOffset, int cellBytes, int cellCount, int additionalCellLength)
    {
        if (cellCount == ushort.MaxValue)
            return false;

        var totalCellBytes = checked(cellBytes + additionalCellLength);
        var pointerArrayEnd = checked(
            headerOffset
            + SqliteBtreePageHeader.LeafHeaderSize
            + ((cellCount + 1) * sizeof(ushort)));
        return _usableSpace - totalCellBytes >= pointerArrayEnd;
    }

    private SqliteTableLeafCell CreateTableLeafPlanningCell(long rowId, ReadOnlySpan<byte> record)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        return layout.UsesOverflow
            ? SqliteTableLeafCell.Create(
                rowId,
                checked((ulong)record.Length),
                record[..layout.LocalPayloadLength],
                firstOverflowPage: 1,
                _usableSpace)
            : SqliteTableLeafCell.Create(rowId, record, _usableSpace);
    }

    private byte[] BuildTableLeafPage(
        IReadOnlyList<PendingTableCell> cells,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage: false);
        foreach (var cell in cells)
            builder.Append(CreateTableLeafCell(cell.RowId, cell.Record, allocator, overflowPages));
        return builder.Build();
    }

    private PreparedIndexTree BuildIndexTree(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        IIndexRebuildPageAllocator allocator)
    {
        var comparer = CreateIndexComparer(table, index);
        var leafGroups = PartitionIndexLeafRecords(
            $"index '{index.Name}' on table '{tableName}'",
            BuildIndexRecords(tableName, table, index, comparer),
            comparer);
        return BuildIndexTreeFromLeafGroups(
            $"index '{index.Name}'",
            leafGroups,
            comparer,
            allocator);
    }

    private PreparedIndexTree BuildIndexTreeFromLeafGroups(
        string treeDescription,
        IReadOnlyList<List<byte[]>> leafGroups,
        SqliteIndexRecordComparer comparer,
        IIndexRebuildPageAllocator allocator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeDescription);
        ArgumentNullException.ThrowIfNull(leafGroups);
        var overflowPages = new List<PageImage>();
        if (leafGroups.Count == 1)
        {
            return new PreparedIndexTree(
                BuildIndexLeafPage(leafGroups[0], comparer, allocator, overflowPages),
                Array.Empty<PageImage>(),
                Array.Empty<PageImage>(),
                overflowPages);
        }

        var leafChildren = new IndexTreeNode[leafGroups.Count];
        for (var leafIndex = 0; leafIndex < leafGroups.Count; leafIndex++)
        {
            leafChildren[leafIndex] = IndexTreeNode.CreateLeaf(
                allocator.ReservePage(),
                new List<byte[]>(leafGroups[leafIndex]));
        }

        IReadOnlyList<IndexTreeNode> levelChildren = leafChildren;
        while (true)
        {
            var root = TryBuildIndexInteriorPlan(
                CloneIndexTreeNodes(levelChildren),
                treeDescription,
                comparer,
                throwOnPromotionFailure: false);
            if (root is not null)
            {
                var interiorPages = new List<PageImage>();
                var leafPages = new List<PageImage>();
                MaterializeIndexTreeChildren(
                    root.Children,
                    comparer,
                    allocator,
                    overflowPages,
                    interiorPages,
                    leafPages);
                return new PreparedIndexTree(
                    BuildIndexInteriorPage(root, comparer, allocator, overflowPages),
                    interiorPages,
                    leafPages,
                    overflowPages);
            }

            var plans = PartitionIndexInteriorChildren(treeDescription, levelChildren, comparer);
            var parentChildren = new IndexTreeNode[plans.Count];
            for (var planIndex = 0; planIndex < plans.Count; planIndex++)
            {
                parentChildren[planIndex] = IndexTreeNode.CreateInterior(
                    allocator.ReservePage(),
                    plans[planIndex]);
            }

            levelChildren = parentChildren;
        }
    }

    private IndexInteriorPlan? TryBuildIndexInteriorPlan(
        IReadOnlyList<IndexTreeNode> children,
        string indexName,
        SqliteIndexRecordComparer comparer,
        bool throwOnPromotionFailure)
    {
        if (children.Count < 2)
        {
            throw new ArgumentException(
                "A SQLite index-interior page requires at least two children.",
                nameof(children));
        }

        var childHeight = GetIndexTreeHeight(children[0]);
        for (var childIndex = 1; childIndex < children.Count; childIndex++)
        {
            if (GetIndexTreeHeight(children[childIndex]) != childHeight)
            {
                throw new InvalidOperationException(
                    "SQLite index-interior planning requires every child to have the same height.");
            }
        }

        var plannedChildren = children.ToArray();
        var separators = new List<byte[]>(plannedChildren.Length - 1);
        try
        {
            for (var childIndex = 0; childIndex < plannedChildren.Length - 1; childIndex++)
            {
                separators.Add(PromoteIndexTreeSeparator(
                    plannedChildren[childIndex],
                    plannedChildren[childIndex + 1],
                    indexName,
                    comparer,
                    out var left,
                    out var right));
                plannedChildren[childIndex] = left;
                plannedChildren[childIndex + 1] = right;
            }
        }
        catch (EmbeddedSqlException) when (!throwOnPromotionFailure)
        {
            return null;
        }

        try
        {
            var builder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                plannedChildren[^1].PageNumber,
                comparer);
            for (var childIndex = 0; childIndex < separators.Count; childIndex++)
            {
                var separator = separators[childIndex];
                builder.Append(
                    SqliteIndexInteriorCell.Create(
                        plannedChildren[childIndex].PageNumber,
                        CreateIndexLeafPlanningCell(separator)),
                    separator);
            }

            _ = builder.Build();
            return new IndexInteriorPlan(plannedChildren, separators);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<IndexInteriorPlan> PartitionIndexInteriorChildren(
        string treeDescription,
        IReadOnlyList<IndexTreeNode> children,
        SqliteIndexRecordComparer comparer)
    {
        if (children.Count < 2)
            throw new ArgumentException("An index-interior partition requires at least two children.", nameof(children));

        var groups = new List<IndexInteriorGroupRange>();
        var start = 0;
        while (start < children.Count)
        {
            if (children.Count - start == 1)
            {
                groups.Add(new IndexInteriorGroupRange(start, 1));
                break;
            }

            var bestCount = 0;
            for (var candidateCount = 2;
                 start + candidateCount <= children.Count;
                 candidateCount++)
            {
                var candidate = TryBuildIndexInteriorPlan(
                    CloneIndexTreeNodes(children, start, candidateCount),
                    treeDescription,
                    comparer,
                    throwOnPromotionFailure: false);
                if (candidate is null)
                    break;

                bestCount = candidateCount;
            }

            if (bestCount == 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist {treeDescription} because its index records cannot be partitioned into non-empty equal-height child trees.");
            }

            groups.Add(new IndexInteriorGroupRange(start, bestCount));
            start += bestCount;
        }

        if (groups[^1].Count == 1)
        {
            if (groups.Count < 2 || groups[^2].Count < 3)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist {treeDescription} because its index records cannot be partitioned into non-empty equal-height child trees.");
            }

            var previous = groups[^2];
            var last = groups[^1];
            groups[^2] = previous with { Count = previous.Count - 1 };
            groups[^1] = new IndexInteriorGroupRange(last.Start - 1, last.Count + 1);
        }

        var plans = new List<IndexInteriorPlan>(groups.Count);
        foreach (var group in groups)
        {
            var plan = TryBuildIndexInteriorPlan(
                CloneIndexTreeNodes(children, group.Start, group.Count),
                treeDescription,
                comparer,
                throwOnPromotionFailure: true);
            if (plan is null)
            {
                throw new InvalidOperationException(
                    "A planned SQLite index-interior page no longer fits its validated child partition.");
            }

            plans.Add(plan);
        }

        return plans;
    }

    private byte[] PromoteIndexTreeSeparator(
        IndexTreeNode left,
        IndexTreeNode right,
        string treeDescription,
        SqliteIndexRecordComparer comparer,
        out IndexTreeNode plannedLeft,
        out IndexTreeNode plannedRight)
    {
        if (TryExtractMaximumIndexRecord(left, comparer, out var separator, out plannedLeft))
        {
            plannedRight = right;
            return separator;
        }

        if (TryExtractMinimumIndexRecord(right, comparer, out separator, out plannedRight))
        {
            plannedLeft = left;
            return separator;
        }

        throw new EmbeddedSqlException(
            $"The managed file engine cannot persist {treeDescription} because no separator can be promoted while retaining non-empty equal-height child trees.");
    }

    private bool TryExtractMaximumIndexRecord(
        IndexTreeNode node,
        SqliteIndexRecordComparer comparer,
        out byte[] record,
        out IndexTreeNode remainder)
    {
        if (node.IsLeaf)
        {
            if (node.Records.Count < 2)
            {
                record = null!;
                remainder = null!;
                return false;
            }

            var records = new List<byte[]>(node.Records);
            record = records[^1];
            records.RemoveAt(records.Count - 1);
            remainder = IndexTreeNode.CreateLeaf(node.PageNumber, records);
            return true;
        }

        var plan = node.InteriorPlan
            ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
        var lastChildIndex = plan.Children.Count - 1;
        if (TryExtractMaximumIndexRecord(
                plan.Children[lastChildIndex],
                comparer,
                out record,
                out var lastRemainder))
        {
            var children = plan.Children.ToArray();
            children[lastChildIndex] = lastRemainder;
            return TryCreateIndexInteriorNode(
                node.PageNumber,
                children,
                plan.Separators,
                comparer,
                out remainder);
        }

        var previousChildIndex = lastChildIndex - 1;
        if (!TryExtractMaximumIndexRecord(
                plan.Children[previousChildIndex],
                comparer,
                out var replacementSeparator,
                out var previousRemainder)
            || !TryInsertMinimumIndexRecord(
                plan.Children[lastChildIndex],
                plan.Separators[^1],
                comparer,
                out var expandedLast)
            || !TryExtractMaximumIndexRecord(
                expandedLast,
                comparer,
                out record,
                out lastRemainder))
        {
            record = null!;
            remainder = null!;
            return false;
        }

        var rebalancedChildren = plan.Children.ToArray();
        rebalancedChildren[previousChildIndex] = previousRemainder;
        rebalancedChildren[lastChildIndex] = lastRemainder;
        var rebalancedSeparators = plan.Separators.Select(value => value.ToArray()).ToArray();
        rebalancedSeparators[^1] = replacementSeparator;
        return TryCreateIndexInteriorNode(
            node.PageNumber,
            rebalancedChildren,
            rebalancedSeparators,
            comparer,
            out remainder);
    }

    private bool TryExtractMinimumIndexRecord(
        IndexTreeNode node,
        SqliteIndexRecordComparer comparer,
        out byte[] record,
        out IndexTreeNode remainder)
    {
        if (node.IsLeaf)
        {
            if (node.Records.Count < 2)
            {
                record = null!;
                remainder = null!;
                return false;
            }

            var records = new List<byte[]>(node.Records);
            record = records[0];
            records.RemoveAt(0);
            remainder = IndexTreeNode.CreateLeaf(node.PageNumber, records);
            return true;
        }

        var plan = node.InteriorPlan
            ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
        if (TryExtractMinimumIndexRecord(
                plan.Children[0],
                comparer,
                out record,
                out var firstRemainder))
        {
            var children = plan.Children.ToArray();
            children[0] = firstRemainder;
            return TryCreateIndexInteriorNode(
                node.PageNumber,
                children,
                plan.Separators,
                comparer,
                out remainder);
        }

        if (!TryExtractMinimumIndexRecord(
                plan.Children[1],
                comparer,
                out var replacementSeparator,
                out var secondRemainder)
            || !TryInsertMaximumIndexRecord(
                plan.Children[0],
                plan.Separators[0],
                comparer,
                out var expandedFirst)
            || !TryExtractMinimumIndexRecord(
                expandedFirst,
                comparer,
                out record,
                out firstRemainder))
        {
            record = null!;
            remainder = null!;
            return false;
        }

        var rebalancedChildren = plan.Children.ToArray();
        rebalancedChildren[0] = firstRemainder;
        rebalancedChildren[1] = secondRemainder;
        var rebalancedSeparators = plan.Separators.Select(value => value.ToArray()).ToArray();
        rebalancedSeparators[0] = replacementSeparator;
        return TryCreateIndexInteriorNode(
            node.PageNumber,
            rebalancedChildren,
            rebalancedSeparators,
            comparer,
            out remainder);
    }

    private bool TryInsertMinimumIndexRecord(
        IndexTreeNode node,
        byte[] record,
        SqliteIndexRecordComparer comparer,
        out IndexTreeNode expanded)
    {
        if (node.IsLeaf)
        {
            var records = new List<byte[]>(node.Records.Count + 1) { record };
            records.AddRange(node.Records);
            if (!CanBuildIndexLeafPage(records, comparer))
            {
                expanded = null!;
                return false;
            }

            expanded = IndexTreeNode.CreateLeaf(node.PageNumber, records);
            return true;
        }

        var plan = node.InteriorPlan
            ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
        if (!TryInsertMinimumIndexRecord(plan.Children[0], record, comparer, out var firstChild))
        {
            expanded = null!;
            return false;
        }

        var children = plan.Children.ToArray();
        children[0] = firstChild;
        return TryCreateIndexInteriorNode(
            node.PageNumber,
            children,
            plan.Separators,
            comparer,
            out expanded);
    }

    private bool TryInsertMaximumIndexRecord(
        IndexTreeNode node,
        byte[] record,
        SqliteIndexRecordComparer comparer,
        out IndexTreeNode expanded)
    {
        if (node.IsLeaf)
        {
            var records = new List<byte[]>(node.Records.Count + 1);
            records.AddRange(node.Records);
            records.Add(record);
            if (!CanBuildIndexLeafPage(records, comparer))
            {
                expanded = null!;
                return false;
            }

            expanded = IndexTreeNode.CreateLeaf(node.PageNumber, records);
            return true;
        }

        var plan = node.InteriorPlan
            ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
        var lastChildIndex = plan.Children.Count - 1;
        if (!TryInsertMaximumIndexRecord(
                plan.Children[lastChildIndex],
                record,
                comparer,
                out var lastChild))
        {
            expanded = null!;
            return false;
        }

        var children = plan.Children.ToArray();
        children[lastChildIndex] = lastChild;
        return TryCreateIndexInteriorNode(
            node.PageNumber,
            children,
            plan.Separators,
            comparer,
            out expanded);
    }

    private bool TryCreateIndexInteriorNode(
        uint pageNumber,
        IReadOnlyList<IndexTreeNode> children,
        IReadOnlyList<byte[]> separators,
        SqliteIndexRecordComparer comparer,
        out IndexTreeNode node)
    {
        try
        {
            var builder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                children[^1].PageNumber,
                comparer);
            for (var index = 0; index < separators.Count; index++)
            {
                builder.Append(
                    SqliteIndexInteriorCell.Create(
                        children[index].PageNumber,
                        CreateIndexLeafPlanningCell(separators[index])),
                    separators[index]);
            }

            _ = builder.Build();
            node = IndexTreeNode.CreateInterior(
                pageNumber,
                new IndexInteriorPlan(
                    children.ToArray(),
                    separators.Select(value => value.ToArray()).ToArray()));
            return true;
        }
        catch (InvalidOperationException)
        {
            node = null!;
            return false;
        }
    }

    private bool CanBuildIndexLeafPage(
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer)
    {
        try
        {
            var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
            foreach (var record in records)
                builder.Append(CreateIndexLeafPlanningCell(record), record);
            _ = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private byte[] BuildIndexInteriorPage(
        IndexInteriorPlan plan,
        SqliteIndexRecordComparer comparer,
        IIndexRebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        if (plan.Separators.Count != plan.Children.Count - 1)
            throw new InvalidOperationException("SQLite index-interior planning produced an invalid separator count.");

        var builder = new SqliteIndexInteriorPageBuilder(
            _pageSize,
            _usableSpace,
            plan.Children[^1].PageNumber,
            comparer);
        for (var childIndex = 0; childIndex < plan.Separators.Count; childIndex++)
        {
            var separator = plan.Separators[childIndex];
            var key = CreateIndexLeafCell(separator, allocator, overflowPages);
            builder.Append(
                SqliteIndexInteriorCell.Create(plan.Children[childIndex].PageNumber, key),
                separator);
        }

        return builder.Build();
    }

    private void MaterializeIndexTreeChildren(
        IReadOnlyList<IndexTreeNode> children,
        SqliteIndexRecordComparer comparer,
        IIndexRebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages,
        ICollection<PageImage> interiorPages,
        ICollection<PageImage> leafPages)
    {
        foreach (var child in children)
        {
            if (child.IsLeaf)
            {
                leafPages.Add(new PageImage(
                    child.PageNumber,
                    BuildIndexLeafPage(child.Records, comparer, allocator, overflowPages)));
                continue;
            }

            var plan = child.InteriorPlan
                ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
            MaterializeIndexTreeChildren(
                plan.Children,
                comparer,
                allocator,
                overflowPages,
                interiorPages,
                leafPages);
            interiorPages.Add(new PageImage(
                child.PageNumber,
                BuildIndexInteriorPage(plan, comparer, allocator, overflowPages)));
        }
    }

    private static int GetIndexTreeHeight(IndexTreeNode node)
        => node.IsLeaf
            ? 0
            : checked(1 + GetIndexTreeHeight(
                (node.InteriorPlan
                    ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan."))
                .Children[0]));

    private static IndexTreeNode[] CloneIndexTreeNodes(
        IReadOnlyList<IndexTreeNode> nodes,
        int start,
        int count)
    {
        if (start < 0 || count < 0 || start > nodes.Count - count)
            throw new ArgumentOutOfRangeException(nameof(start), "SQLite index child range is outside the planned tree.");

        var clone = new IndexTreeNode[count];
        for (var index = 0; index < count; index++)
            clone[index] = CloneIndexTreeNode(nodes[start + index]);
        return clone;
    }

    private static IndexTreeNode[] CloneIndexTreeNodes(IReadOnlyList<IndexTreeNode> nodes)
        => CloneIndexTreeNodes(nodes, 0, nodes.Count);

    private static IndexTreeNode CloneIndexTreeNode(IndexTreeNode node)
    {
        if (node.IsLeaf)
            return IndexTreeNode.CreateLeaf(node.PageNumber, new List<byte[]>(node.Records));

        var plan = node.InteriorPlan
            ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
        return IndexTreeNode.CreateInterior(
            node.PageNumber,
            new IndexInteriorPlan(
                CloneIndexTreeNodes(plan.Children),
                plan.Separators.Select(separator => separator.ToArray()).ToArray()));
    }

    private List<List<byte[]>> PartitionIndexLeafRecords(
        string treeDescription,
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer)
    {
        // Planning only needs each cell's encoded length to place page
        // boundaries; BuildIndexRecords already proved strict order over the
        // whole sequence, so re-decoding and re-comparing every record here
        // (through a throwaway page builder) was pure duplicated work. The fit
        // arithmetic mirrors SqliteIndexLeafPageBuilder.EnsureFits exactly so
        // page boundaries are unchanged.
        const int HeaderOffset = 0;
        var leafGroups = new List<List<byte[]>> { new() };
        var cellBytes = 0;
        var cellCount = 0;
        foreach (var record in records)
        {
            var encodedLength = SqliteIndexLeafCell.CalculateEncodedLength(
                checked((ulong)record.Length),
                _usableSpace);
            if (!IndexLeafCellFits(HeaderOffset, cellBytes, cellCount, encodedLength))
            {
                if (cellCount == 0 || leafGroups[^1].Count == 0)
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist {treeDescription} because one key cannot fit in a SQLite index leaf.");
                }

                leafGroups.Add([]);
                cellBytes = 0;
                cellCount = 0;
                if (!IndexLeafCellFits(HeaderOffset, cellBytes, cellCount, encodedLength))
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist {treeDescription} because one key cannot fit in a SQLite index leaf.");
                }
            }

            cellBytes = checked(cellBytes + encodedLength);
            cellCount++;
            leafGroups[^1].Add(record);
        }

        return leafGroups;
    }

    /// <summary>
    /// Mirrors <c>SqliteIndexLeafPageBuilder.EnsureFits</c>: whether one more
    /// cell and its pointer still fit in the leaf's usable space.
    /// </summary>
    private bool IndexLeafCellFits(int headerOffset, int cellBytes, int cellCount, int additionalCellLength)
    {
        if (cellCount == ushort.MaxValue)
            return false;

        var totalCellBytes = checked(cellBytes + additionalCellLength);
        var pointerArrayEnd = checked(
            headerOffset
            + SqliteBtreePageHeader.LeafHeaderSize
            + ((cellCount + 1) * sizeof(ushort)));
        return _usableSpace - totalCellBytes >= pointerArrayEnd;
    }

    private SqliteIndexLeafCell CreateIndexLeafPlanningCell(ReadOnlySpan<byte> record)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        return layout.UsesOverflow
            ? SqliteIndexLeafCell.Create(
                checked((ulong)record.Length),
                record[..layout.LocalPayloadLength],
                firstOverflowPage: 1,
                _usableSpace)
            : SqliteIndexLeafCell.Create(record, _usableSpace);
    }

    private byte[] BuildIndexLeafPage(
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer,
        IIndexRebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
        foreach (var record in records)
        {
            // BuildIndexRecords already proved this sequence strictly ordered and
            // decodable, and the cell is derived from the record right here.
            builder.AppendTrusted(CreateIndexLeafCell(record, allocator, overflowPages));
        }

        return builder.Build();
    }

    private SqliteTableLeafCell CreateTableLeafCell(
        long rowId,
        byte[] record,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        if (!layout.UsesOverflow)
            return SqliteTableLeafCell.Create(rowId, record, _usableSpace);

        var overflowPayload = record.AsSpan(layout.LocalPayloadLength);
        var payloadCapacity = _usableSpace - SqliteOverflowPageView.HeaderLength;
        var overflowPageCount = checked((uint)((overflowPayload.Length + payloadCapacity - 1) / payloadCapacity));
        var overflowPageNumbers = new uint[checked((int)overflowPageCount)];
        for (var pageOffset = 0; pageOffset < overflowPageNumbers.Length; pageOffset++)
            overflowPageNumbers[pageOffset] = allocator.ReservePage();

        for (var pageOffset = 0U; pageOffset < overflowPageCount; pageOffset++)
        {
            var pageNumber = overflowPageNumbers[pageOffset];
            var payloadOffset = checked((int)(pageOffset * (uint)payloadCapacity));
            var payloadLength = Math.Min(payloadCapacity, overflowPayload.Length - payloadOffset);
            var nextOverflowPage = pageOffset + 1 == overflowPageCount
                ? 0
                : overflowPageNumbers[pageOffset + 1];
            overflowPages.Add(new PageImage(
                pageNumber,
                SqliteOverflowPageView.Create(
                    _pageSize,
                    _usableSpace,
                    nextOverflowPage,
                    overflowPayload.Slice(payloadOffset, payloadLength)).ToArray()));
        }

        return SqliteTableLeafCell.Create(
            rowId,
            checked((ulong)record.Length),
            record.AsSpan(0, layout.LocalPayloadLength),
            overflowPageNumbers[0],
            _usableSpace);
    }

    private SqliteIndexLeafCell CreateIndexLeafCell(
        byte[] record,
        IIndexRebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        if (!layout.UsesOverflow)
            return SqliteIndexLeafCell.Create(record, _usableSpace);

        var overflowPayload = record.AsSpan(layout.LocalPayloadLength);
        var payloadCapacity = _usableSpace - SqliteOverflowPageView.HeaderLength;
        var overflowPageCount = checked((uint)((overflowPayload.Length + payloadCapacity - 1) / payloadCapacity));
        var overflowPageNumbers = new uint[checked((int)overflowPageCount)];
        for (var pageOffset = 0; pageOffset < overflowPageNumbers.Length; pageOffset++)
            overflowPageNumbers[pageOffset] = allocator.ReservePage();

        for (var pageOffset = 0U; pageOffset < overflowPageCount; pageOffset++)
        {
            var pageNumber = overflowPageNumbers[pageOffset];
            var payloadOffset = checked((int)(pageOffset * (uint)payloadCapacity));
            var payloadLength = Math.Min(payloadCapacity, overflowPayload.Length - payloadOffset);
            var nextPageNumber = pageOffset + 1 == overflowPageCount
                ? 0
                : overflowPageNumbers[pageOffset + 1];
            overflowPages.Add(new PageImage(
                pageNumber,
                SqliteOverflowPageView.Create(
                    _pageSize,
                    _usableSpace,
                    nextPageNumber,
                    overflowPayload.Slice(payloadOffset, payloadLength)).ToArray()));
        }

        return SqliteIndexLeafCell.Create(
            checked((ulong)record.Length),
            record.AsSpan(0, layout.LocalPayloadLength),
            overflowPageNumbers[0],
            _usableSpace);
    }

    private IEnumerable<(long RowId, byte[] Record)> EnumerateRowCells(string name, EmbeddedTable table)
    {
        // Pair every row with its tracked rowid and emit in ascending rowid order so the
        // leaf cells are sorted, as a valid SQLite b-tree requires. This preserves the
        // exact rowids across persistence for both alias and hidden-rowid tables.
        var ordered = table.Rows
            .Select((row, index) => (
                RowId: index < table.RowIds.Count ? table.RowIds[index] : index + 1,
                Row: row))
            .OrderBy(entry => entry.RowId);
        foreach (var (rowId, row) in ordered)
            yield return (rowId, BuildTableRecord(table, row));
    }

    /// <summary>
    /// Encodes one rowid-table row as its SQLite record payload.
    /// </summary>
    private byte[] BuildTableRecord(EmbeddedTable table, IReadOnlyList<SqlValue> row)
    {
        var record = ProjectStoredRow(table, row);
        var aliasIndex = table.RowidAliasColumnIndex;
        if (aliasIndex >= 0)
        {
            // A single-column INTEGER PRIMARY KEY is a rowid alias: store its value as
            // the SQLite rowid and NULL in the record, exactly as SQLite does.
            var storedAliasIndex = 0;
            for (var columnIndex = 0; columnIndex < aliasIndex; columnIndex++)
            {
                if (!table.ColumnDefinitions[columnIndex].IsGenerated
                    || table.ColumnDefinitions[columnIndex].GeneratedStored)
                {
                    storedAliasIndex++;
                }
            }

            record[storedAliasIndex] = SqlValue.Null;
        }

        return SqliteRecordCodec.Encode(record, _textEncoding);
    }

    private static SqlValue[] ProjectStoredRow(
        EmbeddedTable table,
        IReadOnlyList<SqlValue> row)
    {
        if (row.Count != table.ColumnDefinitions.Length)
            throw new EmbeddedSqlException($"The managed file engine cannot persist table '{table.Name}' because a row has an invalid column count.");

        var stored = new List<SqlValue>(row.Count);
        for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
        {
            var column = table.ColumnDefinitions[columnIndex];
            if (!column.IsGenerated || column.GeneratedStored)
                stored.Add(row[columnIndex]);
        }

        return stored.ToArray();
    }

    private IReadOnlyList<byte[]> BuildWithoutRowidTableRecords(
        string tableName,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        SqliteIndexRecordComparer comparer)
    {
        if (table.Rows.Count != table.RowIds.Count)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its row and rowid counts are inconsistent.");
        }

        table.ValidateRows(tableName, table.Rows);
        var records = new List<WithoutRowidRecord>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            if (row.Length != table.ColumnDefinitions.Length)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because a row has an invalid column count.");
            }

            var key = primaryKeySchema.ProjectKey(row);
            if (key.Any(value => value.Kind == SqlValueKind.Null))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary key contains NULL.");
            }

            var record = SqliteRecordCodec.Encode(
                OrderWithoutRowidRecord(tableName, table, primaryKeySchema, row),
                _textEncoding);
            comparer.Validate(record);
            records.Add(new WithoutRowidRecord(record, key));
        }

        records.Sort((left, right) => comparer.Compare(left.Record, right.Record));
        for (var index = 1; index < records.Count; index++)
        {
            if (comparer.Compare(records[index - 1].Key, records[index].Key) >= 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary keys are not strictly increasing in declared key order.");
            }
            if (comparer.Compare(records[index - 1].Record, records[index].Record) >= 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its complete SQLite index records are not strictly ordered.");
            }
        }

        return records.Select(record => record.Record).ToArray();
    }

    private static SqlValue[] OrderWithoutRowidRecord(
        string tableName,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        IReadOnlyList<SqlValue> row)
    {
        var storedColumnCount = table.ColumnDefinitions.Count(
            column => !column.IsGenerated || column.GeneratedStored);
        var values = new SqlValue[storedColumnCount];
        var primaryKeyColumns = new bool[row.Count];
        var destination = 0;
        foreach (var term in primaryKeySchema.Terms)
        {
            if (term.ColumnIndex >= row.Count || primaryKeyColumns[term.ColumnIndex])
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary-key metadata is inconsistent.");
            }
            if (table.ColumnDefinitions[term.ColumnIndex].IsGenerated
                && !table.ColumnDefinitions[term.ColumnIndex].GeneratedStored)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because VIRTUAL generated column '{term.ColumnName}' cannot be part of its primary key.");
            }

            primaryKeyColumns[term.ColumnIndex] = true;
            values[destination++] = row[term.ColumnIndex];
        }

        for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
        {
            var column = table.ColumnDefinitions[columnIndex];
            if (!primaryKeyColumns[columnIndex] && (!column.IsGenerated || column.GeneratedStored))
                values[destination++] = row[columnIndex];
        }

        if (destination != values.Length)
            throw new InvalidOperationException("WITHOUT ROWID record column ordering is incomplete.");

        return values;
    }

    private static SqlValue[] RestoreWithoutRowidRecord(
        string tableName,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        IReadOnlyList<SqlValue> storedValues)
    {
        var storedColumnCount = table.ColumnDefinitions.Count(
            column => !column.IsGenerated || column.GeneratedStored);
        if (storedValues.Count > storedColumnCount)
        {
            throw new InvalidDataException(
                $"Stored WITHOUT ROWID table '{tableName}' record has {storedValues.Count} stored column(s), but the schema requires {storedColumnCount}.");
        }

        var row = new SqlValue[table.ColumnDefinitions.Length];
        var primaryKeyColumns = new bool[row.Length];
        var source = 0;
        foreach (var term in primaryKeySchema.Terms)
        {
            if (term.ColumnIndex >= row.Length || primaryKeyColumns[term.ColumnIndex])
            {
                throw new InvalidDataException(
                    $"Stored WITHOUT ROWID table '{tableName}' has inconsistent primary-key metadata.");
            }

            // Key columns lead the record, so a short row can never truncate one.
            if (source >= storedValues.Count)
            {
                throw new InvalidDataException(
                    $"Stored WITHOUT ROWID table '{tableName}' record is missing primary-key value(s).");
            }

            primaryKeyColumns[term.ColumnIndex] = true;
            row[term.ColumnIndex] = storedValues[source++];
        }

        for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
        {
            var column = table.ColumnDefinitions[columnIndex];
            if (column.IsGenerated && !column.GeneratedStored)
            {
                row[columnIndex] = SqlValue.Null;
            }
            else if (!primaryKeyColumns[columnIndex])
            {
                // ADD COLUMN appends past the key columns without rewriting existing
                // records, so missing trailing columns read as their declared default.
                row[columnIndex] = source < storedValues.Count
                    ? storedValues[source]
                    : column.DefaultValue ?? SqlValue.Null;
                source++;
            }
        }

        if (source < storedValues.Count)
            throw new InvalidDataException($"Stored WITHOUT ROWID table '{tableName}' record has trailing values.");

        return row;
    }

    private IReadOnlyList<byte[]> BuildIndexRecords(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        SqliteIndexRecordComparer comparer)
    {
        if (table.Rows.Count != table.RowIds.Count)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist index '{index.Name}' because table '{tableName}' has inconsistent row and rowid counts.");
        }

        var storageColumns = table.WithoutRowid
            ? GetWithoutRowidIndexStorageColumns(table, index)
            : null;
        var decorated = new List<(SqlValue[] SortKey, byte[] Record)>(table.Rows.Count);
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            if (row.Length != table.ColumnDefinitions.Length)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because table '{tableName}' has a row with an invalid column count.");
            }

            var rowId = table.HasRowid ? table.RowIds[rowIndex] : (long?)null;
            if (!IndexExpressionSemantics.Qualifies(
                    index,
                    table,
                    row,
                    rowId,
                    _indexExpressionEvaluator.EvaluateIndexExpression))
            {
                continue;
            }
            var key = IndexExpressionSemantics.ProjectKey(
                index,
                table,
                row,
                rowId,
                _indexExpressionEvaluator.EvaluateIndexExpression);

            SqlValue[] values;
            if (table.WithoutRowid)
            {
                values = new SqlValue[storageColumns!.Count];
                Array.Copy(key, values, key.Length);
                for (var column = index.Columns.Count; column < storageColumns.Count; column++)
                    values[column] = row[storageColumns[column].ColumnIndex];
            }
            else
            {
                values = new SqlValue[index.Columns.Count + 1];
                Array.Copy(key, values, key.Length);
                values[^1] = SqlValue.Integer(rowId!.Value);
            }
            var record = SqliteRecordCodec.Encode(values, _textEncoding);
            // `values` is already the decoded form of `record`: every SqlValue
            // factory normalises on construction (SqlValue.Real folds NaN to
            // NULL), and Encode self-checks its header/body sizing. Decoding the
            // record back just to obtain a sort key cost one decode plus a fresh
            // value array and string per row of every index.
            decorated.Add((values, record));
        }

        SortPropagatingComparerExceptions(decorated, (left, right) => comparer.Compare(left.SortKey, right.SortKey));
        var records = new List<byte[]>(decorated.Count);
        for (var indexPosition = 0; indexPosition < decorated.Count; indexPosition++)
        {
            if (indexPosition > 0
                && comparer.Compare(decorated[indexPosition - 1].SortKey, decorated[indexPosition].SortKey) >= 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because its complete SQLite index keys are not strictly ordered.");
            }

            records.Add(decorated[indexPosition].Record);
        }

        return records;
    }

    /// <summary>
    /// Sorts <paramref name="list"/> with <paramref name="comparison"/>, unwrapping the
    /// <see cref="InvalidOperationException"/> ("Failed to compare two elements in the
    /// array.") that <see cref="List{T}.Sort(Comparison{T})"/> wraps around any exception a
    /// comparison delegate throws. An application-defined collation callback registered via
    /// <c>RegisterCollation</c> can throw arbitrary exceptions (including
    /// non-Ahtola ones), and callers must see that original exception, not an opaque
    /// sort-failure wrapper that discards it as an inner exception.
    /// </summary>
    internal static void SortPropagatingComparerExceptions<T>(List<T> list, Comparison<T> comparison)
    {
        try
        {
            list.Sort(comparison);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal SqliteIndexRecordComparer CreateIndexComparer(
        EmbeddedTable table,
        EmbeddedIndex index,
        bool allowDeferredCustomCollation = false,
        bool forceDeferCustomCollation = false)
    {
        if (!table.WithoutRowid)
        {
            var rowidTerms = index.Columns
                .Select(column => new SqliteIndexComparisonTerm(
                    column.Descending ? SqliteKeySortOrder.Descending : SqliteKeySortOrder.Ascending,
                    GetIndexCollation(table, column)))
                .ToArray();
            return CreateIndexComparerOrThrowNoSuchCollation(
                index,
                rowidTerms,
                allowDeferredCustomCollation,
                forceDeferCustomCollation);
        }

        var terms = GetWithoutRowidIndexStorageColumns(table, index)
            .Select(column => new SqliteIndexComparisonTerm(
                column.Descending ? SqliteKeySortOrder.Descending : SqliteKeySortOrder.Ascending,
                GetIndexCollation(table, column)))
            .ToArray();
        return CreateIndexComparerOrThrowNoSuchCollation(
            index,
            terms,
            allowDeferredCustomCollation,
            forceDeferCustomCollation);
    }

    /// <summary>
    /// Builds the comparer for <paramref name="index"/>'s complete key terms, converting the
    /// <see cref="SqliteIndexRecordComparer"/> constructor's <see cref="NotSupportedException"/>
    /// (thrown only when <paramref name="allowDeferredCustomCollation"/> is <see langword="false"/>
    /// and a term names a collation with neither a built-in implementation nor a bound
    /// application-defined callback) into the SQLite-style
    /// <see cref="EmbeddedSqlException"/> a write against this index must fail closed with —
    /// never a raw <see cref="NotSupportedException"/> that leaks a managed-engine implementation
    /// detail to callers expecting SQLite's "no such collation sequence" wording.
    /// </summary>
    private SqliteIndexRecordComparer CreateIndexComparerOrThrowNoSuchCollation(
        EmbeddedIndex index,
        SqliteIndexComparisonTerm[] terms,
        bool allowDeferredCustomCollation,
        bool forceDeferCustomCollation = false)
    {
        try
        {
            return new SqliteIndexRecordComparer(
                _textEncoding,
                terms,
                allowDeferredCustomCollation,
                forceDeferCustomCollation);
        }
        catch (NotSupportedException exception)
        {
            var unavailable = terms.FirstOrDefault(
                term => !term.Collation.IsAvailable || !term.Collation.IsSupportedByManagedIndexWriter);
            var name = unavailable?.Collation.Name ?? "?";
            throw new EmbeddedSqlException(
                $"The managed file engine cannot use index '{index.Name}' because no such collation sequence: {name}",
                exception);
        }
    }

    internal SqliteIndexRecordComparer CreatePrimaryKeyComparer(SqlitePrimaryKeySchema schema)
        => new(
            _textEncoding,
            schema.Terms.Select(term => new SqliteIndexComparisonTerm(term.SortOrder, term.Collation)).ToArray());

    /// <summary>
    /// Projects a row into the exact comparison-key shape the durable pager uses when persisting
    /// <paramref name="index"/> (or, when <paramref name="index"/> is <see langword="null"/>, the
    /// table's own WITHOUT ROWID primary-key b-tree), so a transaction's mutation-overlay rows sort
    /// identically to — and merge deterministically with — rows read from the committed b-tree via
    /// <see cref="CreateIndexComparer"/> / <see cref="CreatePrimaryKeyComparer"/>.
    /// </summary>
    internal SqlValue[] BuildIndexMergeKey(
        EmbeddedTable table,
        EmbeddedIndex? index,
        SqlValue[] values,
        long? rowId,
        Func<Expression, EmbeddedTable, SqlValue[], long?, SqlValue> evaluateExpression)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(evaluateExpression);

        if (index is null)
        {
            var primaryKeySchema = table.PrimaryKeySchema
                ?? throw new InvalidOperationException(
                    $"WITHOUT ROWID table '{table.Name}' is missing its primary-key metadata.");
            return primaryKeySchema.ProjectKey(values);
        }

        if (!table.WithoutRowid)
        {
            var key = IndexExpressionSemantics.ProjectKey(index, table, values, rowId, evaluateExpression);
            var withRowId = new SqlValue[key.Length + 1];
            Array.Copy(key, withRowId, key.Length);
            withRowId[^1] = SqlValue.Integer(
                rowId ?? throw new InvalidOperationException(
                    $"Rowid table '{table.Name}' index seek requires a rowid."));
            return withRowId;
        }

        var storageColumns = GetWithoutRowidIndexStorageColumns(table, index);
        var storageKey = new SqlValue[storageColumns.Count];
        for (var position = 0; position < storageColumns.Count; position++)
        {
            var column = storageColumns[position];
            storageKey[position] = column.ColumnIndex >= 0
                ? values[column.ColumnIndex]
                : evaluateExpression(column.Expression!, table, values, rowId);
        }

        return storageKey;
    }

    private IReadOnlyList<EmbeddedIndexColumn> GetWithoutRowidIndexStorageColumns(
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        var primaryKeySchema = table.PrimaryKeySchema
            ?? throw new EmbeddedSqlException(
                $"The managed file engine cannot persist index '{index.Name}' because WITHOUT ROWID table '{table.Name}' is missing primary-key metadata.");
        var columns = new List<EmbeddedIndexColumn>(index.Columns);
        foreach (var term in primaryKeySchema.Terms)
        {
            var keyCollation = term.Collation.Name
                ?? throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because primary-key collation metadata is unavailable.");
            var alreadyPresent = index.Columns.Any(column =>
                column.ColumnIndex == term.ColumnIndex
                && string.Equals(
                    GetIndexCollation(table, column).Name,
                    keyCollation,
                    StringComparison.OrdinalIgnoreCase));
            if (alreadyPresent)
                continue;

            columns.Add(new EmbeddedIndexColumn(
                term.ColumnName,
                term.ColumnIndex,
                keyCollation,
                term.SortOrder == SqliteKeySortOrder.Descending));
        }

        return columns;
    }

    /// <summary>
    /// Resolves the collation to use for one index storage column, binding
    /// an application-defined comparison delegate when a runtime resolver is
    /// attached and a callback is registered under the collation's name.
    /// This also lets an application override BINARY/NOCASE/RTRIM by
    /// registering a callback under that exact name; ordinary built-in
    /// columns are unaffected because no callback is registered for them,
    /// so the resolver returns <see langword="null"/> and default built-in
    /// behavior is preserved. With no resolver attached (the true
    /// cold-start catalog-load path) or no callback registered under the
    /// name, this returns a collation descriptor with no bound delegate;
    /// callers decide whether that is fatal (write/plan/seek) or
    /// deferrable (initial structural catalog load).
    /// </summary>
    private SqliteKeyCollation GetIndexCollation(EmbeddedTable table, EmbeddedIndexColumn column)
    {
        var collation = SqliteKeyCollation.FromName(IndexExpressionSemantics.GetCollationName(table, column));
        if (_collationResolver is null || collation.Name is null)
            return collation;

        var comparison = _collationResolver(collation.Name);
        return comparison is null ? collation : collation.WithComparison(comparison);
    }

    /// <summary>
    /// Checks whether <paramref name="index"/>'s partial-index predicate or index-expression
    /// bodies embed a <c>COLLATE</c> reference that this store cannot currently resolve — neither
    /// a built-in collation nor one bound via <see cref="SetCollationResolver"/> — so that
    /// <see cref="ValidateStoredIndex"/> can decline to (re)evaluate the predicate/expression
    /// through <see cref="BuildIndexRecords"/> before the caller has a chance to register it.
    /// This mirrors <see cref="SqliteIndexRecordComparer.HasDeferredTerms"/>, which covers only
    /// collations trailing an indexed term (column-level), never ones nested inside a predicate
    /// or expression body.
    /// </summary>
    private bool TryFindUnresolvedEmbeddedIndexCollation(EmbeddedIndex index, out string? unresolvedName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IndexExpressionSemantics.CollectEmbeddedCollationNames(index, names);
        foreach (var name in names)
        {
            if (IsCollationNameResolvable(name))
                continue;

            unresolvedName = name;
            return true;
        }

        unresolvedName = null;
        return false;
    }

    private bool IsCollationNameResolvable(string name)
    {
        var collation = SqliteKeyCollation.FromName(name);
        if (collation.IsSupportedByManagedIndexWriter)
            return true;

        return _collationResolver is not null && _collationResolver(name) is not null;
    }

    private PreparedSchemaTree BuildSchemaTree(
        IReadOnlyList<ManagedSchemaRow> entries,
        RebuildPageAllocator allocator)
    {
        var cells = new List<SqliteTableLeafCell>(entries.Count);
        var overflowPages = new List<PageImage>();
        long rowId = 1;
        foreach (var entry in entries)
        {
            var record = SqliteRecordCodec.Encode(
                [
                    SqlValue.Text(entry.Type),
                    SqlValue.Text(entry.Name),
                    SqlValue.Text(entry.TableName),
                    SqlValue.Integer(entry.RootPage),
                    entry.Sql is null ? SqlValue.Null : SqlValue.Text(entry.Sql),
                ],
                _textEncoding);
            try
            {
                cells.Add(CreateTableLeafCell(rowId++, record, allocator, overflowPages));
            }
            catch (ArgumentException exception)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist the schema for '{entry.Name}' because its row cannot fit in a SQLite schema page.",
                    exception);
            }
        }

        if (TryBuildSchemaLeafPage(cells, isFirstPage: true, out var rootPage))
        {
            return new PreparedSchemaTree(
                rootPage,
                Array.Empty<PageImage>(),
                Array.Empty<PageImage>(),
                overflowPages);
        }

        var leafGroups = PartitionSchemaLeafCells(cells);
        var leafPages = new List<PageImage>(leafGroups.Count);
        var leafChildren = new TableTreeChild[leafGroups.Count];
        for (var leafIndex = 0; leafIndex < leafGroups.Count; leafIndex++)
        {
            var pageNumber = allocator.ReservePage();
            leafPages.Add(new PageImage(
                pageNumber,
                BuildSchemaLeafPage(leafGroups[leafIndex], isFirstPage: false)));
            leafChildren[leafIndex] = new TableTreeChild(
                pageNumber,
                leafGroups[leafIndex][^1].RowId);
        }

        IReadOnlyList<TableTreeChild> levelChildren = leafChildren;
        var interiorPages = new List<PageImage>();
        while (!TryBuildTableInteriorPage(levelChildren, isFirstPage: true, out rootPage))
        {
            var groups = PartitionTableInteriorChildren(levelChildren);
            var parentChildren = new TableTreeChild[groups.Count];
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var pageNumber = allocator.ReservePage();
                interiorPages.Add(new PageImage(
                    pageNumber,
                    BuildTableInteriorPage(group)));
                parentChildren[groupIndex] = new TableTreeChild(pageNumber, group[^1].MaximumRowId);
            }

            levelChildren = parentChildren;
        }

        return new PreparedSchemaTree(rootPage, interiorPages, leafPages, overflowPages);
    }

    private List<List<SqliteTableLeafCell>> PartitionSchemaLeafCells(
        IReadOnlyList<SqliteTableLeafCell> cells)
    {
        var groups = new List<List<SqliteTableLeafCell>> { new() };
        foreach (var cell in cells)
        {
            var group = groups[^1];
            group.Add(cell);
            if (TryBuildSchemaLeafPage(group, isFirstPage: false, out _))
                continue;

            group.RemoveAt(group.Count - 1);
            if (group.Count == 0)
            {
                throw new EmbeddedSqlException(
                    "The managed file engine cannot persist a sqlite_schema row because it does not fit in a SQLite schema page.");
            }

            groups.Add([cell]);
            if (!TryBuildSchemaLeafPage(groups[^1], isFirstPage: false, out _))
            {
                throw new EmbeddedSqlException(
                    "The managed file engine cannot persist a sqlite_schema row because it does not fit in a SQLite schema page.");
            }
        }

        return groups;
    }

    private byte[] BuildSchemaLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        bool isFirstPage)
    {
        if (!TryBuildSchemaLeafPage(cells, isFirstPage, out var page))
        {
            throw new InvalidOperationException(
                "SQLite schema table-leaf cells and their pointer array do not fit in the page's usable space.");
        }

        return page;
    }

    private bool TryBuildSchemaLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        bool isFirstPage,
        out byte[] page)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage);
            foreach (var cell in cells)
                builder.Append(cell);
            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private static List<ManagedSchemaRow> BuildSchemaEntries(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        IReadOnlyDictionary<string, uint> rootPages,
        IReadOnlyDictionary<string, uint> indexRootPages)
    {
        var entries = new List<ManagedSchemaRow>();
        foreach (var name in tables.Keys)
            entries.Add(ManagedSchemaRowFactory.ForTable(name, tables[name], rootPages[name]));

        foreach (var definition in virtualTables.Values.OrderBy(
                     static value => value.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(ManagedSchemaRowFactory.ForVirtualTable(definition));
        }

        foreach (var tableName in tables.Keys)
        {
            foreach (var index in tables[tableName].Indexes)
            {
                if (!indexRootPages.TryGetValue(index.Name, out var rootPage))
                {
                    throw new InvalidOperationException(
                        $"SQLite schema construction is missing root page for index '{index.Name}'.");
                }

                entries.Add(ManagedSchemaRowFactory.ForIndex(
                    tableName,
                    tables[tableName],
                    index,
                    rootPage,
                    ManagedSchemaSqlForm.Persisted));
            }
        }

        foreach (var name in views.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            entries.Add(ManagedSchemaRowFactory.ForView(views[name]));

        foreach (var trigger in triggers.Values
                     .OrderBy(value => value.DeclarationOrder)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(ManagedSchemaRowFactory.ForTrigger(trigger));
        }

        return entries;
    }

    private IReadOnlyList<IndexDefinition> GetIndexDefinitions(
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, EmbeddedDatabase.VirtualTableDefinition> virtualTables,
        IReadOnlyDictionary<string, EmbeddedTable>? previousTables = null)
    {
        var names = new HashSet<string>(tables.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var name in views.Keys)
        {
            if (!names.Add(name))
                throw new EmbeddedSqlException($"The managed file engine cannot persist duplicate schema name '{name}'.");
        }
        foreach (var name in triggers.Keys)
        {
            if (!names.Add(name))
                throw new EmbeddedSqlException($"The managed file engine cannot persist duplicate schema name '{name}'.");
        }
        foreach (var name in virtualTables.Keys)
        {
            if (!names.Add(name))
                throw new EmbeddedSqlException($"The managed file engine cannot persist duplicate schema name '{name}'.");
        }

        var definitions = new List<IndexDefinition>();
        foreach (var tableName in tableNames)
        {
            var table = tables[tableName];
            foreach (var index in table.Indexes)
            {
                var structuralOnly = _collationResolver is null
                    || IsIndexUnchangedFromPrevious(tableName, table, index, previousTables);
                ValidateIndexRepresentable(tableName, table, index, structuralOnly: structuralOnly);
                if (!names.Add(index.Name))
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist index '{index.Name}' because its schema name is already in use.");
                }

                definitions.Add(new IndexDefinition(tableName, table, index));
            }
        }

        return definitions;
    }

    private void ValidateIndexRepresentable(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        bool structuralOnly = false)
    {
        ArgumentNullException.ThrowIfNull(index);
        IndexExpressionSemantics.ValidateDefinition(tableName, table, index);
        IndexExpressionSemantics.ValidateRoundTrip(tableName, table, index);

        foreach (var column in index.Columns)
        {
            if (!column.IsExpression
                && !string.Equals(table.Columns[column.ColumnIndex], column.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because its column metadata is inconsistent.");
            }
            var collation = GetIndexCollation(table, column);
            if (collation.IsSupportedByManagedIndexWriter)
                continue;

            // A structural-only pass (initial catalog load, before this store
            // has a runtime collation resolver) can neither prove nor
            // disprove that the durable order/content for this
            // application-defined collation is still valid, so it defers
            // that judgement instead of failing the whole catalog open.
            // Everything else about the index — page layout, cell/record
            // decodability, schema round-trip — is still fully validated.
            // Ordering, content, and uniqueness are (re)validated once a
            // matching collation callback is registered; see
            // EmbeddedDatabase.RegisterCollation.
            if (structuralOnly)
                continue;

            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist index '{index.Name}' because no such collation sequence: {collation.Name}");
        }

        if (table.WithoutRowid)
        {
            var primaryKeySchema = table.PrimaryKeySchema
                ?? throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because WITHOUT ROWID table '{tableName}' is missing primary-key metadata.");
            try
            {
                primaryKeySchema.EnsureSupportedByManagedIndexWriter();
            }
            catch (NotSupportedException exception)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because its WITHOUT ROWID primary-key suffix is unsupported: {exception.Message}",
                    exception);
            }
        }
    }

    private static EmbeddedIndex CreateIndexDefinition(
        string tableName,
        EmbeddedTable table,
        CreateIndexStatement statement)
        => EmbeddedIndexFactory.Create(tableName, table, statement);

    private void ValidateStoredIndex(
        ManagedSchemaRow entry,
        EmbeddedTable table,
        EmbeddedIndex index,
        ISet<uint> occupiedBtreePages,
        bool structuralOnly = false)
    {
        if (entry.RootPage < 2 || entry.RootPage > _pager.CommittedPageCount)
        {
            throw new EmbeddedSqlException(
                $"Stored index '{entry.Name}' has invalid rootpage {entry.RootPage}.");
        }

        var overflowReader = new SqliteOverflowChainReader(_pager, _header);
        var comparer = CreateIndexComparer(table, index, allowDeferredCustomCollation: structuralOnly);
        IReadOnlyList<byte[]> actualRecords;
        try
        {
            var rootPage = _pager.ReadCommittedPage(entry.RootPage);
            var rootHeader = SqliteBtreePageHeader.Parse(rootPage);
            actualRecords = rootHeader.PageType switch
            {
                SqliteBtreePageType.IndexLeaf => ReadIndexLeafRecords(rootPage, overflowReader, comparer),
                SqliteBtreePageType.IndexInterior => ReadIndexInteriorRecords(
                    entry,
                    rootPage,
                    overflowReader,
                    occupiedBtreePages,
                    comparer),
                _ => throw new InvalidDataException(
                    $"SQLite index root page has unsupported page type {rootHeader.PageType}."),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new EmbeddedSqlException(
                $"Stored index '{entry.Name}' is not a valid supported SQLite index b-tree.",
                exception);
        }

        if (comparer.HasDeferredTerms)
        {
            // Page shape and record decodability are proven above, but this
            // index depends on an application-defined collation with no
            // registered callback yet, so its order, content, and uniqueness
            // cannot be checked here. It stays unusable for planning/writes
            // until EmbeddedDatabase.RegisterCollation revalidates it with a
            // real callback (see RevalidateIndexOrderAndContent).
            return;
        }

        if (TryFindUnresolvedEmbeddedIndexCollation(index, out var unresolvedName))
        {
            // The partial-index predicate or index-expression body itself embeds a
            // COLLATE reference (as opposed to a trailing COLLATE on an indexed term,
            // already covered by comparer.HasDeferredTerms above) that has no bound
            // callback yet. Evaluating BuildIndexRecords below would call into
            // EmbeddedDatabase's private expression evaluator and fail hard with "no
            // such collation sequence" — before the caller ever gets a chance to
            // register it. Structural passes defer judgement the same way deferred
            // column terms do; a live re-check (structuralOnly: false, from
            // RegisterCollation/RevalidateIndexOrderAndContent) still fails closed so
            // a genuinely-missing collation is never silently treated as valid.
            if (structuralOnly)
                return;

            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist index '{entry.Name}' because no such collation sequence: {unresolvedName}");
        }

        ValidateUniqueIndexRecords(entry.TableName, table, index, actualRecords);
        var expectedRecords = BuildIndexRecords(entry.TableName, table, index, comparer);
        if (actualRecords.Count != expectedRecords.Count)
        {
            throw new EmbeddedSqlException(
                $"Stored index '{entry.Name}' has {actualRecords.Count} record(s), but table '{entry.TableName}' requires {expectedRecords.Count}.");
        }

        for (var recordIndex = 0; recordIndex < expectedRecords.Count; recordIndex++)
        {
            if (!actualRecords[recordIndex].AsSpan().SequenceEqual(expectedRecords[recordIndex]))
            {
                throw new EmbeddedSqlException(
                    $"Stored index '{entry.Name}' does not match table '{entry.TableName}' at record {recordIndex}.");
            }
        }
    }

    private void ValidateUniqueIndexRecords(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        IReadOnlyList<byte[]> records)
    {
        if (!index.Unique)
            return;

        var logicalComparer = new SqliteIndexRecordComparer(
            _textEncoding,
            index.Columns.Select(column => new SqliteIndexComparisonTerm(
                column.Descending ? SqliteKeySortOrder.Descending : SqliteKeySortOrder.Ascending,
                GetIndexCollation(table, column))).ToArray());
        SqlValue[]? previousKey = null;
        foreach (var record in records)
        {
            var values = SqliteRecordCodec.Decode(record, _textEncoding);
            if (values.Length < index.Columns.Count)
            {
                throw new EmbeddedSqlException(
                    $"Stored unique index '{index.Name}' on table '{tableName}' has a truncated key record.");
            }

            var key = values.Take(index.Columns.Count).ToArray();
            if (key.Any(value => value.Kind == SqlValueKind.Null))
                continue;
            if (previousKey is not null && logicalComparer.Compare(previousKey, key) == 0)
            {
                throw new EmbeddedSqlException(
                    $"Stored unique index '{index.Name}' on table '{tableName}' contains duplicate non-NULL keys.");
            }

            previousKey = key;
        }
    }

    /// <summary>
    /// Re-validates one durable index's on-disk order, content, and
    /// uniqueness using the collation resolver currently attached to this
    /// store, without re-running the full catalog-load pass. Called by
    /// <c>EmbeddedDatabase</c> when a collation callback is registered,
    /// replaced, or unregistered, to decide whether an existing
    /// custom-collation index becomes eligible for planning/writes (clean)
    /// or must be treated as unusable until REINDEX rebuilds it (dirty).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the index's durable order/content/
    /// uniqueness is proven consistent with the active collation callback;
    /// <see langword="false"/> otherwise, with <paramref name="failureReason"/>
    /// set to a human-readable explanation.
    /// </returns>
    internal bool RevalidateIndexOrderAndContent(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);
        if (!_indexRootPages.TryGetValue(index.Name, out var rootPage))
        {
            failureReason = $"Index '{index.Name}' has no durable root page to revalidate.";
            return false;
        }

        try
        {
            ValidateIndexRepresentable(tableName, table, index, structuralOnly: false);
            var entry = new ManagedSchemaRow("index", index.Name, tableName, rootPage, index.Sql);
            ValidateStoredIndex(entry, table, index, new HashSet<uint>(), structuralOnly: false);
            failureReason = null;
            return true;
        }
        catch (EmbeddedSqlException exception)
        {
            failureReason = exception.Message;
            return false;
        }
    }

    private IReadOnlyList<byte[]> ReadIndexLeafRecords(
        ReadOnlySpan<byte> page,
        SqliteOverflowChainReader overflowReader,
        SqliteIndexRecordComparer? comparer = null)
    {
        var leaf = SqliteIndexLeafPageView.Parse(
            page,
            _usableSpace,
            _textEncoding,
            overflowReader: overflowReader,
            recordComparer: comparer);
        var records = new byte[leaf.Cells.Count][];
        for (var index = 0; index < records.Length; index++)
            records[index] = leaf.GetRecord(index);
        return records;
    }

    private IReadOnlyList<byte[]> ReadIndexInteriorRecords(
        ManagedSchemaRow entry,
        ReadOnlySpan<byte> rootPage,
        SqliteOverflowChainReader overflowReader,
        ISet<uint> occupiedBtreePages,
        SqliteIndexRecordComparer? comparer = null)
    {
        return ReadIndexInteriorNodeRecords(
            entry,
            entry.RootPage,
            rootPage,
            overflowReader,
            occupiedBtreePages,
            comparer ?? new SqliteIndexRecordComparer(_textEncoding)).Records;
    }

    private IndexTreeReadResult ReadIndexInteriorNodeRecords(
        ManagedSchemaRow entry,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        SqliteOverflowChainReader overflowReader,
        ISet<uint> occupiedBtreePages,
        SqliteIndexRecordComparer comparer)
    {
        var interior = SqliteIndexInteriorPageView.Parse(
            pageImage,
            _usableSpace,
            _textEncoding,
            overflowReader: overflowReader,
            recordComparer: comparer);
        if (interior.Cells.Count == 0)
        {
            throw new InvalidDataException(
                $"Stored index '{entry.Name}' has an unsupported index-interior page {pageNumber} without a separator.");
        }

        SqliteBtreePageType? directChildType = null;
        foreach (var childPage in interior.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(interior.Header.RightMostChildPage))
        {
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references invalid child page {childPage}.");
            }

            var currentChildType = SqliteBtreePageHeader.Parse(_pager.ReadCommittedPage(childPage)).PageType;
            if (currentChildType is not (SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior))
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references unsupported child type {currentChildType}.");
            }
            if (directChildType is { } expectedChildType && currentChildType != expectedChildType)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
            }

            directChildType = currentChildType;
        }

        var records = new List<byte[]>();
        byte[]? previousRecord = null;
        int? childHeight = null;
        SqliteBtreePageType? childType = null;
        for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
        {
            var childPage = childIndex == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[childIndex].Cell.LeftChildPage;
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references invalid child page {childPage}.");
            }
            if (!occupiedBtreePages.Add(childPage))
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} reuses b-tree page {childPage} as a child.");
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            var childHeader = SqliteBtreePageHeader.Parse(childPageImage);
            if (childHeader.PageType is not (SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior))
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references unsupported child type {childHeader.PageType}.");
            }
            if (childType is { } expectedChildType && childHeader.PageType != expectedChildType)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
            }

            childType = childHeader.PageType;
            IndexTreeReadResult childResult;
            switch (childHeader.PageType)
            {
                case SqliteBtreePageType.IndexLeaf:
                    {
                        var leafRecords = ReadIndexLeafRecords(childPageImage, overflowReader, comparer);
                        if (leafRecords.Count == 0)
                        {
                            throw new InvalidDataException(
                                $"Stored index '{entry.Name}' has an empty leaf child page {childPage}.");
                        }

                        childResult = new IndexTreeReadResult(leafRecords, 0);
                        break;
                    }
                case SqliteBtreePageType.IndexInterior:
                    childResult = ReadIndexInteriorNodeRecords(
                        entry,
                        childPage,
                        childPageImage,
                        overflowReader,
                        occupiedBtreePages,
                        comparer);
                    break;
                default:
                    throw new InvalidOperationException("SQLite index child type validation is incomplete.");
            }

            if (childHeight is { } expectedHeight && childResult.Height != expectedHeight)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
            }

            childHeight = childResult.Height;
            AppendIndexRecords(
                entry.Name,
                records,
                childResult.Records,
                comparer,
                ref previousRecord,
                $"interior page {pageNumber} children");
            if (childIndex < interior.Cells.Count)
            {
                var separator = interior.GetRecord(childIndex);
                if (!comparer.HasDeferredTerms && comparer.Compare(childResult.Records[^1], separator) >= 0)
                {
                    throw new InvalidDataException(
                        $"Stored index '{entry.Name}' interior page {pageNumber} separator {childIndex} does not follow child page {childPage}.");
                }

                AppendIndexRecord(
                    entry.Name,
                    records,
                    separator,
                    comparer,
                    ref previousRecord,
                    $"interior page {pageNumber} children");
            }
        }

        return new IndexTreeReadResult(
            records,
            checked((childHeight ?? throw new InvalidDataException(
                $"Stored index '{entry.Name}' has an empty interior page {pageNumber}.")) + 1));
    }

    private static void AppendIndexRecords(
        string indexName,
        ICollection<byte[]> records,
        IReadOnlyList<byte[]> values,
        SqliteIndexRecordComparer comparer,
        ref byte[]? previousRecord,
        string level)
    {
        foreach (var value in values)
            AppendIndexRecord(indexName, records, value, comparer, ref previousRecord, level);
    }

    private static void AppendIndexRecord(
        string indexName,
        ICollection<byte[]> records,
        byte[] value,
        SqliteIndexRecordComparer comparer,
        ref byte[]? previousRecord,
        string level)
    {
        if (!comparer.HasDeferredTerms && previousRecord is not null && comparer.Compare(previousRecord, value) >= 0)
        {
            throw new InvalidDataException(
                $"Stored index '{indexName}' {level} are not globally ordered by their declared complete keys.");
        }

        records.Add(value);
        previousRecord = value;
    }

    private void ValidateSchemaEntries(IReadOnlyList<ManagedSchemaRow> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootPages = new HashSet<uint>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || !names.Add(entry.Name))
                throw new EmbeddedSqlException("Managed file database sqlite_schema has duplicate or empty object names.");

            switch (entry.Type)
            {
                case "table":
                    if (entry.Sql is null)
                        throw new EmbeddedSqlException($"Managed file database table '{entry.Name}' is missing SQL text.");
                    if (!string.Equals(entry.Name, entry.TableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table '{entry.Name}' has a mismatched sqlite_schema table name.");
                    }
                    if (entry.RootPage == 0)
                    {
                        var (declaration, _) = ManagedVirtualTableSchemaSql.Parse(entry.Sql);
                        if (!string.Equals(declaration.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database virtual table '{entry.Name}' has mismatched CREATE VIRTUAL TABLE SQL.");
                        }
                        break;
                    }
                    goto case "index";
                case "index":
                    if (entry.Sql is null
                        && !entry.Name.StartsWith(
                            $"sqlite_autoindex_{entry.TableName}_",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database index '{entry.Name}' has NULL SQL but is not an implicit constraint index.");
                    }
                    if (entry.RootPage < 2 || entry.RootPage > _pager.CommittedPageCount)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database {entry.Type} '{entry.Name}' has invalid rootpage {entry.RootPage}.");
                    }
                    if (!rootPages.Add(entry.RootPage))
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema reuses rootpage {entry.RootPage}.");
                    }
                    break;
                case "view":
                    if (entry.RootPage != 0 || !string.Equals(entry.Name, entry.TableName, StringComparison.OrdinalIgnoreCase))
                        throw new EmbeddedSqlException($"Managed file database view '{entry.Name}' has an invalid sqlite_schema rootpage or table name.");
                    break;
                case "trigger":
                    if (entry.RootPage != 0)
                        throw new EmbeddedSqlException($"Managed file database trigger '{entry.Name}' has a non-zero rootpage.");
                    break;
                default:
                    throw new EmbeddedSqlException(
                        $"Managed file database has unsupported sqlite_schema type '{entry.Type}'.");
            }
        }
    }

    private static string BuildCreateIndexSql(string tableName, EmbeddedIndex index)
        => IndexSqlFormatter.BuildCreateIndexSql(tableName, index);

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string ComputeSchemaSignature(IReadOnlyList<ManagedSchemaRow> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in EnumerateSignatureEntries(entries))
        {
            builder.Append(entry.Type).Append('\u0001')
                .Append(entry.Name).Append('\u0001')
                .Append(entry.TableName).Append('\u0001')
                .Append(entry.RootPage).Append('\u0001')
                .Append(entry.Sql).Append('\u0002');
        }

        return builder.ToString();
    }

    private static IEnumerable<ManagedSchemaRow> EnumerateSignatureEntries(
        IReadOnlyList<ManagedSchemaRow> entries)
    {
        foreach (var entry in entries
                     .Where(entry => entry.Type == "table")
                     .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return entry;
        }

        var indexesByTable = entries
            .Where(entry => entry.Type == "index")
            .GroupBy(entry => entry.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in indexesByTable.Keys.OrderBy(
                     name => name,
                     StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in indexesByTable[tableName])
            {
                yield return entry;
            }
        }

        foreach (var entry in entries
                     .Where(entry => entry.Type is "view" or "trigger")
                     .OrderBy(entry => entry.Type, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return entry;
        }
    }

    private static string RequireText(SqlValue value, string field)
        => value.Kind == SqlValueKind.Text
            ? value.AsText()
            : throw new EmbeddedSqlException($"Managed file database sqlite_schema column '{field}' is not text.");

    private static string? RequireNullableText(SqlValue value, string field)
        => value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Text => value.AsText(),
            _ => throw new EmbeddedSqlException(
                $"Managed file database sqlite_schema column '{field}' is neither text nor NULL."),
        };

    private static long RequireInteger(SqlValue value, string field)
        => value.Kind == SqlValueKind.Integer
            ? value.AsInteger()
            : throw new EmbeddedSqlException($"Managed file database sqlite_schema column '{field}' is not an integer.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Assigns page numbers for one complete catalog replacement.
    /// </summary>
    /// <remarks>
    /// A regular replacement consumes validated former freelist leaves first,
    /// then may reinitialize other old pages because the replacement transaction
    /// proves its entire active/freelist partition before its WAL commit marker.
    /// It is not an in-place page allocator.
    /// </remarks>
    /// <summary>
    /// The minimal surface an index-tree builder needs from whatever page
    /// allocator is driving it: a way to reserve one fresh page number.
    /// </summary>
    /// <remarks>
    /// <see cref="RebuildPageAllocator"/> implements this for the whole-catalog
    /// rewrite, and <see cref="StagedIndexRebuildPageAllocator"/> implements it
    /// for a single targeted index rebuild through
    /// <see cref="SqliteStagedBtreePageIo"/>, so the index-tree builders
    /// (<see cref="BuildIndexTree"/> and everything it calls) stay unaware of
    /// which persistence path is driving them.
    /// </remarks>
    private interface IIndexRebuildPageAllocator
    {
        uint ReservePage();
    }

    /// <summary>
    /// Adapts <see cref="SqliteStagedBtreePageIo.AllocatePage"/> — which
    /// prefers freelist reuse over growing the file, exactly like real SQLite
    /// allocation — to <see cref="IIndexRebuildPageAllocator"/>, so a targeted
    /// REINDEX can drive the same index-tree builders the whole-catalog
    /// rewrite uses without allocating from that rewrite's naive full-range
    /// scan, which assumes it owns every page in the database.
    /// </summary>
    private sealed class StagedIndexRebuildPageAllocator(ISqliteBtreePageIo pageIo) : IIndexRebuildPageAllocator
    {
        public uint ReservePage() => pageIo.AllocatePage();
    }

    private sealed class RebuildPageAllocator : IIndexRebuildPageAllocator
    {
        private readonly uint _sourcePageCount;
        private readonly bool _compact;
        private readonly Queue<uint> _reusableLeaves;
        private readonly HashSet<uint> _reusableLeafSet;
        private readonly HashSet<uint> _reservedPages;
        private uint _nextExistingPage;
        private uint _nextAppendedPage;

        public RebuildPageAllocator(
            uint sourcePageCount,
            IReadOnlyList<uint> reusableLeaves,
            bool compact,
            IReadOnlyCollection<uint>? reservedPages = null)
        {
            ArgumentOutOfRangeException.ThrowIfZero(sourcePageCount);
            ArgumentNullException.ThrowIfNull(reusableLeaves);

            _sourcePageCount = sourcePageCount;
            _compact = compact;
            _reusableLeafSet = new HashSet<uint>();
            _reusableLeaves = new Queue<uint>();
            // Pages a caller has already committed to a preserved-in-place index
            // tree (see EmbeddedFileStore.TryPreserveUnavailableIndexTrees) must
            // never also be handed out here as free/reusable: that would mean
            // this rebuild's own recomputed freelist and a preserved index tree
            // disagree about who owns the page, which is exactly the kind of
            // silent page-ownership corruption ValidateRewritePlan/AddActivePage
            // exist to catch elsewhere. compact (VACUUM/page-size migration)
            // never receives reserved pages — see the PersistCore call site.
            _reservedPages = reservedPages is { Count: > 0 }
                ? new HashSet<uint>(reservedPages)
                : new HashSet<uint>();
            if (!compact)
            {
                foreach (var pageNumber in reusableLeaves.Order())
                {
                    if (pageNumber < 2 || pageNumber > sourcePageCount || !_reusableLeafSet.Add(pageNumber))
                    {
                        throw new InvalidDataException(
                            "Managed file rebuild received an invalid or duplicate validated freelist leaf.");
                    }

                    if (_reservedPages.Contains(pageNumber))
                    {
                        throw new InvalidDataException(
                            $"Managed file rebuild freelist page {pageNumber} collides with a preserved index page.");
                    }

                    _reusableLeaves.Enqueue(pageNumber);
                }
            }

            _nextExistingPage = compact ? 0U : 2U;
            _nextAppendedPage = compact ? 2U : sourcePageCount == uint.MaxValue ? 0U : sourcePageCount + 1;
            HighestAllocatedPage = SchemaRootPage;
        }

        public uint HighestAllocatedPage { get; private set; }

        public uint ReservePage()
        {
            if (_reusableLeaves.TryDequeue(out var reusablePage))
                return RecordAllocation(reusablePage);

            while (!_compact && _nextExistingPage != 0)
            {
                var existingPage = _nextExistingPage;
                _nextExistingPage = existingPage == _sourcePageCount ? 0 : existingPage + 1;
                if (!_reusableLeafSet.Contains(existingPage) && !_reservedPages.Contains(existingPage))
                    return RecordAllocation(existingPage);
            }

            if (_nextAppendedPage == 0 || _nextAppendedPage == uint.MaxValue)
            {
                throw new EmbeddedSqlException(
                    "The managed file engine cannot allocate SQLite page UInt32.MaxValue.");
            }

            var appendedPage = _nextAppendedPage;
            _nextAppendedPage++;
            return RecordAllocation(appendedPage);
        }

        private uint RecordAllocation(uint pageNumber)
        {
            if (pageNumber < 2)
                throw new InvalidOperationException("Managed file rebuild cannot allocate SQLite page 1 as data.");
            if (pageNumber > HighestAllocatedPage)
                HighestAllocatedPage = pageNumber;
            return pageNumber;
        }
    }

    private sealed record PageImage(uint PageNumber, byte[] Page);

    private sealed record PendingTableCell(long RowId, byte[] Record);

    private sealed record WithoutRowidRecord(byte[] Record, SqlValue[] Key);

    private sealed record PreparedSchemaTree(
        byte[] RootPage,
        IReadOnlyList<PageImage> InteriorPages,
        IReadOnlyList<PageImage> LeafPages,
        IReadOnlyList<PageImage> OverflowPages);

    private sealed record PreparedTableTree(
        byte[] RootPage,
        IReadOnlyList<PageImage> InteriorPages,
        IReadOnlyList<PageImage> LeafPages,
        IReadOnlyList<PageImage> OverflowPages);

    private sealed record TableTreeChild(uint PageNumber, long MaximumRowId);

    private readonly record struct SchemaTreeReadResult(
        long? MinimumRowId,
        long? MaximumRowId,
        int Height);

    private readonly record struct TableTreeReadResult(
        long MinimumRowId,
        long MaximumRowId,
        int Height);

    private sealed record PreparedIndexTree(
        byte[] RootPage,
        IReadOnlyList<PageImage> InteriorPages,
        IReadOnlyList<PageImage> LeafPages,
        IReadOnlyList<PageImage> OverflowPages);

    /// <summary>
    /// Bundles a preserved-in-place index's existing root page number together with a
    /// byte-for-byte clone of its tree read from committed storage, so a full rewrite never has
    /// to re-derive one from the other — or risk them getting out of sync — once
    /// <see cref="TryPreserveUnavailableIndexTrees"/> has decided to preserve it.
    /// </summary>
    private sealed record PreservedIndexTree(uint RootPage, PreparedIndexTree Tree);

    private sealed record IndexTreeReadResult(IReadOnlyList<byte[]> Records, int Height);

    private sealed record IndexInteriorPlan(
        IReadOnlyList<IndexTreeNode> Children,
        IReadOnlyList<byte[]> Separators);

    private sealed class IndexTreeNode
    {
        private IndexTreeNode(uint pageNumber, List<byte[]>? records, IndexInteriorPlan? interiorPlan)
        {
            if (pageNumber == 0)
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");

            PageNumber = pageNumber;
            _records = records;
            InteriorPlan = interiorPlan;
        }

        private readonly List<byte[]>? _records;

        public uint PageNumber { get; }

        public bool IsLeaf => _records is not null;

        public List<byte[]> Records => _records
            ?? throw new InvalidOperationException("SQLite index interior nodes do not own leaf records.");

        public IndexInteriorPlan? InteriorPlan { get; }

        public static IndexTreeNode CreateLeaf(uint pageNumber, List<byte[]> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            if (records.Count == 0)
                throw new ArgumentException("SQLite index leaf nodes must contain at least one record.", nameof(records));
            return new IndexTreeNode(pageNumber, records, interiorPlan: null);
        }

        public static IndexTreeNode CreateInterior(uint pageNumber, IndexInteriorPlan interiorPlan)
        {
            ArgumentNullException.ThrowIfNull(interiorPlan);
            if (interiorPlan.Children.Count < 2
                || interiorPlan.Separators.Count != interiorPlan.Children.Count - 1)
            {
                throw new ArgumentException("SQLite index interior nodes require a valid multi-child plan.", nameof(interiorPlan));
            }

            return new IndexTreeNode(pageNumber, records: null, interiorPlan);
        }
    }

    private sealed record IndexInteriorGroupRange(int Start, int Count);

    private sealed record IndexDefinition(string TableName, EmbeddedTable Table, EmbeddedIndex Index);
}
