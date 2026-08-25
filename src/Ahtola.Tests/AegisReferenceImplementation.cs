namespace Ahtola.Tests;

/// <summary>
/// A deliberately slow, structurally independent transcription of the CFRG AEGIS
/// pseudocode (<c>draft-irtf-cfrg-aegis-aead</c>), used only to differential-test
/// the production implementation.
/// </summary>
/// <remarks>
/// <para>
/// This is test-only scaffolding and is intentionally written to share nothing
/// with production: it keeps the state as <c>byte[16]</c> blocks rather than
/// <c>Vector128&lt;byte&gt;</c>, and computes the AES round with a plain S-box
/// lookup table and textbook <c>ShiftRows</c>/<c>MixColumns</c> instead of the
/// bitsliced constant-time circuit. A table lookup would be a cache-timing
/// oracle, which is exactly why production does not use one -- so this code must
/// never be moved out of the test project.
/// </para>
/// <para>
/// Degree 1 is AEGIS-128L / AEGIS-256, degree 2 and 4 are the X2 and X4 modes.
/// Only 128-bit tags are produced, matching Turso's instantiation.
/// </para>
/// </remarks>
internal static class AegisReferenceImplementation
{
    private static readonly byte[] SBox =
    [
        0x63, 0x7c, 0x77, 0x7b, 0xf2, 0x6b, 0x6f, 0xc5, 0x30, 0x01, 0x67, 0x2b, 0xfe, 0xd7, 0xab, 0x76,
        0xca, 0x82, 0xc9, 0x7d, 0xfa, 0x59, 0x47, 0xf0, 0xad, 0xd4, 0xa2, 0xaf, 0x9c, 0xa4, 0x72, 0xc0,
        0xb7, 0xfd, 0x93, 0x26, 0x36, 0x3f, 0xf7, 0xcc, 0x34, 0xa5, 0xe5, 0xf1, 0x71, 0xd8, 0x31, 0x15,
        0x04, 0xc7, 0x23, 0xc3, 0x18, 0x96, 0x05, 0x9a, 0x07, 0x12, 0x80, 0xe2, 0xeb, 0x27, 0xb2, 0x75,
        0x09, 0x83, 0x2c, 0x1a, 0x1b, 0x6e, 0x5a, 0xa0, 0x52, 0x3b, 0xd6, 0xb3, 0x29, 0xe3, 0x2f, 0x84,
        0x53, 0xd1, 0x00, 0xed, 0x20, 0xfc, 0xb1, 0x5b, 0x6a, 0xcb, 0xbe, 0x39, 0x4a, 0x4c, 0x58, 0xcf,
        0xd0, 0xef, 0xaa, 0xfb, 0x43, 0x4d, 0x33, 0x85, 0x45, 0xf9, 0x02, 0x7f, 0x50, 0x3c, 0x9f, 0xa8,
        0x51, 0xa3, 0x40, 0x8f, 0x92, 0x9d, 0x38, 0xf5, 0xbc, 0xb6, 0xda, 0x21, 0x10, 0xff, 0xf3, 0xd2,
        0xcd, 0x0c, 0x13, 0xec, 0x5f, 0x97, 0x44, 0x17, 0xc4, 0xa7, 0x7e, 0x3d, 0x64, 0x5d, 0x19, 0x73,
        0x60, 0x81, 0x4f, 0xdc, 0x22, 0x2a, 0x90, 0x88, 0x46, 0xee, 0xb8, 0x14, 0xde, 0x5e, 0x0b, 0xdb,
        0xe0, 0x32, 0x3a, 0x0a, 0x49, 0x06, 0x24, 0x5c, 0xc2, 0xd3, 0xac, 0x62, 0x91, 0x95, 0xe4, 0x79,
        0xe7, 0xc8, 0x37, 0x6d, 0x8d, 0xd5, 0x4e, 0xa9, 0x6c, 0x56, 0xf4, 0xea, 0x65, 0x7a, 0xae, 0x08,
        0xba, 0x78, 0x25, 0x2e, 0x1c, 0xa6, 0xb4, 0xc6, 0xe8, 0xdd, 0x74, 0x1f, 0x4b, 0xbd, 0x8b, 0x8a,
        0x70, 0x3e, 0xb5, 0x66, 0x48, 0x03, 0xf6, 0x0e, 0x61, 0x35, 0x57, 0xb9, 0x86, 0xc1, 0x1d, 0x9e,
        0xe1, 0xf8, 0x98, 0x11, 0x69, 0xd9, 0x8e, 0x94, 0x9b, 0x1e, 0x87, 0xe9, 0xce, 0x55, 0x28, 0xdf,
        0x8c, 0xa1, 0x89, 0x0d, 0xbf, 0xe6, 0x42, 0x68, 0x41, 0x99, 0x2d, 0x0f, 0xb0, 0x54, 0xbb, 0x16,
    ];

