using System.Buffers.Binary;
using System.Security.Cryptography;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Mvcc;

/// <summary>
/// Durable MVCC logical log (Turso <c>db-log</c> framing constants).
/// Stores commit frames with upsert/delete ops so an <see cref="MvStore"/>
/// can recover after reopen. Encrypted stores use Turso's authenticated,
/// chunked logical-log payload layout while preserving visible recovery framing.
/// </summary>
internal sealed class MvccLogicalLog : IDisposable
{
    // Turso logical_log.rs constants.
    private const byte LegacyLogVersion = 3;
    private const byte CurrentLogVersion = 4;
    private const int LogHeaderSize = MvccLogicalLogFormat.LogHeaderSize;
    private const int TxHeaderSize = MvccLogicalLogFormat.TxHeaderSize;
    private const int TxTrailerSize = MvccLogicalLogFormat.TxTrailerSize;
    private const byte OpUpsertTable = 0;
    private const byte OpDeleteTable = 1;
    private const byte OpBaseTombstone = 0x80;

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly LogicalLogEncryption? _encryption;
    private IFile? _file;
    private long _offset;
    private ulong _salt;
    private byte _version;
    private bool _disposed;

    private MvccLogicalLog(
        IFileSystem fileSystem,
        string path,
        IFile file,
        long offset,
        ulong salt,
        byte version,
        LogicalLogEncryption? encryption)
    {
        _fileSystem = fileSystem;
        _path = path;
        _file = file;
        _offset = offset;
        _salt = salt;
        _version = version;
        _encryption = encryption;
    }

    internal string Path => _path;

    internal long Offset
    {
        get { lock (_gate) return _offset; }
    }

    /// <summary>
    /// Whether this log needs the exclusive materializing checkpoint that upgrades
    /// legacy rowid-only frames before typed keys can be appended.
    /// </summary>
    internal bool RequiresVersion4Upgrade
    {
        get { lock (_gate) return _version < CurrentLogVersion; }
    }

    /// <summary>Bytes past the log header (approximate "frames" size for checkpoint stats).</summary>
    internal long ApproximatePayloadBytes
    {
        get
        {
            lock (_gate)
                return Math.Max(0L, _offset - LogHeaderSize);
        }
    }

    internal static string LogPathForDatabase(string databasePath)
    {
        // Turso: db_path.with_extension("db-log") → "file.db-log" for "file.db"
        return databasePath + "-log";
    }

