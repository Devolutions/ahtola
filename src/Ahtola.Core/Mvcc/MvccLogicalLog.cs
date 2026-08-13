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
    private const byte LogVersion = 3;
    private const int LogHeaderSize = 56;
    private const int LogHeaderSaltStart = 8;
    private const int LogHeaderCrcStart = 52;
    private const uint FrameMagic = 0x5854564D; // "MVTX"
    private const uint EndMagic = 0x4554564D; // "MVTE"
    private const int TxHeaderSize = 24; // magic(4)+payload(8)+op_count(4)+commit_ts(8)
    private const int TxTrailerSize = 8; // crc(4)+end_magic(4)
    private const byte OpUpsertTable = 0;
    private const byte OpDeleteTable = 1;

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly object _gate = new();
    private IFile? _file;
    private long _offset;
    private ulong _salt;
    private bool _disposed;

    private MvccLogicalLog(IFileSystem fileSystem, string path, IFile file, long offset, ulong salt)
    {
        _fileSystem = fileSystem;
        _path = path;
        _file = file;
        _offset = offset;
        _salt = salt;
    }

    internal string Path => _path;

    internal long Offset
    {
        get { lock (_gate) return _offset; }
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
                var salt = ValidateHeader(header);
                return new MvccLogicalLog(fileSystem, path, existing, existing.Length, salt);
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
            WriteHeader(header, salt);
            file.Write(0, header);
            file.FlushToDisk();
            return new MvccLogicalLog(fileSystem, path, file, LogHeaderSize, salt);
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

            var payload = EncodeOps(ops);
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

            file.Write(_offset, frame);
            file.FlushToDisk();
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

                var ops = DecodeOps(frame.AsSpan(TxHeaderSize, (int)payloadSize), (int)opCount);
                store.ApplyRecoveredCommit(commitTs, ops);
                position += frameLen;
            }

            _offset = Math.Max(_offset, position);
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
            WriteHeader(header, _salt);
            file.SetLength(0);
            file.Write(0, header);
            file.FlushToDisk();
            _offset = LogHeaderSize;
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

    private static void WriteHeader(Span<byte> header, ulong salt)
    {
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, LogMagic);
        header[4] = LogVersion;
        header[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)LogHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[LogHeaderSaltStart..], salt);
        var crc = Crc32C.Compute(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[LogHeaderCrcStart..], crc);
    }

    private static ulong ValidateHeader(ReadOnlySpan<byte> header)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != LogMagic)
            throw new InvalidDataException("Invalid MVCC logical log magic.");
        var version = header[4];
        if (version is not (2 or 3))
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

        return BinaryPrimitives.ReadUInt64LittleEndian(header[LogHeaderSaltStart..]);
    }

    private static byte[] EncodeOps(IReadOnlyList<MvccLogOp> ops)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        foreach (var op in ops)
        {
            writer.Write(op.IsDelete ? OpDeleteTable : OpUpsertTable);
            writer.Write(op.RowId.TableId);
            writer.Write(op.RowId.RowId);
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

    private static List<MvccLogOp> DecodeOps(ReadOnlySpan<byte> payload, int opCount)
    {
        var ops = new List<MvccLogOp>(opCount);
        var offset = 0;
        for (var i = 0; i < opCount; i++)
        {
            if (offset >= payload.Length)
                throw new InvalidDataException("MVCC log op truncated.");
            var kind = payload[offset++];
            if (offset + 16 > payload.Length)
                throw new InvalidDataException("MVCC log op row id truncated.");
            var tableId = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
            offset += 8;
            var rowId = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
            offset += 8;
            if (offset + 4 > payload.Length)
                throw new InvalidDataException("MVCC log op cell count truncated.");
            var cellCount = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
            offset += 4;
            if (kind == OpDeleteTable)
            {
                ops.Add(MvccLogOp.Delete(new MvccRowId(tableId, rowId)));
                continue;
            }

            var cells = new SqlValue[cellCount];
            for (var c = 0; c < cellCount; c++)
                cells[c] = ReadCell(payload, ref offset);
            ops.Add(MvccLogOp.Upsert(new MvccRowId(tableId, rowId), cells));
        }

        return ops;
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

/// <summary>One recovered or to-be-logged MVCC operation.</summary>
internal readonly struct MvccLogOp
{
    private MvccLogOp(MvccRowId rowId, SqlValue[]? cells, bool isDelete)
    {
        RowId = rowId;
        Cells = cells;
        IsDelete = isDelete;
    }

    internal MvccRowId RowId { get; }
    internal SqlValue[]? Cells { get; }
    internal bool IsDelete { get; }

    internal static MvccLogOp Upsert(MvccRowId rowId, SqlValue[] cells)
        => new(rowId, cells, isDelete: false);

    internal static MvccLogOp Delete(MvccRowId rowId)
        => new(rowId, cells: null, isDelete: true);
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
