using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Ahtola.Core.Storage.Crypto;

/// <summary>
/// AEGIS-256X with a parallelism degree of 1, 2 or 4, i.e. AEGIS-256,
/// AEGIS-256X2 and AEGIS-256X4 from the CFRG AEGIS specification
/// (<c>draft-irtf-cfrg-aegis-aead</c>). A degree of 1 is defined by that document
/// to be exactly AEGIS-256, because the per-lane context separator
/// <c>Byte(0) || Byte(0)</c> is all zero and therefore a no-op.
/// </summary>
/// <remarks>
/// The state is <c>D</c> parallel AEGIS-256 states of six AES blocks each, laid
/// out as <c>v[j * degree + lane]</c>. Only 128-bit tags are produced, matching
/// Turso's <c>::&lt;16&gt;</c> instantiation.
/// </remarks>
internal static class Aegis256X<TRound>
    where TRound : IAhtolaAesRoundPolicy
{
    internal const int KeySize = 32;
    internal const int NonceSize = 32;
    internal const int TagSize = 16;

    private const int StateBlocks = 6;
    private const int MaxDegree = AegisParameters.MaxDegree;
    private const int MaxRate = 16 * MaxDegree;

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

        var rate = 16 * degree;
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

        var rate = 16 * degree;
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
        Span<Vector128<byte>> k0Lanes = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> k1Lanes = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> k0n0Lanes = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> k1n1Lanes = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            var k0 = Vector128.Create<byte>(key[..16]);
            var k1 = Vector128.Create<byte>(key[16..32]);
            var n0 = Vector128.Create<byte>(nonce[..16]);
            var n1 = Vector128.Create<byte>(nonce[16..32]);
            var k0n0 = Vector128.Xor(k0, n0);
            var k1n1 = Vector128.Xor(k1, n1);
            var k0c0 = Vector128.Xor(k0, AegisParameters.C0);
            var k1c1 = Vector128.Xor(k1, AegisParameters.C1);

            for (var lane = 0; lane < degree; lane++)
            {
                state[(0 * degree) + lane] = k0n0;
                state[(1 * degree) + lane] = k1n1;
                state[(2 * degree) + lane] = AegisParameters.C1;
                state[(3 * degree) + lane] = AegisParameters.C0;
                state[(4 * degree) + lane] = k0c0;
                state[(5 * degree) + lane] = k1c1;

                context[lane] = AegisParameters.CreateContextSeparator(lane, degree);
                k0Lanes[lane] = k0;
                k1Lanes[lane] = k1;
                k0n0Lanes[lane] = k0n0;
                k1n1Lanes[lane] = k1n1;
            }

            for (var round = 0; round < 4; round++)
            {
                ApplyContext(state, degree, context);
                Update(state, degree, k0Lanes);
                ApplyContext(state, degree, context);
                Update(state, degree, k1Lanes);
                ApplyContext(state, degree, context);
                Update(state, degree, k0n0Lanes);
                ApplyContext(state, degree, context);
                Update(state, degree, k1n1Lanes);
            }
        }
        finally
        {
            context.Clear();
            k0Lanes.Clear();
            k1Lanes.Clear();
            k0n0Lanes.Clear();
            k1n1Lanes.Clear();
        }
    }

    private static void ApplyContext(
        Span<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<Vector128<byte>> context)
    {
        for (var lane = 0; lane < degree; lane++)
        {
            state[(3 * degree) + lane] = Vector128.Xor(state[(3 * degree) + lane], context[lane]);
            state[(5 * degree) + lane] = Vector128.Xor(state[(5 * degree) + lane], context[lane]);
        }
    }

    private static void Update(
        Span<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<Vector128<byte>> message)
    {
        for (var lane = 0; lane < degree; lane++)
        {
            var s0 = state[(0 * degree) + lane];
            var s1 = state[(1 * degree) + lane];
            var s2 = state[(2 * degree) + lane];
            var s3 = state[(3 * degree) + lane];
            var s4 = state[(4 * degree) + lane];
            var s5 = state[(5 * degree) + lane];

            state[(0 * degree) + lane] = TRound.Round(s5, Vector128.Xor(s0, message[lane]));
            state[(1 * degree) + lane] = TRound.Round(s0, s1);
            state[(2 * degree) + lane] = TRound.Round(s1, s2);
            state[(3 * degree) + lane] = TRound.Round(s2, s3);
            state[(4 * degree) + lane] = TRound.Round(s3, s4);
            state[(5 * degree) + lane] = TRound.Round(s4, s5);
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
        Span<Vector128<byte>> m = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            AegisParameters.LoadLanes(block, degree, m);
            Update(state, degree, m);
        }
        finally
        {
            m.Clear();
        }
    }

    private static void Encode(
        Span<Vector128<byte>> state,
        int degree,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        bool decrypting)
    {
        Span<Vector128<byte>> keystream = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> lanes = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> result = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            AegisParameters.LoadLanes(input, degree, lanes);
            DeriveKeystream(state, degree, keystream);
            for (var lane = 0; lane < degree; lane++)
                result[lane] = Vector128.Xor(lanes[lane], keystream[lane]);

            // Enc absorbs the plaintext it consumed; Dec absorbs the plaintext it
            // produced. The state always sees the plaintext block.
            Update(state, degree, decrypting ? result : lanes);
            AegisParameters.StoreLanes(result, degree, output);
        }
        finally
        {
            keystream.Clear();
            lanes.Clear();
            result.Clear();
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
        Span<Vector128<byte>> keystream = stackalloc Vector128<byte>[MaxDegree];
        Span<Vector128<byte>> lanes = stackalloc Vector128<byte>[MaxDegree];
        try
        {
            scratch[..rate].Clear();
            ciphertext.CopyTo(scratch);
            AegisParameters.LoadLanes(scratch, degree, lanes);
            DeriveKeystream(state, degree, keystream);
            for (var lane = 0; lane < degree; lane++)
                lanes[lane] = Vector128.Xor(lanes[lane], keystream[lane]);

            decoded[..rate].Clear();
            AegisParameters.StoreLanes(lanes, degree, decoded);
            decoded[..ciphertext.Length].CopyTo(plaintext);

            // The state absorbs ZeroPad(xn, R), matching what Enc absorbed.
            decoded[ciphertext.Length..rate].Clear();
            AegisParameters.LoadLanes(decoded, degree, lanes);
            Update(state, degree, lanes);
        }
        finally
        {
            keystream.Clear();
            lanes.Clear();
        }
    }

    private static void DeriveKeystream(
        ReadOnlySpan<Vector128<byte>> state,
        int degree,
        Span<Vector128<byte>> keystream)
    {
        for (var lane = 0; lane < degree; lane++)
        {
            var s1 = state[(1 * degree) + lane];
            var s2 = state[(2 * degree) + lane];
            var s3 = state[(3 * degree) + lane];
            var s4 = state[(4 * degree) + lane];
            var s5 = state[(5 * degree) + lane];

            keystream[lane] = Vector128.Xor(
                Vector128.Xor(Vector128.Xor(s1, s4), s5),
                Vector128.BitwiseAnd(s2, s3));
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
                t[lane] = Vector128.Xor(state[(3 * degree) + lane], u);

            for (var round = 0; round < 7; round++)
                Update(state, degree, t);

            var folded = Vector128<byte>.Zero;
            for (var lane = 0; lane < degree; lane++)
            {
                var laneTag = state[(0 * degree) + lane];
                for (var block = 1; block < 6; block++)
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
