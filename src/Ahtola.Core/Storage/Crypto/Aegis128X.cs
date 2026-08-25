using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Ahtola.Core.Storage.Crypto;

/// <summary>
/// AEGIS-128X with a parallelism degree of 1, 2 or 4, i.e. AEGIS-128L,
/// AEGIS-128X2 and AEGIS-128X4 from the CFRG AEGIS specification
/// (<c>draft-irtf-cfrg-aegis-aead</c>). A degree of 1 is defined by that document
/// to be exactly AEGIS-128L, because the per-lane context separator
/// <c>Byte(0) || Byte(0)</c> is all zero and therefore a no-op.
/// </summary>
/// <remarks>
/// <para>
/// The state is <c>D</c> parallel AEGIS-128L states of eight AES blocks each,
/// laid out as <c>v[j * degree + lane]</c>. Only 128-bit tags are produced:
/// Turso instantiates every AEGIS cipher as <c>::&lt;16&gt;</c>, and a 256-bit tag
/// would silently change the encrypted-page metadata size.
/// </para>
/// <para>
/// Every buffer that holds key, state or keystream material is a
/// <c>stackalloc</c> that is cleared before the frame is left; nothing is pooled,
/// because pool reuse would leak plaintext across callers.
/// </para>
/// </remarks>
internal static class Aegis128X<TRound>
    where TRound : IAhtolaAesRoundPolicy
{
    internal const int KeySize = 16;
    internal const int NonceSize = 16;
    internal const int TagSize = 16;

    private const int StateBlocks = 8;
    private const int MaxDegree = AegisParameters.MaxDegree;
    private const int MaxRate = 32 * MaxDegree;

    /// <summary>Encrypts <paramref name="plaintext"/> and writes the detached tag.</summary>
    internal static void Encrypt(
        int degree,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        AegisParameters.ValidateDegree(degree);
        AegisParameters.ValidateArguments(key.Length, KeySize, nonce.Length, NonceSize, tag.Length, TagSize);
        if (ciphertext.Length != plaintext.Length)
            throw new ArgumentException("AEGIS ciphertext and plaintext lengths must match.", nameof(ciphertext));

        var rate = 32 * degree;
        Span<Vector128<byte>> state = stackalloc Vector128<byte>[StateBlocks * MaxDegree];
        Span<byte> block = stackalloc byte[MaxRate];
        Span<byte> encoded = stackalloc byte[MaxRate];
        try
        {
            state = state[..(StateBlocks * degree)];
            Initialize(state, degree, key, nonce);
            AbsorbAssociatedData(state, degree, rate, associatedData, block);

            var position = 0;
            while (position + rate <= plaintext.Length)
            {
                Encode(state, degree, plaintext.Slice(position, rate), ciphertext.Slice(position, rate), decrypting: false);
                position += rate;
            }

            var tail = plaintext.Length - position;
            if (tail > 0)
            {
                block[..rate].Clear();
                plaintext[position..].CopyTo(block);
                Encode(state, degree, block[..rate], encoded[..rate], decrypting: false);
                encoded[..tail].CopyTo(ciphertext[position..]);
            }

            Finalize(state, degree, (ulong)associatedData.Length << 3, (ulong)plaintext.Length << 3, tag);
        }
        finally
        {
            state.Clear();
            block.Clear();
            encoded.Clear();
        }
    }

    /// <summary>
    /// Authenticates and decrypts. Returns <see langword="false"/> and leaves
    /// <paramref name="plaintext"/> zeroed when the tag does not verify.
    /// </summary>
    internal static bool TryDecrypt(
        int degree,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext)
    {
        AegisParameters.ValidateDegree(degree);
        AegisParameters.ValidateArguments(key.Length, KeySize, nonce.Length, NonceSize, tag.Length, TagSize);
        if (plaintext.Length != ciphertext.Length)
            throw new ArgumentException("AEGIS plaintext and ciphertext lengths must match.", nameof(plaintext));

        var rate = 32 * degree;
        Span<Vector128<byte>> state = stackalloc Vector128<byte>[StateBlocks * MaxDegree];
        Span<byte> block = stackalloc byte[MaxRate];
        Span<byte> decoded = stackalloc byte[MaxRate];
        Span<byte> expectedTag = stackalloc byte[TagSize];
        try
        {
            state = state[..(StateBlocks * degree)];
            Initialize(state, degree, key, nonce);
            AbsorbAssociatedData(state, degree, rate, associatedData, block);

            var position = 0;
            while (position + rate <= ciphertext.Length)
            {
                Encode(state, degree, ciphertext.Slice(position, rate), plaintext.Slice(position, rate), decrypting: true);
                position += rate;
            }

            var tail = ciphertext.Length - position;
            if (tail > 0)
                DecodePartial(state, degree, rate, ciphertext[position..], plaintext[position..], block, decoded);

            Finalize(state, degree, (ulong)associatedData.Length << 3, (ulong)plaintext.Length << 3, expectedTag);
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedTag, tag))
                return true;

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
        finally
        {
            state.Clear();
            block.Clear();
            decoded.Clear();
            expectedTag.Clear();
        }
    }

    private static void Initialize(
        Span<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce)
    {
        Span<Vector128<byte>> context = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> keyLanes = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> nonceLanes = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            var k = Vector128.Create<byte>(key);
            var n = Vector128.Create<byte>(nonce);
            var kn = Vector128.Xor(k, n);
            var kc0 = Vector128.Xor(k, AegisParameters.C0);
            var kc1 = Vector128.Xor(k, AegisParameters.C1);

            for (var lane = 0; lane < degree; lane++)
            {
                state[(0 * degree) + lane] = kn;
                state[(1 * degree) + lane] = AegisParameters.C1;
                state[(2 * degree) + lane] = AegisParameters.C0;
                state[(3 * degree) + lane] = AegisParameters.C1;
                state[(4 * degree) + lane] = kn;
                state[(5 * degree) + lane] = kc0;
                state[(6 * degree) + lane] = kc1;
                state[(7 * degree) + lane] = kc0;

                context[lane] = AegisParameters.CreateContextSeparator(lane, degree);
                keyLanes[lane] = k;
                nonceLanes[lane] = n;
            }

            for (var round = 0; round < 10; round++)
            {
                for (var lane = 0; lane < degree; lane++)
                {
                    state[(3 * degree) + lane] = Vector128.Xor(state[(3 * degree) + lane], context[lane]);
                    state[(7 * degree) + lane] = Vector128.Xor(state[(7 * degree) + lane], context[lane]);
                }

                Update(state, degree, nonceLanes, keyLanes);
            }
        }
        finally
        {
            context.Clear();
            keyLanes.Clear();
            nonceLanes.Clear();
        }
    }

    private static void Update(
        Span<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<Vector128<byte>> message0,
        ReadOnlySpan<Vector128<byte>> message1)
    {
        for (var lane = 0; lane < degree; lane++)
        {
            var s0 = state[(0 * degree) + lane];
            var s1 = state[(1 * degree) + lane];
            var s2 = state[(2 * degree) + lane];
            var s3 = state[(3 * degree) + lane];
            var s4 = state[(4 * degree) + lane];
            var s5 = state[(5 * degree) + lane];
            var s6 = state[(6 * degree) + lane];
            var s7 = state[(7 * degree) + lane];

            state[(0 * degree) + lane] = TRound.Round(s7, Vector128.Xor(s0, message0[lane]));
            state[(1 * degree) + lane] = TRound.Round(s0, s1);
            state[(2 * degree) + lane] = TRound.Round(s1, s2);
            state[(3 * degree) + lane] = TRound.Round(s2, s3);
            state[(4 * degree) + lane] = TRound.Round(s3, Vector128.Xor(s4, message1[lane]));
            state[(5 * degree) + lane] = TRound.Round(s4, s5);
            state[(6 * degree) + lane] = TRound.Round(s5, s6);
            state[(7 * degree) + lane] = TRound.Round(s6, s7);
        }
    }

    private static void AbsorbAssociatedData(
        Span<Vector128<byte>> state,
        int degree,
        int rate,
        ReadOnlySpan<byte> associatedData,
        Span<byte> scratch)
    {
        var position = 0;
        while (position + rate <= associatedData.Length)
        {
            Absorb(state, degree, associatedData.Slice(position, rate));
            position += rate;
        }

        if (position >= associatedData.Length)
            return;

        scratch[..rate].Clear();
        associatedData[position..].CopyTo(scratch);
        Absorb(state, degree, scratch[..rate]);
    }

    private static void Absorb(Span<Vector128<byte>> state, int degree, ReadOnlySpan<byte> block)
    {
        Span<Vector128<byte>> m0 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> m1 = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            AegisParameters.LoadLanes(block, degree, m0);
            AegisParameters.LoadLanes(block[(16 * degree)..], degree, m1);
            Update(state, degree, m0, m1);
        }
        finally
        {
            m0.Clear();
            m1.Clear();
        }
    }

    /// <summary>
    /// Runs one full-rate <c>Enc</c> or <c>Dec</c> block. The two directions share
    /// the keystream derivation and differ only in which value is fed back into
    /// <c>Update</c>: the plaintext in both cases.
    /// </summary>
    private static void Encode(
        Span<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        bool decrypting)
    {
        Span<Vector128<byte>> out0 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> out1 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> in0 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> in1 = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            AegisParameters.LoadLanes(input, degree, in0);
            AegisParameters.LoadLanes(input[(16 * degree)..], degree, in1);
            ApplyKeystream(state, degree, in0, in1, out0, out1);

            // Enc absorbs the plaintext it just consumed; Dec absorbs the
            // plaintext it just produced. Either way the state sees the plaintext.
            Update(state, degree, decrypting ? out0 : in0, decrypting ? out1 : in1);

            AegisParameters.StoreLanes(out0, degree, output);
            AegisParameters.StoreLanes(out1, degree, output[(16 * degree)..]);
        }
        finally
        {
            out0.Clear();
            out1.Clear();
            in0.Clear();
            in1.Clear();
        }
    }

    private static void DecodePartial(
        Span<Vector128<byte>> state,
        int degree,
        int rate,
        ReadOnlySpan<byte> ciphertext,
        Span<byte> plaintext,
        Span<byte> scratch,
        Span<byte> decoded)
    {
        Span<Vector128<byte>> out0 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> out1 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> in0 = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> in1 = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            scratch[..rate].Clear();
            ciphertext.CopyTo(scratch);
            AegisParameters.LoadLanes(scratch, degree, in0);
            AegisParameters.LoadLanes(scratch[(16 * degree)..], degree, in1);
            ApplyKeystream(state, degree, in0, in1, out0, out1);

            decoded[..rate].Clear();
            AegisParameters.StoreLanes(out0, degree, decoded);
            AegisParameters.StoreLanes(out1, degree, decoded[(16 * degree)..]);
            decoded[..ciphertext.Length].CopyTo(plaintext);

            // The state absorbs ZeroPad(xn, R), matching what Enc absorbed.
            decoded[ciphertext.Length..rate].Clear();
            AegisParameters.LoadLanes(decoded, degree, out0);
            AegisParameters.LoadLanes(decoded[(16 * degree)..], degree, out1);
            Update(state, degree, out0, out1);
        }
        finally
        {
            out0.Clear();
            out1.Clear();
            in0.Clear();
            in1.Clear();
        }
    }

    private static void ApplyKeystream(
        ReadOnlySpan<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<Vector128<byte>> in0,
        ReadOnlySpan<Vector128<byte>> in1,
        Span<Vector128<byte>> out0,
        Span<Vector128<byte>> out1)
    {
        for (var lane = 0; lane < degree; lane++)
        {
            var s1 = state[(1 * degree) + lane];
            var s2 = state[(2 * degree) + lane];
            var s3 = state[(3 * degree) + lane];
            var s5 = state[(5 * degree) + lane];
            var s6 = state[(6 * degree) + lane];
            var s7 = state[(7 * degree) + lane];

            var z0 = Vector128.Xor(Vector128.Xor(s1, s6), Vector128.BitwiseAnd(s2, s3));
            var z1 = Vector128.Xor(Vector128.Xor(s2, s5), Vector128.BitwiseAnd(s6, s7));
            out0[lane] = Vector128.Xor(in0[lane], z0);
            out1[lane] = Vector128.Xor(in1[lane], z1);
        }
    }

    private static void Finalize(
        Span<Vector128<byte>> state,
        int degree,
        ulong associatedDataLengthInBits,
        ulong messageLengthInBits,
        Span<byte> tag)
    {
        Span<byte> lengths = stackalloc byte[16];
        Span<Vector128<byte>> t = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            BinaryPrimitives.WriteUInt64LittleEndian(lengths, associatedDataLengthInBits);
            BinaryPrimitives.WriteUInt64LittleEndian(lengths[8..], messageLengthInBits);
            var u = Vector128.Create<byte>(lengths);

            for (var lane = 0; lane < degree; lane++)
                t[lane] = Vector128.Xor(state[(2 * degree) + lane], u);

            for (var round = 0; round < 7; round++)
                Update(state, degree, t, t);

            var folded = Vector128<byte>.Zero;
            for (var lane = 0; lane < degree; lane++)
            {
                var laneTag = state[(0 * degree) + lane];
                for (var block = 1; block < 7; block++)
                    laneTag = Vector128.Xor(laneTag, state[(block * degree) + lane]);
                folded = Vector128.Xor(folded, laneTag);
            }

            folded.CopyTo(tag);
        }
        finally
        {
            lengths.Clear();
            t.Clear();
        }
    }
}
