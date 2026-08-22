using System.Buffers;
using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// An asynchronous page store over a SQLite-format database file.
/// </summary>
/// <remarks>
/// The store preserves the same page alignment, header, and append invariants
/// as <see cref="SqlitePageStore"/>, but depends only on <see cref="IAsyncFile"/>.
/// Page codecs remain synchronous because encoding and decoding are CPU work;
/// codec calls are made only between asynchronous file operations.
/// </remarks>
public sealed class AsyncSqlitePageStore : IAsyncDisposable
{
    private const int MaximumSequentialWriteBytes = 1 << 20;

    private readonly IAsyncFile _file;
    private readonly IPageCodec? _pageCodec;
    private readonly bool _ownsPageCodec;
    private SqliteDatabaseHeader _header;
    private bool _disposed;

    private AsyncSqlitePageStore(
        IAsyncFile file,
        SqliteDatabaseHeader header,
        IPageCodec? pageCodec,
        bool ownsPageCodec,
        string path)
    {
        _file = file;
        _header = header;
        _pageCodec = pageCodec;
        _ownsPageCodec = ownsPageCodec;
        PageSize = header.PageSize;
        Path = path;
    }

    /// <summary>Page size in bytes; fixed for the life of the store.</summary>
    public int PageSize { get; }

    /// <summary>The storage path used to open the database.</summary>
    public string Path { get; }

    /// <summary>Whether the underlying file was opened read-only.</summary>
    public bool IsReadOnly => _file.IsReadOnly;

    /// <summary>The database header currently stored on page 1.</summary>
    public SqliteDatabaseHeader Header
    {
        get
        {
            ThrowIfDisposed();
            return _header;
        }
    }

    /// <summary>
    /// Opens an existing SQLite-format file and validates its header and length.
    /// </summary>
    public static ValueTask<AsyncSqlitePageStore> OpenAsync(
        IAsyncFileSystem fileSystem,
        string path,
        bool readOnly = false,
        AhtolaEncryptionOptions? encryption = null,
        IPageCodec? pageCodec = null,
        CancellationToken cancellationToken = default)
        => OpenCoreAsync(
            fileSystem,
            path,
            readOnly,
            encryption,
            pageCodec,
            allowTrailingPages: false,
            cancellationToken);

    /// <summary>
    /// Opens a store while allowing pager-verified trailing physical pages.
    /// </summary>
    internal static ValueTask<AsyncSqlitePageStore> OpenForPagerAsync(
        IAsyncFileSystem fileSystem,
        string path,
        bool readOnly = false,
        AhtolaEncryptionOptions? encryption = null,
        IPageCodec? pageCodec = null,
        CancellationToken cancellationToken = default)
        => OpenCoreAsync(
            fileSystem,
            path,
            readOnly,
            encryption,
            pageCodec,
            allowTrailingPages: true,
            cancellationToken);

