using System.Buffers.Binary;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>How a persisted OPFS file participates in AHTLA page encryption.</summary>
internal enum BrowserPersistedFileKind
{
    /// <summary>A page-structured SQLite database, including attached and VACUUM targets.</summary>
    Database,

    /// <summary>A write-ahead log whose frame bodies are encrypted.</summary>
    Wal,

    /// <summary>A DELETE-mode rollback journal whose page records are encrypted.</summary>
    Journal,

    /// <summary>Content that is never encrypted, such as the rebuildable WAL index.</summary>
    Passthrough,
}

/// <summary>
/// A unit-aligned plaintext region captured while the managed engine mutates the
/// in-memory mirror, together with the layout facts needed to encrypt it later.
/// </summary>
/// <remarks>
/// Capturing whole pages, frames, or journal records at write time makes the
/// asynchronous transform independent of how the engine happened to split its
/// writes, and preserves the engine's exact durability ordering because each
/// captured region reflects the file at that point in the operation stream.
/// </remarks>
internal sealed class BrowserPlaintextCapture(
    BrowserPersistedFileKind kind,
    long position,
    byte[] bytes,
    int pageSize,
    uint journalChecksumNonce)
{
    internal BrowserPersistedFileKind Kind { get; } = kind;

    internal long Position { get; } = position;

    internal byte[] Bytes { get; } = bytes;

    /// <summary>The page size in force for this region, or zero for passthrough content.</summary>
    internal int PageSize { get; } = pageSize;

    /// <summary>The rollback journal checksum nonce, valid only for journal records.</summary>
    internal uint JournalChecksumNonce { get; } = journalChecksumNonce;
}

/// <summary>
/// A transformed region ready to be written to OPFS, plus the WAL chain state to
/// commit once that write succeeds.
/// </summary>
internal sealed class BrowserPersistedWrite(
    long position,
    byte[] bytes,
    string path,
    BrowserWalChainUpdate? walUpdate)
{
    internal long Position { get; } = position;

    internal byte[] Bytes { get; } = bytes;

    internal string Path { get; } = path;

    internal BrowserWalChainUpdate? WalUpdate { get; } = walUpdate;
}

/// <summary>The WAL rolling-checksum state produced by transforming one WAL region.</summary>
internal sealed class BrowserWalChainUpdate(
    bool resetsChain,
    int frameSize,
    (uint First, uint Second) seed,
    SqliteWalChecksumByteOrder byteOrder,
    int firstFrameIndex,
    List<(uint First, uint Second)> frameChecksums)
{
    internal bool ResetsChain { get; } = resetsChain;

    internal int FrameSize { get; } = frameSize;

    internal (uint First, uint Second) Seed { get; } = seed;

    internal SqliteWalChecksumByteOrder ByteOrder { get; } = byteOrder;

    internal int FirstFrameIndex { get; } = firstFrameIndex;

    internal List<(uint First, uint Second)> FrameChecksums { get; } = frameChecksums;
}

/// <summary>
/// Converts between the plaintext image the managed engine operates on and the
/// exact AHTLA-encrypted image stored in OPFS.
/// </summary>
/// <remarks>
/// <para>
/// Database pages, WAL frame bodies, and rollback journal page records are
/// encrypted with the same layout the desktop engine writes, so files round-trip
/// between browser and desktop byte-for-byte. WAL and journal headers stay in the
/// clear exactly as SQLite defines them; the rolling WAL checksums and per-record
/// journal checksums are recomputed over the encrypted bytes, matching what the
/// desktop pager produces when a page codec is bound.
/// </para>
/// <para>
/// Nothing here is a browser-specific container: an encrypted OPFS file is a
/// plain Ahtola encrypted database, WAL, or journal.
/// </para>
/// </remarks>
internal sealed class BrowserEncryptedPersistence(AhtolaAsyncPageTransformer pages) : IAsyncDisposable
{
    private const int WalHeaderSize = SqliteWalHeader.Size;
    private const int WalFrameHeaderSize = SqliteWalFrameHeader.Size;

    private readonly Dictionary<string, WalChain> _walChains = new(StringComparer.Ordinal);

