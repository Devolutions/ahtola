using System.Buffers.Binary;
using System.Security.Cryptography;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// A read-only AHTLA snapshot that keeps WAL page locations, never WAL page images.
/// OPFS holds the directory's exclusive Web Lock for the lifetime of the connection.
/// </summary>
internal sealed class EncryptedBoundedPageSnapshot : IAsyncDisposable
{
    private readonly IAsyncFile _database;
    private readonly SqliteWalFile? _wal;
    private readonly AhtolaAsyncPageTransformer _pages;
    private readonly Dictionary<uint, long> _walPages;
    private readonly uint _mainPageCount;
    private bool _disposed;

    private EncryptedBoundedPageSnapshot(
        IAsyncFile database,
        SqliteWalFile? wal,
        AhtolaAsyncPageTransformer pages,
        Dictionary<uint, long> walPages,
        uint mainPageCount,
        uint pageCount,
        int pageSize,
        int usableSpace)
    {
        _database = database;
        _wal = wal;
        _pages = pages;
        _walPages = walPages;
        _mainPageCount = mainPageCount;
        PageCount = pageCount;
        PageSize = pageSize;
        UsableSpace = usableSpace;
    }

    internal uint PageCount { get; }
    internal int PageSize { get; }
    internal int UsableSpace { get; }

    internal static async ValueTask<EncryptedBoundedPageSnapshot> OpenAsync(
        IAsyncFileSystem fileSystem,
        string path,
        AhtolaAsyncPageTransformer pages,
        CancellationToken cancellationToken)
    {
        IAsyncFile? database = null;
        SqliteWalFile? wal = null;
        EncryptedBoundedPageSnapshot? snapshot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            database = await fileSystem.OpenFileAsync(
                path, FileOpenMode.OpenExisting, readOnly: true, cancellationToken).ConfigureAwait(false);
            var length = await database.GetLengthAsync(cancellationToken).ConfigureAwait(false);
            if (length < SqliteDatabaseHeader.Size)
                throw new InvalidDataException("Encrypted database is smaller than a SQLite header.");

            var prefix = new byte[SqliteDatabaseHeader.Size];
            if (await database.ReadAsync(0, prefix, cancellationToken).ConfigureAwait(false) != prefix.Length)
                throw new InvalidDataException("Encrypted database header was truncated.");
            if (!prefix.AsSpan(0, 5).SequenceEqual("AHTLA"u8))
                throw new InvalidDataException("Bounded encrypted reads require an AHTLA database; plaintext fallback is not permitted.");

            // The page-size field is visible but unauthenticated until page 1 decrypts.
            // Decode validates its SQLite bounds before any page-sized allocation.
            var pageSize = SqlitePageSize.Decode(BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(16)));
            if (length < pageSize || length % pageSize != 0)
                throw new InvalidDataException("Encrypted database is not a whole number of pages.");
            var mainPageCount = checked((uint)(length / pageSize));

            var firstPage = await ReadDatabasePageAsync(database, pages, 1, pageSize, cancellationToken)
                .ConfigureAwait(false);
            var header = SqliteDatabaseHeader.Parse(firstPage);
            if (header.PageSize != pageSize || header.ReservedSpace != pages.ReservedBytes)
                throw new InvalidDataException("Authenticated AHTLA header disagrees with page size or cipher reserve.");
            if (header.ReadVersion != header.WriteVersion
                || header.ReadVersion is not (SqliteFileFormatVersion.Legacy or SqliteFileFormatVersion.Wal))
                throw new NotSupportedException("Bounded encrypted reads support only DELETE and WAL databases.");

