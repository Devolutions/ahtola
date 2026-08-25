using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Ahtola.Core.Storage.Crypto;

/// <summary>
/// Shared constants and lane helpers for the AEGIS family
/// (<c>draft-irtf-cfrg-aegis-aead</c>).
/// </summary>
internal static class AegisParameters
{
    /// <summary>Highest parallelism degree the specification recommends implementing.</summary>
    internal const int MaxDegree = 4;

    /// <summary>The AEGIS <c>C0</c> constant.</summary>
    internal static Vector128<byte> C0 { get; } = Vector128.Create<byte>(
        [0x00, 0x01, 0x01, 0x02, 0x03, 0x05, 0x08, 0x0d, 0x15, 0x22, 0x37, 0x59, 0x90, 0xe9, 0x79, 0x62]);

    /// <summary>The AEGIS <c>C1</c> constant.</summary>
    internal static Vector128<byte> C1 { get; } = Vector128.Create<byte>(
        [0xdb, 0x3d, 0x18, 0x55, 0x6d, 0xc2, 0x2f, 0xf1, 0x20, 0x11, 0x31, 0x42, 0x73, 0xb5, 0x28, 0xdd]);

    /// <summary>Rejects parallelism degrees outside the specified set.</summary>
    internal static void ValidateDegree(int degree)
    {
        if (degree is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(degree),
                degree,
                "AEGIS parallelism degree must be 1, 2 or 4.");
        }
    }

    /// <summary>Rejects key, nonce and tag sizes the cipher does not define.</summary>
    internal static void ValidateArguments(
        int keyLength,
        int expectedKeyLength,
        int nonceLength,
        int expectedNonceLength,
        int tagLength,
        int expectedTagLength)
    {
        if (keyLength != expectedKeyLength)
        {
            throw new ArgumentException(
                $"AEGIS requires a {expectedKeyLength}-byte key, but the supplied key has {keyLength} bytes.",
                "key");
        }

        if (nonceLength != expectedNonceLength)
        {
            throw new ArgumentException(
                $"AEGIS requires a {expectedNonceLength}-byte nonce, but the supplied nonce has {nonceLength} bytes.",
                "nonce");
        }

        if (tagLength != expectedTagLength)
        {
            throw new ArgumentException(
                $"Ahtola only produces {expectedTagLength}-byte AEGIS tags, but {tagLength} bytes were requested.",
                "tag");
        }
    }

    /// <summary>
    /// Builds <c>ctx[i]</c>: the lane index, the highest lane index, then 112 zero
    /// bits. Degree 1 yields the all-zero separator, which is why AEGIS-128X and
    /// AEGIS-256X at degree 1 reduce exactly to AEGIS-128L and AEGIS-256.
    /// </summary>
    internal static Vector128<byte> CreateContextSeparator(int lane, int degree)
    {
        Span<byte> separator = stackalloc byte[16];
        separator.Clear();
        separator[0] = (byte)lane;
        separator[1] = (byte)(degree - 1);
        return Vector128.Create<byte>(separator);
    }

    /// <summary>Reads <paramref name="degree"/> consecutive AES blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LoadLanes(ReadOnlySpan<byte> source, int degree, Span<Vector128<byte>> lanes)
    {
        for (var lane = 0; lane < degree; lane++)
            lanes[lane] = Vector128.Create<byte>(source.Slice(lane * 16, 16));
    }

    /// <summary>Writes <paramref name="degree"/> consecutive AES blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void StoreLanes(ReadOnlySpan<Vector128<byte>> lanes, int degree, Span<byte> destination)
    {
        for (var lane = 0; lane < degree; lane++)
            lanes[lane].CopyTo(destination[(lane * 16)..]);
    }
}
