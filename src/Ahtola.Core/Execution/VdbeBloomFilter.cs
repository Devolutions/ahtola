using System.Text;

namespace Ahtola.Core.Execution;

/// <summary>
/// A pure-managed bloom filter for fast probabilistic set membership testing, mirroring
/// Turso's <c>vdbe::bloom_filter::BloomFilter</c>. One filter is attached per cursor that
/// builds a hash table or ephemeral index: the build side adds keys
/// (<see cref="VdbeOpcode.BloomFilterAdd"/>) and the probe side asks
/// "is this key definitely absent?" (<see cref="VdbeOpcode.BloomFilter"/>), so a negative
/// answer is always correct while a positive one permits false positives.
/// </summary>
/// <remarks>
/// <para>
/// The hash function itself does not need to match Turso's rapidhash: only within-engine
/// consistency matters. Key equivalence, however, must mirror Turso exactly — numerically
/// equal values (10 and 10.0, 0.0 and -0.0) hash into the same domain so membership can
/// never return a false negative for them, while integers too large for exact f64
/// representation fall back to the integer domain (tag 1) so they cannot collide with
/// their lossy double image.
/// </para>
/// <para>
/// NULL semantics follow Turso: a single NULL key increments the count but is never hashed
/// into the bit array, <c>Contains</c> on NULL always returns false, composite keys hash
/// NULL components as a no-op on insert, and a composite probe containing any NULL
/// component can never match.
/// </para>
/// <para>
/// Allocation-free on the probe path except for text UTF-8 encoding; no reflection, no
/// codegen — NativeAOT and trim safe.
/// </para>
/// </remarks>
internal sealed class VdbeBloomFilter
{
    /// <summary>Default number of expected items (Turso <c>DEFAULT_EXPECTED_ITEMS</c>).</summary>
    private const int DefaultExpectedItems = 1024;

    /// <summary>Default false positive rate, 1% (Turso <c>DEFAULT_FALSE_POSITIVE_RATE</c>).</summary>
    private const double DefaultFalsePositiveRate = 0.01;

    // FNV-1a 64 constants (same precedent as ManagedVectorIndexState.Fingerprint).
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325UL;
    private const ulong FnvPrime = 0x100000001B3UL;

    // Value-domain tags, mirroring Turso's hash_value discriminators.
    private const byte LargeIntegerTag = 1;
    private const byte NumericTag = 2;
    private const byte TextTag = 3;
    private const byte BlobTag = 4;

    private readonly ulong[] _bits;
    private readonly int _bitMask;
    private readonly int _probeCount;
    private int _count;

    public VdbeBloomFilter()
        : this(DefaultExpectedItems, DefaultFalsePositiveRate)
    {
    }

    public VdbeBloomFilter(int expectedItems, double falsePositiveRate)
    {
        if (expectedItems < 1)
        {
            expectedItems = 1;
        }

        // Optimal bit-array size m = -(n * ln p) / (ln 2)^2, rounded up to a power of two so
        // index reduction is a mask. For n = 1024, p = 0.01 this is ~9815 bits -> 16384.
        var optimalBits = -(expectedItems * Math.Log(falsePositiveRate)) / (Math.Log(2) * Math.Log(2));
        var bitCount = 64;
        while (bitCount < optimalBits)
        {
            bitCount <<= 1;
        }

        _bits = new ulong[bitCount / 64];
        _bitMask = bitCount - 1;

        // Optimal probe count k = (m / n) * ln 2, at least one. For 16384 bits / 1024 items
        // this is 11.09 -> 11 probes.
        _probeCount = Math.Max(1, (int)Math.Round((double)bitCount / expectedItems * Math.Log(2)));
    }

    /// <summary>Number of items inserted into the filter (Turso <c>count</c>).</summary>
    public int Count => _count;

    /// <summary>Inserts a single key value (Turso <c>insert_value</c>). NULL counts but is not hashed.</summary>
    public void InsertValue(SqlValue value)
    {
        if (value.Kind != SqlValueKind.Null)
        {
            var hasher = new FnvHasher();
            HashValue(ref hasher, value);
            SetProbes(hasher.Finish());
        }

        _count++;
    }

    /// <summary>
    /// Inserts a composite key (Turso <c>insert_values</c>): all components are folded into a
    /// single hash. NULL components contribute nothing to the hash but the key still counts.
    /// </summary>
    public void InsertValues(IReadOnlyList<SqlValue> values)
    {
        var hasher = new FnvHasher();
        for (var i = 0; i < values.Count; i++)
        {
            HashValue(ref hasher, values[i]);
        }

        SetProbes(hasher.Finish());
        _count++;
    }

