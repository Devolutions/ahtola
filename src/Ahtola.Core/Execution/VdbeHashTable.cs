using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Execution;

/// <summary>
/// Lifecycle state of a hash-join hash table, mirroring Turso's <c>HashTableState</c>
/// without the spill states (the managed port is in-memory only).
/// </summary>
public enum VdbeHashTableState
{
    Building,
    Probing,
    Closed,
}

/// <summary>
/// A single entry in a <see cref="VdbeHashTable"/>, mirroring Turso's <c>HashEntry</c>:
/// the precomputed key hash, the key values, the source rowid, and any payload columns.
/// </summary>
public sealed class VdbeHashEntry
{
    internal VdbeHashEntry(ulong hash, SqlValue[] keys, long rowId, SqlValue[] payload)
    {
        Hash = hash;
        Keys = keys;
        RowId = rowId;
        Payload = payload;
    }

    public ulong Hash { get; }

    public SqlValue[] Keys { get; }

    public long RowId { get; }

    public SqlValue[] Payload { get; }
}

/// <summary>
/// A pure-managed hash table for VDBE hash joins, mirroring Turso's
/// <c>vdbe::hash_table::HashTable</c> for the in-memory (non-spilled) paths. The build side
/// inserts rows (<see cref="VdbeOpcode.HashBuild"/>), the table is finalized
/// (<see cref="VdbeOpcode.HashBuildFinalize"/>), and the probe side iterates matches
/// (<see cref="VdbeOpcode.HashProbe"/>/<see cref="VdbeOpcode.HashNext"/>). Anti-joins
/// additionally track which entries matched (<see cref="VdbeOpcode.HashMarkMatched"/>) and
/// scan the unmatched remainder (<see cref="VdbeOpcode.HashScanUnmatched"/>/
/// <see cref="VdbeOpcode.HashNextUnmatched"/>).
/// </summary>
/// <remarks>
/// <para>
/// The hash function itself does not need to match Turso's rapidhash: only within-engine
/// consistency matters. Key equivalence, however, must mirror Turso exactly — numerically
/// equal values (10 and 10.0, 0.0 and -0.0) hash into the same float domain so a probe can
/// never miss a match, while integers too large for exact f64 representation fall back to
/// the integer domain (tag 1) so they cannot collide with their lossy double image.
/// </para>
/// <para>
/// Join equality never treats NULL as equal to anything, so <see cref="Probe"/> with a key
/// containing NULL always reports no match (mirroring Turso's early return). Distinct
/// insertion (<see cref="InsertDistinct"/>) instead treats NULL as equal to NULL so it
/// deduplicates. No reflection, no codegen — NativeAOT and trim safe.
/// </para>
/// <para>
/// Turso's grace-hash-join opcodes (<c>HashGraceInit</c>, <c>HashGraceLoadPartition</c>,
/// <c>HashGraceNextProbe</c>, <c>HashGraceAdvancePartition</c>) are deliberately not ported:
/// they exist solely to spill oversized build sides to disk partitions and re-probe them,
/// and the managed port keeps hash joins fully in memory. Port them only if a
/// memory-budget spill requirement appears.
/// </para>
/// </remarks>
public sealed class VdbeHashTable
{
    /// <summary>Default bucket count (Turso <c>DEFAULT_BUCKETS</c>).</summary>
    public const int DefaultBucketCount = 1024;

    // Value-domain tags, mirroring Turso's hash_join_key discriminators.
    private const byte NullTag = 0;
    private const byte IntegerTag = 1;
    private const byte FloatTag = 2;
    private const byte TextTag = 3;
    private const byte BlobTag = 4;

    // FNV-1a 64 constants (same precedent as VdbeBloomFilter).
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325UL;
    private const ulong FnvPrime = 0x100000001B3UL;

    private readonly List<VdbeHashEntry>[] _buckets;
    private readonly List<bool>[]? _matchedBits;
    private readonly string?[] _collations;
    private readonly bool _trackMatched;

