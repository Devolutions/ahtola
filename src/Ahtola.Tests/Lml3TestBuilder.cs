using System.Buffers.Binary;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Hand-rolled constructor for synthetic "lml3" MVCC logical-log byte streams, mirroring the
/// server-side wire format documented in <c>turso-src/core/mvcc/persistent_storage/logical_log.rs</c>
/// and <c>turso-src/sync/engine/src/database_sync_operations.rs</c>. Used only by tests to build
/// exact request/response fixtures for <see cref="ManagedReplicaLml3Decoder"/>.
/// </summary>
internal static class Lml3TestBuilder
{
    public const uint LogMagic = 0x4C4D4C32;
    public const byte LogVersion = 3;
    public const uint FrameMagic = 0x5854564D;
    public const uint ExtFrameMagic = 0x5845564D;
    public const uint EndMagic = 0x4554564D;
    public const ushort PortableChangesExtensionType = 1;

    public static byte[] BuildHeader(ulong salt, byte? versionOverride = null, byte? flagsOverride = null, ushort? hdrLenOverride = null, bool corruptReserved = false)
    {
        var header = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), LogMagic);
        header[4] = versionOverride ?? LogVersion;
        header[5] = flagsOverride ?? 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), hdrLenOverride ?? 56);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), salt);
        if (corruptReserved)
            header[20] = 1;

        // reserved bytes (16..52) already zero
        var crc = Lml3Crc32CForTests.Compute(header.AsSpan(0, 56)); // computed with crc field zeroed
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), crc);
        return header;
    }

    public static uint HeaderSeedCrc(ulong salt)
    {
        Span<byte> saltBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(saltBytes, salt);
        return Lml3Crc32CForTests.Compute(saltBytes);
    }

    /// <summary>Builds one recovery op: tag(1) + flags(1) + table_id(4,LE) + varint(len) + payload [+ varint(extLen) + extension].</summary>
    public static byte[] BuildRecoveryOp(byte tag, byte flags, int tableId, byte[] payload, byte[]? extension = null)
    {
        var result = new List<byte>();
        result.Add(tag);
        result.Add(flags);
        var tableIdBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tableIdBytes, tableId);
        result.AddRange(tableIdBytes);
        AppendSqliteVarint(result, (ulong)payload.Length);
        result.AddRange(payload);
        if (extension is not null)
        {
            AppendSqliteVarint(result, (ulong)extension.Length);
            result.AddRange(extension);
        }

        return result.ToArray();
    }

    public static byte[] SqliteVarintRowId(long rowId) => SqliteVarintOf(unchecked((ulong)rowId));

    public static byte[] SqliteVarintOf(ulong value)
    {
        Span<byte> buffer = stackalloc byte[SqliteVarint.MaximumLength];
        var length = SqliteVarint.Write(value, buffer);
        return buffer[..length].ToArray();
    }

    public static byte[] UpsertTablePayload(long rowId, byte[] record)
    {
        var result = new List<byte>();
        AppendSqliteVarint(result, unchecked((ulong)rowId));
        result.AddRange(record);
        return result.ToArray();
    }

    public static byte[] DeleteTablePayload(long rowId)
    {
        var result = new List<byte>();
        AppendSqliteVarint(result, unchecked((ulong)rowId));
        return result.ToArray();
    }

    public static byte[] DeleteExtension(ulong field, byte[] record)
    {
        var result = new List<byte>();
        AppendProtoVarint(result, (field << 3) | 2);
        AppendProtoVarint(result, (ulong)record.Length);
        result.AddRange(record);
        return result.ToArray();
    }

    public static byte[] SchemaRecord(string type, string name, long rootpage, string sql)
        => SqliteRecordCodec.Encode(
        [
            Core.SqlValue.Text(type),
            Core.SqlValue.Text(name),
            Core.SqlValue.Text(name),
            Core.SqlValue.Integer(rootpage),
            Core.SqlValue.Text(sql),
        ]);

    public static byte[] UpdateHeaderPayload(uint userVersion, uint applicationId)
    {
        var payload = new byte[100];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(60), userVersion);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(68), applicationId);
        return payload;
    }

    /// <summary>Builds one (unwrapped) PortableLogicalTxn protobuf message body.</summary>
    public static byte[] BuildPortableLogicalTxn(
        ulong endOffset,
        ulong commitTs,
        IReadOnlyList<string>? strings = null,
        IReadOnlyList<(long MvTableId, ulong NameRef)>? objectMap = null,
        IReadOnlyList<(ulong KeyRef, ulong ValueRef)>? meta = null)
    {
        var result = new List<byte>();
        AppendProtoTag(result, 1, 0);
        AppendProtoVarint(result, endOffset);
        AppendProtoTag(result, 2, 0);
        AppendProtoVarint(result, commitTs);
        foreach (var s in strings ?? [])
        {
            AppendProtoTag(result, 12, 2);
            var bytes = Encoding.UTF8.GetBytes(s);
            AppendProtoVarint(result, (ulong)bytes.Length);
            result.AddRange(bytes);
        }

        foreach (var (mvTableId, nameRef) in objectMap ?? [])
        {
            var obj = new List<byte>();
            AppendProtoTag(obj, 1, 0);
            AppendProtoVarint(obj, ZigZagEncode(mvTableId));
            AppendProtoTag(obj, 2, 0);
            AppendProtoVarint(obj, nameRef);
            AppendProtoTag(result, 13, 2);
            AppendProtoVarint(result, (ulong)obj.Count);
            result.AddRange(obj);
        }

        foreach (var (keyRef, valueRef) in meta ?? [])
        {
            var m = new List<byte>();
            AppendProtoTag(m, 1, 0);
            AppendProtoVarint(m, keyRef);
            AppendProtoTag(m, 2, 0);
            AppendProtoVarint(m, valueRef);
            AppendProtoTag(result, 14, 2);
            AppendProtoVarint(result, (ulong)m.Count);
            result.AddRange(m);
        }

        return result.ToArray();
    }

    /// <summary>Wraps a message with a protobuf varint length prefix ("delimited" framing).</summary>
    public static byte[] Delimited(byte[] message)
    {
        var result = new List<byte>();
        AppendProtoVarint(result, (ulong)message.Length);
        result.AddRange(message);
        return result.ToArray();
    }

    public static byte[] BuildExtensionRecord(ushort type, byte[] payload, ushort flags = 0)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0), type);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result.AsSpan(8));
        return result;
    }

    /// <summary>
    /// Builds one complete TX frame (header, optional extension block, recovery payload, trailer),
    /// chaining the CRC from <paramref name="runningCrc"/> and returning the new running CRC.
    /// </summary>
    public static byte[] BuildFrame(
        ref uint runningCrc,
        byte[] recoveryPayload,
        int opCount,
        byte[]? extensionBlock = null,
        uint? frameFlagsOverride = null,
        uint? corruptTrailerCrc = null,
        uint? corruptTrailerMagic = null)
    {
        var hasExtension = extensionBlock is not null;
        var headerSize = hasExtension ? 40 : 24;
        var frame = new List<byte>();
        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), hasExtension ? ExtFrameMagic : FrameMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), (ulong)recoveryPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)opCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), 123456789UL); // commit_ts (structural filler only)
        if (hasExtension)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)extensionBlock!.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), 1); // extension_record_count (caller's block must match)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), frameFlagsOverride ?? 1); // HAS_EXTENSION_BLOCK
        }

        frame.AddRange(header);
        if (hasExtension)
            frame.AddRange(extensionBlock!);
        frame.AddRange(recoveryPayload);

        var body = frame.ToArray();
        var crc = corruptTrailerCrc ?? Lml3Crc32CForTests.Append(runningCrc, body);
        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), corruptTrailerMagic ?? EndMagic);

        runningCrc = Lml3Crc32CForTests.Append(runningCrc, body);
        frame.AddRange(trailer);
        return frame.ToArray();
    }

    public static byte[] BuildFrameWithExtensionRecordCount(
        ref uint runningCrc,
        byte[] recoveryPayload,
        int opCount,
        byte[] extensionBlock,
        uint extensionRecordCount,
        uint frameFlags = 1)
    {
        var frame = new List<byte>();
        var header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), ExtFrameMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), (ulong)recoveryPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)opCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), 42UL);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)extensionBlock.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), extensionRecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), frameFlags);
        frame.AddRange(header);
        frame.AddRange(extensionBlock);
        frame.AddRange(recoveryPayload);

        var body = frame.ToArray();
        var crc = Lml3Crc32CForTests.Append(runningCrc, body);
        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), EndMagic);
        runningCrc = crc;
        frame.AddRange(trailer);
        return frame.ToArray();
    }

    private static void AppendSqliteVarint(List<byte> destination, ulong value)
        => destination.AddRange(SqliteVarintOf(value));

    private static void AppendProtoTag(List<byte> destination, ulong field, ulong wireType)
        => AppendProtoVarint(destination, (field << 3) | wireType);

    private static void AppendProtoVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }

        destination.Add((byte)value);
    }

    private static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));
}

/// <summary>Independent CRC32C implementation used only to build test fixtures (kept separate from the production implementation so a bug in one is not masked by the other).</summary>
internal static class Lml3Crc32CForTests
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    public static uint Append(uint previousCrc, ReadOnlySpan<byte> data)
    {
        var crc = previousCrc ^ 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] CreateTable()
    {
        const uint poly = 0x82F63B78u;
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