    /// <summary>Checks membership of a single key (Turso <c>contains_value</c>). NULL never matches.</summary>
    public bool ContainsValue(SqlValue value)
    {
        if (value.Kind == SqlValueKind.Null)
        {
            return false;
        }

        var hasher = new FnvHasher();
        HashValue(ref hasher, value);
        return TestProbes(hasher.Finish());
    }

    /// <summary>
    /// Checks membership of a composite key (Turso <c>contains_values</c>). If any component
    /// is NULL the key can never match.
    /// </summary>
    public bool ContainsValues(IReadOnlyList<SqlValue> values)
    {
        var hasher = new FnvHasher();
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (value.Kind == SqlValueKind.Null)
            {
                return false;
            }

            HashValue(ref hasher, value);
        }

        return TestProbes(hasher.Finish());
    }

    /// <summary>Resets the filter (Turso <c>clear</c>): forgets every inserted key.</summary>
    public void Clear()
    {
        Array.Clear(_bits, 0, _bits.Length);
        _count = 0;
    }

    /// <summary>
    /// Hashes one value into <paramref name="hasher"/> mirroring Turso's <c>hash_value</c>.
    /// NULL is a no-op so NULL components of composite keys do not perturb the hash of the
    /// remaining components.
    /// </summary>
    private static void HashValue(ref FnvHasher hasher, SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                break;
            case SqlValueKind.Integer:
                {
                    var integer = value.AsInteger();
                    // Hash integers in the same domain as numerically equivalent REALs so
                    // membership can never return a false negative for e.g. 10 vs 10.0.
                    var asReal = (double)integer;
                    if (double.IsFinite(asReal) && (long)asReal == integer)
                    {
                        HashNumeric(ref hasher, asReal);
                    }
                    else
                    {
                        // Fall back to the integer domain when the float representation would
                        // lose precision (|i| > 2^53).
                        hasher.WriteByte(LargeIntegerTag);
                        hasher.WriteUInt64(unchecked((ulong)integer));
                    }

                    break;
                }
            case SqlValueKind.Real:
                HashNumeric(ref hasher, value.AsReal());
                break;
            case SqlValueKind.Text:
                hasher.WriteByte(TextTag);
                hasher.WriteBytes(Encoding.UTF8.GetBytes(value.AsText()));
                break;
            case SqlValueKind.Blob:
                hasher.WriteByte(BlobTag);
                hasher.WriteBytes(value.AsBlobSpan());
                break;
        }
    }

    /// <summary>
    /// Hashes INTEGER and REAL values into the same domain (Turso <c>hash_numeric</c>) so
    /// numerically equal values collide by design (10 == 10.0, -0.0 == 0.0).
    /// </summary>
    private static void HashNumeric(ref FnvHasher hasher, double value)
    {
        hasher.WriteByte(NumericTag);
        hasher.WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(NormalizeSignedZero(value))));
    }

    /// <summary>Normalizes signed zero so 0.0 and -0.0 hash the same (Turso <c>normalized_f64_bits</c>).</summary>
    private static double NormalizeSignedZero(double value)
        => value == 0.0 ? 0.0 : value;

    private void SetProbes(ulong hash)
    {
        ForEachProbe(hash, static (self, bit) => self._bits[bit >> 6] |= 1UL << (bit & 63));
    }

    private bool TestProbes(ulong hash)
    {
        var first = (int)(hash & (ulong)_bitMask);
        // Force an odd step so the probe sequence is coprime with the power-of-two bit count
        // and covers the whole table instead of half of it.
        var step = (int)((hash >> 32) | 1UL);
        var bit = first;
        for (var probe = 0; probe < _probeCount; probe++)
        {
            var index = bit & _bitMask;
            if ((_bits[index >> 6] & (1UL << (index & 63))) == 0)
            {
                return false;
            }

            bit += step;
        }

        return true;
    }

    private void ForEachProbe(ulong hash, Action<VdbeBloomFilter, int> visit)
    {
        var first = (int)(hash & (ulong)_bitMask);
        var step = (int)((hash >> 32) | 1UL);
        var bit = first;
        for (var probe = 0; probe < _probeCount; probe++)
        {
            visit(this, bit & _bitMask);
            bit += step;
        }
    }

    /// <summary>Incremental FNV-1a 64-bit hasher; deterministic and endian independent.</summary>
    private struct FnvHasher
    {
        private ulong _hash;

        public FnvHasher()
        {
            _hash = FnvOffsetBasis;
        }

        public void WriteByte(byte value)
        {
            _hash ^= value;
            _hash *= FnvPrime;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                WriteByte(value);
            }
        }

        public void WriteUInt64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public readonly ulong Finish() => _hash;
    }
}
