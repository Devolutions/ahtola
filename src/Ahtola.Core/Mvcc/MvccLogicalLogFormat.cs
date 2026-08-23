using System.Buffers.Binary;

namespace Ahtola.Core.Mvcc;

/// <summary>
/// Shared framing and encrypted-payload rules for the managed MVCC logical log.
/// Mirrors Turso's <c>core/mvcc/persistent_storage/logical_log.rs</c>.
/// </summary>
internal static class MvccLogicalLogFormat
{
    internal const uint LogMagic = 0x4C4D4C32; // "LML2"
    internal const int LogHeaderSize = 56;
    internal const int LogHeaderSaltStart = 8;
    internal const int LogHeaderCrcStart = 52;
    internal const uint FrameMagic = 0x5854564D; // "MVTX"
    internal const uint EndMagic = 0x4554564D; // "MVTE"
    internal const int TxHeaderSize = 24;
    internal const int TxTrailerSize = 8;
    internal const int EncryptedPayloadChunkSize = 32 * 1024;
    internal const int EncryptionTagSize = 16;
    internal const int EncryptionNonceSize = 12;
    internal const int EncryptionOverhead = EncryptionTagSize + EncryptionNonceSize;
    internal const int EncryptedChunkAssociatedDataSize = 32;

    internal static int GetEncryptedPayloadSize(int plaintextSize)
    {
        if (plaintextSize < 0)
            throw new ArgumentOutOfRangeException(nameof(plaintextSize));

        var chunkCount = GetEncryptedChunkCount(plaintextSize);
        return checked(plaintextSize + (chunkCount * EncryptionOverhead));
    }

    internal static int GetEncryptedChunkCount(int plaintextSize)
    {
        if (plaintextSize < 0)
            throw new ArgumentOutOfRangeException(nameof(plaintextSize));
        return plaintextSize == 0
            ? 0
            : checked((plaintextSize + EncryptedPayloadChunkSize - 1) / EncryptedPayloadChunkSize);
    }

    internal static int GetPlaintextChunkLength(int plaintextSize, int chunkIndex)
    {
        var chunkCount = GetEncryptedChunkCount(plaintextSize);
        if ((uint)chunkIndex >= (uint)chunkCount)
            throw new InvalidDataException("MVCC encrypted payload chunk index is out of range.");

        var start = checked(chunkIndex * EncryptedPayloadChunkSize);
        return Math.Min(EncryptedPayloadChunkSize, plaintextSize - start);
    }

    internal static byte[] BuildEncryptedChunkAssociatedData(
        ulong salt,
        int plaintextSize,
        uint opCount,
        ulong commitTs,
        int chunkIndex)
    {
        var chunkCount = GetEncryptedChunkCount(plaintextSize);
        if ((uint)chunkIndex >= (uint)chunkCount)
            throw new InvalidDataException("MVCC encrypted payload chunk index is out of range.");

        var associatedData = new byte[EncryptedChunkAssociatedDataSize];
        BinaryPrimitives.WriteUInt64LittleEndian(associatedData, salt);
        if (chunkIndex + 1 == chunkCount)
            BinaryPrimitives.WriteUInt64LittleEndian(associatedData.AsSpan(8), checked((ulong)plaintextSize));
        BinaryPrimitives.WriteUInt32LittleEndian(associatedData.AsSpan(16), opCount);
        BinaryPrimitives.WriteUInt64LittleEndian(associatedData.AsSpan(20), commitTs);
        BinaryPrimitives.WriteUInt32LittleEndian(associatedData.AsSpan(28), checked((uint)chunkIndex));
        return associatedData;
    }

    internal static (ulong Salt, byte Version) ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < LogHeaderSize)
            throw new InvalidDataException("MVCC logical log header is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LogMagic)
            throw new InvalidDataException("Invalid MVCC logical log magic.");

        var version = header[4];
        if (version is not (2 or 3 or 4))
            throw new InvalidDataException($"Unsupported MVCC logical log version {version}.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != LogHeaderSize)
            throw new InvalidDataException("Invalid MVCC logical log header length.");

        Span<byte> crcBuffer = stackalloc byte[LogHeaderSize];
        header[..LogHeaderSize].CopyTo(crcBuffer);
        crcBuffer[LogHeaderCrcStart..].Clear();
        var expected = Crc32C.Compute(crcBuffer);
        var actual = BinaryPrimitives.ReadUInt32LittleEndian(header[LogHeaderCrcStart..]);
        if (expected != actual)
            throw new InvalidDataException("MVCC logical log header CRC mismatch.");

        return (BinaryPrimitives.ReadUInt64LittleEndian(header[LogHeaderSaltStart..]), version);
    }

    internal static (int PayloadSize, uint OpCount, ulong CommitTs) ReadFrameHeader(
        ReadOnlySpan<byte> header)
    {
        if (header.Length < TxHeaderSize)
            throw new InvalidDataException("MVCC logical log frame header is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != FrameMagic)
            throw new InvalidDataException("Invalid MVCC log frame magic.");

        var payloadSize = BinaryPrimitives.ReadUInt64LittleEndian(header[4..]);
        if (payloadSize > int.MaxValue)
            throw new InvalidDataException("MVCC log frame payload too large.");

        return (
            (int)payloadSize,
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[16..]));
    }

    /// <summary>
    /// Determines whether bytes beginning at a frame header contain any complete
    /// CRC-valid frame boundary. This does not trust the unauthenticated payload
    /// length, so an enlarged length cannot masquerade as a torn append.
    /// </summary>
    internal static bool ContainsCompleteFrameBoundary(ReadOnlySpan<byte> frameBytes)
    {
        if (frameBytes.Length < TxHeaderSize + TxTrailerSize)
            return false;

        var crc = Crc32C.InitialState;
        for (var index = 0; index < TxHeaderSize; index++)
            crc = Crc32C.Update(crc, frameBytes[index]);

        for (var trailerOffset = TxHeaderSize;
             trailerOffset + TxTrailerSize <= frameBytes.Length;
             trailerOffset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(frameBytes[(trailerOffset + sizeof(uint))..])
                    == EndMagic
                && BinaryPrimitives.ReadUInt32LittleEndian(frameBytes[trailerOffset..])
                    == Crc32C.Complete(crc))
            {
                return true;
            }

            crc = Crc32C.Update(crc, frameBytes[trailerOffset]);
        }

        return false;
    }
}