    private SqlValue[]? _currentProbeKeys;
    private ulong? _currentProbeHash;
    private int _probeEntryIndex;
    private int _probeBucketIndex;
    private int _unmatchedScanBucket;
    private int _unmatchedScanEntry;
    private long _entryCount;

    public VdbeHashTable(
        int keyCount,
        IReadOnlyList<string?> collations,
        bool trackMatched,
        long memoryBudget)
    {
        if (keyCount < 1)
            throw new ArgumentOutOfRangeException(nameof(keyCount));
        if (collations is null)
            throw new ArgumentNullException(nameof(collations));
        if (collations.Count != keyCount)
            throw new ArgumentException(
                $"Hash table expects {keyCount} collations but received {collations.Count}.",
                nameof(collations));

        var retainedCollations = new string?[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            if (!SqliteIndexRecordComparer.IsSupportedCollation(collations[i]))
                throw new ArgumentException($"Hash table does not support collation {collations[i]}.", nameof(collations));

            retainedCollations[i] = collations[i];
        }

        _collations = retainedCollations;
        _trackMatched = trackMatched;
        MemoryBudget = memoryBudget;

        _buckets = new List<VdbeHashEntry>[DefaultBucketCount];
        for (var i = 0; i < _buckets.Length; i++)
            _buckets[i] = [];

        if (trackMatched)
        {
            _matchedBits = new List<bool>[DefaultBucketCount];
            for (var i = 0; i < _matchedBits.Length; i++)
                _matchedBits[i] = [];
        }
    }

    public VdbeHashTableState State { get; private set; } = VdbeHashTableState.Building;

    /// <summary>Number of entries inserted into the table (Turso <c>num_entries</c>).</summary>
    public long EntryCount => _entryCount;

    /// <summary>
    /// Memory budget accepted from <see cref="VdbeOpcode.HashBuild"/>. The managed port never
    /// spills, so the budget is retained for parity only.
    /// </summary>
    public long MemoryBudget { get; }

    /// <summary>
    /// Inserts one build-side row (Turso <c>insert_pending</c>). Rows whose key contains NULL
    /// are silently dropped unless the table tracks matched entries, mirroring Turso's early
    /// return — an anti-join build must retain them so the unmatched scan can emit them.
    /// </summary>
    public void InsertPending(SqlValue[] keys, long rowId, SqlValue[] payload)
    {
        if (State != VdbeHashTableState.Building)
            throw new InvalidOperationException("Hash table can only accept inserts while building.");

        if (HasNullKey(keys) && !_trackMatched)
            return;

        var hash = HashKeys(keys);
        var bucketIndex = BucketIndex(hash);
        _buckets[bucketIndex].Add(new VdbeHashEntry(hash, keys, rowId, payload));
        _matchedBits?[bucketIndex].Add(false);
        _entryCount++;
    }

    /// <summary>
    /// Inserts one key if it is not already present (Turso <c>insert_distinct</c>), returning
    /// whether the key was new. NULL compares equal to NULL here, so NULL keys deduplicate.
    /// </summary>
    public bool InsertDistinct(SqlValue[] keys)
    {
        if (State != VdbeHashTableState.Building)
            throw new InvalidOperationException("Hash table can only accept inserts while building.");

        var hash = HashKeys(keys);
        var bucketIndex = BucketIndex(hash);
        var bucket = _buckets[bucketIndex];
        for (var i = 0; i < bucket.Count; i++)
        {
            var entry = bucket[i];
            if (entry.Hash == hash && KeysEqualDistinct(entry.Keys, keys))
                return false;
        }

        bucket.Add(new VdbeHashEntry(hash, keys, rowId: 0, payload: []));
        _entryCount++;
        return true;
    }

