using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using ArmAes = System.Runtime.Intrinsics.Arm.Aes;
using X86Aes = System.Runtime.Intrinsics.X86.Aes;

namespace Ahtola.Core.Storage.Crypto;

/// <summary>
/// The single AES encryption round <c>AESRound(in, rk)</c> that every AEGIS
/// variant is built from: <c>SubBytes</c>, <c>ShiftRows</c>, <c>MixColumns</c>,
/// then <c>AddRoundKey</c> (FIPS-197 section 5.1).
/// </summary>
/// <remarks>
/// <para>
/// Three implementations are selected inside the method body so the JIT and ILC
/// can constant-fold the <c>IsSupported</c> probes while NativeAOT still keeps a
/// working fallback branch for targets without AES instructions (notably
/// browser-wasm):
/// </para>
/// <list type="bullet">
///   <item><description>x86/x64 <c>AESENC</c>, which is
///   <c>ShiftRows -&gt; SubBytes -&gt; MixColumns -&gt; AddRoundKey</c>. <c>SubBytes</c> is
///   byte-wise so it commutes with <c>ShiftRows</c>, making <c>AESENC</c> exactly
///   <c>AESRound</c>.</description></item>
///   <item><description>Arm <c>AESE</c> + <c>AESMC</c>. <c>AESE</c> applies
///   <c>AddRoundKey</c> <em>first</em>, so the round key must be supplied as zero
///   and XOR-ed in after <c>AESMC</c>.</description></item>
///   <item><description>A table-free bitsliced software round. Table-driven AES
///   would turn the machines without AES instructions into a cache-timing oracle,
///   so the fallback uses the Boyar-Peralta combinational S-box and branch-free
///   bit permutations. No array is ever indexed by a secret value.</description></item>
/// </list>
/// </remarks>
internal static class AhtolaAesRound
{
    /// <summary>Whether a dedicated AES round instruction backs <see cref="Encrypt"/>.</summary>
    internal static bool IsHardwareAccelerated => X86Aes.IsSupported || ArmAes.IsSupported;

    /// <summary>Applies one AES round to <paramref name="state"/> with <paramref name="roundKey"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Encrypt(Vector128<byte> state, Vector128<byte> roundKey)
    {
        if (X86Aes.IsSupported)
            return X86Aes.Encrypt(state, roundKey);

        if (ArmAes.IsSupported)
        {
            // AESE(x, 0) = ShiftRows(SubBytes(x)); AESMC then applies MixColumns.
            var substituted = ArmAes.Encrypt(state, Vector128<byte>.Zero);
            return Vector128.Xor(ArmAes.MixColumns(substituted), roundKey);
        }

        return EncryptSoftware(state, roundKey);
    }

    /// <summary>
    /// The constant-time software round. Exposed so the suite can prove the
    /// fallback matches the hardware path on machines that have AES instructions.
    /// </summary>
    internal static Vector128<byte> EncryptSoftware(Vector128<byte> state, Vector128<byte> roundKey)
    {
        Span<byte> bytes = stackalloc byte[16];
        Span<uint> planes = stackalloc uint[8];
        try
        {
            state.CopyTo(bytes);
            Orthogonalize(bytes, planes);
            SubBytes(planes);
            ShiftRows(planes);
            MixColumns(planes);
            Deorthogonalize(planes, bytes);
            return Vector128.Xor(Vector128.Create<byte>(bytes), roundKey);
        }
        finally
        {
            bytes.Clear();
            planes.Clear();
        }
    }

    /// <summary>
    /// Splits 16 state bytes into eight 16-bit planes where bit <c>i</c> of
    /// <c>planes[k]</c> is bit <c>k</c> of state byte <c>i</c>.
    /// </summary>
    private static void Orthogonalize(ReadOnlySpan<byte> bytes, Span<uint> planes)
    {
        // Explicit little-endian reads keep byte i at bit offset 8*i on every host.
        var low = Transpose8(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        var high = Transpose8(BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
        for (var k = 0; k < 8; k++)
        {
            var shift = k * 8;
            planes[k] = (uint)(((low >> shift) & 0xFF) | (((high >> shift) & 0xFF) << 8));
        }
    }

    /// <summary>Inverse of <see cref="Orthogonalize"/>.</summary>
    private static void Deorthogonalize(ReadOnlySpan<uint> planes, Span<byte> bytes)
    {
        ulong low = 0;
        ulong high = 0;
        for (var k = 0; k < 8; k++)
        {
            var shift = k * 8;
            low |= (ulong)(planes[k] & 0xFF) << shift;
            high |= (ulong)((planes[k] >> 8) & 0xFF) << shift;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Transpose8(low));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], Transpose8(high));
    }

