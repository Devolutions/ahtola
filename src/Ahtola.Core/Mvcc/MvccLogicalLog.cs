using System.Buffers.Binary;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Mvcc;

/// <summary>
/// Durable MVCC logical log (Turso <c>db-log</c> framing constants).
/// Phase 2 stores commit frames with upsert/delete ops so an <see cref="MvStore"/>
/// can recover after reopen. Full Turso CRC-chain / encryption parity is iterative.
/// </summary>
internal sealed class MvccLogicalLog : IDisposable
{
    // Turso logical_log.rs constants.
    private const uint LogMagic = 0x4C4D4C32; // "LML2"
    private const byte LegacyLogVersion = 3;
    private const byte CurrentLogVersion = 4;
    private const int LogHeaderSize = 56;
    private const int LogHeaderSaltStart = 8;
    private const int LogHeaderCrcStart = 52;
    private const uint FrameMagic = 0x5854564D; // "MVTX"
    private const uint EndMagic = 0x4554564D; // "MVTE"
    private const int TxHeaderSize = 24; // magic(4)+payload(8)+op_count(4)+commit_ts(8)
    private const int TxTrailerSize = 8; // crc(4)+end_magic(4)
    private const byte OpUpsertTable = 0;
    private const byte OpDeleteTable = 1;
    private const byte OpBaseTombstone = 0x80;

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly object _gate = new();
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
        byte version)
    {
        _fileSystem = fileSystem;
        _path = path;
        _file = file;
        _offset = offset;
        _salt = salt;
        _version = version;
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

    internal static MvccLogicalLog CreateOrOpen(IFileSystem fileSystem, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var path = LogPathForDatabase(databasePath);
        if (fileSystem.FileExists(path))
        {
            var existing = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: false);
            try
            {
                if (existing.Length < LogHeaderSize)
                {
                    existing.Dispose();
                    fileSystem.DeleteFile(path);
                    return CreateNew(fileSystem, path);
                }

                Span<byte> header = stackalloc byte[LogHeaderSize];
                ReadExact(existing, 0, header);
                try
                {
                    var (salt, version) = ValidateHeader(header);
                    return new MvccLogicalLog(fileSystem, path, existing, existing.Length, salt, version);
                }
                catch (InvalidDataException) when (existing.Length == LogHeaderSize)
                {
                    // A header-only log has no committed frame to lose. This is
                    // the recoverable interruption point of a non-atomic V3→V4
                    // header rewrite, so recreate it instead of making a valid
                    // catalog permanently unopenable.
                    existing.Dispose();
                    fileSystem.DeleteFile(path);
                    return CreateNew(fileSystem, path);
                }
            }
            catch
            {
                existing.Dispose();
                throw;
            }
        }

        return CreateNew(fileSystem, path);
    }

    private static MvccLogicalLog CreateNew(IFileSystem fileSystem, string path)
    {
        var file = fileSystem.OpenFile(path, FileOpenMode.CreateNew, readOnly: false);
        try
        {
            var salt = unchecked((ulong)Random.Shared.NextInt64());
            Span<byte> header = stackalloc byte[LogHeaderSize];
            WriteHeader(header, salt, CurrentLogVersion);
            file.Write(0, header);
            file.FlushToDisk();
            return new MvccLogicalLog(
                fileSystem,
                path,
                file,
                LogHeaderSize,
                salt,
                CurrentLogVersion);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Append one committed transaction frame and flush.</summary>
    internal void AppendCommit(ulong commitTs, IReadOnlyList<MvccLogOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        lock (_gate)
        {
            ThrowIfDisposed();
            var file = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));

            if (RequiresVersion4(ops) && _version < CurrentLogVersion)
            {
                throw new MvccLogicalLogUpgradeRequiredException(
                    "MVCC typed keys require an exclusive checkpoint before upgrading the logical log to version 4.");
            }

            var payload = EncodeOps(ops, _version);
            var frameSize = TxHeaderSize + payload.Length + TxTrailerSize;
            var frame = new byte[frameSize];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), FrameMagic);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(4, 8), (ulong)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12, 4), (uint)ops.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(16, 8), commitTs);
            payload.CopyTo(frame.AsSpan(TxHeaderSize));
            var crc = Crc32C.Compute(frame.AsSpan(0, TxHeaderSize + payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(TxHeaderSize + payload.Length, 4),
                crc);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(TxHeaderSize + payload.Length + 4, 4),
                EndMagic);

            try
            {
                file.Write(_offset, frame);
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

            long position = LogHeaderSize;
            Span<byte> header = stackalloc byte[TxHeaderSize];
            while (position + TxHeaderSize + TxTrailerSize <= file.Length)
            {
                ReadExact(file, position, header);
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (magic != FrameMagic)
                    throw new InvalidDataException($"Invalid MVCC log frame magic at offset {position}.");

                var payloadSize = BinaryPrimitives.ReadUInt64LittleEndian(header[4..]);
                var opCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
                var commitTs = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
                if (payloadSize > int.MaxValue)
                    throw new InvalidDataException("MVCC log frame payload too large.");

                var frameLen = TxHeaderSize + (int)payloadSize + TxTrailerSize;
                if (position + frameLen > file.Length)
                    break; // torn tail — stop (fail-closed leave partial unrecovered)

                var frame = new byte[frameLen];
                ReadExact(file, position, frame);
                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                    frame.AsSpan(TxHeaderSize + (int)payloadSize, 4));
                var end = BinaryPrimitives.ReadUInt32LittleEndian(
                    frame.AsSpan(TxHeaderSize + (int)payloadSize + 4, 4));
                if (end != EndMagic)
                    throw new InvalidDataException("MVCC log frame end magic mismatch.");
                var actualCrc = Crc32C.Compute(frame.AsSpan(0, TxHeaderSize + (int)payloadSize));
                if (actualCrc != expectedCrc)
                    throw new InvalidDataException("MVCC log frame CRC mismatch.");

                var ops = DecodeOps(
                    frame.AsSpan(TxHeaderSize, (int)payloadSize),
                    (int)opCount,
                    _version);
                store.ApplyRecoveredCommit(commitTs, ops);
                position += frameLen;
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

            _offset = position;
        }
    }

    /// <summary>Truncate log after checkpoint (keep header only).</summary>
    internal void TruncateAfterCheckpoint()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var file = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));
            Span<byte> header = stackalloc byte[LogHeaderSize];
            WriteHeader(header, _salt, _version);
            file.SetLength(0);
            file.Write(0, header);
            file.FlushToDisk();
            _offset = LogHeaderSize;
        }
    }

    /// <summary>
    /// Rewrites a legacy header only after its frames were materialized into the
    /// SQLite catalog. This is intentionally forbidden while a frame remains,
    /// avoiding a mixed V3/V4 log after interruption.
    /// </summary>
    internal void UpgradeToVersion4AfterCheckpoint()
    {
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
                    replacement.FlushToDisk();
                }

                var current = _file ?? throw new ObjectDisposedException(nameof(MvccLogicalLog));
                // The catalog checkpoint already made every V3 frame durable in
                // SQLite pages. Clearing the destination first gives every
                // IAtomicFileSystem the same safe replacement contract: if the
                // process stops here, reopen recreates a harmless empty V4 log.
                current.SetLength(0);
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
                file.FlushToDisk();
                file.Write(0, header);
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
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void WriteHeader(Span<byte> header, ulong salt, byte version)
    {
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, LogMagic);
        header[4] = version;
        header[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)LogHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[LogHeaderSaltStart..], salt);
        var crc = Crc32C.Compute(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[LogHeaderCrcStart..], crc);
    }

    private static (ulong Salt, byte Version) ValidateHeader(ReadOnlySpan<byte> header)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != LogMagic)
            throw new InvalidDataException("Invalid MVCC logical log magic.");
        var version = header[4];
        if (version is not (2 or LegacyLogVersion or CurrentLogVersion))
            throw new InvalidDataException($"Unsupported MVCC logical log version {version}.");
        var hdrLen = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (hdrLen != LogHeaderSize)
            throw new InvalidDataException("Invalid MVCC logical log header length.");

        Span<byte> crcBuf = stackalloc byte[LogHeaderSize];
        header[..LogHeaderSize].CopyTo(crcBuf);
        crcBuf[LogHeaderCrcStart..].Clear();
        var expected = Crc32C.Compute(crcBuf);
        var actual = BinaryPrimitives.ReadUInt32LittleEndian(header[LogHeaderCrcStart..]);
        if (expected != actual)
            throw new InvalidDataException("MVCC logical log header CRC mismatch.");

        return (BinaryPrimitives.ReadUInt64LittleEndian(header[LogHeaderSaltStart..]), version);
    }

    private static bool RequiresVersion4(IReadOnlyList<MvccLogOp> ops)
        => ops.Any(static op => !op.RowId.Key.IsInteger);

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
    private static readonly uint[] Table = CreateTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

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
