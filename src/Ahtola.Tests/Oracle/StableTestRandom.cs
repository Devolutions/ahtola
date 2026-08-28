using System.Globalization;
using System.Text;

namespace Ahtola.Tests.Oracle;

internal sealed class StableTestSeed
{
    private const string EnvironmentVariable = "AHTOLA_TEST_SEED";

    private StableTestSeed(ulong rootSeed, string source)
    {
        RootSeed = rootSeed;
        Source = source;
    }

    public ulong RootSeed { get; }

    public string Source { get; }

    public static StableTestSeed Create(ulong defaultSeed)
    {
        var text = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(text))
            return new StableTestSeed(defaultSeed, "test default");

        if (!TryParseSeed(text, out var seed))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must be an unsigned 64-bit integer in decimal or 0x-prefixed hexadecimal form; received '{text}'.");
        }

        return new StableTestSeed(seed, EnvironmentVariable);
    }

    public StableRandomStream Derive(string streamName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        var streamSeed = Mix(RootSeed ^ StableHash(streamName));
        return new StableRandomStream(
            new StablePrng(streamSeed),
            RootSeed,
            streamSeed,
            streamName,
            Source);
    }

    private static bool TryParseSeed(string text, out ulong seed)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out seed)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
    }

    private static ulong StableHash(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            hash ^= octet;
            hash *= prime;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9;
        value ^= value >> 27;
        value *= 0x94d049bb133111eb;
        return value ^ (value >> 31);
    }
}

internal sealed class StableRandomStream
{
    internal StableRandomStream(StablePrng random, ulong rootSeed, ulong seed, string name, string source)
    {
        Random = random;
        RootSeed = rootSeed;
        Seed = seed;
        Name = name;
        Source = source;
    }

    public StablePrng Random { get; }

    public ulong RootSeed { get; }

    public ulong Seed { get; }

    public string Name { get; }

    public string Source { get; }

    public string Diagnostics =>
        $"root seed={RootSeed} ({Source}), stream='{Name}', stream seed={Seed}; replay with AHTOLA_TEST_SEED={RootSeed}";
}

/// <summary>
/// SplitMix64 with explicitly fixed output and range-selection algorithms.
/// </summary>
internal sealed class StablePrng
{
    private ulong _state;

    public StablePrng(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        var value = _state += 0x9e3779b97f4a7c15;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9;
        value = (value ^ (value >> 27)) * 0x94d049bb133111eb;
        return value ^ (value >> 31);
    }

    public int NextInt32(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        var bound = (uint)exclusiveMaximum;
        var threshold = unchecked((0u - bound) % bound);

        while (true)
        {
            var candidate = (uint)NextUInt64();
            if (candidate >= threshold)
                return (int)(candidate % bound);
        }
    }

    public int NextInt32(int inclusiveMinimum, int exclusiveMaximum)
    {
        if (exclusiveMaximum <= inclusiveMinimum)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));

        return inclusiveMinimum + NextInt32(exclusiveMaximum - inclusiveMinimum);
    }

    public bool NextBoolean() => (NextUInt64() & 1) != 0;

    public byte[] NextBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var value = NextUInt64();
            for (var shift = 0; shift < 64 && offset < bytes.Length; shift += 8)
                bytes[offset++] = (byte)(value >> shift);
        }

        return bytes;
    }
}
