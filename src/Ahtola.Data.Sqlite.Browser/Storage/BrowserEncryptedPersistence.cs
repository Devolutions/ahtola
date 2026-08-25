using System.Buffers.Binary;
using System.Security.Cryptography;
using Ahtola.Core.Mvcc;
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

    /// <summary>An MVCC logical log whose transaction payloads are encrypted.</summary>
    MvccLog,

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
    uint journalChecksumNonce,
    ulong mvccSalt = 0,
    byte mvccVersion = 0)
{
    internal BrowserPersistedFileKind Kind { get; } = kind;

    internal long Position { get; } = position;

    internal byte[] Bytes { get; } = bytes;

    /// <summary>The page size in force for this region, or zero for passthrough content.</summary>
    internal int PageSize { get; } = pageSize;

    /// <summary>The rollback journal checksum nonce, valid only for journal records.</summary>
    internal uint JournalChecksumNonce { get; } = journalChecksumNonce;

    /// <summary>The authenticated logical-log salt, valid only for MVCC captures.</summary>
    internal ulong MvccSalt { get; } = mvccSalt;

    /// <summary>The authenticated logical-log format version, valid only for MVCC captures.</summary>
    internal byte MvccVersion { get; } = mvccVersion;
}

/// <summary>
/// A transformed region ready to be written to OPFS, plus the WAL chain state to
/// commit once that write succeeds.
/// </summary>
internal sealed class BrowserPersistedWrite(
    long position,
    byte[] bytes,
    string path,
    BrowserWalChainUpdate? walUpdate,
    long? persistedLength = null)
{
    internal long Position { get; } = position;

    internal byte[] Bytes { get; } = bytes;

    internal string Path { get; } = path;

    internal BrowserWalChainUpdate? WalUpdate { get; } = walUpdate;

    /// <summary>A transformed file length to publish after this write succeeds.</summary>
    internal long? PersistedLength { get; } = persistedLength;
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
/// The encryption roles assigned to a persisted directory, separating the files
/// that must be loaded from the abandoned engine temporaries that must not be.
/// </summary>
internal sealed class BrowserLoadPlan(
    IReadOnlyList<(string Path, BrowserPersistedFileKind Kind)> files,
    IReadOnlyList<string> transientArtifacts,
    IReadOnlyList<string> ignoredPaths)
{
    /// <summary>Files to load, in an order where every base path precedes its sidecars.</summary>
    internal IReadOnlyList<(string Path, BrowserPersistedFileKind Kind)> Files { get; } = files;

    /// <summary>Abandoned engine temporaries that are safe to discard.</summary>
    internal IReadOnlyList<string> TransientArtifacts { get; } = transientArtifacts;

    /// <summary>
    /// Paths that carry no content the encrypted engine can use and are left
    /// untouched rather than decrypted or deleted.
    /// </summary>
    internal IReadOnlyList<string> IgnoredPaths { get; } = ignoredPaths;
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
    /// <summary>The AHTLA cipher this persistence layer writes.</summary>
    internal Core.Storage.AhtolaEncryptionCipher Cipher => pages.Cipher;

    private const int WalHeaderSize = SqliteWalHeader.Size;
    private const int WalFrameHeaderSize = SqliteWalFrameHeader.Size;
    private const int RoleProbeLength = 16;

    private readonly Dictionary<string, WalChain> _walChains = new(StringComparer.Ordinal);
    private readonly BrowserPersistedFileRoles _roles = new();
    private Func<string, bool>? _basePathExists;

    /// <summary>
    /// Supplies the predicate that reports whether a base database path currently
    /// exists, so a sidecar can be recognized even before its database is opened.
    /// </summary>
    internal void SetBasePathProbe(Func<string, bool> basePathExists)
        => _basePathExists = basePathExists;

    /// <summary>Declares a path the caller already knows is a database.</summary>
    internal void RegisterDatabase(string path) => _roles.RegisterDatabase(path);

    /// <summary>
    /// Resolves the encryption role of a path that the engine is mutating, probing
    /// its current bytes so a database whose name resembles a sidecar is not
    /// demoted (and, for <c>-shm</c>, silently persisted in the clear).
    /// </summary>
    private BrowserPersistedFileKind ClassifyForWrite(string path, IFile file)
    {
        Span<byte> probe = stackalloc byte[RoleProbeLength];
        var read = file.Length == 0 ? 0 : file.Read(0, probe);
        var role = _roles.Resolve(path, probe[..Math.Max(read, 0)], _basePathExists);
        return MapRole(path, role);
    }

    private static BrowserPersistedFileKind MapRole(string path, BrowserPersistedFileRole role)
        => role switch
        {
            BrowserPersistedFileRole.Database => BrowserPersistedFileKind.Database,
            BrowserPersistedFileRole.Wal => BrowserPersistedFileKind.Wal,
            BrowserPersistedFileRole.Journal => BrowserPersistedFileKind.Journal,
            BrowserPersistedFileRole.SharedMemory => BrowserPersistedFileKind.Passthrough,
            BrowserPersistedFileRole.MvccLog => BrowserPersistedFileKind.MvccLog,
            _ => throw new InvalidOperationException($"Unknown persisted browser file role {role}."),
        };

    /// <summary>
    /// Expands a just-completed engine write to whole pages, WAL frames, or journal
    /// records and copies that region out of <paramref name="file"/>.
    /// </summary>
    internal BrowserPlaintextCapture Capture(string path, long position, int length, IFile file)
    {
        var kind = ClassifyForWrite(path, file);
        if (kind == BrowserPersistedFileKind.Passthrough)
            return ReadRegion(kind, file, position, length, pageSize: 0, journalChecksumNonce: 0);

        var fileLength = file.Length;
        return kind switch
        {
            BrowserPersistedFileKind.Database => CaptureDatabase(path, position, length, file, fileLength),
            BrowserPersistedFileKind.Wal => CaptureWal(path, position, length, file, fileLength),
            BrowserPersistedFileKind.Journal => CaptureJournal(path, position, length, file, fileLength),
            BrowserPersistedFileKind.MvccLog => CaptureMvccLog(path, position, length, file, fileLength),
            _ => ReadRegion(kind, file, position, length, pageSize: 0, journalChecksumNonce: 0),
        };
    }

    /// <summary>Maps a plaintext mirror length to its encrypted persisted length.</summary>
    internal long MapPersistedLength(string path, long logicalLength, IFile file)
    {
        var kind = ClassifyForWrite(path, file);
        return kind == BrowserPersistedFileKind.MvccLog
            ? GetMvccPersistedLength(path, file, logicalLength)
            : logicalLength;
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
            BrowserPersistedFileKind.MvccLog
                => await PrepareMvccLogAsync(path, capture, cancellationToken).ConfigureAwait(false),
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
    internal void NotifyDeleted(string path)
    {
        _walChains.Remove(path);
        _roles.Forget(path);
    }

    /// <summary>Moves cached state along with an atomically replaced file.</summary>
    internal void NotifyReplaced(string sourcePath, string destinationPath)
    {
        if (_walChains.Remove(sourcePath, out var chain))
            _walChains[destinationPath] = chain;
        else
            _walChains.Remove(destinationPath);
        _roles.Rename(sourcePath, destinationPath);
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
    /// Assigns encryption roles to every persisted path and separates the abandoned
    /// engine temporaries that must not be loaded or decrypted.
    /// </summary>
    /// <remarks>
    /// Paths are resolved shortest-first because a sidecar name is always strictly
    /// longer than the database it belongs to, so every base path is known before
    /// anything derived from it is classified.
    /// </remarks>
    internal BrowserLoadPlan PlanLoad(
        IReadOnlyList<string> paths,
        Func<string, byte[]> probeHeader)
    {
        var ordered = paths
            .OrderBy(static path => path.Length)
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        var loadable = new List<(string Path, BrowserPersistedFileKind Kind)>();
        var transient = new List<string>();
        var ignored = new List<string>();
        var present = new HashSet<string>(ordered, StringComparer.Ordinal);
        foreach (var path in ordered)
        {
            if (BrowserPersistedFileRoles.IsTransientArtifact(path))
            {
                transient.Add(path);
                continue;
            }

            var role = _roles.Resolve(path, probeHeader(path), present.Contains);
            loadable.Add((path, MapRole(path, role)));
        }

        return new BrowserLoadPlan(loadable, transient, ignored);
    }

    /// <summary>
    /// Turns the loaded encrypted images into the plaintext the engine reads.
    /// </summary>
    /// <remarks>
    /// A crash can leave a torn page in the main database, so recovery has to run
    /// in the encrypted domain before any page is authenticated. Hot rollback
    /// journals are replayed onto the encrypted image first, and a page that still
    /// fails authentication is satisfied from a committed WAL frame when one exists.
    /// Only when no recovery source can supply the page does the open fail.
    /// </remarks>
    internal async ValueTask<Dictionary<string, byte[]>> DecryptLoadedImagesAsync(
        BrowserLoadPlan plan,
        Dictionary<string, byte[]> encryptedImages,
        CancellationToken cancellationToken)
    {
        var plaintext = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var walPages = new Dictionary<string, IReadOnlyDictionary<uint, byte[]>>(StringComparer.Ordinal);

        foreach (var (path, kind) in plan.Files)
        {
            if (kind is not BrowserPersistedFileKind.Wal)
                continue;

            _walChains.Remove(path);
            var (Image, CommittedPages) = await DecryptWalImageAsync(
                    path,
                    encryptedImages[path],
                    cancellationToken)
                .ConfigureAwait(false);
            plaintext[path] = Image;
            walPages[path] = CommittedPages;
        }

        foreach (var (path, kind) in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (kind)
            {
                case BrowserPersistedFileKind.Wal:
                    break;
                case BrowserPersistedFileKind.Passthrough:
                    plaintext[path] = encryptedImages[path];
                    break;
                case BrowserPersistedFileKind.Journal:
                    plaintext[path] = await DecryptJournalImageAsync(
                            encryptedImages[path],
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case BrowserPersistedFileKind.MvccLog:
                    plaintext[path] = await DecryptMvccLogImageAsync(
                            path,
                            encryptedImages[path],
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case BrowserPersistedFileKind.Database:
                    {
                        var image = encryptedImages[path];
                        if (encryptedImages.TryGetValue(path + "-journal", out var journal))
                            image = ApplyEncryptedJournalRollback(image, journal);
                        walPages.TryGetValue(path + "-wal", out var recoveryPages);
                        plaintext[path] = await DecryptDatabaseImageAsync(
                                image,
                                recoveryPages,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                default:
                    throw new InvalidOperationException($"Unknown persisted browser file kind {kind}.");
            }
        }

        return plaintext;
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

        var capture = ReadRegion(
            BrowserPersistedFileKind.Database,
            file,
            start,
            checked((int)(end - start)),
            pageSize,
            journalChecksumNonce: 0);
        return capture;
    }

    private static BrowserPlaintextCapture CaptureMvccLog(
        string path,
        long position,
        int length,
        IFile file,
        long fileLength)
    {
        if (position == 0)
        {
            var header = new byte[length];
            ReadExact(file, 0, header, path);
            var headerInfo = fileLength >= MvccLogicalLogFormat.LogHeaderSize
                ? MvccLogicalLogFormat.ValidateHeader(
                    ReadFileRegion(file, 0, MvccLogicalLogFormat.LogHeaderSize, path))
                : (Salt: 0UL, Version: (byte)0);
            return new BrowserPlaintextCapture(
                BrowserPersistedFileKind.MvccLog,
                0,
                header,
                pageSize: 0,
                journalChecksumNonce: 0,
                headerInfo.Salt,
                headerInfo.Version);
        }

        if (position < MvccLogicalLogFormat.LogHeaderSize)
        {
            throw new InvalidDataException(
                $"Encrypted browser MVCC log '{path}' received a write inside its fixed header.");
        }

        var logHeader = ReadFileRegion(file, 0, MvccLogicalLogFormat.LogHeaderSize, path);
        var (salt, version) = MvccLogicalLogFormat.ValidateHeader(logHeader);
        var persistedPosition = GetMvccPersistedLength(path, file, position);
        var frame = ReadFileRegion(file, position, length, path);
        ValidatePlaintextMvccFrame(frame, path);
        return new BrowserPlaintextCapture(
            BrowserPersistedFileKind.MvccLog,
            persistedPosition,
            frame,
            pageSize: 0,
            journalChecksumNonce: 0,
            salt,
            version);
    }

    private static long GetMvccPersistedLength(string path, IFile file, long logicalLength)
    {
        if (logicalLength is 0)
            return 0;
        if (logicalLength < MvccLogicalLogFormat.LogHeaderSize)
            return logicalLength;
        if (logicalLength > file.Length)
            throw new InvalidDataException($"MVCC logical length exceeds the mirror image for '{path}'.");

        var logHeader = ReadFileRegion(file, 0, MvccLogicalLogFormat.LogHeaderSize, path);
        _ = MvccLogicalLogFormat.ValidateHeader(logHeader);
        long logicalPosition = MvccLogicalLogFormat.LogHeaderSize;
        long persistedPosition = MvccLogicalLogFormat.LogHeaderSize;
        while (logicalPosition < logicalLength)
        {
            if (logicalPosition + MvccLogicalLogFormat.TxHeaderSize > logicalLength)
                throw new InvalidDataException($"MVCC logical length cuts through a frame header in '{path}'.");

            var frameHeader = ReadFileRegion(
                file,
                logicalPosition,
                MvccLogicalLogFormat.TxHeaderSize,
                path);
            var (payloadSize, _, _) = MvccLogicalLogFormat.ReadFrameHeader(frameHeader);
            var logicalFrameSize = checked(
                MvccLogicalLogFormat.TxHeaderSize
                + payloadSize
                + MvccLogicalLogFormat.TxTrailerSize);
            if (logicalPosition + logicalFrameSize > logicalLength)
                throw new InvalidDataException($"MVCC logical length cuts through a transaction frame in '{path}'.");

            var frame = ReadFileRegion(file, logicalPosition, logicalFrameSize, path);
            ValidatePlaintextMvccFrame(frame, path);
            logicalPosition += logicalFrameSize;
            persistedPosition += checked(
                MvccLogicalLogFormat.TxHeaderSize
                + MvccLogicalLogFormat.GetEncryptedPayloadSize(payloadSize)
                + MvccLogicalLogFormat.TxTrailerSize);
        }

        return persistedPosition;
    }

    private static void ValidatePlaintextMvccFrame(ReadOnlySpan<byte> frame, string path)
    {
        var (payloadSize, _, _) = MvccLogicalLogFormat.ReadFrameHeader(frame);
        var expectedLength = checked(
            MvccLogicalLogFormat.TxHeaderSize
            + payloadSize
            + MvccLogicalLogFormat.TxTrailerSize);
        if (frame.Length != expectedLength)
            throw new InvalidDataException($"Encrypted browser MVCC log '{path}' received a partial frame write.");

        var trailerOffset = MvccLogicalLogFormat.TxHeaderSize + payloadSize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame[(trailerOffset + 4)..])
            != MvccLogicalLogFormat.EndMagic)
        {
            throw new InvalidDataException($"MVCC logical-log frame end magic is invalid in '{path}'.");
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frame[trailerOffset..]);
        if (expectedCrc != Crc32C.Compute(frame[..trailerOffset]))
            throw new InvalidDataException($"MVCC logical-log frame CRC is invalid in '{path}'.");
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

    private async ValueTask<BrowserPersistedWrite> PrepareMvccLogAsync(
        string path,
        BrowserPlaintextCapture capture,
        CancellationToken cancellationToken)
    {
        if (capture.Position == 0)
        {
            if (capture.Bytes.Length >= MvccLogicalLogFormat.LogHeaderSize)
                _ = MvccLogicalLogFormat.ValidateHeader(capture.Bytes);
            return new BrowserPersistedWrite(
                0,
                capture.Bytes,
                path,
                walUpdate: null,
                persistedLength: capture.Bytes.LongLength);
        }

        ValidatePlaintextMvccFrame(capture.Bytes, path);
        var (payloadSize, opCount, commitTs) = MvccLogicalLogFormat.ReadFrameHeader(capture.Bytes);
        var encryptedPayloadSize = MvccLogicalLogFormat.GetEncryptedPayloadSize(payloadSize);
        var encryptedFrame = new byte[checked(
            MvccLogicalLogFormat.TxHeaderSize
            + encryptedPayloadSize
            + MvccLogicalLogFormat.TxTrailerSize)];
        capture.Bytes.AsSpan(0, MvccLogicalLogFormat.TxHeaderSize).CopyTo(encryptedFrame);

        var plaintextOffset = MvccLogicalLogFormat.TxHeaderSize;
        var encryptedOffset = MvccLogicalLogFormat.TxHeaderSize;
        var chunkCount = MvccLogicalLogFormat.GetEncryptedChunkCount(payloadSize);
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plaintextLength = MvccLogicalLogFormat.GetPlaintextChunkLength(payloadSize, chunkIndex);
            var nonce = new byte[MvccLogicalLogFormat.EncryptionNonceSize];
            RandomNumberGenerator.Fill(nonce);
            var associatedData = MvccLogicalLogFormat.BuildEncryptedChunkAssociatedData(
                capture.MvccSalt,
                payloadSize,
                opCount,
                commitTs,
                chunkIndex,
                capture.MvccVersion);
            var result = await pages
                .EncryptLogicalLogChunkAsync(
                    capture.Bytes.AsMemory(plaintextOffset, plaintextLength),
                    nonce,
                    associatedData,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Ciphertext.Length != plaintextLength
                || result.Tag.Length != MvccLogicalLogFormat.EncryptionTagSize)
            {
                throw new CryptographicException(
                    "The asynchronous AES-GCM provider returned an invalid MVCC logical-log chunk.");
            }

            result.Ciphertext.CopyTo(encryptedFrame.AsSpan(encryptedOffset));
            result.Tag.CopyTo(encryptedFrame.AsSpan(encryptedOffset + plaintextLength));
            nonce.CopyTo(encryptedFrame.AsSpan(
                encryptedOffset + plaintextLength + MvccLogicalLogFormat.EncryptionTagSize));
            plaintextOffset += plaintextLength;
            encryptedOffset += plaintextLength + MvccLogicalLogFormat.EncryptionOverhead;
        }

        var trailerOffset = MvccLogicalLogFormat.TxHeaderSize + encryptedPayloadSize;
        BinaryPrimitives.WriteUInt32LittleEndian(
            encryptedFrame.AsSpan(trailerOffset),
            Crc32C.Compute(encryptedFrame.AsSpan(0, trailerOffset)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            encryptedFrame.AsSpan(trailerOffset + sizeof(uint)),
            MvccLogicalLogFormat.EndMagic);
        return new BrowserPersistedWrite(
            capture.Position,
            encryptedFrame,
            path,
            walUpdate: null,
            persistedLength: checked(capture.Position + encryptedFrame.LongLength));
    }

    private async ValueTask<byte[]> DecryptMvccLogImageAsync(
        string path,
        byte[] encryptedImage,
        CancellationToken cancellationToken)
    {
        if (encryptedImage.Length < MvccLogicalLogFormat.LogHeaderSize)
            return encryptedImage;

        var (salt, version) = MvccLogicalLogFormat.ValidateHeader(encryptedImage);
        using var plaintext = new MemoryStream(encryptedImage.Length);
        plaintext.Write(encryptedImage, 0, MvccLogicalLogFormat.LogHeaderSize);
        var position = MvccLogicalLogFormat.LogHeaderSize;
        while (position < encryptedImage.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position + MvccLogicalLogFormat.TxHeaderSize > encryptedImage.Length)
                break;

            var frameHeader = encryptedImage
                .AsSpan(position, MvccLogicalLogFormat.TxHeaderSize)
                .ToArray();
            var (payloadSize, opCount, commitTs) = MvccLogicalLogFormat.ReadFrameHeader(frameHeader);
            var encryptedPayloadSize = MvccLogicalLogFormat.GetEncryptedPayloadSize(payloadSize);
            var encryptedFrameSize = checked(
                MvccLogicalLogFormat.TxHeaderSize
                + encryptedPayloadSize
                + MvccLogicalLogFormat.TxTrailerSize);
            if (position + encryptedFrameSize > encryptedImage.Length)
            {
                if (IsCompletePlaintextMvccFrame(encryptedImage, position, payloadSize))
                {
                    throw new InvalidDataException(
                        $"Encrypted browser storage contains a plaintext MVCC logical-log frame in '{path}'. "
                        + "Automatic migration is not safe.");
                }
                if (MvccLogicalLogFormat.ContainsCompleteFrameBoundary(
                        encryptedImage.AsSpan(position)))
                {
                    throw new InvalidDataException(
                        $"MVCC logical-log payload length does not match its complete frame boundary in '{path}'. "
                        + "The authenticated frame metadata was tampered with.");
                }

                break;
            }

            var trailerOffset = position + MvccLogicalLogFormat.TxHeaderSize + encryptedPayloadSize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(encryptedImage.AsSpan(trailerOffset + sizeof(uint)))
                != MvccLogicalLogFormat.EndMagic)
            {
                throw new InvalidDataException($"MVCC logical-log frame end magic is invalid in '{path}'.");
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                encryptedImage.AsSpan(trailerOffset));
            var actualCrc = Crc32C.Compute(
                encryptedImage.AsSpan(
                    position,
                    MvccLogicalLogFormat.TxHeaderSize + encryptedPayloadSize));
            if (expectedCrc != actualCrc)
                throw new InvalidDataException($"MVCC logical-log frame CRC is invalid in '{path}'.");

            var payload = new byte[payloadSize];
            var plaintextOffset = 0;
            var encryptedOffset = position + MvccLogicalLogFormat.TxHeaderSize;
            try
            {
                var chunkCount = MvccLogicalLogFormat.GetEncryptedChunkCount(payloadSize);
                for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    var plaintextLength = MvccLogicalLogFormat.GetPlaintextChunkLength(
                        payloadSize,
                        chunkIndex);
                    var associatedData = MvccLogicalLogFormat.BuildEncryptedChunkAssociatedData(
                        salt,
                        payloadSize,
                        opCount,
                        commitTs,
                        chunkIndex,
                        version);
                    var chunk = await pages
                        .DecryptLogicalLogChunkAsync(
                            encryptedImage.AsMemory(encryptedOffset, plaintextLength),
                            encryptedImage.AsMemory(
                                encryptedOffset + plaintextLength,
                                MvccLogicalLogFormat.EncryptionTagSize),
                            encryptedImage.AsMemory(
                                encryptedOffset
                                + plaintextLength
                                + MvccLogicalLogFormat.EncryptionTagSize,
                                MvccLogicalLogFormat.EncryptionNonceSize),
                            associatedData,
                            cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        if (chunk.Length != plaintextLength)
                            throw new CryptographicException("Decrypted MVCC logical-log chunk has an invalid length.");
                        chunk.CopyTo(payload.AsSpan(plaintextOffset));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(chunk);
                    }

                    plaintextOffset += plaintextLength;
                    encryptedOffset += plaintextLength + MvccLogicalLogFormat.EncryptionOverhead;
                }
            }
            catch (CryptographicException exception)
            {
                CryptographicOperations.ZeroMemory(payload);
                throw new InvalidDataException(
                    $"MVCC logical-log payload authentication failed in '{path}'. "
                    + "The key is wrong or the log was tampered with.",
                    exception);
            }

            plaintext.Write(frameHeader);
            plaintext.Write(payload);
            var plaintextFrameCrc = Crc32C.Compute(
                plaintext.GetBuffer().AsSpan(
                    checked((int)plaintext.Position - payload.Length - MvccLogicalLogFormat.TxHeaderSize),
                    MvccLogicalLogFormat.TxHeaderSize + payload.Length));
            var trailer = new byte[MvccLogicalLogFormat.TxTrailerSize];
            BinaryPrimitives.WriteUInt32LittleEndian(trailer, plaintextFrameCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(
                trailer.AsSpan(sizeof(uint)),
                MvccLogicalLogFormat.EndMagic);
            plaintext.Write(trailer);
            CryptographicOperations.ZeroMemory(payload);
            position += encryptedFrameSize;
        }

        return plaintext.ToArray();
    }

    private static bool IsCompletePlaintextMvccFrame(
        ReadOnlySpan<byte> image,
        int position,
        int payloadSize)
    {
        var frameSize = checked(
            MvccLogicalLogFormat.TxHeaderSize
            + payloadSize
            + MvccLogicalLogFormat.TxTrailerSize);
        if (position + frameSize > image.Length)
            return false;

        var trailerOffset = position + MvccLogicalLogFormat.TxHeaderSize + payloadSize;
        return BinaryPrimitives.ReadUInt32LittleEndian(image[(trailerOffset + sizeof(uint))..])
                   == MvccLogicalLogFormat.EndMagic
               && BinaryPrimitives.ReadUInt32LittleEndian(image[trailerOffset..])
                   == Crc32C.Compute(image.Slice(
                       position,
                       MvccLogicalLogFormat.TxHeaderSize + payloadSize));
    }

    private async ValueTask<byte[]> DecryptDatabaseImageAsync(
        byte[] encryptedImage,
        IReadOnlyDictionary<uint, byte[]>? recoveryPages,
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
            byte[] page;
            try
            {
                page = await pages
                    .DecryptPageAsync(encryptedImage.AsMemory(offset, pageSize), pageNumber, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException) when (
                recoveryPages is not null
                && recoveryPages.TryGetValue(pageNumber, out var recovered)
                && recovered.Length == pageSize)
            {
                // A crash during checkpoint can tear a page that a committed WAL
                // frame still holds. Recovering it here keeps the open alive; the
                // pager then re-applies the same frame through its own checkpoint.
                page = recovered;
            }

            page.CopyTo(plaintext.AsSpan(offset));
        }

        return plaintext;
    }

    /// <summary>
    /// Replays a hot rollback journal's encrypted page images back into the
    /// encrypted database image, restoring the pre-transaction content before any
    /// page is authenticated.
    /// </summary>
    private static byte[] ApplyEncryptedJournalRollback(byte[] databaseImage, byte[] journalImage)
    {
        if (!SqliteRollbackJournalFormat.HasMagic(journalImage))
            return databaseImage;
        if (!SqliteRollbackJournalFormat.TryReadLayout(
                journalImage,
                out var recordCount,
                out var checksumNonce,
                out var pageSize,
                out var sectorSize))
        {
            return databaseImage;
        }

        var originalPageCount = BinaryPrimitives.ReadUInt32BigEndian(journalImage.AsSpan(16));
        if (originalPageCount == 0 || databaseImage.Length % pageSize != 0)
            return databaseImage;

        var recordSize = checked((int)SqliteRollbackJournalFormat.GetRecordSize(pageSize));
        var restored = databaseImage.AsSpan().ToArray();
        var applied = 0u;
        for (var offset = sectorSize; offset + recordSize <= journalImage.Length; offset += recordSize)
        {
            if (recordCount != uint.MaxValue && applied >= recordCount)
                break;

            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(journalImage.AsSpan(offset));
            if (pageNumber == 0 || pageNumber > originalPageCount)
                break;

            var page = journalImage.AsSpan(
                offset + SqliteRollbackJournalFormat.RecordPageNumberSize,
                pageSize);
            var storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(
                journalImage.AsSpan(offset + SqliteRollbackJournalFormat.RecordPageNumberSize + pageSize));
            if (SqliteRollbackJournalFormat.ComputeChecksum(page, checksumNonce) != storedChecksum)
                break;

            var target = checked((int)(pageNumber - 1)) * pageSize;
            if (target + pageSize > restored.Length)
                break;

            page.CopyTo(restored.AsSpan(target));
            applied++;
        }

        if (applied == 0)
            return databaseImage;

        var originalLength = checked((int)originalPageCount) * pageSize;
        if (restored.Length > originalLength)
            Array.Resize(ref restored, originalLength);
        return restored;
    }

    private async ValueTask<(byte[] Image, IReadOnlyDictionary<uint, byte[]> CommittedPages)> DecryptWalImageAsync(
        string path,
        byte[] encryptedImage,
        CancellationToken cancellationToken)
    {
        var committed = new Dictionary<uint, byte[]>();
        if (encryptedImage.Length < WalHeaderSize)
            return (encryptedImage, committed);

        SqliteWalHeader header;
        try
        {
            header = SqliteWalHeader.Parse(encryptedImage.AsSpan(0, WalHeaderSize));
        }
        catch (InvalidDataException)
        {
            return (encryptedImage, committed);
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

        var pending = new Dictionary<uint, byte[]>();
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

            pending[frameHeader.PageNumber] = body;
            if (!frameHeader.IsCommit)
                continue;

            // Only frames up to a commit marker may repair the main database,
            // mirroring exactly what a checkpoint is allowed to write.
            foreach (var (pageNumber, image) in pending)
                committed[pageNumber] = image;
            pending.Clear();
        }

        _walChains[path] = chain;
        return (plaintext, committed);
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

    private static byte[] ReadFileRegion(IFile file, long position, int length, string path)
    {
        var bytes = new byte[length];
        if (length != 0)
            ReadExact(file, position, bytes, path);
        return bytes;
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
