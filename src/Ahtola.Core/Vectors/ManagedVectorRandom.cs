namespace Ahtola.Core.Vectors;

/// <summary>
/// A deterministic, pure-managed pseudo-random generator for vector index training.
/// </summary>
/// <remarks>
/// <para>
/// <c>xoshiro256**</c> seeded through <c>SplitMix64</c>. The engine deliberately does not use the
/// framework's built-in pseudo-random generator: its shared instance is seeded from the environment,
/// its instance algorithm is an implementation detail that has already changed across .NET versions,
/// and either property would make a trained index's centroids — and therefore its persisted state
/// bytes — differ between runs, platforms and framework updates.
/// </para>
/// <para>
/// Nothing here reads the clock, a hash code, a thread identity or any ambient state, so the same
/// seed always produces the same stream on x64, ARM64, NativeAOT and WebAssembly.
/// </para>
/// </remarks>
internal sealed class ManagedVectorRandom
{
    private ulong _state0;
    private ulong _state1;
    private ulong _state2;
    private ulong _state3;

    public ManagedVectorRandom(long seed)
    {
        // SplitMix64 expansion, the reference seeding procedure for the xoshiro family: it turns a
        // single (possibly zero, possibly low-entropy) seed into four well-mixed words so that
        // seed = 0 and seed = 1 do not produce correlated streams.
        var state = unchecked((ulong)seed);
        _state0 = SplitMix64(ref state);
        _state1 = SplitMix64(ref state);
        _state2 = SplitMix64(ref state);
        _state3 = SplitMix64(ref state);

        // The generator is undefined for an all-zero state; SplitMix64 cannot produce four zero
        // words in a row, but the guard keeps that a proven property rather than an assumed one.
        if ((_state0 | _state1 | _state2 | _state3) == 0)
            _state3 = 0x9E3779B97F4A7C15UL;
    }

    /// <summary>FNV-1a over the caller-supplied text, mixed with the declared seed.</summary>
    /// <remarks>
    /// Deliberately not the runtime string hash, which is randomized per process and would make the
    /// trained centroids differ between two runs over identical rows.
    /// </remarks>
    public static long DeriveSeed(long seed, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var hash = 0xCBF29CE484222325UL;
        foreach (var character in fingerprint)
        {
            hash ^= character;
            hash *= 0x100000001B3UL;
        }

        return unchecked((long)(hash ^ (ulong)seed));
    }

    /// <summary>The next 64 raw bits.</summary>
    public ulong NextUInt64()
    {
        var result = unchecked(RotateLeft(_state1 * 5UL, 7) * 9UL);
        var t = _state1 << 17;

        _state2 ^= _state0;
        _state3 ^= _state1;
        _state1 ^= _state2;
        _state0 ^= _state3;
        _state2 ^= t;
        _state3 = RotateLeft(_state3, 45);

        return result;
    }

    /// <summary>A uniform value in <c>[0, bound)</c>, rejection sampled so the stream stays unbiased.</summary>
    public long NextBounded(long bound)
    {
        if (bound <= 0)
            throw new ArgumentOutOfRangeException(nameof(bound));

        var limit = (ulong)bound;
        var threshold = (ulong.MaxValue - limit + 1) % limit;
        while (true)
        {
            var candidate = NextUInt64();
            if (candidate >= threshold)
                return (long)(candidate % limit);
        }
    }

    /// <summary>A uniform double in <c>[0, 1)</c> using the top 53 bits, as the xoshiro reference does.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    private static ulong SplitMix64(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static ulong RotateLeft(ulong value, int offset) => (value << offset) | (value >> (64 - offset));
}