    private static readonly byte[] C0 =
        [0x00, 0x01, 0x01, 0x02, 0x03, 0x05, 0x08, 0x0d, 0x15, 0x22, 0x37, 0x59, 0x90, 0xe9, 0x79, 0x62];

    private static readonly byte[] C1 =
        [0xdb, 0x3d, 0x18, 0x55, 0x6d, 0xc2, 0x2f, 0xf1, 0x20, 0x11, 0x31, 0x42, 0x73, 0xb5, 0x28, 0xdd];

    /// <summary>AEGIS-128X encryption at the given parallelism degree.</summary>
    internal static (byte[] Ciphertext, byte[] Tag) Encrypt128(
        int degree,
        byte[] key,
        byte[] nonce,
        byte[] associatedData,
        byte[] message)
    {
        var v = Initialize128(degree, key, nonce);
        var rate = 32 * degree;

        foreach (var block in SplitZeroPadded(associatedData, rate))
            Update128(v, degree, block[..(16 * degree)], block[(16 * degree)..]);

        var ciphertext = new byte[message.Length];
        var position = 0;
        foreach (var block in SplitZeroPadded(message, rate))
        {
            var keystream = Keystream128(v, degree);
            var output = Xor(block, keystream);
            Update128(v, degree, block[..(16 * degree)], block[(16 * degree)..]);
            var take = Math.Min(rate, message.Length - position);
            Array.Copy(output, 0, ciphertext, position, take);
            position += take;
        }

        return (ciphertext, Finalize128(v, degree, (ulong)associatedData.Length * 8, (ulong)message.Length * 8));
    }

    /// <summary>AEGIS-128X decryption at the given parallelism degree.</summary>
    internal static (byte[] Plaintext, byte[] Tag) Decrypt128(
        int degree,
        byte[] key,
        byte[] nonce,
        byte[] associatedData,
        byte[] ciphertext)
    {
        var v = Initialize128(degree, key, nonce);
        var rate = 32 * degree;

        foreach (var block in SplitZeroPadded(associatedData, rate))
            Update128(v, degree, block[..(16 * degree)], block[(16 * degree)..]);

        var plaintext = new byte[ciphertext.Length];
        var position = 0;
        while (position < ciphertext.Length)
        {
            var take = Math.Min(rate, ciphertext.Length - position);
            var block = new byte[rate];
            Array.Copy(ciphertext, position, block, 0, take);

            var keystream = Keystream128(v, degree);
            var output = Xor(block, keystream);
            Array.Copy(output, 0, plaintext, position, take);

            // The state absorbs ZeroPad(xn, R): the recovered plaintext, padded.
            var absorbed = new byte[rate];
            Array.Copy(output, 0, absorbed, 0, take);
            Update128(v, degree, absorbed[..(16 * degree)], absorbed[(16 * degree)..]);
            position += take;
        }

        return (plaintext, Finalize128(v, degree, (ulong)associatedData.Length * 8, (ulong)ciphertext.Length * 8));
    }