    private static async ValueTask<AsyncSqlitePageStore> OpenCoreAsync(
        IAsyncFileSystem fileSystem,
        string path,
        bool readOnly,
        AhtolaEncryptionOptions? encryption,
        IPageCodec? pageCodec,
        bool allowTrailingPages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(path);
        PageCodecSupport.RejectCombinedTransforms(encryption, pageCodec);
        cancellationToken.ThrowIfCancellationRequested();

        IPageCodec? boundCodec = null;
        var ownsCodec = false;
        var file = await fileSystem.OpenFileAsync(
            path,
            FileOpenMode.OpenExisting,
            readOnly,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var length = await file.GetLengthAsync(cancellationToken).ConfigureAwait(false);
            if (length < SqliteDatabaseHeader.Size)
                throw new InvalidDataException("File is too small to contain a SQLite database header.");

            var rawHeader = new byte[SqliteDatabaseHeader.Size];
            if (await file.ReadAsync(0, rawHeader, cancellationToken).ConfigureAwait(false)
                != SqliteDatabaseHeader.Size)
            {
                throw new InvalidDataException("Failed to read the complete SQLite database header.");
            }

            SqliteDatabaseHeader header;
            if (pageCodec is not null)
            {
                PageCodecSupport.ValidateExternalCodec(pageCodec);
                boundCodec = pageCodec;
                header = await OpenWithCodecAsync(
                    file,
                    length,
                    boundCodec,
                    rawHeader,
                    requireAhtolaMagic: false,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (IsAhtolaEncrypted(rawHeader))
            {
                if (encryption is null)
                {
                    throw new InvalidDataException(
                        "Database is encrypted with Ahtola page encryption. Supply AhtolaEncryptionOptions; plaintext fallback is not permitted.");
                }

                var pageSize = SqlitePageSize.Decode(
                    BinaryPrimitives.ReadUInt16BigEndian(rawHeader.AsSpan(16)));
                boundCodec = EncryptionPageCodec.Create(encryption, pageSize);
                ownsCodec = true;
                header = await OpenWithCodecAsync(
                    file,
                    length,
                    boundCodec,
                    rawHeader,
                    requireAhtolaMagic: true,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (encryption is not null)
                {
                    throw new InvalidDataException(
                        "Encryption was requested, but the database contains a plaintext SQLite header. Plaintext fallback is not permitted.");
                }

                header = SqliteDatabaseHeader.Parse(rawHeader);
            }

            ValidateFileLayout(length, header, allowTrailingPages);
            return new AsyncSqlitePageStore(file, header, boundCodec, ownsCodec, path);
        }
        catch
        {
            try
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                PageCodecSupport.DisposeOwned(boundCodec, ownsCodec);
            }

            throw;
        }
    }

    private static async ValueTask<SqliteDatabaseHeader> OpenWithCodecAsync(
        IAsyncFile file,
        long length,
        IPageCodec codec,
        ReadOnlyMemory<byte> rawHeader,
        bool requireAhtolaMagic,
        CancellationToken cancellationToken)
    {
        var bootstrapLength = (int)Math.Min(
            length,
            Math.Max(SqliteDatabaseHeader.Size, PageCodecHeaderInfo.SqliteBootstrapHeaderLength));
        var bootstrap = new byte[bootstrapLength];
        if (await file.ReadAsync(0, bootstrap, cancellationToken).ConfigureAwait(false)
            != bootstrapLength)
        {
            throw new InvalidDataException("Failed to read the page-codec bootstrap prefix.");
        }

        var layout = codec.BootstrapPageInfo(bootstrap);
        var pageSize = layout.PageSize;
        if (length < pageSize)
            throw new InvalidDataException("Database is smaller than its declared page size.");
        if (length % pageSize != 0)
            throw new InvalidDataException("Database file is not a whole number of pages.");

        if (codec is EncryptionPageCodec encryptionCodec)
        {
            if (requireAhtolaMagic && !IsAhtolaEncrypted(rawHeader.Span))
                throw new InvalidDataException("Encrypted Ahtola database is missing the AHTLA header magic.");
            encryptionCodec.ValidateEncryptedHeader(rawHeader.Span);
        }

        var encodedFirstPage = new byte[pageSize];
        if (await file.ReadAsync(0, encodedFirstPage, cancellationToken).ConfigureAwait(false)
            != pageSize)
        {
            throw new InvalidDataException("Failed to read the complete first page.");
        }

        var plaintextFirstPage = new byte[pageSize];
        PageCodecSupport.Decode(
            codec,
            PageLocation.Database,
            1,
            encodedFirstPage,
            plaintextFirstPage);
        var header = SqliteDatabaseHeader.Parse(plaintextFirstPage);
        if (header.PageSize != pageSize)
        {
            throw new InvalidDataException(
                $"Page codec bootstrap page size {pageSize} disagrees with decoded header page size {header.PageSize}.");
        }

        var requiredReserved = codec.RequiredReservedBytes;
        if (requiredReserved != 0 && header.ReservedSpace != requiredReserved)
        {
            throw new InvalidDataException(
                $"Database reserves {header.ReservedSpace} bytes per page, but the page codec requires {requiredReserved}.");
        }

        return header;
    }

    /// <summary>
    /// Creates a fresh single-page SQLite database containing an empty table root.
    /// </summary>
    public static async ValueTask<AsyncSqlitePageStore> CreateAsync(
        IAsyncFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader? header = null,
        bool overwrite = false,
        AhtolaEncryptionOptions? encryption = null,
        IPageCodec? pageCodec = null,
        bool flushOnCreate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveHeader = (header ?? SqliteDatabaseHeader.CreateDefault()) with
        {
            ChangeCounter = 1,
            DatabaseSizeInPages = 1,
            VersionValidFor = 1,
        };
        var boundCodec = PageCodecSupport.Bind(
            encryption,
            pageCodec,
            effectiveHeader.PageSize,
            out var ownsCodec);

        var mode = overwrite ? FileOpenMode.OpenOrCreate : FileOpenMode.CreateNew;
        IAsyncFile? file = null;
        var createdArtifact = false;
        try
        {
            if (boundCodec is not null)
                effectiveHeader = PageCodecSupport.ApplyReservedBytes(boundCodec, effectiveHeader);

            var pageSize = effectiveHeader.PageSize;
            var firstPage = new byte[pageSize];
            effectiveHeader.WriteTo(firstPage);
            SqliteBtreePageHeader
                .CreateEmpty(
                    SqliteBtreePageType.TableLeaf,
                    pageSize,
                    isFirstPage: true,
                    usableSpace: effectiveHeader.UsableSpace)
                .WriteTo(firstPage);

            file = await fileSystem.OpenFileAsync(
                path,
                mode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            createdArtifact = mode == FileOpenMode.CreateNew;
            await file.SetLengthAsync(0, cancellationToken).ConfigureAwait(false);
            if (boundCodec is null)
            {
                await file.WriteAsync(0, firstPage, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var encoded = new byte[pageSize];
                PageCodecSupport.Encode(
                    boundCodec,
                    PageLocation.Database,
                    1,
                    firstPage,
                    encoded);
                await file.WriteAsync(0, encoded, cancellationToken).ConfigureAwait(false);
            }

            await file.SetLengthAsync(pageSize, cancellationToken).ConfigureAwait(false);
            if (flushOnCreate)
                await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            return new AsyncSqlitePageStore(
                file,
                effectiveHeader,
                boundCodec,
                ownsCodec,
                path);
        }
        catch
        {
            try
            {
                if (file is not null)
                    await file.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                PageCodecSupport.DisposeOwned(boundCodec, ownsCodec);
            }
            catch
            {
            }

            if (createdArtifact)
            {
                try
                {
                    await fileSystem.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    /// <summary>Returns the number of whole pages currently in the file.</summary>
    public async ValueTask<uint> GetPageCountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var length = await _file.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        return checked((uint)(length / PageSize));
    }

    /// <summary>Reads a page into exactly one page of caller-provided memory.</summary>
    public async ValueTask ReadPageAsync(
        uint pageNumber,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (destination.Length != PageSize)
            throw new ArgumentException($"Destination must be exactly {PageSize} bytes.", nameof(destination));

        var count = await GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        ValidateReadablePageNumber(pageNumber, count);
        await EnsurePageMaterializedAsync(pageNumber, cancellationToken).ConfigureAwait(false);

        var offset = PageOffset(pageNumber);
        if (_pageCodec is not null)
        {
            var encodedPage = new byte[PageSize];
            var encodedRead = await _file.ReadAsync(
                offset,
                encodedPage,
                cancellationToken).ConfigureAwait(false);
            if (encodedRead != PageSize)
            {
                throw new InvalidDataException(
                    $"Short read on encoded page {pageNumber}: expected {PageSize} bytes, got {encodedRead}. The file may be truncated.");
            }

            PageCodecSupport.Decode(
                _pageCodec,
                PageLocation.Database,
                pageNumber,
                encodedPage,
                destination.Span);
            return;
        }

        var read = await _file.ReadAsync(
            offset,
            destination,
            cancellationToken).ConfigureAwait(false);
        if (read != PageSize)
        {
            throw new InvalidDataException(
                $"Short read on page {pageNumber}: expected {PageSize} bytes, got {read}. The file may be truncated.");
        }
    }

    /// <summary>Reads a page into a newly allocated array.</summary>
    public async ValueTask<byte[]> ReadPageAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        var page = new byte[PageSize];
        await ReadPageAsync(pageNumber, page, cancellationToken).ConfigureAwait(false);
        return page;
    }

    internal async ValueTask<byte[]> ReadRawPageAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var count = await GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        ValidateReadablePageNumber(pageNumber, count);
        await EnsurePageMaterializedAsync(pageNumber, cancellationToken).ConfigureAwait(false);

        var page = new byte[PageSize];
        var read = await _file.ReadAsync(
            PageOffset(pageNumber),
            page,
            cancellationToken).ConfigureAwait(false);
        if (read != PageSize)
        {
            throw new InvalidDataException(
                $"Short raw read on page {pageNumber}: expected {PageSize} bytes, got {read}.");
        }

        return page;
    }

    internal async ValueTask EnsurePageMaterializedAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_file is not IAsyncPageMaterializingFile materializingFile || pageNumber < 1)
            return;

        var count = await GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        if (pageNumber <= count)
        {
            await materializingFile.EnsureMaterializedAsync(
                PageOffset(pageNumber),
                PageSize,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal bool SupportsPageMaterialization => _file is IAsyncPageMaterializingFile;

    internal async ValueTask RefreshHeaderAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var firstPage = await ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
        var header = SqliteDatabaseHeader.Parse(firstPage);
        if (header.PageSize != PageSize)
            throw new InvalidDataException("SQLite database page size changed; dispose and reopen this pager.");

        var length = await _file.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        ValidateFileLayout(length, header, allowTrailingPages: false);
        _header = header;
    }

    internal async ValueTask ReplaceRawContentAsync(
        IAsyncFile source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        ThrowIfReadOnly();

        var sourceLength = await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        await _file.SetLengthAsync(0, cancellationToken).ConfigureAwait(false);
        var offset = 0L;
        while (offset < sourceLength)
        {
            var count = checked((int)Math.Min(buffer.Length, sourceLength - offset));
            var read = await source.ReadAsync(
                offset,
                buffer.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
            if (read != count)
            {
                throw new InvalidDataException(
                    "Replacement SQLite database file was truncated while being copied.");
            }

            await _file.WriteAsync(
                offset,
                buffer.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
            offset += count;
        }

        await _file.SetLengthAsync(sourceLength, cancellationToken).ConfigureAwait(false);
        await _file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask WriteUnpublishedImageAsync(
        uint pageCount,
        Func<uint, ReadOnlyMemory<byte>> getPageImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getPageImage);
        return WriteUnpublishedImageAsync(
            pageCount,
            (pageNumber, _) => ValueTask.FromResult(getPageImage(pageNumber)),
            cancellationToken);
    }

    internal async ValueTask WriteUnpublishedImageAsync(
        uint pageCount,
        Func<uint, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> getPageImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getPageImage);
        ThrowIfDisposed();
        ThrowIfReadOnly();
        if (pageCount == 0)
            throw new ArgumentOutOfRangeException(nameof(pageCount), pageCount, "A SQLite database has at least one page.");

        var targetLength = checked((long)pageCount * PageSize);
        await _file.SetLengthAsync(targetLength, cancellationToken).ConfigureAwait(false);
        if (await _file.GetLengthAsync(cancellationToken).ConfigureAwait(false) != targetLength)
        {
            throw new InvalidDataException(
                "Preallocating the vacuum destination did not reach its page boundary.");
        }

        var pagesPerBatch = Math.Max(1, MaximumSequentialWriteBytes / PageSize);
        var batchPages = (int)Math.Min(pageCount, (uint)pagesPerBatch);
        var buffer = ArrayPool<byte>.Shared.Rent(checked(batchPages * PageSize));
        SqliteDatabaseHeader? firstPageHeader = null;
        try
        {
            var buffered = 0;
            var batchOffset = 0L;
            for (var pageNumber = 1U; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await getPageImage(pageNumber, cancellationToken).ConfigureAwait(false);
                if (page.Length != PageSize)
                {
                    throw new InvalidDataException(
                        $"Vacuum destination page {pageNumber} is {page.Length} bytes; expected {PageSize}.");
                }

                if (pageNumber == 1)
                {
                    firstPageHeader = SqliteDatabaseHeader.Parse(page.Span);
                    if (firstPageHeader.PageSize != PageSize)
                    {
                        throw new InvalidDataException(
                            "Vacuum destination page 1 does not declare the store's page size.");
                    }

                    if (firstPageHeader.DatabaseSizeInPages != pageCount
                        || firstPageHeader.VersionValidFor != firstPageHeader.ChangeCounter)
                    {
                        throw new InvalidDataException(
                            "Vacuum destination page 1 must authoritatively declare its own page count.");
                    }
                }

                StagePage(pageNumber, page, buffer, buffered * PageSize);

                buffered++;
                if (buffered != batchPages && pageNumber != pageCount)
                    continue;

                await _file.WriteAsync(
                    batchOffset,
                    buffer.AsMemory(0, buffered * PageSize),
                    cancellationToken).ConfigureAwait(false);
                batchOffset += buffered * PageSize;
                buffered = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await AssertPageAlignedAsync(cancellationToken).ConfigureAwait(false);
        if (await _file.GetLengthAsync(cancellationToken).ConfigureAwait(false) != targetLength)
        {
            throw new InvalidDataException(
                "Writing the vacuum destination changed its expected file length.");
        }

        await _file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
        _header = firstPageHeader
            ?? throw new InvalidDataException("Vacuum destination did not receive a first page.");
    }

    /// <summary>Writes or appends exactly one complete page image.</summary>
    public ValueTask WritePageAsync(
        uint pageNumber,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
        => WritePageCoreAsync(
            pageNumber,
            source,
            allowShrinkPageOneHeader: false,
            cancellationToken);

    internal ValueTask WriteShrinkCheckpointPageOneAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
        => WritePageCoreAsync(
            pageNumber: 1,
            source,
            allowShrinkPageOneHeader: true,
            cancellationToken);

    private async ValueTask WritePageCoreAsync(
        uint pageNumber,
        ReadOnlyMemory<byte> source,
        bool allowShrinkPageOneHeader,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        if (source.Length != PageSize)
            throw new ArgumentException($"Page data must be exactly {PageSize} bytes.", nameof(source));
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "Page numbers are 1-based.");

        var count = await GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        if (pageNumber > count + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Cannot write page {pageNumber}: it would skip past the current end of {count} page(s).");
        }

        SqliteDatabaseHeader? updatedHeader = null;
        if (pageNumber == 1)
        {
            updatedHeader = SqliteDatabaseHeader.Parse(source.Span);
            if (updatedHeader.PageSize != PageSize)
                throw new InvalidOperationException("Page 1 header cannot change the store's page size.");
            if (updatedHeader.VersionValidFor == updatedHeader.ChangeCounter
                && updatedHeader.DatabaseSizeInPages != count
                && (!allowShrinkPageOneHeader
                    || updatedHeader.DatabaseSizeInPages == 0
                    || updatedHeader.DatabaseSizeInPages >= count))
            {
                throw new InvalidOperationException(
                    "Page 1 header page count must match the current file size when it is authoritative.");
            }
        }
        else if (allowShrinkPageOneHeader)
        {
            throw new InvalidOperationException(
                "Only page 1 may be installed through the shrink checkpoint path.");
        }

        if (pageNumber == count + 1)
        {
            await WriteAppendedPageAsync(
                pageNumber,
                source,
                count,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteRawPageAsync(pageNumber, source, cancellationToken).ConfigureAwait(false);
        await AssertPageAlignedAsync(cancellationToken).ConfigureAwait(false);
        if (updatedHeader is not null)
            _header = updatedHeader;
    }

    private async ValueTask WriteAppendedPageAsync(
        uint pageNumber,
        ReadOnlyMemory<byte> source,
        uint previousPageCount,
        CancellationToken cancellationToken)
    {
        var originalLength = checked((long)previousPageCount * PageSize);
        try
        {
            await WriteRawPageAsync(pageNumber, source, cancellationToken).ConfigureAwait(false);
            await AssertPageAlignedAsync(cancellationToken).ConfigureAwait(false);
            await UpdateHeaderPageCountAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception writeException)
        {
            try
            {
                if (await _file.GetLengthAsync(CancellationToken.None).ConfigureAwait(false)
                    != originalLength)
                {
                    await _file.SetLengthAsync(
                        originalLength,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackException)
            {
                throw new InvalidDataException(
                    "Appending a page failed and the prior file length could not be restored.",
                    new AggregateException(writeException, rollbackException));
            }

            throw;
        }
    }

    private async ValueTask UpdateHeaderPageCountAsync(
        uint pageCount,
        CancellationToken cancellationToken)
    {
        var updatedHeader = _header with
        {
            DatabaseSizeInPages = pageCount,
            VersionValidFor = _header.ChangeCounter,
        };
        if (_pageCodec is null)
        {
            var headerBytes = new byte[SqliteDatabaseHeader.Size];
            updatedHeader.WriteTo(headerBytes);
            await _file.WriteAsync(0, headerBytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var firstPage = await ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
            updatedHeader.WriteTo(firstPage);
            await WriteRawPageAsync(1, firstPage, cancellationToken).ConfigureAwait(false);
        }

        _header = updatedHeader;
    }

    /// <summary>Flushes all written pages to durable storage.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReadOnly)
            await _file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask TruncateToPageCountAsync(
        uint pageCount,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var currentPageCount = await GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        if (pageCount == 0 || pageCount > currentPageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageCount),
                pageCount,
                $"Truncation page count must be between 1 and the current {currentPageCount} page(s).");
        }

        if (pageCount == currentPageCount)
            return;

        var firstPage = await ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
        var header = SqliteDatabaseHeader.Parse(firstPage);
        if (header.VersionValidFor != header.ChangeCounter
            || header.DatabaseSizeInPages != pageCount)
        {
            throw new InvalidOperationException(
                "Cannot truncate a SQLite database before page 1 durably declares the requested authoritative page count.");
        }

        var targetLength = checked((long)pageCount * PageSize);
        await _file.SetLengthAsync(targetLength, cancellationToken).ConfigureAwait(false);
        if (await _file.GetLengthAsync(cancellationToken).ConfigureAwait(false) != targetLength)
        {
            throw new InvalidDataException(
                "SQLite database truncation did not reach its requested page boundary.");
        }

        await AssertPageAlignedAsync(cancellationToken).ConfigureAwait(false);
        _header = header;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await _file.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            PageCodecSupport.DisposeOwned(_pageCodec, _ownsPageCodec);
        }
    }

    private long PageOffset(uint pageNumber) => (long)(pageNumber - 1) * PageSize;

    private async ValueTask WriteRawPageAsync(
        uint pageNumber,
        ReadOnlyMemory<byte> page,
        CancellationToken cancellationToken)
    {
        if (_pageCodec is null)
        {
            await _file.WriteAsync(
                PageOffset(pageNumber),
                page,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var onDiskPage = new byte[PageSize];
        PageCodecSupport.Encode(
            _pageCodec,
            PageLocation.Database,
            pageNumber,
            page.Span,
            onDiskPage);
        await _file.WriteAsync(
            PageOffset(pageNumber),
            onDiskPage,
            cancellationToken).ConfigureAwait(false);
    }

    private void StagePage(
        uint pageNumber,
        ReadOnlyMemory<byte> page,
        byte[] destination,
        int destinationOffset)
    {
        var slot = destination.AsSpan(destinationOffset, PageSize);
        if (_pageCodec is null)
            page.Span.CopyTo(slot);
        else
            PageCodecSupport.Encode(
                _pageCodec,
                PageLocation.Database,
                pageNumber,
                page.Span,
                slot);
    }

    private async ValueTask AssertPageAlignedAsync(CancellationToken cancellationToken)
    {
        var length = await _file.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length % PageSize != 0)
            throw new InvalidDataException("Write left the database file at a non page-aligned length.");
    }

    private static bool IsAhtolaEncrypted(ReadOnlySpan<byte> header)
        => header.Length >= 5 && header[..5].SequenceEqual("AHTLA"u8);

    private static void ValidateFileLayout(
        long length,
        SqliteDatabaseHeader header,
        bool allowTrailingPages)
    {
        var pageSize = header.PageSize;
        if (length < pageSize)
            throw new InvalidDataException("File is smaller than a single page.");
        if (length % pageSize != 0)
            throw new InvalidDataException("SQLite database file is not a whole number of pages.");

        var pageCount = length / pageSize;
        if (header.DatabaseSizeInPages != 0
            && header.VersionValidFor == header.ChangeCounter
            && (header.DatabaseSizeInPages > pageCount
                || (!allowTrailingPages && header.DatabaseSizeInPages != pageCount)))
        {
            throw new InvalidDataException(
                $"Header page count {header.DatabaseSizeInPages} disagrees with file size ({pageCount} page(s)).");
        }
    }

    private static void ValidateReadablePageNumber(uint pageNumber, uint count)
    {
        if (pageNumber < 1 || pageNumber > count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Page number is out of range for a database of {count} page(s).");
        }
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("The page store was opened read-only.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