    /// <summary>Classifies a persisted path by SQLite's file naming conventions.</summary>
    internal static BrowserPersistedFileKind Classify(string path)
    {
        if (path.EndsWith("-wal", StringComparison.Ordinal))
            return BrowserPersistedFileKind.Wal;
        if (path.EndsWith("-journal", StringComparison.Ordinal))
            return BrowserPersistedFileKind.Journal;
        if (path.EndsWith("-shm", StringComparison.Ordinal))
            return BrowserPersistedFileKind.Passthrough;
        return BrowserPersistedFileKind.Database;
    }

    /// <summary>
    /// Expands a just-completed engine write to whole pages, WAL frames, or journal
    /// records and copies that region out of <paramref name="file"/>.
    /// </summary>
    internal BrowserPlaintextCapture Capture(string path, long position, int length, IFile file)
    {
        var kind = Classify(path);
        if (kind == BrowserPersistedFileKind.Passthrough)
            return ReadRegion(kind, file, position, length, pageSize: 0, journalChecksumNonce: 0);

        var fileLength = file.Length;
        return kind switch
        {
            BrowserPersistedFileKind.Database => CaptureDatabase(path, position, length, file, fileLength),
            BrowserPersistedFileKind.Wal => CaptureWal(path, position, length, file, fileLength),
            BrowserPersistedFileKind.Journal => CaptureJournal(path, position, length, file, fileLength),
            _ => ReadRegion(kind, file, position, length, pageSize: 0, journalChecksumNonce: 0),
        };
    }

