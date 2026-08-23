using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>The durable journal modes implemented by the managed pager.</summary>
/// <remarks>
/// <see cref="Mvcc"/> is Turso's main-memory MVCC mode (header version 255). The
/// physical pager still keeps a WAL for page durability underneath; the MVCC
/// store lives on <c>EmbeddedDatabase</c> and is selected when this mode is active.
/// </remarks>
public enum SqliteJournalMode
{
    Delete,
    Wal,
    /// <summary>Turso MVCC mode (<c>PRAGMA journal_mode=mvcc</c>).</summary>
    Mvcc,
}

/// <summary>
/// Writes and recovers SQLite-compatible DELETE-mode rollback journals.
/// Page records contain the exact on-disk page image so encrypted databases
/// can be restored without exposing plaintext in the journal.
/// </summary>
internal static class SqliteRollbackJournal
{
    private const int HeaderSize = SqliteRollbackJournalFormat.HeaderSize;
    private const int SectorSize = SqliteRollbackJournalFormat.SectorSize;
    private static ReadOnlySpan<byte> Magic => SqliteRollbackJournalFormat.Magic;

    internal static bool IsHot(IFileSystem fileSystem, string journalPath)
    {
        if (!fileSystem.FileExists(journalPath))
            return false;

        using var journal = fileSystem.OpenFile(journalPath, FileOpenMode.OpenExisting, readOnly: true);
        if (journal.Length <= Magic.Length)
            return false;

        Span<byte> magic = stackalloc byte[Magic.Length];
        ReadExact(journal, 0, magic, "SQLite rollback journal magic");
        return magic.SequenceEqual(Magic);
    }