    /// <summary>AEGIS-256X encryption at the given parallelism degree.</summary>
    internal static (byte[] Ciphertext, byte[] Tag) Encrypt256(
        int degree,
        byte[] key,
        byte[] nonce,
        byte[] associatedData,
        byte[] message)
    {
        var v = Initialize256(degree, key, nonce);
        var rate = 16 * degree;

        foreach (var block in SplitZeroPadded(associatedData, rate))
            Update256(v, degree, block);

        var ciphertext = new byte[message.Length];
        var position = 0;
        foreach (var block in SplitZeroPadded(message, rate))
        {
            var keystream = Keystream256(v, degree);
            var output = Xor(block, keystream);
            Update256(v, degree, block);
            var take = Math.Min(rate, message.Length - position);
            Array.Copy(output, 0, ciphertext, position, take);
            position += take;
        }

        return (ciphertext, Finalize256(v, degree, (ulong)associatedData.Length * 8, (ulong)message.Length * 8));
    }

    /// <summary>AEGIS-256X decryption at the given parallelism degree.</summary>
    internal static (byte[] Plaintext, byte[] Tag) Decrypt256(
        int degree,
        byte[] key,
        byte[] nonce,
        byte[] associatedData,
        byte[] ciphertext)
    {
        var v = Initialize256(degree, key, nonce);
        var rate = 16 * degree;

        foreach (var block in SplitZeroPadded(associatedData, rate))
            Update256(v, degree, block);

        var plaintext = new byte[ciphertext.Length];
        var position = 0;
        while (position < ciphertext.Length)
        {
            var take = Math.Min(rate, ciphertext.Length - position);
            var block = new byte[rate];
            Array.Copy(ciphertext, position, block, 0, take);

            var keystream = Keystream256(v, degree);
            var output = Xor(block, keystream);
            Array.Copy(output, 0, plaintext, position, take);

            var absorbed = new byte[rate];
            Array.Copy(output, 0, absorbed, 0, take);
            Update256(v, degree, absorbed);
            position += take;
        }

        return (plaintext, Finalize256(v, degree, (ulong)associatedData.Length * 8, (ulong)ciphertext.Length * 8));
    }

    private static byte[][][] Initialize128(int degree, byte[] key, byte[] nonce)
    {
        var v = new byte[8][][];
        for (var j = 0; j < 8; j++)
            v[j] = new byte[degree][];

        for (var i = 0; i < degree; i++)
        {
            v[0][i] = Xor(key, nonce);
            v[1][i] = (byte[])C1.Clone();
            v[2][i] = (byte[])C0.Clone();
            v[3][i] = (byte[])C1.Clone();
            v[4][i] = Xor(key, nonce);
            v[5][i] = Xor(key, C0);
            v[6][i] = Xor(key, C1);
            v[7][i] = Xor(key, C0);
        }

        var nonceLanes = Repeat(nonce, degree);
        var keyLanes = Repeat(key, degree);
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < degree; i++)
            {
                var ctx = Context(i, degree);
                v[3][i] = Xor(v[3][i], ctx);
                v[7][i] = Xor(v[7][i], ctx);
            }

