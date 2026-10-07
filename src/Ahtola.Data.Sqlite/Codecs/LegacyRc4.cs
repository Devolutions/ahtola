namespace Ahtola.Data.Sqlite.Codecs;

/// <summary>
/// RC4 stream cipher, used only to read and write the legacy page formats that require it.
/// Never use it to protect new data.
/// </summary>
internal static class LegacyRc4
{
    internal const int StateLength = 256;

    /// <summary>Runs the RC4 key schedule.</summary>
    internal static byte[] ScheduleKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            throw new ArgumentException("RC4 needs a non-empty key.", nameof(key));

        var state = new byte[StateLength];
        for (var i = 0; i < state.Length; i++)
            state[i] = (byte)i;

        var j = 0;
        for (var i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        return state;
    }

    /// <summary>XORs <paramref name="input"/> with the key stream from its start.</summary>
    internal static void ApplyScheduled(ReadOnlySpan<byte> scheduledState, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output is shorter than the input.", nameof(output));

        Span<byte> state = stackalloc byte[StateLength];
        scheduledState.CopyTo(state);
        var i = 0;
        var j = 0;
        for (var k = 0; k < input.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + state[i]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
            output[k] = (byte)(input[k] ^ state[(state[i] + state[j]) & 0xFF]);
        }
    }

    internal static void Apply(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
        => ApplyScheduled(ScheduleKey(key), input, output);
}