    /// <summary>
    /// Encrypts a captured region without mutating chain state, so a failed or
    /// cancelled OPFS write leaves the transform exactly where it started.
    /// </summary>
    internal async ValueTask<BrowserPersistedWrite> PrepareAsync(
        string path,
        BrowserPlaintextCapture capture,
        CancellationToken cancellationToken)
        => capture.Kind switch
        {
            BrowserPersistedFileKind.Passthrough
                => new BrowserPersistedWrite(capture.Position, capture.Bytes, path, walUpdate: null),
            BrowserPersistedFileKind.Database
                => await PrepareDatabaseAsync(path, capture, cancellationToken).ConfigureAwait(false),
            BrowserPersistedFileKind.Wal
                => await PrepareWalAsync(path, capture, cancellationToken).ConfigureAwait(false),
            BrowserPersistedFileKind.Journal
                => await PrepareJournalAsync(path, capture, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown persisted browser file kind {capture.Kind}."),
        };

    /// <summary>Commits WAL chain state after its transformed bytes reached OPFS.</summary>
    internal void CommitWrite(BrowserPersistedWrite write)
    {
        if (write.WalUpdate is not { } update)
            return;

        if (!_walChains.TryGetValue(write.Path, out var chain))
        {
            chain = new WalChain();
            _walChains[write.Path] = chain;
        }

        if (update.ResetsChain)
        {
            chain.FrameSize = update.FrameSize;
            chain.Seed = update.Seed;
            chain.ByteOrder = update.ByteOrder;
            chain.FrameChecksums.Clear();
            chain.Initialized = true;
        }

        if (update.FrameChecksums.Count == 0)
            return;

        if (chain.FrameChecksums.Count > update.FirstFrameIndex)
            chain.FrameChecksums.RemoveRange(update.FirstFrameIndex, chain.FrameChecksums.Count - update.FirstFrameIndex);
        chain.FrameChecksums.AddRange(update.FrameChecksums);
    }

    /// <summary>Forgets any cached state for a recreated file.</summary>
    internal void NotifyCreated(string path) => _walChains.Remove(path);

    /// <summary>Forgets any cached state for a deleted file.</summary>
    internal void NotifyDeleted(string path) => _walChains.Remove(path);

    /// <summary>Moves cached state along with an atomically replaced file.</summary>
    internal void NotifyReplaced(string sourcePath, string destinationPath)
    {
        if (_walChains.Remove(sourcePath, out var chain))
            _walChains[destinationPath] = chain;
        else
            _walChains.Remove(destinationPath);
    }

    /// <summary>Drops WAL frame checksums that a truncation removed.</summary>
    internal void NotifyLengthSet(string path, long length)
    {
        if (!_walChains.TryGetValue(path, out var chain) || !chain.Initialized || chain.FrameSize <= 0)
            return;
        if (length < WalHeaderSize)
        {
            chain.FrameChecksums.Clear();
            return;
        }

        var frameCount = checked((int)((length - WalHeaderSize) / chain.FrameSize));
        if (chain.FrameChecksums.Count > frameCount)
            chain.FrameChecksums.RemoveRange(frameCount, chain.FrameChecksums.Count - frameCount);
    }

    /// <summary>
    /// Decrypts a complete OPFS image into the plaintext the managed engine reads,
    /// rebuilding the WAL rolling-checksum chain so later appends stay valid.
    /// </summary>
    internal async ValueTask<byte[]> DecryptImageAsync(
        string path,
        byte[] encryptedImage,
        CancellationToken cancellationToken)
    {
        var kind = Classify(path);
        _walChains.Remove(path);
        return kind switch
        {
            BrowserPersistedFileKind.Passthrough => encryptedImage,
            BrowserPersistedFileKind.Database
                => await DecryptDatabaseImageAsync(encryptedImage, cancellationToken).ConfigureAwait(false),
            BrowserPersistedFileKind.Wal
                => await DecryptWalImageAsync(path, encryptedImage, cancellationToken).ConfigureAwait(false),
            BrowserPersistedFileKind.Journal
                => await DecryptJournalImageAsync(encryptedImage, cancellationToken).ConfigureAwait(false),
            _ => encryptedImage,
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _walChains.Clear();
        return pages.DisposeAsync();
    }

    private BrowserPlaintextCapture CaptureDatabase(
        string path,
        long position,
        int length,
        IFile file,
        long fileLength)
    {
        var pageSize = ReadDatabasePageSize(path, file, fileLength);
        var start = position / pageSize * pageSize;
        var end = (position + length + pageSize - 1) / pageSize * pageSize;
        if (end > fileLength)
        {
            throw new InvalidDataException(
                $"Encrypted browser database '{path}' received a write ending at {position + length}, "
                + $"which is not covered by whole {pageSize}-byte pages within its {fileLength}-byte image.");
        }

        return ReadRegion(
            BrowserPersistedFileKind.Database,
            file,
            start,
            checked((int)(end - start)),
            pageSize,
            journalChecksumNonce: 0);
    }

    private BrowserPlaintextCapture CaptureWal(
        string path,
        long position,
        int length,
        IFile file,
        long fileLength)
    {
        if (fileLength < WalHeaderSize)
        {
            if (position + length > WalHeaderSize)
            {
                throw new InvalidDataException(
                    $"Encrypted browser WAL '{path}' received bytes past its header before the header existed.");
            }

            return ReadRegion(
                BrowserPersistedFileKind.Passthrough,
                file,
                position,
                length,
                pageSize: 0,
                journalChecksumNonce: 0);
        }

        var header = new byte[WalHeaderSize];
        ReadExact(file, 0, header, path);
        int pageSize;
        try
        {
            pageSize = SqliteWalHeader.Parse(header).PageSize;
        }
        catch (InvalidDataException) when (position + length <= WalHeaderSize)
        {
            // The engine is (re)establishing this WAL header. Header bytes are
            // plaintext in both images, so persist them verbatim; the next header
            // write re-establishes the encrypted checksum chain.
            return ReadRegion(
                BrowserPersistedFileKind.Passthrough,
                file,
                position,
                length,
                pageSize: 0,
                journalChecksumNonce: 0);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"Encrypted browser WAL '{path}' received frame bytes at {position} while its header was unreadable, "
                + "so the frame page image cannot be encrypted.",
                exception);
        }

        var frameSize = WalFrameHeaderSize + pageSize;
        var start = position < WalHeaderSize
            ? 0
            : WalHeaderSize + ((position - WalHeaderSize) / frameSize * frameSize);
        var end = position + length;
        if (end > WalHeaderSize)
        {
            var frames = (end - WalHeaderSize + frameSize - 1) / frameSize;
            end = WalHeaderSize + (frames * frameSize);
        }
        else
        {
            end = WalHeaderSize;
        }

        if (end > fileLength)
        {
            throw new InvalidDataException(
                $"Encrypted browser WAL '{path}' received a write ending at {position + length}, "
                + $"which is not covered by whole {frameSize}-byte frames within its {fileLength}-byte image.");
        }

        return ReadRegion(
            BrowserPersistedFileKind.Wal,
            file,
            start,
            checked((int)(end - start)),
            pageSize,
            journalChecksumNonce: 0);
    }

    private static BrowserPlaintextCapture CaptureJournal(
        string path,
        long position,
        int length,
        IFile file,
        long fileLength)
    {
        var headerLength = checked((int)Math.Min(SqliteRollbackJournalFormat.HeaderSize, fileLength));
        var header = new byte[headerLength];
        ReadExact(file, 0, header, path);
        if (!SqliteRollbackJournalFormat.TryReadLayout(
                header,
                out _,
                out var checksumNonce,
                out var pageSize,
                out var sectorSize))
        {
            if (position + length > SqliteRollbackJournalFormat.HeaderSize)
            {
                throw new InvalidDataException(
                    $"Encrypted browser rollback journal '{path}' received bytes at {position} while its header was "
                    + "unreadable, so page records cannot be encrypted.");
            }

            // Header not established yet (or being invalidated): persist verbatim.
            return ReadRegion(
                BrowserPersistedFileKind.Passthrough,
                file,
                position,
                length,
                pageSize: 0,
                journalChecksumNonce: 0);
        }

        var recordSize = SqliteRollbackJournalFormat.GetRecordSize(pageSize);
        long start;
        long end;
        if (position < sectorSize)
        {
            start = 0;
            end = Math.Min(sectorSize, fileLength);
            if (position + length > sectorSize)
            {
                throw new InvalidDataException(
                    $"Encrypted browser rollback journal '{path}' received a write spanning its header sector and page records.");
            }
        }
        else
        {
            var recordIndex = (position - sectorSize) / recordSize;
            start = sectorSize + (recordIndex * recordSize);
            var lastIndex = (position + length - sectorSize + recordSize - 1) / recordSize;
            end = sectorSize + (lastIndex * recordSize);
            if (end > fileLength)
            {
                // The engine builds a record from three writes (page number, page
                // image, checksum). Persist nothing until the record is complete:
                // the encrypted image needs the whole page to derive its checksum,
                // and a record only becomes meaningful once its checksum lands.
                return new BrowserPlaintextCapture(
                    BrowserPersistedFileKind.Passthrough,
                    position,
                    [],
                    pageSize: 0,
                    journalChecksumNonce: 0);
            }
        }

        return ReadRegion(
            position < sectorSize ? BrowserPersistedFileKind.Passthrough : BrowserPersistedFileKind.Journal,
            file,
            start,
            checked((int)(end - start)),
            pageSize,
            checksumNonce);
    }

    private async ValueTask<BrowserPersistedWrite> PrepareDatabaseAsync(
        string path,
        BrowserPlaintextCapture capture,
        CancellationToken cancellationToken)
    {
        var pageSize = capture.PageSize;
        var encrypted = new byte[capture.Bytes.Length];
        var firstPageNumber = checked((uint)(capture.Position / pageSize)) + 1;
        for (var offset = 0; offset < capture.Bytes.Length; offset += pageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = checked(firstPageNumber + (uint)(offset / pageSize));
            var page = await pages
                .EncryptPageAsync(capture.Bytes.AsMemory(offset, pageSize), pageNumber, cancellationToken)
                .ConfigureAwait(false);
            page.CopyTo(encrypted.AsSpan(offset));
        }

        return new BrowserPersistedWrite(capture.Position, encrypted, path, walUpdate: null);
    }

    private async ValueTask<BrowserPersistedWrite> PrepareWalAsync(
        string path,
        BrowserPlaintextCapture capture,
        CancellationToken cancellationToken)
    {
        var frameSize = WalFrameHeaderSize + capture.PageSize;
        var encrypted = capture.Bytes.AsSpan().ToArray();
        var offset = 0;
        var resetsChain = false;
        var seed = default((uint First, uint Second));
        var byteOrder = SqliteWalChecksumByteOrder.LittleEndian;

        if (capture.Position == 0)
        {
            // The WAL header is plaintext in both images and its checksum covers only
            // itself, so it seeds the encrypted chain exactly as it seeds the plaintext one.
            var header = SqliteWalHeader.Parse(capture.Bytes.AsSpan(0, WalHeaderSize));
            seed = (header.Checksum1, header.Checksum2);
            byteOrder = header.ChecksumByteOrder;
            resetsChain = true;
            offset = WalHeaderSize;
        }

        var firstFrameIndex = capture.Position == 0
            ? 0
            : checked((int)((capture.Position - WalHeaderSize) / frameSize));
        var chain = _walChains.TryGetValue(path, out var existing) ? existing : EmptyWalChain;
        var running = resetsChain
            ? seed
            : ResolveWalChecksum(path, chain, firstFrameIndex);
        var effectiveByteOrder = resetsChain ? byteOrder : chain.ByteOrder;
        var produced = new List<(uint First, uint Second)>();

        for (var frameIndex = firstFrameIndex; offset < encrypted.Length; frameIndex++, offset += frameSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = encrypted.AsMemory(offset, frameSize);
            var frameHeader = SqliteWalFrameHeader.Parse(frame.Span[..WalFrameHeaderSize]);
            var body = await pages
                .EncryptPageAsync(
                    capture.Bytes.AsMemory(offset + WalFrameHeaderSize, capture.PageSize),
                    frameHeader.PageNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            body.CopyTo(frame.Span[WalFrameHeaderSize..]);
            running = WriteWalFrameChecksum(frame.Span, effectiveByteOrder, running);
            produced.Add(running);
        }

        return new BrowserPersistedWrite(
            capture.Position,
            encrypted,
            path,
            new BrowserWalChainUpdate(
                resetsChain,
                frameSize,
                seed,
                resetsChain ? byteOrder : chain.ByteOrder,
                firstFrameIndex,
                produced));
    }

    private async ValueTask<BrowserPersistedWrite> PrepareJournalAsync(
        string path,
        BrowserPlaintextCapture capture,
        CancellationToken cancellationToken)
    {
        var pageSize = capture.PageSize;
        var recordSize = checked((int)SqliteRollbackJournalFormat.GetRecordSize(pageSize));
        var encrypted = capture.Bytes.AsSpan().ToArray();
        for (var offset = 0; offset < encrypted.Length; offset += recordSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(offset, recordSize));
            if (pageNumber == 0)
                continue;

            var page = await pages
                .EncryptPageAsync(
                    capture.Bytes.AsMemory(
                        offset + SqliteRollbackJournalFormat.RecordPageNumberSize,
                        pageSize),
                    pageNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            WriteJournalRecord(encrypted, offset, pageSize, page, capture.JournalChecksumNonce);
        }

        return new BrowserPersistedWrite(capture.Position, encrypted, path, walUpdate: null);
    }

    private async ValueTask<byte[]> DecryptDatabaseImageAsync(
        byte[] encryptedImage,
        CancellationToken cancellationToken)
    {
        if (encryptedImage.Length == 0)
            return encryptedImage;
        if (encryptedImage.Length < AhtolaEncryptedPageFormat.SqliteHeaderSize)
            throw new InvalidDataException("Encrypted browser database image is smaller than a page header.");
        if (!AhtolaEncryptedPageFormat.IsAhtolaEncrypted(encryptedImage))
        {
            throw new InvalidDataException(
                AhtolaPasswordEncryption.EnsureEncryptedOrNotDatabasePhrase(
                    "Encryption was requested, but the browser database contains a plaintext SQLite header. "
                    + "Plaintext fallback is not permitted."));
        }

        var pageSize = ReadEncodedPageSize(encryptedImage);
        if (encryptedImage.Length % pageSize != 0)
            throw new InvalidDataException("Encrypted browser database image is not a whole number of pages.");

        var plaintext = new byte[encryptedImage.Length];
        for (var offset = 0; offset < encryptedImage.Length; offset += pageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = checked((uint)(offset / pageSize)) + 1;
            var page = await pages
                .DecryptPageAsync(encryptedImage.AsMemory(offset, pageSize), pageNumber, cancellationToken)
                .ConfigureAwait(false);
            page.CopyTo(plaintext.AsSpan(offset));
        }

        return plaintext;
    }

    private async ValueTask<byte[]> DecryptWalImageAsync(
        string path,
        byte[] encryptedImage,
        CancellationToken cancellationToken)
    {
        if (encryptedImage.Length < WalHeaderSize)
            return encryptedImage;

        SqliteWalHeader header;
        try
        {
            header = SqliteWalHeader.Parse(encryptedImage.AsSpan(0, WalHeaderSize));
        }
        catch (InvalidDataException)
        {
            return encryptedImage;
        }

        var frameSize = WalFrameHeaderSize + header.PageSize;
        var plaintext = encryptedImage.AsSpan().ToArray();
        var chain = new WalChain
        {
            Initialized = true,
            FrameSize = frameSize,
            Seed = (header.Checksum1, header.Checksum2),
            ByteOrder = header.ChecksumByteOrder,
        };

        var encryptedRunning = chain.Seed;
        var plaintextRunning = chain.Seed;
        for (var offset = WalHeaderSize; offset + frameSize <= encryptedImage.Length; offset += frameSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteWalFrameHeader frameHeader;
            try
            {
                frameHeader = SqliteWalFrameHeader.Parse(encryptedImage.AsSpan(offset, WalFrameHeaderSize));
            }
            catch (InvalidDataException)
            {
                break;
            }

            var expected = ComputeWalFrameChecksum(
                encryptedImage.AsSpan(offset, frameSize),
                chain.ByteOrder,
                encryptedRunning);
            if (expected != (frameHeader.Checksum1, frameHeader.Checksum2))
            {
                // Stop at the first frame that does not belong to this chain, exactly
                // as the pager's recovery scan does; the tail stays verbatim.
                break;
            }

            var body = await pages
                .DecryptPageAsync(
                    encryptedImage.AsMemory(offset + WalFrameHeaderSize, header.PageSize),
                    frameHeader.PageNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            body.CopyTo(plaintext.AsSpan(offset + WalFrameHeaderSize));
            plaintextRunning = WriteWalFrameChecksum(
                plaintext.AsSpan(offset, frameSize),
                chain.ByteOrder,
                plaintextRunning);
            encryptedRunning = expected;
            chain.FrameChecksums.Add(encryptedRunning);
        }

        _walChains[path] = chain;
        return plaintext;
    }

    private async ValueTask<byte[]> DecryptJournalImageAsync(
        byte[] encryptedImage,
        CancellationToken cancellationToken)
    {
        if (!SqliteRollbackJournalFormat.TryReadLayout(
                encryptedImage,
                out _,
                out var checksumNonce,
                out var pageSize,
                out var sectorSize))
        {
            return encryptedImage;
        }

        var recordSize = checked((int)SqliteRollbackJournalFormat.GetRecordSize(pageSize));
        var plaintext = encryptedImage.AsSpan().ToArray();
        for (var offset = sectorSize; offset + recordSize <= encryptedImage.Length; offset += recordSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(encryptedImage.AsSpan(offset));
            if (pageNumber == 0)
                break;

            var encryptedPage = encryptedImage.AsMemory(
                offset + SqliteRollbackJournalFormat.RecordPageNumberSize,
                pageSize);
            var storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(
                encryptedImage.AsSpan(offset + SqliteRollbackJournalFormat.RecordPageNumberSize + pageSize));
            if (SqliteRollbackJournalFormat.ComputeChecksum(encryptedPage.Span, checksumNonce) != storedChecksum)
                break;

            var page = await pages
                .DecryptPageAsync(encryptedPage, pageNumber, cancellationToken)
                .ConfigureAwait(false);
            WriteJournalRecord(plaintext, offset, pageSize, page, checksumNonce);
        }

        return plaintext;
    }

    private static void WriteJournalRecord(
        byte[] plaintext,
        int offset,
        int pageSize,
        byte[] page,
        uint checksumNonce)
    {
        page.CopyTo(plaintext.AsSpan(offset + SqliteRollbackJournalFormat.RecordPageNumberSize));
        BinaryPrimitives.WriteUInt32BigEndian(
            plaintext.AsSpan(offset + SqliteRollbackJournalFormat.RecordPageNumberSize + pageSize),
            SqliteRollbackJournalFormat.ComputeChecksum(page, checksumNonce));
    }

    private static readonly WalChain EmptyWalChain = new();

    private static (uint First, uint Second) ResolveWalChecksum(string path, WalChain chain, int frameIndex)
    {
        if (!chain.Initialized)
        {
            throw new InvalidDataException(
                $"Encrypted browser WAL '{path}' received a frame before its header established the checksum chain.");
        }
        if (frameIndex == 0)
            return chain.Seed;
        if (chain.FrameChecksums.Count < frameIndex)
        {
            throw new InvalidDataException(
                $"Encrypted browser WAL '{path}' received frame {frameIndex + 1} without the preceding encrypted checksum chain.");
        }

        return chain.FrameChecksums[frameIndex - 1];
    }

    private static (uint First, uint Second) ComputeWalFrameChecksum(
        ReadOnlySpan<byte> frame,
        SqliteWalChecksumByteOrder byteOrder,
        (uint First, uint Second) previous)
    {
        var (First, Second) = SqliteWalChecksum.Calculate(frame[..8], byteOrder, previous.First, previous.Second);
        return SqliteWalChecksum.Calculate(frame[WalFrameHeaderSize..], byteOrder, First, Second);
    }

    private static (uint First, uint Second) WriteWalFrameChecksum(
        Span<byte> frame,
        SqliteWalChecksumByteOrder byteOrder,
        (uint First, uint Second) previous)
    {
        var checksum = ComputeWalFrameChecksum(frame, byteOrder, previous);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(16, 4), checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(20, 4), checksum.Second);
        return checksum;
    }

    private static int ReadDatabasePageSize(string path, IFile file, long fileLength)
    {
        if (fileLength < AhtolaEncryptedPageFormat.SqliteHeaderSize)
        {
            throw new InvalidDataException(
                $"Encrypted browser database '{path}' was written before its {AhtolaEncryptedPageFormat.SqliteHeaderSize}-byte header existed.");
        }

        var header = new byte[PageCodecHeaderInfo.SqliteBootstrapHeaderLength];
        ReadExact(file, 0, header, path);
        return ReadEncodedPageSize(header);
    }

    private static int ReadEncodedPageSize(ReadOnlySpan<byte> header)
    {
        var encoded = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        var pageSize = encoded == 1 ? 65_536 : encoded;
        _ = SqlitePageSize.Encode(pageSize);
        return pageSize;
    }

    private static BrowserPlaintextCapture ReadRegion(
        BrowserPersistedFileKind kind,
        IFile file,
        long position,
        int length,
        int pageSize,
        uint journalChecksumNonce)
    {
        var bytes = new byte[length];
        if (length != 0)
            ReadExact(file, position, bytes, kind.ToString());
        return new BrowserPlaintextCapture(kind, position, bytes, pageSize, journalChecksumNonce);
    }

    private static void ReadExact(IFile file, long position, Span<byte> destination, string path)
    {
        if (file.Read(position, destination) != destination.Length)
        {
            throw new InvalidDataException(
                $"Encrypted browser storage could not read {destination.Length} bytes at {position} from '{path}'.");
        }
    }

    private sealed class WalChain
    {
        public bool Initialized { get; set; }

        public int FrameSize { get; set; }

        public (uint First, uint Second) Seed { get; set; }

        public SqliteWalChecksumByteOrder ByteOrder { get; set; } = SqliteWalChecksumByteOrder.LittleEndian;

        public List<(uint First, uint Second)> FrameChecksums { get; } = [];
    }
}
