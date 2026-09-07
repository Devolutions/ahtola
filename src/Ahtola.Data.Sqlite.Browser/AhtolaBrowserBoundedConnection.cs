using System.Runtime.Versioning;
using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// A read-only, page-bounded asynchronous scan connection over an unencrypted plaintext
/// database. Opened by <see cref="AhtolaBrowserDataSource.OpenBoundedScanConnectionAsync"/>.
/// </summary>
/// <remarks>
/// This never goes through <c>EmbeddedFileStore</c>/<c>EmbeddedDatabase</c> or the existing
/// whole-image mirror (<see cref="BrowserMirroredFileSystem"/>) at all: it opens its own
/// <see cref="OpfsAsyncFileSystem"/> (and therefore its own OPFS Web Lock) and layers
/// <see cref="AsyncSqlitePager"/> directly on top, so the default <c>WholeImage</c> profile and
/// the existing <see cref="AhtolaBrowserSynchronousMode.ReadOnlyMirror"/> synchronous mode are
/// provably unaffected — different types, different connection method, zero shared code path.
/// Because OPFS directories are single-owner, a bounded scan connection cannot be open at the
/// same time as a <c>WholeImage</c>/mirror connection (or another bounded scan connection) over
/// the same directory; opening either while the other already holds the lock fails the same way
/// opening two ordinary data sources over one directory already does.
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class AhtolaBrowserBoundedConnection : IAsyncDisposable
{
    private readonly IAsyncFileSystem _fileSystem;
    private readonly bool _ownsFileSystem;
    private readonly AsyncSqlitePager _pager;
    private readonly AsyncSchemaCatalog _catalog;
    private readonly SqliteTextEncoding _textEncoding;
    private readonly int _pageBudget;
    private int _disposed;

    private AhtolaBrowserBoundedConnection(
        IAsyncFileSystem fileSystem,
        bool ownsFileSystem,
        AsyncSqlitePager pager,
        AsyncSchemaCatalog catalog,
        SqliteTextEncoding textEncoding,
        int pageBudget)
    {
        _fileSystem = fileSystem;
        _ownsFileSystem = ownsFileSystem;
        _pager = pager;
        _catalog = catalog;
        _textEncoding = textEncoding;
        _pageBudget = pageBudget;
    }

    /// <summary>
    /// Opens a bounded scan connection over a real OPFS directory (production entry point: see
    /// <see cref="AhtolaBrowserDataSource.OpenBoundedScanConnectionAsync"/>).
    /// </summary>
    internal static async ValueTask<AhtolaBrowserBoundedConnection> OpenAsync(
        string ownedDirectory,
        string databasePath,
        int sharedBufferSize,
        int pageBudget,
        CancellationToken cancellationToken)
    {
        var persistent = await OpfsAsyncFileSystem
            .CreateAsync(ownedDirectory, sharedBufferSize, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await OpenAsync(
                persistent,
                ownsFileSystem: true,
                databasePath,
                pageBudget,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await persistent.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens a bounded scan connection over any <see cref="IAsyncFileSystem"/> — the seam that
    /// lets this whole pipeline (schema loading, classification, the bounded scan cursor) be
    /// exercised end to end off-browser, exactly like <see cref="BrowserMirroredFileSystem"/>'s
    /// own <c>IBrowserPersistentStore</c> seam, using an
    /// <c>AsyncFileSystemAdapter</c>-wrapped <c>InMemoryFileSystem</c> in tests instead of real
    /// OPFS.
    /// </summary>
    internal static async ValueTask<AhtolaBrowserBoundedConnection> OpenAsync(
        IAsyncFileSystem fileSystem,
        bool ownsFileSystem,
        string databasePath,
        int pageBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageBudget, 1);

        var canonicalPath = fileSystem is IStoragePathResolver resolver
            ? resolver.GetCanonicalPath(databasePath)
            : databasePath;

        AsyncSqlitePager? pager = null;
        try
        {
            pager = await AsyncSqlitePager
                .OpenAsync(
                    fileSystem,
                    canonicalPath,
                    canonicalPath + "-wal",
                    readOnly: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            AsyncSchemaCatalog catalog;
            SqliteTextEncoding textEncoding;
            await using (var bootstrapTransaction = await pager
                             .BeginReadAsync(cancellationToken: cancellationToken)
                             .ConfigureAwait(false))
            {
                var bootstrapCache = new BoundedAsyncPageCache(bootstrapTransaction, pager.UsableSpace, pageBudget);
                var page1 = await bootstrapCache.ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
                textEncoding = SqliteDatabaseHeader.Parse(page1).TextEncoding;
                catalog = await AsyncSchemaCatalogLoader
                    .LoadAsync(bootstrapCache, textEncoding, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new AhtolaBrowserBoundedConnection(
                fileSystem,
                ownsFileSystem,
                pager,
                catalog,
                textEncoding,
                pageBudget);
        }
        catch
        {
            if (pager is not null)
                await pager.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Classifies and, if supported, executes <paramref name="sql"/>. Throws
    /// <see cref="AhtolaBrowserBoundedQueryException"/> synchronously (before any page is read)
    /// for any unsupported statement shape — see the classifier's supported v1 shape. This
    /// connection never falls back to a different execution path on its own.
    /// </summary>
    public async ValueTask<AhtolaBrowserBoundedReader> ExecuteBoundedScanAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var plan = BoundedRowidScanShapeClassifier.TryClassify(sql, _catalog, out var rejectionReason);
        if (plan is null)
            throw new AhtolaBrowserBoundedQueryException(rejectionReason);

        var readTransaction = await _pager.BeginReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var pageCache = new BoundedAsyncPageCache(readTransaction, _pager.UsableSpace, _pageBudget);
            var rows = AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
                pageCache,
                plan.RootPage,
                plan.RowidAliasColumnIndex,
                _textEncoding,
                plan.Limit,
                cancellationToken);
            return new AhtolaBrowserBoundedReader(
                rows,
                plan.ProjectedColumnIndexes,
                plan.ColumnNames,
                readTransaction,
                cancellationToken);
        }
        catch
        {
            await readTransaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _pager.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_ownsFileSystem && _fileSystem is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