    internal static async ValueTask<bool> IsHotAsync(
        IAsyncFileSystem fileSystem,
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(journalPath);

        if (!await fileSystem.FileExistsAsync(journalPath, cancellationToken).ConfigureAwait(false))
            return false;

        var journal = await fileSystem
            .OpenFileAsync(
                journalPath,
                FileOpenMode.OpenExisting,
                readOnly: true,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (await journal.GetLengthAsync(cancellationToken).ConfigureAwait(false) <= Magic.Length)
                return false;

            var magic = new byte[Magic.Length];
            await ReadExactAsync(
                    journal,
                    0,
                    magic,
                    "SQLite rollback journal magic",
                    cancellationToken)
                .ConfigureAwait(false);
            return magic.AsSpan().SequenceEqual(Magic);
        }
        finally
        {
            await journal.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static void RecoverIfPresent(
        IFileSystem fileSystem,
        string databasePath,
        string journalPath,
        bool readOnly)
    {
        if (!fileSystem.FileExists(journalPath))
            return;

        if (!IsHot(fileSystem, journalPath))
        {
            if (!readOnly)
                fileSystem.DeleteFile(journalPath);
            return;
        }

        if (readOnly)
        {
            throw new InvalidDataException(
                "Cannot safely open the SQLite database read-only because it has a hot rollback journal. "
                + "Open it writable to recover the journal.");
        }

        using var journal = fileSystem.OpenFile(journalPath, FileOpenMode.OpenExisting, readOnly: true);
        var header = ReadHeader(journal);
        var recordSize = checked((long)header.PageSize + 8);
        var page = new byte[header.PageSize];
        Span<byte> pageNumberBytes = stackalloc byte[4];
        Span<byte> checksumBytes = stackalloc byte[4];
        var restoredPages = new HashSet<uint>();
        var pageNumbers = new List<JournalRecord>();
        var recordOffset = (long)header.SectorSize;

        if (header.RecordCount == uint.MaxValue)
        {
            // SQLite writes 0xffffffff when a journal header is finalized without a
            // known record count (crash mid-transaction). SQLite's pager_playback
            // then replays records until pager_playback_one_page reports SQLITE_DONE
            // (zero/out-of-range page number or a failed checksum) and applies every
            // record collected before that point. A torn final record therefore ends
            // the scan gracefully instead of failing recovery.
            while (recordOffset + recordSize <= journal.Length)
            {
                ReadExact(journal, recordOffset, pageNumberBytes, "SQLite rollback journal page number");
                var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(pageNumberBytes);
                if (pageNumber == 0)
                    break;
                if (!TryCollectRecord(
                        journal,
                        header,
                        page,
                        checksumBytes,
                        recordOffset,
                        pageNumber,
                        restoredPages,
                        pageNumbers))
                {
                    break;
                }

                recordOffset += recordSize;
            }
        }
        else
        {
            var requiredLength = checked((long)header.SectorSize + ((long)header.RecordCount * recordSize));
            // Trailing bytes after the declared records are ignored (SQLite may leave
            // preallocated journal capacity). Truncation below the declared payload is not.
            if (journal.Length < requiredLength)
            {
                throw new InvalidDataException(
                    "SQLite rollback journal is truncated before its declared page records.");
            }

            for (var index = 0; index < header.RecordCount; index++)
            {
                ReadExact(journal, recordOffset, pageNumberBytes, "SQLite rollback journal page number");
                var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(pageNumberBytes);
                ValidateAndCollectRecord(
                    journal,
                    header,
                    page,
                    checksumBytes,
                    recordOffset,
                    pageNumber,
                    restoredPages,
                    pageNumbers);
                recordOffset += recordSize;
            }
        }

        using var database = fileSystem.OpenFile(databasePath, FileOpenMode.OpenExisting);
        foreach (var record in pageNumbers)
        {
            ReadExact(journal, record.RecordOffset + 4, page, $"SQLite rollback journal page {record.PageNumber}");
            database.Write(checked((long)(record.PageNumber - 1) * header.PageSize), page);
        }

        database.SetLength(checked((long)header.InitialDatabasePageCount * header.PageSize));
        database.FlushToDisk();
        journal.Dispose();
        Invalidate(journalPath, fileSystem);
    }

    internal static async ValueTask RecoverIfPresentAsync(
        IAsyncFileSystem fileSystem,
        string databasePath,
        string journalPath,
        bool readOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentException.ThrowIfNullOrEmpty(journalPath);

        if (!await fileSystem.FileExistsAsync(journalPath, cancellationToken).ConfigureAwait(false))
            return;

        if (!await IsHotAsync(fileSystem, journalPath, cancellationToken).ConfigureAwait(false))
        {
            if (!readOnly)
                await fileSystem.DeleteFileAsync(journalPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (readOnly)
        {
            throw new InvalidDataException(
                "Cannot safely open the SQLite database read-only because it has a hot rollback journal. "
                + "Open it writable to recover the journal.");
        }

        IAsyncFile? journal = await fileSystem
            .OpenFileAsync(
                journalPath,
                FileOpenMode.OpenExisting,
                readOnly: true,
                cancellationToken)
            .ConfigureAwait(false);
        IAsyncFile? database = null;
        try
        {
            var header = await ReadHeaderAsync(journal, cancellationToken).ConfigureAwait(false);
            var recordSize = checked((long)header.PageSize + 8);
            var page = new byte[header.PageSize];
            var pageNumberBytes = new byte[4];
            var checksumBytes = new byte[4];
            var restoredPages = new HashSet<uint>();
            var pageNumbers = new List<JournalRecord>();
            var recordOffset = (long)header.SectorSize;
            var journalLength = await journal.GetLengthAsync(cancellationToken).ConfigureAwait(false);

            if (header.RecordCount == uint.MaxValue)
            {
                while (recordOffset + recordSize <= journalLength)
                {
                    await ReadExactAsync(
                            journal,
                            recordOffset,
                            pageNumberBytes,
                            "SQLite rollback journal page number",
                            cancellationToken)
                        .ConfigureAwait(false);
                    var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(pageNumberBytes);
                    if (pageNumber == 0)
                        break;
                    if (!await TryCollectRecordAsync(
                            journal,
                            header,
                            page,
                            checksumBytes,
                            recordOffset,
                            pageNumber,
                            restoredPages,
                            pageNumbers,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        break;
                    }

                    recordOffset += recordSize;
                }
            }
            else
            {
                var requiredLength = checked(
                    (long)header.SectorSize + ((long)header.RecordCount * recordSize));
                if (journalLength < requiredLength)
                {
                    throw new InvalidDataException(
                        "SQLite rollback journal is truncated before its declared page records.");
                }

                for (var index = 0; index < header.RecordCount; index++)
                {
                    await ReadExactAsync(
                            journal,
                            recordOffset,
                            pageNumberBytes,
                            "SQLite rollback journal page number",
                            cancellationToken)
                        .ConfigureAwait(false);
                    var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(pageNumberBytes);
                    await ValidateAndCollectRecordAsync(
                            journal,
                            header,
                            page,
                            checksumBytes,
                            recordOffset,
                            pageNumber,
                            restoredPages,
                            pageNumbers,
                            cancellationToken)
                        .ConfigureAwait(false);
                    recordOffset += recordSize;
                }
            }

            database = await fileSystem
                .OpenFileAsync(
                    databasePath,
                    FileOpenMode.OpenExisting,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var record in pageNumbers)
            {
                await ReadExactAsync(
                        journal,
                        record.RecordOffset + 4,
                        page,
                        $"SQLite rollback journal page {record.PageNumber}",
                        cancellationToken)
                    .ConfigureAwait(false);
                await database
                    .WriteAsync(
                        checked((long)(record.PageNumber - 1) * header.PageSize),
                        page,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await database
                .SetLengthAsync(
                    checked((long)header.InitialDatabasePageCount * header.PageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            await database.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

            await journal.DisposeAsync().ConfigureAwait(false);
            journal = null;
            await DeleteAsync(
                    fileSystem,
                    journalPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (journal is not null)
                await journal.DisposeAsync().ConfigureAwait(false);
            if (database is not null)
                await database.DisposeAsync().ConfigureAwait(false);
        }
    }

    private readonly record struct JournalRecord(uint PageNumber, long RecordOffset);

    /// <summary>
    /// Collects one record during an unknown-count (<c>0xffffffff</c>) scan,
    /// returning <see langword="false"/> when SQLite's <c>pager_playback</c> would
    /// report <c>SQLITE_DONE</c> and stop replaying.
    /// </summary>
    private static bool TryCollectRecord(
        IFile journal,
        JournalHeader header,
        byte[] page,
        Span<byte> checksumBytes,
        long recordOffset,
        uint pageNumber,
        HashSet<uint> restoredPages,
        List<JournalRecord> pageNumbers)
    {
        if (pageNumber == 0 || pageNumber > header.InitialDatabasePageCount)
            return false;
        if (!restoredPages.Add(pageNumber))
            return false;

        ReadExact(journal, recordOffset + 4, page, $"SQLite rollback journal page {pageNumber}");
        ReadExact(
            journal,
            recordOffset + 4 + header.PageSize,
            checksumBytes,
            $"SQLite rollback journal checksum for page {pageNumber}");
        var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(checksumBytes);
        if (ComputeChecksum(page, header.ChecksumNonce) != expectedChecksum)
        {
            restoredPages.Remove(pageNumber);
            return false;
        }

        pageNumbers.Add(new JournalRecord(pageNumber, recordOffset));
        return true;
    }

    private static async ValueTask<bool> TryCollectRecordAsync(
        IAsyncFile journal,
        JournalHeader header,
        byte[] page,
        byte[] checksumBytes,
        long recordOffset,
        uint pageNumber,
        HashSet<uint> restoredPages,
        List<JournalRecord> pageNumbers,
        CancellationToken cancellationToken)
    {
        if (pageNumber == 0 || pageNumber > header.InitialDatabasePageCount)
            return false;
        if (!restoredPages.Add(pageNumber))
            return false;

        await ReadExactAsync(
                journal,
                recordOffset + 4,
                page,
                $"SQLite rollback journal page {pageNumber}",
                cancellationToken)
            .ConfigureAwait(false);
        await ReadExactAsync(
                journal,
                recordOffset + 4 + header.PageSize,
                checksumBytes,
                $"SQLite rollback journal checksum for page {pageNumber}",
                cancellationToken)
            .ConfigureAwait(false);
        var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(checksumBytes);
        if (ComputeChecksum(page, header.ChecksumNonce) != expectedChecksum)
        {
            restoredPages.Remove(pageNumber);
            return false;
        }

        pageNumbers.Add(new JournalRecord(pageNumber, recordOffset));
        return true;
    }

    private static void ValidateAndCollectRecord(
        IFile journal,
        JournalHeader header,
        byte[] page,
        Span<byte> checksumBytes,
        long recordOffset,
        uint pageNumber,
        HashSet<uint> restoredPages,
        List<JournalRecord> pageNumbers)
    {
        if (pageNumber == 0 || pageNumber > header.InitialDatabasePageCount)
            throw new InvalidDataException($"SQLite rollback journal contains invalid page number {pageNumber}.");
        if (!restoredPages.Add(pageNumber))
            throw new InvalidDataException($"SQLite rollback journal contains duplicate page {pageNumber}.");

        ReadExact(journal, recordOffset + 4, page, $"SQLite rollback journal page {pageNumber}");
        ReadExact(
            journal,
            recordOffset + 4 + header.PageSize,
            checksumBytes,
            $"SQLite rollback journal checksum for page {pageNumber}");
        var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(checksumBytes);
        var actualChecksum = ComputeChecksum(page, header.ChecksumNonce);
        if (actualChecksum != expectedChecksum)
        {
            throw new InvalidDataException(
                $"SQLite rollback journal checksum for page {pageNumber} is invalid.");
        }

        pageNumbers.Add(new JournalRecord(pageNumber, recordOffset));
    }

    private static async ValueTask ValidateAndCollectRecordAsync(
        IAsyncFile journal,
        JournalHeader header,
        byte[] page,
        byte[] checksumBytes,
        long recordOffset,
        uint pageNumber,
        HashSet<uint> restoredPages,
        List<JournalRecord> pageNumbers,
        CancellationToken cancellationToken)
    {
        if (pageNumber == 0 || pageNumber > header.InitialDatabasePageCount)
            throw new InvalidDataException($"SQLite rollback journal contains invalid page number {pageNumber}.");
        if (!restoredPages.Add(pageNumber))
            throw new InvalidDataException($"SQLite rollback journal contains duplicate page {pageNumber}.");

        await ReadExactAsync(
                journal,
                recordOffset + 4,
                page,
                $"SQLite rollback journal page {pageNumber}",
                cancellationToken)
            .ConfigureAwait(false);
        await ReadExactAsync(
                journal,
                recordOffset + 4 + header.PageSize,
                checksumBytes,
                $"SQLite rollback journal checksum for page {pageNumber}",
                cancellationToken)
            .ConfigureAwait(false);
        var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(checksumBytes);
        var actualChecksum = ComputeChecksum(page, header.ChecksumNonce);
        if (actualChecksum != expectedChecksum)
        {
            throw new InvalidDataException(
                $"SQLite rollback journal checksum for page {pageNumber} is invalid.");
        }

        pageNumbers.Add(new JournalRecord(pageNumber, recordOffset));
    }

    internal static void Commit(
        IFileSystem fileSystem,
        string journalPath,
        SqlitePageStore pageStore,
        IReadOnlyCollection<uint> pageNumbers,
        Action applyDatabaseChanges,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(journalPath);
        ArgumentNullException.ThrowIfNull(pageStore);
        ArgumentNullException.ThrowIfNull(pageNumbers);
        ArgumentNullException.ThrowIfNull(applyDatabaseChanges);
        synchronousMode.Validate(nameof(synchronousMode));

        var originalPageCount = pageStore.PageCount;
        var pages = pageNumbers
            .Where(pageNumber => pageNumber >= 1 && pageNumber <= originalPageCount)
            .Distinct()
            .OrderBy(pageNumber => pageNumber)
            .ToArray();
        var checksumNonce = unchecked((uint)Random.Shared.NextInt64());

        if (fileSystem.FileExists(journalPath))
            RecoverIfPresent(fileSystem, pageStore.Path, journalPath, readOnly: false);

        var journalCreated = false;
        try
        {
            using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.CreateNew))
            {
                journalCreated = true;
                var zeroHeader = new byte[SectorSize];
                WriteHeader(
                    zeroHeader,
                    pages.Length,
                    checksumNonce,
                    originalPageCount,
                    pageStore.PageSize,
                    includeMagic: false);
                journal.Write(0, zeroHeader);

                var recordOffset = (long)SectorSize;
                Span<byte> pageNumberBytes = stackalloc byte[4];
                Span<byte> checksumBytes = stackalloc byte[4];
                foreach (var pageNumber in pages)
                {
                    var rawPage = pageStore.ReadRawPage(pageNumber);
                    BinaryPrimitives.WriteUInt32BigEndian(pageNumberBytes, pageNumber);
                    journal.Write(recordOffset, pageNumberBytes);
                    journal.Write(recordOffset + 4, rawPage);
                    BinaryPrimitives.WriteUInt32BigEndian(
                        checksumBytes,
                        ComputeChecksum(rawPage, checksumNonce));
                    journal.Write(recordOffset + 4 + pageStore.PageSize, checksumBytes);
                    recordOffset += pageStore.PageSize + 8L;
                }

                journal.SetLength(recordOffset);
                if (synchronousMode.UsesFullRollbackBarriers())
                    journal.FlushToDisk();

                Span<byte> durableHeader = stackalloc byte[HeaderSize];
                WriteHeader(
                    durableHeader,
                    pages.Length,
                    checksumNonce,
                    originalPageCount,
                    pageStore.PageSize,
                    includeMagic: true);
                journal.Write(0, durableHeader);
                if (synchronousMode.SyncsCheckpoint())
                    journal.FlushToDisk();
            }

            applyDatabaseChanges();
            Invalidate(journalPath, fileSystem, synchronousMode);
        }
        catch
        {
            if (!journalCreated)
                TryDelete(fileSystem, journalPath);
            throw;
        }
    }

    internal static async ValueTask<AsyncWriter> BeginAsync(
        IAsyncFileSystem fileSystem,
        string journalPath,
        int recordCount,
        uint checksumNonce,
        uint initialDatabasePageCount,
        int pageSize,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(journalPath);
        if (recordCount < 0)
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        ValidateHeaderFields(initialDatabasePageCount, pageSize);
        synchronousMode.Validate(nameof(synchronousMode));

        var journal = await fileSystem
            .OpenFileAsync(
                journalPath,
                FileOpenMode.CreateNew,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var zeroHeader = new byte[SectorSize];
            WriteHeader(
                zeroHeader,
                recordCount,
                checksumNonce,
                initialDatabasePageCount,
                pageSize,
                includeMagic: false);
            await journal.WriteAsync(0, zeroHeader, cancellationToken).ConfigureAwait(false);
            return new AsyncWriter(
                journal,
                recordCount,
                checksumNonce,
                initialDatabasePageCount,
                pageSize,
                synchronousMode);
        }
        catch
        {
            try
            {
                await journal.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    internal static async ValueTask DeleteAsync(
        IAsyncFileSystem fileSystem,
        string journalPath,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(journalPath);
        synchronousMode.Validate(nameof(synchronousMode));

        var journal = await fileSystem
            .OpenFileAsync(
                journalPath,
                FileOpenMode.OpenExisting,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await journal
                .WriteAsync(0, new byte[Magic.Length], cancellationToken)
                .ConfigureAwait(false);
            if (synchronousMode.UsesFullRollbackBarriers())
                await journal.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await journal.DisposeAsync().ConfigureAwait(false);
        }

        await TryDeleteAsync(fileSystem, journalPath, cancellationToken).ConfigureAwait(false);
    }

    internal sealed class AsyncWriter : IAsyncDisposable
    {
        private readonly IAsyncFile _journal;
        private readonly int _recordCount;
        private readonly uint _checksumNonce;
        private readonly uint _initialDatabasePageCount;
        private readonly int _pageSize;
        private readonly SqliteSynchronousMode _synchronousMode;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly HashSet<uint> _writtenPages = [];
        private int _writtenRecordCount;
        private long _recordOffset = SectorSize;
        private bool _finalized;
        private bool _disposed;

        internal AsyncWriter(
            IAsyncFile journal,
            int recordCount,
            uint checksumNonce,
            uint initialDatabasePageCount,
            int pageSize,
            SqliteSynchronousMode synchronousMode)
        {
            _journal = journal;
            _recordCount = recordCount;
            _checksumNonce = checksumNonce;
            _initialDatabasePageCount = initialDatabasePageCount;
            _pageSize = pageSize;
            _synchronousMode = synchronousMode;
        }

        internal async ValueTask WritePageRecordAsync(
            uint pageNumber,
            ReadOnlyMemory<byte> rawPage,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber == 0 || pageNumber > _initialDatabasePageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageNumber),
                    pageNumber,
                    $"Page number must be between 1 and {_initialDatabasePageCount}.");
            }
            if (rawPage.Length != _pageSize)
            {
                throw new ArgumentException(
                    $"SQLite rollback journal page data must be exactly {_pageSize} bytes.",
                    nameof(rawPage));
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_finalized)
                    throw new InvalidOperationException("The SQLite rollback journal is already finalized.");
                if (_writtenRecordCount >= _recordCount)
                    throw new InvalidOperationException("The SQLite rollback journal already contains its declared page records.");
                if (_writtenPages.Contains(pageNumber))
                {
                    throw new InvalidOperationException(
                        $"The SQLite rollback journal already contains page {pageNumber}.");
                }

                var pageNumberBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(pageNumberBytes, pageNumber);
                await _journal
                    .WriteAsync(_recordOffset, pageNumberBytes, cancellationToken)
                    .ConfigureAwait(false);
                await _journal
                    .WriteAsync(_recordOffset + 4, rawPage, cancellationToken)
                    .ConfigureAwait(false);

                var checksumBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(
                    checksumBytes,
                    ComputeChecksum(rawPage.Span, _checksumNonce));
                await _journal
                    .WriteAsync(_recordOffset + 4 + _pageSize, checksumBytes, cancellationToken)
                    .ConfigureAwait(false);

                _recordOffset += _pageSize + 8L;
                _writtenRecordCount++;
                _writtenPages.Add(pageNumber);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_finalized)
                    return;
                if (_writtenRecordCount != _recordCount)
                {
                    throw new InvalidOperationException(
                        $"The SQLite rollback journal contains {_writtenRecordCount} of {_recordCount} declared page records.");
                }

                await _journal.SetLengthAsync(_recordOffset, cancellationToken).ConfigureAwait(false);
                if (_synchronousMode.UsesFullRollbackBarriers())
                    await _journal.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

                var durableHeader = new byte[HeaderSize];
                WriteHeader(
                    durableHeader,
                    _recordCount,
                    _checksumNonce,
                    _initialDatabasePageCount,
                    _pageSize,
                    includeMagic: true);
                await _journal.WriteAsync(0, durableHeader, cancellationToken).ConfigureAwait(false);
                if (_synchronousMode.SyncsCheckpoint())
                    await _journal.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
                _finalized = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;

                _disposed = true;
                await _journal.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static JournalHeader ReadHeader(IFile journal)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(journal, 0, header, "SQLite rollback journal header");
        return ParseHeader(header);
    }

    private static JournalHeader ParseHeader(ReadOnlySpan<byte> header)
    {
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("SQLite rollback journal magic is invalid.");

        var recordCount = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var checksumNonce = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        var initialDatabasePageCount = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var sectorSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var encodedPageSize = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        if (encodedPageSize < SqlitePageSize.Minimum
            || encodedPageSize > SqlitePageSize.Maximum
            || (encodedPageSize & (encodedPageSize - 1)) != 0)
        {
            throw new InvalidDataException(
                $"SQLite rollback journal page size {encodedPageSize} is invalid.");
        }
        var pageSize = (int)encodedPageSize;
        // SQLite sector sizes are powers of two. Accept common values so journals
        // written by stock SQLite/Turso can be recovered, not only Ahtola's 512.
        if (sectorSize < 512
            || sectorSize > 65536
            || (sectorSize & (sectorSize - 1)) != 0)
        {
            throw new InvalidDataException(
                $"SQLite rollback journal sector size {sectorSize} is invalid.");
        }

        if (initialDatabasePageCount == 0)
            throw new InvalidDataException("SQLite rollback journal declares an empty original database.");

        return new JournalHeader(recordCount, checksumNonce, initialDatabasePageCount, pageSize, sectorSize);
    }

    private static async ValueTask<JournalHeader> ReadHeaderAsync(
        IAsyncFile journal,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(
                journal,
                0,
                header,
                "SQLite rollback journal header",
                cancellationToken)
            .ConfigureAwait(false);
        return ParseHeader(header);
    }

    private static void WriteHeader(
        Span<byte> destination,
        int recordCount,
        uint checksumNonce,
        uint initialDatabasePageCount,
        int pageSize,
        bool includeMagic)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException($"Rollback journal header requires {HeaderSize} bytes.", nameof(destination));

        destination[..HeaderSize].Clear();
        if (includeMagic)
            Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], checked((uint)recordCount));
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], checksumNonce);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], initialDatabasePageCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], SectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], checked((uint)pageSize));
    }

    private static void ValidateHeaderFields(uint initialDatabasePageCount, int pageSize)
    {
        if (initialDatabasePageCount == 0)
            throw new ArgumentOutOfRangeException(nameof(initialDatabasePageCount));
        if (pageSize < SqlitePageSize.Minimum
            || pageSize > SqlitePageSize.Maximum
            || (pageSize & (pageSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "SQLite rollback journal page size must be a supported power of two.");
        }
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> page, uint nonce)
        => SqliteRollbackJournalFormat.ComputeChecksum(page, nonce);

    private static void Invalidate(
        string journalPath,
        IFileSystem fileSystem,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        using (var journal = fileSystem.OpenFile(journalPath, FileOpenMode.OpenExisting))
        {
            journal.Write(0, new byte[Magic.Length]);
            if (synchronousMode.UsesFullRollbackBarriers())
                journal.FlushToDisk();
        }

        TryDelete(fileSystem, journalPath);
    }

    private static void TryDelete(IFileSystem fileSystem, string path)
    {
        try
        {
            fileSystem.DeleteFile(path);
        }
        catch
        {
            // A zeroed journal is not hot. A later writable open retries cleanup.
        }
    }

    private static async ValueTask TryDeleteAsync(
        IAsyncFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await fileSystem.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A zeroed journal is not hot. A later writable open retries cleanup.
        }
    }

    private static void ReadExact(IFile file, long offset, Span<byte> destination, string description)
    {
        var read = file.Read(offset, destination);
        if (read != destination.Length)
            throw new InvalidDataException($"{description} is truncated.");
    }

    private static async ValueTask ReadExactAsync(
        IAsyncFile file,
        long offset,
        Memory<byte> destination,
        string description,
        CancellationToken cancellationToken)
    {
        var read = await file
            .ReadAsync(offset, destination, cancellationToken)
            .ConfigureAwait(false);
        if (read != destination.Length)
            throw new InvalidDataException($"{description} is truncated.");
    }

    private readonly record struct JournalHeader(
        uint RecordCount,
        uint ChecksumNonce,
        uint InitialDatabasePageCount,
        int PageSize,
        uint SectorSize);
}