            var journalPath = path + "-journal";
            if (await fileSystem.FileExistsAsync(journalPath, cancellationToken).ConfigureAwait(false))
            {
                await using var journal = await fileSystem.OpenFileAsync(
                    journalPath, FileOpenMode.OpenExisting, readOnly: true, cancellationToken).ConfigureAwait(false);
                if (await journal.GetLengthAsync(cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("Bounded encrypted reads cannot recover a nonempty rollback journal.");
            }

            var walPages = new Dictionary<uint, long>();
            uint pageCount = mainPageCount;
            if (header.ReadVersion == SqliteFileFormatVersion.Wal
                && await fileSystem.FileExistsAsync(path + "-wal", cancellationToken).ConfigureAwait(false))
            {
                wal = await SqliteWalFile.OpenAsync(
                    fileSystem, path + "-wal", readOnly: true,
                    truncatedHeader: SqliteWalHeader.Create(pageSize, salt1: 0, salt2: 0),
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (wal.PageSize != pageSize)
                    throw new InvalidDataException("Encrypted database and WAL page sizes differ.");

                var pending = new Dictionary<uint, long>();
                var recovery = await wal.ScanFrameHeadersAsync((frameNumber, frame) =>
                {
                    pending[frame.PageNumber] = frameNumber;
                    if (!frame.IsCommit)
                        return;

                    pageCount = frame.DatabaseSizeInPages;
                    foreach (var number in walPages.Keys.Where(number => number > pageCount).ToArray())
                        walPages.Remove(number);
                    foreach (var entry in pending)
                    {
                        if (entry.Key <= pageCount)
                            walPages[entry.Key] = entry.Value;
                    }
                    pending.Clear();
                }, cancellationToken).ConfigureAwait(false);

                if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
                    || recovery.LastValidFrameNumber != recovery.LastCommittedFrameNumber)
                    throw new InvalidDataException("Encrypted WAL has an incomplete, invalid, or uncommitted tail.");
            }
            else if (header.ReadVersion == SqliteFileFormatVersion.Legacy
                     && await fileSystem.FileExistsAsync(path + "-wal", cancellationToken).ConfigureAwait(false))
            {
                await using var unusedWal = await fileSystem.OpenFileAsync(
                    path + "-wal", FileOpenMode.OpenExisting, readOnly: true, cancellationToken).ConfigureAwait(false);
                if (await unusedWal.GetLengthAsync(cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("Legacy encrypted database has an unexpected WAL sidecar.");
            }
            if (header.ReadVersion == SqliteFileFormatVersion.Wal && walPages.Count == 0
                && (header.VersionValidFor != header.ChangeCounter
                    || header.DatabaseSizeInPages != mainPageCount))
                throw new InvalidDataException("Encrypted WAL has no committed pages and the main database is not an authoritative snapshot.");

            if (pageCount == 0 || (walPages.Count == 0 && pageCount > mainPageCount))
                throw new InvalidDataException("Encrypted snapshot has no committed source for its pages.");
            snapshot = new EncryptedBoundedPageSnapshot(
                database, wal, pages, walPages, mainPageCount, pageCount, pageSize, header.UsableSpace);
            database = null;
            wal = null;
            var visibleFirstPage = await snapshot.ReadPageAsync(1, cancellationToken).ConfigureAwait(false);
            var visibleHeader = SqliteDatabaseHeader.Parse(visibleFirstPage);
            if (visibleHeader.PageSize != pageSize || visibleHeader.ReservedSpace != pages.ReservedBytes
                || visibleHeader.ReadVersion != header.ReadVersion
                || visibleHeader.WriteVersion != header.WriteVersion
                || (visibleHeader.VersionValidFor == visibleHeader.ChangeCounter
                    && visibleHeader.DatabaseSizeInPages != pageCount))
                throw new InvalidDataException("Committed encrypted page 1 disagrees with the WAL snapshot.");
            return snapshot;
        }
        catch
        {
            if (snapshot is not null)
                await snapshot.DisposeAsync().ConfigureAwait(false);
            else
            {
                try
                {
                    if (wal is not null)
                        await wal.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (database is not null)
                            await database.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await pages.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            throw;
        }
    }

    internal IAsyncBoundedReadSnapshot BeginRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ReadLease(this);
    }

    private async ValueTask<byte[]> ReadPageAsync(uint pageNumber, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (pageNumber == 0 || pageNumber > PageCount)
            throw new InvalidDataException($"Page {pageNumber} is outside the committed encrypted snapshot.");

        if (_walPages.TryGetValue(pageNumber, out var frameNumber))
        {
            // ReadFrameAsync rechecks the complete chain on the stored bytes before
            // any decryption. A changed frame cannot be treated as authenticated data.
            var frame = await _wal!.ReadFrameAsync(frameNumber, cancellationToken).ConfigureAwait(false);
            if (frame.Header.PageNumber != pageNumber)
                throw new InvalidDataException("Encrypted WAL page location changed after validation.");
            try
            {
                return await _pages.DecryptPageAsync(frame.PageData, pageNumber, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame.PageData);
            }
        }

        if (pageNumber > _mainPageCount)
            throw new InvalidDataException($"Page {pageNumber} is absent from both the WAL and the main database.");
        return await ReadDatabasePageAsync(_database, _pages, pageNumber, PageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadDatabasePageAsync(
        IAsyncFile database,
        AhtolaAsyncPageTransformer pages,
        uint pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var encrypted = new byte[pageSize];
        try
        {
            var read = await database.ReadAsync(
                checked(((long)pageNumber - 1) * pageSize), encrypted, cancellationToken).ConfigureAwait(false);
            if (read != pageSize)
                throw new InvalidDataException($"Encrypted database page {pageNumber} was truncated.");
            return await pages.DecryptPageAsync(encrypted, pageNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_wal is not null)
                await _wal.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _database.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _pages.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ReadLease(EncryptedBoundedPageSnapshot owner) : IAsyncBoundedReadSnapshot
    {
        private bool _disposed;
        public uint PageCount => owner.PageCount;
        public ValueTask<byte[]> ReadPageAsync(uint pageNumber, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return owner.ReadPageAsync(pageNumber, cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
