using System.Runtime.Versioning;
using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// A read-only, page-bounded asynchronous scan connection over a plaintext or AHTLA
/// database. Opened by <see cref="AhtolaBrowserDataSource.OpenBoundedScanConnectionAsync"/>.
/// </summary>
/// <remarks>
/// This never goes through <c>EmbeddedFileStore</c>/<c>EmbeddedDatabase</c> or the existing
/// whole-image mirror (<see cref="BrowserMirroredFileSystem"/>) at all: it opens its own
/// <see cref="OpfsAsyncFileSystem"/> (and therefore its own OPFS Web Lock) and uses
/// <see cref="AsyncSqlitePager"/> for plaintext or an authenticated read-only
/// page snapshot for AHTLA storage. The default <c>WholeImage</c> profile and
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
    private readonly AsyncSqlitePager? _pager;
    private readonly EncryptedBoundedPageSnapshot? _encryptedSnapshot;
    private readonly AsyncSchemaCatalog _catalog;
    private readonly SqliteTextEncoding _textEncoding;
    private readonly int _pageBudget;
    private int _disposed;

    private AhtolaBrowserBoundedConnection(
        IAsyncFileSystem fileSystem,
        bool ownsFileSystem,
        AsyncSqlitePager? pager,
        EncryptedBoundedPageSnapshot? encryptedSnapshot,
        AsyncSchemaCatalog catalog,
        SqliteTextEncoding textEncoding,
        int pageBudget)
    {
        _fileSystem = fileSystem;
        _ownsFileSystem = ownsFileSystem;
        _pager = pager;
        _encryptedSnapshot = encryptedSnapshot;
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
        CancellationToken cancellationToken,
        AhtolaBrowserEncryptionOptions? encryption = null)
    {
        var persistent = await OpfsAsyncFileSystem
            .CreateAsync(ownedDirectory, sharedBufferSize, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            AhtolaAsyncPageTransformer? encryptedPages = null;
            if (encryption is not null)
                encryptedPages = new AhtolaAsyncPageTransformer(
                    await AhtolaBrowserPageCipherFactory.CreateAsync(encryption).ConfigureAwait(false));
            return await OpenAsync(
                persistent,
                ownsFileSystem: true,
                databasePath,
                pageBudget,
                cancellationToken,
                encryptedPages).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        AhtolaAsyncPageTransformer? encryptedPages = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageBudget, 1);

        var canonicalPath = fileSystem is IStoragePathResolver resolver
            ? resolver.GetCanonicalPath(databasePath)
            : databasePath;

        AsyncSqlitePager? pager = null;
        EncryptedBoundedPageSnapshot? encryptedSnapshot = null;
        try
        {
            if (encryptedPages is not null)
            {
                // OpenAsync takes ownership, including when opening fails.
                var pages = encryptedPages;
                encryptedPages = null;
                encryptedSnapshot = await EncryptedBoundedPageSnapshot.OpenAsync(
                    fileSystem, canonicalPath, pages, pageBudget, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                pager = await AsyncSqlitePager
                    .OpenAsync(
                        fileSystem,
                        canonicalPath,
                        canonicalPath + "-wal",
                        readOnly: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            AsyncSchemaCatalog catalog;
            SqliteTextEncoding textEncoding;
            await using (var bootstrapTransaction = pager is not null
                             ? await pager.BeginReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                             : encryptedSnapshot!.BeginRead())
            {
                var usableSpace = pager?.UsableSpace ?? encryptedSnapshot!.UsableSpace;
                var bootstrapCache = new BoundedAsyncPageCache(bootstrapTransaction, usableSpace, pageBudget);
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
                encryptedSnapshot,
                catalog,
                textEncoding,
                pageBudget);
        }
        catch
        {
            if (pager is not null)
                await pager.DisposeAsync().ConfigureAwait(false);
            if (encryptedSnapshot is not null)
                await encryptedSnapshot.DisposeAsync().ConfigureAwait(false);
            if (encryptedPages is not null)
                await encryptedPages.DisposeAsync().ConfigureAwait(false);

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

        IAsyncBoundedReadSnapshot readTransaction = _pager is not null
            ? await _pager.BeginReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            : _encryptedSnapshot!.BeginRead();
        try
        {
            var usableSpace = _pager?.UsableSpace ?? _encryptedSnapshot!.UsableSpace;
            var pageCache = new BoundedAsyncPageCache(readTransaction, usableSpace, _pageBudget);
            var includeHiddenRowId = !plan.WithoutRowid
                && plan.ProjectedColumnIndexes.Contains(plan.Table.Columns.Length);
            var rows = plan.WithoutRowid && plan.FullPrimaryKeyEquals is { } exactKeys
                ? AsyncBoundedWithoutRowidTableScanCursor.SeekPrimaryKeyAsync(
                    pageCache, plan.RootPage, plan.Table, _textEncoding, exactKeys,
                    plan.Limit, plan.Offset, cancellationToken)
                : plan.WithoutRowid
                ? plan.Descending
                    ? AsyncBoundedWithoutRowidTableScanCursor.ScanDescendingAsync(
                        pageCache, plan.RootPage, plan.Table, _textEncoding, plan.Limit, plan.Offset,
                        cancellationToken, plan.FirstPrimaryKeyEquals, plan.FirstPrimaryKeyBounds)
                    : AsyncBoundedWithoutRowidTableScanCursor.ScanAscendingAsync(
                        pageCache, plan.RootPage, plan.Table, _textEncoding, plan.Limit, plan.Offset,
                        cancellationToken, plan.FirstPrimaryKeyEquals,
                        firstPrimaryKeyBounds: plan.FirstPrimaryKeyBounds)
                : plan.Descending
                ? AsyncBoundedRowidTableScanCursor.ScanDescendingAsync(
                    pageCache, plan.RootPage, plan.Table, _textEncoding, plan.Limit, cancellationToken,
                    equalRowId: plan.EqualRowId, rowIdRange: plan.RowIdRange, offset: plan.Offset,
                    includeHiddenRowId: includeHiddenRowId)
                : AsyncBoundedRowidTableScanCursor.ScanAscendingAsync(
                    pageCache, plan.RootPage, plan.Table, _textEncoding, plan.Limit, cancellationToken,
                    equalRowId: plan.EqualRowId, rowIdRange: plan.RowIdRange, offset: plan.Offset,
                    includeHiddenRowId: includeHiddenRowId);
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
            if (_pager is not null)
                await _pager.DisposeAsync().ConfigureAwait(false);
            if (_encryptedSnapshot is not null)
                await _encryptedSnapshot.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_ownsFileSystem && _fileSystem is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
