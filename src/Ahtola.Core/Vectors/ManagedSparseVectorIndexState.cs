using System.Buffers.Binary;

namespace Ahtola.Core.Vectors;

/// <summary>
/// Versioned declaration envelope for a sparse index. Postings are derived from base rows and
/// deliberately are not a second durable source of truth.
/// </summary>
internal static class ManagedSparseVectorIndexState
{
    private const uint Magic = 0x50534841; // AHSP
    private const ushort Version = 1;
    private const ushort Length = 36;

    public static byte[] Encode(ManagedVectorIndexOptions options)
    {
        var state = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(state, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(6), Length);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(8), options.Dimensions);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(12), (int)options.Encoding);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(16), (int)options.Metric);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(20), options.Exact ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(state.AsSpan(24), options.MinimumRows);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(32), ComputeChecksum(state.AsSpan(0, 32)));
        return state;
    }

    public static void Validate(ReadOnlySpan<byte> state, ManagedVectorIndexOptions options)
    {
        if (state.Length != Length)
            throw Corrupt("length");
        if (BinaryPrimitives.ReadUInt32LittleEndian(state) != Magic)
            throw Corrupt("magic");
        if (BinaryPrimitives.ReadUInt16LittleEndian(state[4..]) != Version)
            throw Corrupt("version");
        if (BinaryPrimitives.ReadUInt16LittleEndian(state[6..]) != Length)
            throw Corrupt("header length");

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(state[32..]);
        if (expectedChecksum != ComputeChecksum(state[..32]))
            throw Corrupt("checksum");
        if (BinaryPrimitives.ReadInt32LittleEndian(state[8..]) != options.Dimensions)
            throw Corrupt("dimensions");
        if (BinaryPrimitives.ReadInt32LittleEndian(state[12..]) != (int)options.Encoding)
            throw Corrupt("encoding");
        if (BinaryPrimitives.ReadInt32LittleEndian(state[16..]) != (int)options.Metric)
            throw Corrupt("metric");
        if (BinaryPrimitives.ReadInt32LittleEndian(state[20..]) != 1)
            throw Corrupt("exact mode");
        if (BinaryPrimitives.ReadInt64LittleEndian(state[24..]) != options.MinimumRows)
            throw Corrupt("minimum row count");
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        var checksum = uint.MaxValue;
        foreach (var value in bytes)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0u : 0xEDB88320u);
        }

        return ~checksum;
    }

    private static EmbeddedSqlException Corrupt(string field)
        => new($"managed sparse vector index state is corrupt ({field})");
}