    /// <summary>Completes the build phase (Turso <c>finalize_build</c>): probes become legal.</summary>
    public void FinalizeBuild()
    {
        if (State != VdbeHashTableState.Building)
            throw new InvalidOperationException("Hash table can only be finalized while building.");

        State = VdbeHashTableState.Probing;
    }

    /// <summary>
    /// Looks up the first entry matching <paramref name="keys"/> (Turso <c>probe</c>) and
    /// positions the match iterator just past it so <see cref="NextMatch"/> continues the
    /// chain. A key containing NULL never matches: the keys are remembered (with no hash) and
    /// <see langword="null"/> is returned.
    /// </summary>
    public VdbeHashEntry? Probe(SqlValue[] keys)
    {
        if (State != VdbeHashTableState.Probing)
            throw new InvalidOperationException("Hash table must be finalized before probing.");

        if (HasNullKey(keys))
        {
            _currentProbeKeys = keys;
            _currentProbeHash = null;
            return null;
        }

        var hash = HashKeys(keys);
        _currentProbeKeys = keys;
        _currentProbeHash = hash;
        _probeEntryIndex = 0;
        _probeBucketIndex = BucketIndex(hash);

        var bucket = _buckets[_probeBucketIndex];
        for (var i = 0; i < bucket.Count; i++)
        {
            var entry = bucket[i];
            if (entry.Hash == hash && KeysEqual(entry.Keys, keys))
            {
                _probeEntryIndex = i + 1;
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Continues the match chain started by <see cref="Probe"/> (Turso <c>next_match</c>),
    /// returning the next entry with equal keys or <see langword="null"/> when exhausted.
    /// </summary>
    public VdbeHashEntry? NextMatch()
    {
        if (State != VdbeHashTableState.Probing)
            throw new InvalidOperationException("Hash table must be finalized before probing.");
        if (_currentProbeKeys is null)
            throw new InvalidOperationException("HashNext requires a preceding HashProbe on the same hash table.");

        var hash = _currentProbeHash ?? HashKeys(_currentProbeKeys);
        var bucket = _buckets[_probeBucketIndex];
        for (var i = _probeEntryIndex; i < bucket.Count; i++)
        {
            var entry = bucket[i];
            if (entry.Hash == hash && KeysEqual(entry.Keys, _currentProbeKeys))
            {
                _probeEntryIndex = i + 1;
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Marks the entry most recently returned by <see cref="Probe"/>/<see cref="NextMatch"/>
    /// as matched (Turso <c>mark_current_matched</c>). Tables that do not track matches
    /// ignore the call.
    /// </summary>
    public void MarkCurrentMatched()
    {
        if (!_trackMatched)
            return;

        var entryIndex = _probeEntryIndex - 1;
        var bits = _matchedBits![_probeBucketIndex];
        if (entryIndex < 0 || entryIndex >= bits.Count)
            throw new InvalidOperationException("HashMarkMatched requires a current match from HashProbe or HashNext.");

        bits[entryIndex] = true;
    }

    /// <summary>Clears every matched bit (Turso <c>reset_matched_bits</c>).</summary>
    public void ResetMatchedBits()
    {
        if (_matchedBits is null)
            return;

        for (var i = 0; i < _matchedBits.Length; i++)
        {
            var bits = _matchedBits[i];
            for (var j = 0; j < bits.Count; j++)
                bits[j] = false;
        }
    }

    /// <summary>Restarts the unmatched-entry scan (Turso <c>begin_unmatched_scan</c>).</summary>
    public void BeginUnmatchedScan()
    {
        _unmatchedScanBucket = 0;
        _unmatchedScanEntry = 0;
    }

    /// <summary>
    /// Returns the next entry never marked matched (Turso <c>next_unmatched</c> over the main
    /// buckets), or <see langword="null"/> when the scan is exhausted.
    /// </summary>
    public VdbeHashEntry? NextUnmatched()
    {
        while (_unmatchedScanBucket < _buckets.Length)
        {
            var bucket = _buckets[_unmatchedScanBucket];
            while (_unmatchedScanEntry < bucket.Count)
            {
                var entryIndex = _unmatchedScanEntry;
                _unmatchedScanEntry++;
                if (_matchedBits is null || !_matchedBits[_unmatchedScanBucket][entryIndex])
                    return bucket[entryIndex];
            }

            _unmatchedScanBucket++;
            _unmatchedScanEntry = 0;
        }

        return null;
    }

    /// <summary>
    /// Empties the table and returns it to the building state (Turso <c>clear</c>) so a
    /// correlated subquery can rebuild it for the next outer row.
    /// </summary>
    public void Clear()
    {
        if (_entryCount == 0)
        {
            ResetProbeState();
            State = VdbeHashTableState.Building;
            return;
        }

        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i].Clear();
            _matchedBits?[i].Clear();
        }

        _entryCount = 0;
        ResetProbeState();
        State = VdbeHashTableState.Building;
    }

    /// <summary>Closes the table (Turso <c>close</c>); <see cref="VdbeOpcode.HashClose"/> then drops it.</summary>
    public void Close()
        => State = VdbeHashTableState.Closed;

    private void ResetProbeState()
    {
        _currentProbeKeys = null;
        _currentProbeHash = null;
        _probeEntryIndex = 0;
        _probeBucketIndex = 0;
        _unmatchedScanBucket = 0;
        _unmatchedScanEntry = 0;
    }

    private int BucketIndex(ulong hash)
        => (int)(hash % (ulong)_buckets.Length);

    private static bool HasNullKey(SqlValue[] keys)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].Kind == SqlValueKind.Null)
                return true;
        }

        return false;
    }