    /// <summary>
    /// Transposes the 8x8 bit matrix held in <paramref name="value"/>, swapping
    /// bit <c>8i+j</c> with bit <c>8j+i</c>. Self-inverse.
    /// </summary>
    internal static ulong Transpose8(ulong value)
    {
        var t = (value ^ (value >> 7)) & 0x00AA00AA00AA00AAUL;
        value ^= t ^ (t << 7);
        t = (value ^ (value >> 14)) & 0x0000CCCC0000CCCCUL;
        value ^= t ^ (t << 14);
        t = (value ^ (value >> 28)) & 0x00000000F0F0F0F0UL;
        value ^= t ^ (t << 28);
        return value;
    }

    /// <summary>
    /// The AES S-box as the Boyar-Peralta combinational circuit ("A new
    /// combinational logic minimization technique with applications to
    /// cryptology", eprint 2009/191). Pure XOR/AND/NOT on whole planes, so the
    /// running time and memory access pattern are independent of the data.
    /// </summary>
    private static void SubBytes(Span<uint> q)
    {
        // x0 is the most significant bit of each byte, x7 the least.
        var x0 = q[7];
        var x1 = q[6];
        var x2 = q[5];
        var x3 = q[4];
        var x4 = q[3];
        var x5 = q[2];
        var x6 = q[1];
        var x7 = q[0];

        // Top linear transformation.
        var y14 = x3 ^ x5;
        var y13 = x0 ^ x6;
        var y9 = x0 ^ x3;
        var y8 = x0 ^ x5;
        var t0 = x1 ^ x2;
        var y1 = t0 ^ x7;
        var y4 = y1 ^ x3;
        var y12 = y13 ^ y14;
        var y2 = y1 ^ x0;
        var y5 = y1 ^ x6;
        var y3 = y5 ^ y8;
        var t1 = x4 ^ y12;
        var y15 = t1 ^ x5;
        var y20 = t1 ^ x1;
        var y6 = y15 ^ x7;
        var y10 = y15 ^ t0;
        var y11 = y20 ^ y9;
        var y7 = x7 ^ y11;
        var y17 = y10 ^ y11;
        var y19 = y10 ^ y8;
        var y16 = t0 ^ y11;
        var y21 = y13 ^ y16;
        var y18 = x0 ^ y16;

        // Non-linear section.
        var t2 = y12 & y15;
        var t3 = y3 & y6;
        var t4 = t3 ^ t2;
        var t5 = y4 & x7;
        var t6 = t5 ^ t2;
        var t7 = y13 & y16;
        var t8 = y5 & y1;
        var t9 = t8 ^ t7;
        var t10 = y2 & y7;
        var t11 = t10 ^ t7;
        var t12 = y9 & y11;
        var t13 = y14 & y17;
        var t14 = t13 ^ t12;
        var t15 = y8 & y10;
        var t16 = t15 ^ t12;
        var t17 = t4 ^ t14;
        var t18 = t6 ^ t16;
        var t19 = t9 ^ t14;
        var t20 = t11 ^ t16;
        var t21 = t17 ^ y20;
        var t22 = t18 ^ y19;
        var t23 = t19 ^ y21;
        var t24 = t20 ^ y18;

        var t25 = t21 ^ t22;
        var t26 = t21 & t23;
        var t27 = t24 ^ t26;
        var t28 = t25 & t27;
        var t29 = t28 ^ t22;
        var t30 = t23 ^ t24;
        var t31 = t22 ^ t26;
        var t32 = t31 & t30;
        var t33 = t32 ^ t24;
        var t34 = t23 ^ t33;
        var t35 = t27 ^ t33;
        var t36 = t24 & t35;
        var t37 = t36 ^ t34;
        var t38 = t27 ^ t36;
        var t39 = t29 & t38;
        var t40 = t25 ^ t39;

        var t41 = t40 ^ t37;
        var t42 = t29 ^ t33;
        var t43 = t29 ^ t40;
        var t44 = t33 ^ t37;
        var t45 = t42 ^ t41;
        var z0 = t44 & y15;
        var z1 = t37 & y6;
        var z2 = t33 & x7;
        var z3 = t43 & y16;
        var z4 = t40 & y1;
        var z5 = t29 & y7;
        var z6 = t42 & y11;
        var z7 = t45 & y17;
        var z8 = t41 & y10;
        var z9 = t44 & y12;
        var z10 = t37 & y3;
        var z11 = t33 & y4;
        var z12 = t43 & y13;
        var z13 = t40 & y5;
        var z14 = t29 & y2;
        var z15 = t42 & y9;
        var z16 = t45 & y14;
        var z17 = t41 & y8;

        // Bottom linear transformation.
        var t46 = z15 ^ z16;
        var t47 = z10 ^ z11;
        var t48 = z5 ^ z13;
        var t49 = z9 ^ z10;
        var t50 = z2 ^ z12;
        var t51 = z2 ^ z5;
        var t52 = z7 ^ z8;
        var t53 = z0 ^ z3;
        var t54 = z6 ^ z7;
        var t55 = z16 ^ z17;
        var t56 = z12 ^ t48;
        var t57 = t50 ^ t53;
        var t58 = z4 ^ t46;
        var t59 = z3 ^ t54;
        var t60 = t46 ^ t57;
        var t61 = z14 ^ t57;
        var t62 = t52 ^ t58;
        var t63 = t49 ^ t58;
        var t64 = z4 ^ t59;
        var t65 = t61 ^ t62;
        var t66 = z1 ^ t63;
        var t67 = t64 ^ t65;

        const uint Ones = 0xFFFFu;
        var s0 = t59 ^ t63;
        var s6 = t56 ^ t62 ^ Ones;
        var s7 = t48 ^ t60 ^ Ones;
        var s3 = t53 ^ t66;
        var s4 = t51 ^ t66;
        var s5 = t47 ^ t65;
        var s1 = t64 ^ s3 ^ Ones;
        var s2 = t55 ^ t67 ^ Ones;

        q[7] = s0;
        q[6] = s1;
        q[5] = s2;
        q[4] = s3;
        q[3] = s4;
        q[2] = s5;
        q[1] = s6;
        q[0] = s7;
    }