    internal static MvccLogicalLog CreateOrOpen(
        IFileSystem fileSystem,
        string databasePath,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        synchronousMode.Validate(nameof(synchronousMode));
        var encryption = CreateEncryption(fileSystem);
        var path = LogPathForDatabase(databasePath);
        try
        {
            if (fileSystem.FileExists(path))
            {
                var existing = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: false);
                try
                {
                    if (existing.Length < LogHeaderSize)
                    {
                        existing.Dispose();
                        fileSystem.DeleteFile(path);
                        return CreateNew(fileSystem, path, encryption, synchronousMode);
                    }

                    Span<byte> header = stackalloc byte[LogHeaderSize];
                    ReadExact(existing, 0, header);
                    try
                    {
                        var (salt, version) = MvccLogicalLogFormat.ValidateHeader(header);
                        return new MvccLogicalLog(
                            fileSystem,
                            path,
                            existing,
                            existing.Length,
                            salt,
                            version,
                            encryption);
                    }
                    catch (InvalidDataException) when (existing.Length == LogHeaderSize)
                    {
                        // A header-only log has no committed frame to lose. This is
                        // the recoverable interruption point of a non-atomic V3→V4
                        // header rewrite, so recreate it instead of making a valid
                        // catalog permanently unopenable.
                        existing.Dispose();
                        fileSystem.DeleteFile(path);
                        return CreateNew(fileSystem, path, encryption, synchronousMode);
                    }
                }
                catch
                {
                    existing.Dispose();
                    throw;
                }
            }

            return CreateNew(fileSystem, path, encryption, synchronousMode);
        }
        catch
        {
            encryption?.Dispose();
            throw;
        }
    }

    private static MvccLogicalLog CreateNew(
        IFileSystem fileSystem,
        string path,
        LogicalLogEncryption? encryption,
        SqliteSynchronousMode synchronousMode)
    {
        var file = fileSystem.OpenFile(path, FileOpenMode.CreateNew, readOnly: false);
        try
        {
            var salt = CreateSalt();
            Span<byte> header = stackalloc byte[LogHeaderSize];
            WriteHeader(header, salt, CurrentLogVersion);
            file.Write(0, header);
            if (synchronousMode.SyncsCheckpoint())
                file.FlushToDisk();
            return new MvccLogicalLog(
                fileSystem,
                path,
                file,
                LogHeaderSize,
                salt,
                CurrentLogVersion,
                encryption);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Appends one committed transaction frame and applies the requested barrier.</summary>
    internal void AppendCommit(
        ulong commitTs,
        IReadOnlyList<MvccLogOp> ops,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        ArgumentNullException.ThrowIfNull(ops);
        synchronousMode.Validate(nameof(synchronousMode));
        lock (_gate)
        {
            ThrowIfDisposed();
            var file = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));

            if (RequiresVersion4(ops) && _version < CurrentLogVersion)
            {
                throw new MvccLogicalLogUpgradeRequiredException(
                    "MVCC typed keys require an exclusive checkpoint before upgrading the logical log to version 4.");
            }

            var plaintextPayload = EncodeOps(ops, _version);
            var payload = _encryption is null
                ? plaintextPayload
                : _encryption.EncryptPayload(
                    plaintextPayload,
                    _salt,
                    checked((uint)ops.Count),
                    commitTs,
                    _version);
            var frameSize = checked(TxHeaderSize + payload.Length + TxTrailerSize);
            var frame = new byte[frameSize];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), MvccLogicalLogFormat.FrameMagic);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(4, 8), checked((ulong)plaintextPayload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12, 4), (uint)ops.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(16, 8), commitTs);
            payload.CopyTo(frame.AsSpan(TxHeaderSize));
            var crc = Crc32C.Compute(frame.AsSpan(0, TxHeaderSize + payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(TxHeaderSize + payload.Length, 4),
                crc);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(TxHeaderSize + payload.Length + 4, 4),
                MvccLogicalLogFormat.EndMagic);

            try
            {
                file.Write(_offset, frame);
                if (synchronousMode.SyncsWalCommit())
                    file.FlushToDisk();
            }
            catch (Exception exception)
            {
                // The frame may have reached durable storage even when the
                // caller receives an I/O failure. The commit path must not
                // silently abort it in memory and disagree with recovery.
                throw new MvccLogicalLogCommitIndeterminateException(exception);
            }
            _offset += frame.Length;
        }
    }

    /// <summary>
    /// Appends a durable inclusive checkpoint watermark. A zero-operation frame
    /// is reserved for this purpose; recovery skips transaction frames at or
    /// below the greatest validated watermark, mirroring Turso's
    /// <c>persistent_tx_ts_max</c> replay floor.
    /// </summary>
    internal void AppendCheckpointWatermark(
        ulong durableTimestamp,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
        => AppendCommit(durableTimestamp, [], synchronousMode);

    /// <summary>Replay all frames into <paramref name="store"/> (fresh store expected).</summary>
    internal void ReplayInto(MvStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_gate)
        {
            ThrowIfDisposed();
            var file = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));
            if (file.Length <= LogHeaderSize)
                return;

            // First validate the complete prefix and discover the greatest
            // checkpoint watermark. Applying during this pass would replay
            // already-materialized frames before a later marker is encountered.
            long position = LogHeaderSize;
            ulong durableTimestamp = 0;
            while (position + TxHeaderSize + TxTrailerSize <= file.Length)
            {
                if (!TryReadValidatedFrame(file, position, out var validated))
                    break;
                if (validated.OpCount == 0)
                    durableTimestamp = Math.Max(durableTimestamp, validated.CommitTimestamp);
                position += validated.Length;
            }

            // A short final frame is a torn append, not a valid durability
            // boundary. Retain the validated prefix and physically remove the
            // tail before accepting another commit; otherwise every later reopen
            // would stop at the same bytes and lose valid frames appended after it.
            if (position != file.Length)
            {
                file.SetLength(position);
                file.FlushToDisk();
            }

            var validatedEnd = position;
            if (durableTimestamp != 0)
                store.ApplyRecoveredWatermark(durableTimestamp);
            position = LogHeaderSize;
            while (position < validatedEnd)
            {
                if (!TryReadValidatedFrame(file, position, out var validated))
                {
                    throw new InvalidDataException(
                        "MVCC logical-log validated prefix changed during recovery.");
                }
                if (validated.OpCount != 0
                    && validated.CommitTimestamp > durableTimestamp)
                {
                    store.ApplyRecoveredCommit(validated.CommitTimestamp, validated.Operations);
                }
                position += validated.Length;
            }

            _offset = validatedEnd;
        }
    }

    /// <summary>Truncate log after checkpoint (keep header only).</summary>
    internal void TruncateAfterCheckpoint(
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        synchronousMode.Validate(nameof(synchronousMode));
        lock (_gate)
        {
            ThrowIfDisposed();
            var file = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));
            var freshSalt = CreateSalt();
            Span<byte> header = stackalloc byte[LogHeaderSize];
            WriteHeader(header, freshSalt, _version);
            file.SetLength(0);
            file.Write(0, header);
            if (synchronousMode.SyncsCheckpoint())
                file.FlushToDisk();
            _salt = freshSalt;
            _offset = LogHeaderSize;
        }
    }

    /// <summary>
    /// Rewrites a legacy header only after its frames were materialized into the
    /// SQLite catalog. This is intentionally forbidden while a frame remains,
    /// avoiding a mixed V3/V4 log after interruption.
    /// </summary>
    internal void UpgradeToVersion4AfterCheckpoint(
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        synchronousMode.Validate(nameof(synchronousMode));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_version >= CurrentLogVersion)
                return;
            if (_offset != LogHeaderSize)
            {
                throw new InvalidOperationException(
                    "The MVCC logical log must be checkpointed to a header-only state before its version is upgraded.");
            }

            Span<byte> header = stackalloc byte[LogHeaderSize];
            WriteHeader(header, _salt, CurrentLogVersion);
            if (_fileSystem is IAtomicFileSystem atomicFileSystem)
            {
                var temporaryPath = _path + ".v4-upgrade";
                if (_fileSystem.FileExists(temporaryPath))
                    _fileSystem.DeleteFile(temporaryPath);
                using (var replacement = _fileSystem.OpenFile(
                           temporaryPath,
                           FileOpenMode.CreateNew,
                           readOnly: false))
                {
                    replacement.Write(0, header);
                    if (synchronousMode.SyncsCheckpoint())
                        replacement.FlushToDisk();
                }

                var current = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));
                // The catalog checkpoint already made every V3 frame durable in
                // SQLite pages. Clearing the destination first gives every
                // IAtomicFileSystem the same safe replacement contract: if the
                // process stops here, reopen recreates a harmless empty V4 log.
                current.SetLength(0);
                if (synchronousMode.SyncsCheckpoint())
                    current.FlushToDisk();
                current.Dispose();
                _file = null;
                try
                {
                    atomicFileSystem.ReplaceFileAtomically(
                        temporaryPath,
                        _path,
                        replaceEmptyDestination: true);
                    _file = _fileSystem.OpenFile(_path, FileOpenMode.OpenExisting, readOnly: false);
                }
                catch
                {
                    if (_fileSystem.FileExists(_path))
                        _file = _fileSystem.OpenFile(_path, FileOpenMode.OpenExisting, readOnly: false);
                    throw;
                }
            }
            else
            {
                // The fallback first makes the old header disappear durably.
                // CreateOrOpen treats a short or invalid header-only file as an
                // empty log, so an interruption here cannot strand catalog data.
                var file = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));
                file.SetLength(0);
                if (synchronousMode.SyncsCheckpoint())
                    file.FlushToDisk();
                file.Write(0, header);
                if (synchronousMode.SyncsCheckpoint())
                    file.FlushToDisk();
            }

            _version = CurrentLogVersion;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _file?.Dispose();
            _file = null;
            _encryption?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void WriteHeader(Span<byte> header, ulong salt, byte version)
    {
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, MvccLogicalLogFormat.LogMagic);
        header[4] = version;
        header[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)LogHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[MvccLogicalLogFormat.LogHeaderSaltStart..], salt);
        var crc = Crc32C.Compute(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[MvccLogicalLogFormat.LogHeaderCrcStart..], crc);
    }

    private static LogicalLogEncryption? CreateEncryption(IFileSystem fileSystem)
    {
        var options = GetEncryptionOptions(fileSystem);
        return options is null ? null : new LogicalLogEncryption(options);
    }

    /// <summary>
    /// Fails closed when <paramref name="fileSystem"/> cannot host an MVCC
    /// logical log, before the caller persists journal-mode header 255 or writes
    /// a single <c>MVTX</c> frame.
    /// </summary>
    /// <remarks>
    /// The chunk frame reserves a fixed 16-byte tag plus 12-byte nonce, so only
    /// the AES-GCM ciphers fit; Turso format version 0 defines no logical-log
    /// framing for the wider AEGIS nonces. Both the core encryption file system
    /// and out-of-band backends such as the browser mirror are checked here so a
    /// database is never left half-switched into a mode it cannot commit in.
    /// </remarks>
    internal static void ThrowIfMvccUnsupported(IFileSystem fileSystem)
    {
        var reason = DescribeMvccUnsupportedReason(fileSystem);
        if (reason is not null)
            throw new NotSupportedException(reason);
    }

    /// <summary>
    /// The reason <paramref name="fileSystem"/> cannot host an MVCC logical log,
    /// or <see langword="null"/> when it can.
    /// </summary>
    internal static string? DescribeMvccUnsupportedReason(IFileSystem fileSystem)
    {
        switch (fileSystem)
        {
            case IMvccJournalModePolicy policy when policy.DescribeMvccUnsupportedReason() is { } reason:
                return reason;
            case AhtolaEncryptionFileSystem encrypted:
                return DescribeMvccUnsupportedCipher(encrypted.Encryption.Cipher);
            case IFileSystemDecorator decorator:
                return DescribeMvccUnsupportedReason(decorator.InnerFileSystem);
            default:
                return null;
        }
    }

    /// <summary>
    /// The fail-closed reason for <paramref name="cipher"/>, or
    /// <see langword="null"/> when its nonce and tag match the frame reservation.
    /// </summary>
    internal static string? DescribeMvccUnsupportedCipher(AhtolaEncryptionCipher cipher)
    {
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        if (parameters.NonceSize == MvccLogicalLogFormat.EncryptionNonceSize
            && parameters.TagSize == MvccLogicalLogFormat.EncryptionTagSize)
        {
            return null;
        }

        return $"The MVCC logical log frames every chunk with a {MvccLogicalLogFormat.EncryptionTagSize}-byte tag "
               + $"and a {MvccLogicalLogFormat.EncryptionNonceSize}-byte nonce, so it supports only the AES-GCM "
               + $"ciphers; '{cipher}' uses a {parameters.NonceSize}-byte nonce and Turso format version 0 "
               + "defines no logical-log framing for it.";
    }

    private static AhtolaEncryptionOptions? GetEncryptionOptions(IFileSystem fileSystem)
        => fileSystem switch
        {
            AhtolaEncryptionFileSystem encrypted => encrypted.Encryption,
            IFileSystemDecorator decorator => GetEncryptionOptions(decorator.InnerFileSystem),
            _ => null,
        };

    private static ulong CreateSalt()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private bool TryReadValidatedFrame(
        IFile file,
        long position,
        out ValidatedLogicalFrame validated)
    {
        Span<byte> header = stackalloc byte[TxHeaderSize];
        ReadExact(file, position, header);
        int payloadSize;
        uint opCount;
        ulong commitTs;
        try
        {
            (payloadSize, opCount, commitTs) = MvccLogicalLogFormat.ReadFrameHeader(header);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"Invalid MVCC log frame at offset {position}: {exception.Message}",
                exception);
        }

        var storedPayloadSize = _encryption is null
            ? payloadSize
            : MvccLogicalLogFormat.GetEncryptedPayloadSize(payloadSize);
        var frameLength = checked(TxHeaderSize + storedPayloadSize + TxTrailerSize);
        if (position + frameLength > file.Length)
        {
            if (_encryption is not null
                && IsCompletePlaintextFrame(file, position, payloadSize))
            {
                throw new InvalidDataException(
                    "Encrypted MVCC storage contains a plaintext logical-log frame. "
                    + "Automatic migration is not safe; checkpoint the log without encryption first.");
            }
            if (ContainsCompleteStoredFrame(file, position))
            {
                throw new InvalidDataException(
                    "MVCC logical-log payload length does not match its complete frame boundary. "
                    + "The authenticated frame metadata was tampered with.");
            }

            validated = default;
            return false;
        }

        var frame = new byte[frameLength];
        ReadExact(file, position, frame);
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            frame.AsSpan(TxHeaderSize + storedPayloadSize, 4));
        var end = BinaryPrimitives.ReadUInt32LittleEndian(
            frame.AsSpan(TxHeaderSize + storedPayloadSize + 4, 4));
        if (end != MvccLogicalLogFormat.EndMagic)
            throw new InvalidDataException("MVCC log frame end magic mismatch.");
        var actualCrc = Crc32C.Compute(frame.AsSpan(0, TxHeaderSize + storedPayloadSize));
        if (actualCrc != expectedCrc)
            throw new InvalidDataException("MVCC log frame CRC mismatch.");

        var payload = frame.AsSpan(TxHeaderSize, storedPayloadSize);
        var plaintext = _encryption is null
            ? payload.ToArray()
            : _encryption.DecryptPayload(
                payload,
                payloadSize,
                _salt,
                opCount,
                commitTs,
                _version);
        var operations = DecodeOps(plaintext, checked((int)opCount), _version);
        validated = new ValidatedLogicalFrame(
            frameLength,
            opCount,
            commitTs,
            operations);
        return true;
    }

    private static bool IsCompletePlaintextFrame(IFile file, long position, int payloadSize)
    {
        var frameLength = checked(TxHeaderSize + payloadSize + TxTrailerSize);
        if (position + frameLength > file.Length)
            return false;

        var frame = new byte[frameLength];
        ReadExact(file, position, frame);
        var trailerOffset = TxHeaderSize + payloadSize;
        return BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(trailerOffset + 4))
                   == MvccLogicalLogFormat.EndMagic
               && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(trailerOffset))
                   == Crc32C.Compute(frame.AsSpan(0, trailerOffset));
    }

    private static bool ContainsCompleteStoredFrame(IFile file, long position)
    {
        var remaining = file.Length - position;
        if (remaining <= 0)
            return false;
        if (remaining > int.MaxValue)
        {
            throw new InvalidDataException(
                "MVCC logical-log tail is too large to prove that an oversized payload length is a torn append.");
        }

        var bytes = new byte[(int)remaining];
        ReadExact(file, position, bytes);
        return MvccLogicalLogFormat.ContainsCompleteFrameBoundary(bytes);
    }

    private static bool RequiresVersion4(IReadOnlyList<MvccLogOp> ops)
        => ops.Any(static op => !op.RowId.Key.IsInteger);

    private readonly record struct ValidatedLogicalFrame(
        int Length,
        uint OpCount,
        ulong CommitTimestamp,
        IReadOnlyList<MvccLogOp> Operations);

    private static byte[] EncodeOps(IReadOnlyList<MvccLogOp> ops, byte version)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        foreach (var op in ops)
        {
            writer.Write((byte)(
                (op.IsDelete ? OpDeleteTable : OpUpsertTable)
                | (op.IsBaseTombstone ? OpBaseTombstone : 0)));
            writer.Write(op.RowId.TableId);
            if (version >= CurrentLogVersion)
            {
                WriteObjectName(writer, op.ObjectName);
                writer.Write((byte)op.RowId.Key.Kind);
                if (op.RowId.Key.IsInteger)
                {
                    writer.Write(op.RowId.Key.Integer);
                }
                else
                {
                    var key = op.RowId.Key.Record;
                    writer.Write(key.Length);
                    writer.Write(key.Span);
                }
            }
            else
            {
                writer.Write(op.RowId.RowId);
            }
            if (op.IsDelete)
            {
                writer.Write(0);
                continue;
            }

            var cells = op.Cells ?? [];
            writer.Write(cells.Length);
            foreach (var cell in cells)
                WriteCell(writer, cell);
        }

        return buffer.ToArray();
    }

    private static List<MvccLogOp> DecodeOps(ReadOnlySpan<byte> payload, int opCount, byte version)
    {
        var ops = new List<MvccLogOp>(opCount);
        var offset = 0;
        for (var i = 0; i < opCount; i++)
        {
            if (offset >= payload.Length)
                throw new InvalidDataException("MVCC log op truncated.");
            var encodedKind = payload[offset++];
            var isBaseTombstone = version >= CurrentLogVersion
                && (encodedKind & OpBaseTombstone) != 0;
            var kind = (byte)(encodedKind & ~OpBaseTombstone);
            if (isBaseTombstone && kind != OpDeleteTable)
                throw new InvalidDataException("MVCC log base-tombstone flag requires a delete operation.");
            if (offset + sizeof(long) > payload.Length)
                throw new InvalidDataException("MVCC log op row id truncated.");
            var tableId = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
            offset += 8;
            string? objectName = null;
            if (version >= CurrentLogVersion)
                objectName = ReadObjectName(payload, ref offset);
            MvccKey key;
            if (version >= CurrentLogVersion)
            {
                if (offset >= payload.Length)
                    throw new InvalidDataException("MVCC log key kind truncated.");
                var keyKind = (MvccKeyKind)payload[offset++];
                key = keyKind switch
                {
                    MvccKeyKind.Integer => ReadIntegerKey(payload, ref offset),
                    MvccKeyKind.Record => ReadRecordKey(payload, ref offset),
                    _ => throw new InvalidDataException($"Unknown MVCC log key kind {(byte)keyKind}."),
                };
            }
            else
            {
                if (offset + sizeof(long) > payload.Length)
                    throw new InvalidDataException("MVCC log integer row id truncated.");
                key = MvccKey.FromInteger(BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]));
                offset += sizeof(long);
            }
            if (offset + 4 > payload.Length)
                throw new InvalidDataException("MVCC log op cell count truncated.");
            var cellCount = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
            offset += 4;
            if (kind == OpDeleteTable)
            {
                ops.Add(isBaseTombstone
                    ? MvccLogOp.BaseTombstone(new MvccRowId(tableId, key), objectName)
                    : MvccLogOp.Delete(new MvccRowId(tableId, key), objectName));
                continue;
            }

            var cells = new SqlValue[cellCount];
            for (var c = 0; c < cellCount; c++)
                cells[c] = ReadCell(payload, ref offset);
            ops.Add(MvccLogOp.Upsert(new MvccRowId(tableId, key), cells, objectName));
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException(
                $"MVCC log payload has {payload.Length - offset} trailing byte(s) after {opCount} operation(s).");
        }

        return ops;
    }

    private static MvccKey ReadIntegerKey(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + sizeof(long) > payload.Length)
            throw new InvalidDataException("MVCC log integer key truncated.");
        var value = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += sizeof(long);
        return MvccKey.FromInteger(value);
    }

    private static MvccKey ReadRecordKey(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + sizeof(int) > payload.Length)
            throw new InvalidDataException("MVCC log record-key length truncated.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += sizeof(int);
        if (length < 0 || offset + length > payload.Length)
            throw new InvalidDataException("MVCC log record key truncated.");
        var key = MvccKey.FromRecord(payload.Slice(offset, length));
        offset += length;
        return key;
    }

    private static void WriteObjectName(BinaryWriter writer, string? objectName)
    {
        if (objectName is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(objectName);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadObjectName(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + sizeof(int) > payload.Length)
            throw new InvalidDataException("MVCC log object-name length truncated.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += sizeof(int);
        if (length == -1)
            return null;
        if (length < 0 || offset + length > payload.Length)
            throw new InvalidDataException("MVCC log object name truncated.");
        var name = System.Text.Encoding.UTF8.GetString(payload.Slice(offset, length));
        offset += length;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("MVCC log object name is empty.");
        return name;
    }

    private static void WriteCell(BinaryWriter writer, SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                writer.Write((byte)0);
                break;
            case SqlValueKind.Integer:
                writer.Write((byte)1);
                writer.Write(value.AsInteger());
                break;
            case SqlValueKind.Real:
                writer.Write((byte)2);
                writer.Write(value.AsReal());
                break;
            case SqlValueKind.Text:
                writer.Write((byte)3);
                var textBytes = System.Text.Encoding.UTF8.GetBytes(value.AsText());
                writer.Write(textBytes.Length);
                writer.Write(textBytes);
                break;
            case SqlValueKind.Blob:
                writer.Write((byte)4);
                var blob = value.AsBlob().ToArray();
                writer.Write(blob.Length);
                writer.Write(blob);
                break;
            default:
                writer.Write((byte)0);
                break;
        }

    }

    private static SqlValue ReadCell(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset >= payload.Length)
            throw new InvalidDataException("MVCC log cell truncated.");
        var type = payload[offset++];
        return type switch
        {
            0 => SqlValue.Null,
            1 => ReadInteger(payload, ref offset),
            2 => ReadReal(payload, ref offset),
            3 => ReadText(payload, ref offset),
            4 => ReadBlob(payload, ref offset),
            _ => throw new InvalidDataException($"Unknown MVCC log cell type {type}."),
        };
    }

    private static SqlValue ReadInteger(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + 8 > payload.Length)
            throw new InvalidDataException("MVCC log integer truncated.");
        var value = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        return SqlValue.Integer(value);
    }

    private static SqlValue ReadReal(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + 8 > payload.Length)
            throw new InvalidDataException("MVCC log real truncated.");
        var value = BinaryPrimitives.ReadDoubleLittleEndian(payload[offset..]);
        offset += 8;
        return SqlValue.Real(value);
    }

    private static SqlValue ReadText(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + 4 > payload.Length)
            throw new InvalidDataException("MVCC log text length truncated.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;
        if (length < 0 || offset + length > payload.Length)
            throw new InvalidDataException("MVCC log text truncated.");
        var text = System.Text.Encoding.UTF8.GetString(payload.Slice(offset, length));
        offset += length;
        return SqlValue.Text(text);
    }

    private static SqlValue ReadBlob(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + 4 > payload.Length)
            throw new InvalidDataException("MVCC log blob length truncated.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;
        if (length < 0 || offset + length > payload.Length)
            throw new InvalidDataException("MVCC log blob truncated.");
        var blob = payload.Slice(offset, length).ToArray();
        offset += length;
        return SqlValue.Blob(blob);
    }

    private static void ReadExact(IFile file, long position, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = file.Read(position + total, destination[total..]);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected EOF in MVCC logical log.");
            total += read;
        }
    }

    /// <summary>
    /// Chunk-level encryption for the MVCC logical log.
    /// </summary>
    /// <remarks>
    /// The <c>MVTX</c> chunk frame reserves a fixed 16-byte tag plus 12-byte
    /// nonce, and that overhead is baked into every payload-size and CRC
    /// computation in <see cref="MvccLogicalLogFormat"/>. Turso defines no
    /// logical-log framing for the wider AEGIS nonces, so rather than invent one,
    /// ciphers whose nonce is not 12 bytes are refused up front.
    /// </remarks>
    private sealed class LogicalLogEncryption : IDisposable
    {
        private readonly AhtolaEncryptionOptions _options;
        private readonly Storage.Crypto.IAhtolaAead _aead;

        internal LogicalLogEncryption(AhtolaEncryptionOptions options)
        {
            _options = options.CreateOwnedCopy();
            try
            {
                _aead = _options.CreateAead();
                if (_aead.NonceSize != MvccLogicalLogFormat.EncryptionNonceSize
                    || _aead.TagSize != MvccLogicalLogFormat.EncryptionTagSize)
                {
                    _aead.Dispose();
                    throw new NotSupportedException(
                        DescribeMvccUnsupportedCipher(options.Cipher)
                        ?? $"'{options.Cipher}' cannot frame an MVCC logical-log chunk.");
                }
            }
            catch
            {
                _options.Dispose();
                throw;
            }
        }

        internal byte[] EncryptPayload(
            ReadOnlySpan<byte> plaintext,
            ulong salt,
            uint opCount,
            ulong commitTs,
            byte logVersion)
        {
            var encrypted = new byte[MvccLogicalLogFormat.GetEncryptedPayloadSize(plaintext.Length)];
            var chunkCount = MvccLogicalLogFormat.GetEncryptedChunkCount(plaintext.Length);
            var plaintextOffset = 0;
            var encryptedOffset = 0;
            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var plaintextLength = MvccLogicalLogFormat.GetPlaintextChunkLength(
                    plaintext.Length,
                    chunkIndex);
                var ciphertext = encrypted.AsSpan(encryptedOffset, plaintextLength);
                var tag = encrypted.AsSpan(
                    encryptedOffset + plaintextLength,
                    MvccLogicalLogFormat.EncryptionTagSize);
                var nonce = encrypted.AsSpan(
                    encryptedOffset + plaintextLength + MvccLogicalLogFormat.EncryptionTagSize,
                    MvccLogicalLogFormat.EncryptionNonceSize);
                RandomNumberGenerator.Fill(nonce);
                var associatedData = MvccLogicalLogFormat.BuildEncryptedChunkAssociatedData(
                    salt,
                    plaintext.Length,
                    opCount,
                    commitTs,
                    chunkIndex,
                    logVersion);
                _aead.Encrypt(
                    nonce,
                    plaintext.Slice(plaintextOffset, plaintextLength),
                    ciphertext,
                    tag,
                    associatedData);
                plaintextOffset += plaintextLength;
                encryptedOffset += plaintextLength + MvccLogicalLogFormat.EncryptionOverhead;
            }

            return encrypted;
        }

        internal byte[] DecryptPayload(
            ReadOnlySpan<byte> encrypted,
            int plaintextSize,
            ulong salt,
            uint opCount,
            ulong commitTs,
            byte logVersion)
        {
            if (encrypted.Length != MvccLogicalLogFormat.GetEncryptedPayloadSize(plaintextSize))
                throw new InvalidDataException("MVCC encrypted payload size does not match its frame header.");

            var plaintext = new byte[plaintextSize];
            var chunkCount = MvccLogicalLogFormat.GetEncryptedChunkCount(plaintextSize);
            var plaintextOffset = 0;
            var encryptedOffset = 0;
            try
            {
                for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    var plaintextLength = MvccLogicalLogFormat.GetPlaintextChunkLength(
                        plaintextSize,
                        chunkIndex);
                    var ciphertext = encrypted.Slice(encryptedOffset, plaintextLength);
                    var tag = encrypted.Slice(
                        encryptedOffset + plaintextLength,
                        MvccLogicalLogFormat.EncryptionTagSize);
                    var nonce = encrypted.Slice(
                        encryptedOffset + plaintextLength + MvccLogicalLogFormat.EncryptionTagSize,
                        MvccLogicalLogFormat.EncryptionNonceSize);
                    var associatedData = MvccLogicalLogFormat.BuildEncryptedChunkAssociatedData(
                        salt,
                        plaintextSize,
                        opCount,
                        commitTs,
                        chunkIndex,
                        logVersion);
                    if (!_aead.TryDecrypt(
                            nonce,
                            ciphertext,
                            tag,
                            plaintext.AsSpan(plaintextOffset, plaintextLength),
                            associatedData))
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                        throw new InvalidDataException(
                            "MVCC logical-log payload authentication failed. The key is wrong or the log was tampered with.");
                    }

                    plaintextOffset += plaintextLength;
                    encryptedOffset += plaintextLength + MvccLogicalLogFormat.EncryptionOverhead;
                }

                return plaintext;
            }
            catch (CryptographicException exception)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException(
                    "MVCC logical-log payload authentication failed. The key is wrong or the log was tampered with.",
                    exception);
            }
        }

        public void Dispose()
        {
            _aead.Dispose();
            _options.Dispose();
        }
    }
}