    private ulong HashKeys(SqlValue[] keys)
    {
        var hasher = new FnvHasher();
        for (var i = 0; i < keys.Length; i++)
            HashJoinKey(ref hasher, keys[i], _collations[i]);

        return hasher.Finish();
    }

    /// <summary>
    /// Hashes one key component mirroring Turso's <c>hash_join_key</c>. NULL has its own tag;
    /// integers exactly representable as f64 hash in the float domain with their numeric
    /// equivalents; text honours the per-key collation.
    /// </summary>
    private static void HashJoinKey(ref FnvHasher hasher, SqlValue value, string? collation)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                hasher.WriteByte(NullTag);
                break;
            case SqlValueKind.Integer:
                {
                    var integer = value.AsInteger();
                    var asReal = (double)integer;
                    if (double.IsFinite(asReal) && (long)asReal == integer)
                    {
                        HashReal(ref hasher, asReal);
                    }
                    else
                    {
                        // Fall back to the integer domain when the float representation would
                        // lose precision (|i| > 2^53).
                        hasher.WriteByte(IntegerTag);
                        hasher.WriteUInt64(unchecked((ulong)integer));
                    }

                    break;
                }
            case SqlValueKind.Real:
                HashReal(ref hasher, value.AsReal());
                break;
            case SqlValueKind.Text:
                hasher.WriteByte(TextTag);
                HashText(ref hasher, value.AsText(), collation);
                break;
            case SqlValueKind.Blob:
                hasher.WriteByte(BlobTag);
                hasher.WriteBytes(value.AsBlobSpan());
                break;
        }
    }

    /// <summary>Hashes REAL values with signed zero normalized (Turso <c>hash_numeric</c>).</summary>
    private static void HashReal(ref FnvHasher hasher, double value)
    {
        hasher.WriteByte(FloatTag);
        var normalized = value == 0.0 ? 0.0 : value;
        hasher.WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(normalized)));
    }

    /// <summary>
    /// Hashes text per collation mirroring Turso's <c>hash_text</c>: NOCASE writes the byte
    /// length then ASCII-folded bytes, stopping at the first zero byte; RTRIM trims trailing
    /// spaces; BINARY hashes the raw UTF-8 bytes.
    /// </summary>
    private static void HashText(ref FnvHasher hasher, string text, string? collation)
    {
        if (IsNoCase(collation))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            hasher.WriteUInt64((ulong)bytes.Length);
            foreach (var value in bytes)
            {
                if (value == 0)
                    break;

                hasher.WriteByte(FoldAscii(value));
            }

            return;
        }

        if (IsRTrim(collation))
        {
            hasher.WriteBytes(Encoding.UTF8.GetBytes(text.TrimEnd(' ')));
            return;
        }

        hasher.WriteBytes(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Join equality for one key component (Turso <c>values_equal</c>): NULL is never equal,
    /// INTEGER and REAL compare across kinds, text uses the retained collation.
    /// </summary>
    private static bool ValuesEqual(SqlValue left, SqlValue right, string? collation)
    {
        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return false;

        if (left.Kind is SqlValueKind.Integer or SqlValueKind.Real
            && right.Kind is SqlValueKind.Integer or SqlValueKind.Real)
            return NumericValuesEqual(left, right);

        if (left.Kind == SqlValueKind.Blob && right.Kind == SqlValueKind.Blob)
            return left.AsBlobSpan().SequenceEqual(right.AsBlobSpan());

        if (left.Kind == SqlValueKind.Text && right.Kind == SqlValueKind.Text)
            return TextEquals(left.AsText(), right.AsText(), collation);

        return false;
    }

    /// <summary>Distinct equality (Turso <c>values_equal_distinct</c>): NULL equals NULL.</summary>
    private static bool ValuesEqualDistinct(SqlValue left, SqlValue right, string? collation)
    {
        if (left.Kind == SqlValueKind.Null && right.Kind == SqlValueKind.Null)
            return true;
        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return false;

        return ValuesEqual(left, right, collation);
    }

    private bool KeysEqual(SqlValue[] left, SqlValue[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!ValuesEqual(left[i], right[i], _collations[i]))
                return false;
        }

        return true;
    }

    private bool KeysEqualDistinct(SqlValue[] left, SqlValue[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!ValuesEqualDistinct(left[i], right[i], _collations[i]))
                return false;
        }

        return true;
    }

    private static bool NumericValuesEqual(SqlValue left, SqlValue right)
    {
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
            return left.AsInteger() == right.AsInteger();

        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Real)
            return left.AsReal() == right.AsReal();

        var integer = left.Kind == SqlValueKind.Integer ? left.AsInteger() : right.AsInteger();
        var real = left.Kind == SqlValueKind.Real ? left.AsReal() : right.AsReal();
        return CompareIntegerToReal(integer, real) == 0;
    }

    private static int CompareIntegerToReal(long integer, double real)
    {
        // Same boundaries and truncation logic as SqliteIndexRecordComparer so hash equality
        // and ordering-based index lookups agree.
        const double MinimumInt64 = -9_223_372_036_854_775_808d;
        const double OnePastMaximumInt64 = 9_223_372_036_854_775_808d;
        if (real < MinimumInt64)
            return 1;
        if (real >= OnePastMaximumInt64)
            return -1;

        var truncated = (long)real;
        var comparison = integer.CompareTo(truncated);
        if (comparison != 0 || real == truncated)
            return comparison;

        return real > 0 ? -1 : 1;
    }

    private static bool TextEquals(string left, string right, string? collation)
    {
        if (IsNoCase(collation))
            return SqliteIndexRecordComparer.CompareNoCaseText(left, right) == 0;

        if (IsRTrim(collation))
            return SqliteIndexRecordComparer.CompareRTrimText(left, right) == 0;

        return string.CompareOrdinal(left, right) == 0;
    }

    private static bool IsNoCase(string? collation)
        => collation is not null && string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase);

    private static bool IsRTrim(string? collation)
        => collation is not null && string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase);

    private static byte FoldAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + ((byte)'a' - (byte)'A'))
            : value;

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
