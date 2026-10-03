using Ahtola.Core.Mvcc;

namespace Ahtola.Tests;

/// <summary>
/// Recomputes the CRC an MVCC logical-log frame stores, so tamper tests can forge a frame
/// that passes the unkeyed CRC and reaches the authenticated layer behind it. Version 5
/// logs chain the CRC: the first frame continues <c>crc32c(salt)</c>, later frames continue
/// the previous frame's stored CRC.
/// </summary>
internal static class MvccLogTestCrc
{
    internal static uint FirstFrame(ReadOnlySpan<byte> logHeader, ReadOnlySpan<byte> frameBytes)
        => Frame(logHeader, InitialCrc(logHeader), frameBytes);

    internal static uint Frame(ReadOnlySpan<byte> logHeader, uint previousCrc, ReadOnlySpan<byte> frameBytes)
        => logHeader[4] >= MvccLogicalLogFormat.ChainedVersion
            ? Crc32C.Append(previousCrc, frameBytes)
            : Crc32C.Compute(frameBytes);

    // Turso derive_initial_crc: crc32c of the salt's little-endian bytes, as stored.
    internal static uint InitialCrc(ReadOnlySpan<byte> logHeader)
        => Crc32C.Compute(logHeader.Slice(MvccLogicalLogFormat.LogHeaderSaltStart, sizeof(ulong)));
}