            Update128(v, degree, nonceLanes, keyLanes);
        }

        return v;
    }

    private static void Update128(byte[][][] v, int degree, byte[] message0, byte[] message1)
    {
        for (var i = 0; i < degree; i++)
        {
            var m0 = message0[(i * 16)..((i * 16) + 16)];
            var m1 = message1[(i * 16)..((i * 16) + 16)];
            var s = new byte[8][];
            for (var j = 0; j < 8; j++)
                s[j] = v[j][i];

            var next = new byte[8][];
            next[0] = AesRound(s[7], Xor(s[0], m0));
            next[1] = AesRound(s[0], s[1]);
            next[2] = AesRound(s[1], s[2]);
            next[3] = AesRound(s[2], s[3]);
            next[4] = AesRound(s[3], Xor(s[4], m1));
            next[5] = AesRound(s[4], s[5]);
            next[6] = AesRound(s[5], s[6]);
            next[7] = AesRound(s[6], s[7]);
            for (var j = 0; j < 8; j++)
                v[j][i] = next[j];
        }
    }

    private static byte[] Keystream128(byte[][][] v, int degree)
    {
        var rate = 32 * degree;
        var keystream = new byte[rate];
        for (var i = 0; i < degree; i++)
        {
            var z0 = Xor(Xor(v[6][i], v[1][i]), And(v[2][i], v[3][i]));
            var z1 = Xor(Xor(v[2][i], v[5][i]), And(v[6][i], v[7][i]));
            Array.Copy(z0, 0, keystream, i * 16, 16);
            Array.Copy(z1, 0, keystream, (16 * degree) + (i * 16), 16);
        }

        return keystream;
    }

    private static byte[] Finalize128(byte[][][] v, int degree, ulong adBits, ulong msgBits)
    {
        var u = LengthBlock(adBits, msgBits);
        var t = new byte[16 * degree];
        for (var i = 0; i < degree; i++)
            Array.Copy(Xor(v[2][i], u), 0, t, i * 16, 16);

        for (var round = 0; round < 7; round++)
            Update128(v, degree, t, t);

        var tag = new byte[16];
        for (var i = 0; i < degree; i++)
        {
            var laneTag = (byte[])v[0][i].Clone();
            for (var j = 1; j < 7; j++)
                laneTag = Xor(laneTag, v[j][i]);
            tag = Xor(tag, laneTag);
        }

        return tag;
    }

    private static byte[][][] Initialize256(int degree, byte[] key, byte[] nonce)
    {
        var k0 = key[..16];
        var k1 = key[16..32];
        var n0 = nonce[..16];
        var n1 = nonce[16..32];

        var v = new byte[6][][];
        for (var j = 0; j < 6; j++)
            v[j] = new byte[degree][];

        for (var i = 0; i < degree; i++)
        {
            v[0][i] = Xor(k0, n0);
            v[1][i] = Xor(k1, n1);
            v[2][i] = (byte[])C1.Clone();
            v[3][i] = (byte[])C0.Clone();
            v[4][i] = Xor(k0, C0);
            v[5][i] = Xor(k1, C1);
        }

        var k0Lanes = Repeat(k0, degree);
        var k1Lanes = Repeat(k1, degree);
        var k0n0Lanes = Repeat(Xor(k0, n0), degree);
        var k1n1Lanes = Repeat(Xor(k1, n1), degree);

        for (var round = 0; round < 4; round++)
        {
            foreach (var lanes in new[] { k0Lanes, k1Lanes, k0n0Lanes, k1n1Lanes })
            {
                for (var i = 0; i < degree; i++)
                {
                    var ctx = Context(i, degree);
                    v[3][i] = Xor(v[3][i], ctx);
                    v[5][i] = Xor(v[5][i], ctx);
                }

                Update256(v, degree, lanes);
            }
        }

        return v;
    }

    private static void Update256(byte[][][] v, int degree, byte[] message)
    {
        for (var i = 0; i < degree; i++)
        {
            var m = message[(i * 16)..((i * 16) + 16)];
            var s = new byte[6][];
            for (var j = 0; j < 6; j++)
                s[j] = v[j][i];

            var next = new byte[6][];
            next[0] = AesRound(s[5], Xor(s[0], m));
            next[1] = AesRound(s[0], s[1]);
            next[2] = AesRound(s[1], s[2]);
            next[3] = AesRound(s[2], s[3]);
            next[4] = AesRound(s[3], s[4]);
            next[5] = AesRound(s[4], s[5]);
            for (var j = 0; j < 6; j++)
                v[j][i] = next[j];
        }
    }

    private static byte[] Keystream256(byte[][][] v, int degree)
    {
        var keystream = new byte[16 * degree];
        for (var i = 0; i < degree; i++)
        {
            var z = Xor(Xor(Xor(v[1][i], v[4][i]), v[5][i]), And(v[2][i], v[3][i]));
            Array.Copy(z, 0, keystream, i * 16, 16);
        }

        return keystream;
    }

    private static byte[] Finalize256(byte[][][] v, int degree, ulong adBits, ulong msgBits)
    {
        var u = LengthBlock(adBits, msgBits);
        var t = new byte[16 * degree];
        for (var i = 0; i < degree; i++)
            Array.Copy(Xor(v[3][i], u), 0, t, i * 16, 16);

        for (var round = 0; round < 7; round++)
            Update256(v, degree, t);

        var tag = new byte[16];
        for (var i = 0; i < degree; i++)
        {
            var laneTag = (byte[])v[0][i].Clone();
            for (var j = 1; j < 6; j++)
                laneTag = Xor(laneTag, v[j][i]);
            tag = Xor(tag, laneTag);
        }

        return tag;
    }

    /// <summary>Textbook AES round: SubBytes, ShiftRows, MixColumns, AddRoundKey.</summary>
    private static byte[] AesRound(byte[] state, byte[] roundKey)
    {
        var substituted = new byte[16];
        for (var i = 0; i < 16; i++)
            substituted[i] = SBox[state[i]];

        // The AES state is column-major: byte i is row (i % 4), column (i / 4).
        var shifted = new byte[16];
        for (var column = 0; column < 4; column++)
        {
            for (var row = 0; row < 4; row++)
                shifted[(column * 4) + row] = substituted[(((column + row) % 4) * 4) + row];
        }

        var mixed = new byte[16];
        for (var column = 0; column < 4; column++)
        {
            var a0 = shifted[(column * 4) + 0];
            var a1 = shifted[(column * 4) + 1];
            var a2 = shifted[(column * 4) + 2];
            var a3 = shifted[(column * 4) + 3];
            mixed[(column * 4) + 0] = (byte)(XTime(a0) ^ XTime(a1) ^ a1 ^ a2 ^ a3);
            mixed[(column * 4) + 1] = (byte)(a0 ^ XTime(a1) ^ XTime(a2) ^ a2 ^ a3);
            mixed[(column * 4) + 2] = (byte)(a0 ^ a1 ^ XTime(a2) ^ XTime(a3) ^ a3);
            mixed[(column * 4) + 3] = (byte)(XTime(a0) ^ a0 ^ a1 ^ a2 ^ XTime(a3));
        }

        return Xor(mixed, roundKey);
    }

    private static byte XTime(byte value)
        => (byte)((value << 1) ^ ((value & 0x80) != 0 ? 0x1B : 0x00));

    private static byte[] Context(int lane, int degree)
    {
        var ctx = new byte[16];
        ctx[0] = (byte)lane;
        ctx[1] = (byte)(degree - 1);
        return ctx;
    }

    private static byte[] Repeat(byte[] block, int degree)
    {
        var lanes = new byte[block.Length * degree];
        for (var i = 0; i < degree; i++)
            Array.Copy(block, 0, lanes, i * block.Length, block.Length);
        return lanes;
    }

    private static byte[] LengthBlock(ulong adBits, ulong msgBits)
    {
        var u = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(u, adBits);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(u.AsSpan(8), msgBits);
        return u;
    }

    private static IEnumerable<byte[]> SplitZeroPadded(byte[] data, int rate)
    {
        for (var position = 0; position < data.Length; position += rate)
        {
            var block = new byte[rate];
            Array.Copy(data, position, block, 0, Math.Min(rate, data.Length - position));
            yield return block;
        }
    }

    private static byte[] Xor(byte[] left, byte[] right)
    {
        var result = new byte[left.Length];
        for (var i = 0; i < left.Length; i++)
            result[i] = (byte)(left[i] ^ right[i]);
        return result;
    }

    private static byte[] And(byte[] left, byte[] right)
    {
        var result = new byte[left.Length];
        for (var i = 0; i < left.Length; i++)
            result[i] = (byte)(left[i] & right[i]);
        return result;
    }
}