internal sealed class MvccLogicalLogUpgradeRequiredException : EmbeddedSqlException
{
    internal MvccLogicalLogUpgradeRequiredException(string message)
        : base(message)
    {
    }
}

internal sealed class MvccLogicalLogCommitIndeterminateException : EmbeddedSqlException
{
    internal MvccLogicalLogCommitIndeterminateException(Exception innerException)
        : base(
            "The MVCC logical-log commit may have reached durable storage. Dispose and reopen the database before retrying.",
            innerException)
    {
    }
}

/// <summary>One recovered or to-be-logged MVCC operation.</summary>
internal readonly struct MvccLogOp
{
    private MvccLogOp(
        MvccRowId rowId,
        SqlValue[]? cells,
        bool isDelete,
        bool isBaseTombstone,
        string? objectName)
    {
        RowId = rowId;
        Cells = cells;
        IsDelete = isDelete;
        IsBaseTombstone = isBaseTombstone;
        ObjectName = objectName;
    }

    internal MvccRowId RowId { get; }
    internal SqlValue[]? Cells { get; }
    internal bool IsDelete { get; }
    internal bool IsBaseTombstone { get; }
    internal string? ObjectName { get; }

    internal static MvccLogOp Upsert(MvccRowId rowId, SqlValue[] cells, string? objectName = null)
        => new(rowId, cells, isDelete: false, isBaseTombstone: false, objectName);

    internal static MvccLogOp Delete(MvccRowId rowId, string? objectName = null)
        => new(rowId, cells: null, isDelete: true, isBaseTombstone: false, objectName);

    internal static MvccLogOp BaseTombstone(MvccRowId rowId, string? objectName = null)
        => new(rowId, cells: null, isDelete: true, isBaseTombstone: true, objectName);
}

/// <summary>CRC-32C (Castagnoli) used by Turso logical log framing.</summary>
internal static class Crc32C
{
    internal const uint InitialState = 0xFFFFFFFFu;
    private static readonly uint[] Table = CreateTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = InitialState;
        foreach (var b in data)
            crc = Update(crc, b);
        return Complete(crc);
    }

    internal static uint Update(uint crc, byte value)
        => Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

    internal static uint Complete(uint crc) => crc ^ 0xFFFFFFFFu;

    private static uint[] CreateTable()
    {
        const uint poly = 0x82F63B78u; // reflected Castagnoli
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            table[i] = crc;
        }

        return table;
    }
}