    /// <summary>
    /// <c>ShiftRows</c> as a fixed bit permutation. Byte <c>i</c> of the AES state
    /// is row <c>i mod 4</c>, column <c>i / 4</c>, so row <c>r</c> is a rotation by
    /// <c>4r</c> positions inside each 16-bit plane.
    /// </summary>
    private static void ShiftRows(Span<uint> q)
    {
        for (var k = 0; k < 8; k++)
        {
            var x = q[k];
            q[k] = (x & 0x1111u)
                   | (RotateRight16(x, 4) & 0x2222u)
                   | (RotateRight16(x, 8) & 0x4444u)
                   | (RotateRight16(x, 12) & 0x8888u);
        }
    }

    /// <summary>
    /// <c>MixColumns</c>. Using <c>b_r = xtime(a_r ^ a_(r+1)) ^ a_(r+1) ^ a_(r+2) ^ a_(r+3)</c>
    /// (indices modulo 4 inside a column) makes every row share one formula, so no
    /// row-dependent branch is required.
    /// </summary>
    private static void MixColumns(Span<uint> q)
    {
        Span<uint> rotated1 = stackalloc uint[8];
        Span<uint> rotated2 = stackalloc uint[8];
        Span<uint> rotated3 = stackalloc uint[8];
        Span<uint> sum = stackalloc uint[8];
        Span<uint> doubled = stackalloc uint[8];
        try
        {
            for (var k = 0; k < 8; k++)
            {
                var x = q[k];
                rotated1[k] = ((x >> 1) & 0x7777u) | ((x << 3) & 0x8888u);
                rotated2[k] = ((x >> 2) & 0x3333u) | ((x << 2) & 0xCCCCu);
                rotated3[k] = ((x >> 3) & 0x1111u) | ((x << 1) & 0xEEEEu);
                sum[k] = x ^ rotated1[k];
            }

            // xtime over the plane representation: the carry is the top plane and
            // the reduction polynomial 0x1B touches bits 0, 1, 3 and 4.
            var carry = sum[7];
            doubled[0] = carry;
            doubled[1] = sum[0] ^ carry;
            doubled[2] = sum[1];
            doubled[3] = sum[2] ^ carry;
            doubled[4] = sum[3] ^ carry;
            doubled[5] = sum[4];
            doubled[6] = sum[5];
            doubled[7] = sum[6];

            for (var k = 0; k < 8; k++)
                q[k] = doubled[k] ^ rotated1[k] ^ rotated2[k] ^ rotated3[k];
        }
        finally
        {
            rotated1.Clear();
            rotated2.Clear();
            rotated3.Clear();
            sum.Clear();
            doubled.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateRight16(uint value, int count)
        => ((value >> count) | (value << (16 - count))) & 0xFFFFu;
}

/// <summary>
/// Selects the AES round implementation an AEGIS instantiation uses. Implemented
/// by empty structs so the constrained call devirtualizes and NativeAOT keeps
/// both variants statically reachable without reflection.
/// </summary>
internal interface IAhtolaAesRoundPolicy
{
    /// <summary>Applies one AES round.</summary>
    static abstract Vector128<byte> Round(Vector128<byte> state, Vector128<byte> roundKey);
}

/// <summary>Uses AES instructions when the running CPU has them.</summary>
internal readonly struct AhtolaAcceleratedAesRound : IAhtolaAesRoundPolicy
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Round(Vector128<byte> state, Vector128<byte> roundKey)
        => AhtolaAesRound.Encrypt(state, roundKey);
}

/// <summary>Always uses the bitsliced software round.</summary>
internal readonly struct AhtolaSoftwareAesRound : IAhtolaAesRoundPolicy
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Round(Vector128<byte> state, Vector128<byte> roundKey)
        => AhtolaAesRound.EncryptSoftware(state, roundKey);
}
