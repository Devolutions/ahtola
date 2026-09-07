using Ahtola.Core.Storage;
using System.Runtime.ExceptionServices;

namespace Ahtola.Core.Execution;

internal sealed class VdbeJoinExecutionContext(
    VdbeExecutionOptions options,
    VdbeExecutionMemory memory)
{
    private readonly VdbePendingCleanupRegistry _pendingCleanup = new();

    public VdbeExecutionOptions Options { get; } = options;

    public VdbeExecutionMemory Memory { get; } = memory;

    public CancellationToken CancellationToken { get; private set; }

    public void SetCancellationToken(CancellationToken cancellationToken) =>
        CancellationToken = cancellationToken;

    public void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();

    public Exception? CleanupFailure { get; private set; }

    public void RecordCleanupFailure(Exception exception, IDisposable? retryable = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        CleanupFailure = CleanupFailure is null
            ? exception
            : new AggregateException(CleanupFailure, exception);
        if (retryable is not null)
            _pendingCleanup.Register(retryable);
    }

    public Exception? TakeCleanupFailure()
    {
        var failure = CleanupFailure;
        CleanupFailure = null;
        return failure;
    }

    public bool HasPendingCleanup => _pendingCleanup.HasPending;

    public void RegisterPendingCleanup(IDisposable cleanup) => _pendingCleanup.Register(cleanup);

    public void UnregisterPendingCleanup(IDisposable cleanup) => _pendingCleanup.Unregister(cleanup);

    public void RetryPendingCleanup() => _pendingCleanup.Retry();

    public static VdbeJoinExecutionContext CreateDefault()
    {
        var options = VdbeExecutionOptions.Default;
        return new VdbeJoinExecutionContext(
            options,
            new VdbeExecutionMemory(options.MemoryLimitBytes, options.Metrics));
    }
}

internal static class VdbeHashJoinRuntime
{
    // Mirrors turso-src/core/vdbe/hash_table.rs MIN_PARTITIONS (top-level fan-out chosen once,
    // before any build statistics are known). Ahtola's execution memory limit is a single
    // budget shared by every spill-aware operator in the statement (see
    // VdbeExecutionOptions.MemoryLimitBytes), unlike Turso's dedicated per-hash-table
    // mem_budget. Because a build side can never buffer more than that shared limit before
    // the first spill decision, applying Turso's choose_partition_count formula at that same
    // decision point is provably inert here (it always clamps back down to this constant) -
    // see the HASH gap analysis notes in VdbeHashJoinRuntime.cs history for the derivation.
    // Adaptive fan-out is instead applied later, once a *specific* on-disk partition is found
    // to be oversized and its true size is known - see TrySplitPartition.
    private const int PartitionCount = 16;
    private const int TopPartitionBits = 4; // log2(PartitionCount): bits already consumed by GetPartition.

    // Mirrors turso-src/core/vdbe/hash_table.rs MAX_PARTITIONS: the absolute ceiling on
    // fan-out, whether from the initial (fixed) partitioning or an adaptive split.
    private const int MaxPartitionCount = 128;

    // A split is only ever attempted once, and only one level deep (no recursive
    // re-splitting of an already-split sub-partition), so a modest minimum keeps a
    // marginally-oversized partition from being fragmented into 16+ tiny files for no
    // benefit. This is an Ahtola-specific adaptation of upstream's MIN_PARTITIONS=16, which
    // Turso uses because it always partitions a hash table from scratch.
    private const int MinSplitPartitionCount = 2;

    private const ulong HashOffsetBasis = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;

    /// <summary>
    /// Identifies a build-side spill partition: either a top-level partition
    /// (<see cref="SubIndex"/> &lt; 0) or one of the finer sub-partitions an oversized
    /// top-level partition was lazily split into (see <c>HashSpill.TrySplitPartition</c>).
    /// </summary>
    private readonly record struct PartitionKey(int TopIndex, int SubIndex)
    {
        public static PartitionKey Top(int topIndex) => new(topIndex, -1);
    }

    public static IEnumerable<VdbeJoinRow> Enumerate(
        VdbeJoinOperatorPlan plan,
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        var rows = plan.HashBuildRight
            ? EnumerateCore(
                plan,
                plan.Right,
                plan.Left,
                buildKey: plan.EquiProbe!.BuildRightKey,
                probeKey: plan.EquiProbe.BuildLeftKey,
                buildIsRight: true,
                maximumRows,
                context)
            : EnumerateCore(
                plan,
                plan.Left,
                plan.Right,
                buildKey: plan.EquiProbe!.BuildLeftKey,
                probeKey: plan.EquiProbe.BuildRightKey,
                buildIsRight: false,
                maximumRows,
                context);
        return ObserveCleanup(rows, context);
    }

    private static IEnumerable<VdbeJoinRow> ObserveCleanup(
        IEnumerable<VdbeJoinRow> source,
        VdbeJoinExecutionContext context)
    {
        var enumerator = source.GetEnumerator();
        Exception? primaryFailure = null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                    break;
                }

                if (!hasNext)
                    break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            try
            {
                enumerator.Dispose();
            }
            catch (Exception exception)
            {
                context.RecordCleanupFailure(exception);
            }
        }

        var cleanupFailure = context.TakeCleanupFailure();
        if (primaryFailure is not null)
        {
            if (cleanupFailure is not null)
                throw new AggregateException(primaryFailure, cleanupFailure);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static IEnumerable<VdbeJoinRow> EnumerateCore(
        VdbeJoinOperatorPlan plan,
        VdbeJoinPlanNode buildNode,
        VdbeJoinPlanNode probeNode,
        Func<VdbeJoinRow, string?> buildKey,
        Func<VdbeJoinRow, string?> probeKey,
        bool buildIsRight,
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        HashSpill? spill = null;
        var residency = new PartitionResidencyCache();
        var trackUnmatchedBuild = buildIsRight && plan.Kind is VdbeJoinKind.Right or VdbeJoinKind.Full;
        var spillInfrastructureBytes = VdbeManagedFootprint.EstimateHashSpillInfrastructure(
            context.Options.TemporaryDirectory,
            PartitionCount,
            trackUnmatchedBuild);
        VdbeMemoryReservation? spillInfrastructureReservation = null;
        HashBuildBuffer? buffered = null;
        var emitted = 0;

        try
        {
            if (context.Options.AllowTemporaryFileSpill)
            {
                spillInfrastructureReservation = VdbeMemoryReservation.TryCreate(
                    context.Memory,
                    spillInfrastructureBytes);
            }
            buffered = HashBuildBuffer.TryCreate(context.Memory, trackUnmatchedBuild);
            long ordinal = 0;
            foreach (var row in buildNode.Enumerate(maximumRows: null, context))
            {
                context.ThrowIfCancellationRequested();
                var key = buildKey(row);
                var retainedBytes = Math.Max(
                    VdbeManagedFootprint.EstimateHashBuildEntry(
                        row.Values,
                        key,
                        row.RowIds.Length),
                    VdbeManagedFootprint.EstimateHashBuildEntryFromEncodedLength(
                        EstimateEncodedEntryPayload(row, key),
                        row.Values.Length,
                        row.RowIds.Length));
                if (retainedBytes > context.Memory.LimitBytes)
                    throw new VdbeMemoryLimitExceededException(context.Memory.LimitBytes, retainedBytes);
                var currentOrdinal = ordinal++;

                if (spill is null
                    && buffered is not null
                    && buffered.TryAdd(row, key, currentOrdinal, retainedBytes))
                    continue;

                if (!context.Options.AllowTemporaryFileSpill)
                    throw new VdbeMemoryLimitExceededException(context.Memory.LimitBytes, retainedBytes);

                if (spill is null)
                {
                    spillInfrastructureReservation ??= VdbeMemoryReservation.Create(
                        context.Memory,
                        spillInfrastructureBytes);
                    spill = HashSpill.Create(
                        context,
                        buildNode.ColumnCount,
                        buildNode.SourceCount,
                        trackUnmatchedBuild,
                        ref spillInfrastructureReservation);
                    if (buffered is not null)
                    {
                        foreach (var retained in buffered.Entries)
                            spill.WriteBuild(retained, context);
                        buffered.Dispose();
                        buffered = null;
                    }
                }

                context.Memory.RetainOrThrow(retainedBytes);
                try
                {
                    spill.WriteBuild(
                        new BuildEntry(row, key, currentOrdinal, retainedBytes),
                        context);
                }
                finally
                {
                    context.Memory.Release(retainedBytes);
                }
            }

            if (spill is not null)
                spill.CompleteBuild(context);

            bool[]? matched = trackUnmatchedBuild && spill is null
                ? buffered?.CreateMatchedMap() ?? []
                : null;
            foreach (var probe in probeNode.Enumerate(maximumRows: null, context))
            {
                context.ThrowIfCancellationRequested();
                var matchedProbe = false;
                var key = probeKey(probe);

                if (key is not null)
                {
                    if (spill is null)
                    {
                        if (buffered?.TryGetCandidates(key, out var candidateIndices) == true)
                        {
                            foreach (var buildIndex in candidateIndices)
                            {
                                context.ThrowIfCancellationRequested();
                                var build = buffered[buildIndex];
                                var combined = Combine(build.Row, probe, buildIsRight);
                                if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                                    continue;

                                matchedProbe = true;
                                if (matched is not null)
                                    matched[buildIndex] = true;
                                yield return combined;
                                if (maximumRows is { } maximum && ++emitted >= maximum)
                                    yield break;
                            }
                        }
                    }
                    else
                    {
                        var resolution = residency.Resolve(spill, key, context);
                        if (resolution.Loaded is not null)
                        {
                            foreach (var build in resolution.Loaded.Find(key))
                            {
                                context.ThrowIfCancellationRequested();
                                var combined = Combine(build.Row, probe, buildIsRight);
                                if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                                    continue;

                                matchedProbe = true;
                                if (trackUnmatchedBuild)
                                    spill.MarkMatched(build.Ordinal, context);
                                yield return combined;
                                if (maximumRows is { } maximum && ++emitted >= maximum)
                                    yield break;
                            }
                        }
                        else if (resolution.Index is not null)
                        {
                            foreach (var lease in spill.ReadPartitionIndexMatches(
                                resolution.Id,
                                resolution.Index,
                                key,
                                context,
                                residency))
                            {
                                VdbeJoinRow? combinedResult = null;
                                using (lease)
                                {
                                    context.ThrowIfCancellationRequested();
                                    var build = lease.Entry;
                                    var combined = Combine(build.Row, probe, buildIsRight);
                                    if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                                        continue;

                                    matchedProbe = true;
                                    if (trackUnmatchedBuild)
                                        spill.MarkMatched(build.Ordinal, context);
                                    combinedResult = combined;
                                }

                                yield return combinedResult;
                                if (maximumRows is { } maximum && ++emitted >= maximum)
                                    yield break;
                            }
                        }
                        else
                        {
                            context.Options.Metrics.HashPartitionFallbackScan();
                            foreach (var lease in spill.ReadPartitionByKey(resolution.Id, context))
                            {
                                VdbeJoinRow? combinedResult = null;
                                using (lease)
                                {
                                    context.ThrowIfCancellationRequested();
                                    var build = lease.Entry;
                                    if (!string.Equals(build.Key, key, StringComparison.Ordinal))
                                        continue;

                                    var combined = Combine(build.Row, probe, buildIsRight);
                                    if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                                        continue;

                                    matchedProbe = true;
                                    if (trackUnmatchedBuild)
                                        spill.MarkMatched(build.Ordinal, context);
                                    combinedResult = combined;
                                }

                                yield return combinedResult;
                                if (maximumRows is { } maximum && ++emitted >= maximum)
                                    yield break;
                            }
                        }
                    }
                }

                if (!matchedProbe && buildIsRight && plan.Kind is VdbeJoinKind.Left or VdbeJoinKind.Full)
                {
                    yield return Combine(probe, NullRow(buildNode));
                    if (maximumRows is { } maximum && ++emitted >= maximum)
                        yield break;
                }
            }

            if (trackUnmatchedBuild)
            {
                residency.Dispose();
                var nullProbe = NullRow(probeNode);
                if (spill is null)
                {
                    if (buffered is null)
                        yield break;
                    for (var index = 0; index < buffered.Count; index++)
                    {
                        context.ThrowIfCancellationRequested();
                        if (matched![index])
                            continue;
                        yield return Combine(nullProbe, buffered[index].Row);
                        if (maximumRows is { } maximum && ++emitted >= maximum)
                            yield break;
                    }
                }
                else
                {
                    foreach (var lease in spill.ReadBuildOrder(context))
                    {
                        VdbeJoinRow? combined = null;
                        using (lease)
                        {
                            context.ThrowIfCancellationRequested();
                            var build = lease.Entry;
                            if (spill.IsMatched(build.Ordinal, context))
                                continue;
                            combined = Combine(nullProbe, build.Row);
                        }
                        yield return combined;
                        if (maximumRows is { } maximum && ++emitted >= maximum)
                            yield break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                residency.Dispose();
            }
            catch (Exception exception)
            {
                context.RecordCleanupFailure(exception);
            }
            try
            {
                buffered?.Dispose();
            }
            catch (Exception exception)
            {
                context.RecordCleanupFailure(exception);
            }
            try
            {
                spill?.Dispose();
            }
            catch (Exception exception)
            {
                context.RecordCleanupFailure(exception, spill);
            }
            spillInfrastructureReservation?.Dispose();
        }
    }

    private static bool Matches(
        VdbeJoinOperatorPlan plan,
        VdbeJoinRow build,
        VdbeJoinRow probe,
        VdbeJoinRow combined,
        bool buildIsRight)
    {
        if (plan.Condition is null)
            return true;
        return buildIsRight
            ? plan.Condition(probe, build, combined)
            : plan.Condition(build, probe, combined);
    }

    private static VdbeJoinRow Combine(VdbeJoinRow build, VdbeJoinRow probe, bool buildIsRight) =>
        buildIsRight
            ? Combine(probe, build)
            : Combine(build, probe);

    private static VdbeJoinRow Combine(VdbeJoinRow left, VdbeJoinRow right) =>
        new([.. left.Values, .. right.Values], [.. left.RowIds, .. right.RowIds]);

    private static VdbeJoinRow NullRow(VdbeJoinPlanNode node) =>
        new(
            Enumerable.Repeat(SqlValue.Null, node.ColumnCount).ToArray(),
            new long?[node.SourceCount]);

    private static int GetPartition(string key) => (int)(StableHash(key) & (PartitionCount - 1));

    private static ulong StableHash(string value)
    {
        var hash = HashOffsetBasis;
        foreach (var character in value)
        {
            hash ^= (byte)character;
            hash *= HashPrime;
            hash ^= (byte)(character >> 8);
            hash *= HashPrime;
        }
        return hash;
    }

    private static long EstimateEncodedEntryPayload(VdbeJoinRow row, string? key)
    {
        var total = checked(
            sizeof(long)
            + sizeof(byte)
            + VdbeSpillRecordCodec.EstimateEncodedValuesLength(row.Values));
        if (key is not null)
            total = checked(total + VdbeSpillRecordCodec.EstimateEncodedStringLength(key));
        foreach (var rowId in row.RowIds)
            total = checked(total + sizeof(byte) + (rowId.HasValue ? sizeof(long) : 0));
        return total;
    }

    private sealed record BuildEntry(
        VdbeJoinRow Row,
        string? Key,
        long Ordinal,
        long RetainedBytes);

    private sealed class BuildEntryLease(
        BuildEntry entry,
        VdbeExecutionMemory memory,
        long retainedBytes) : IDisposable
    {
        private bool _transferred;

        public BuildEntry Entry { get; } = entry;

        public long RetainedBytes { get; } = retainedBytes;

        public void Transfer() => _transferred = true;

        public void Dispose()
        {
            if (_transferred)
                return;
            _transferred = true;
            memory.Release(RetainedBytes);
        }
    }

    private sealed class HashBuildBuffer : IDisposable
    {
        private const long BufferObjectBytes = 64;

        private readonly VdbeExecutionMemory _memory;
        private readonly bool _trackMatches;
        private List<BuildEntry>? _entries;
        private Dictionary<string, List<int>>? _buckets;
        private long _retainedBytes;
        private int _retainedRows;

        private HashBuildBuffer(VdbeExecutionMemory memory, bool trackMatches, long retainedBytes)
        {
            _memory = memory;
            _trackMatches = trackMatches;
            _retainedBytes = retainedBytes;
            _entries = [];
            _buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        }

        public int Count => _entries?.Count ?? 0;

        public BuildEntry this[int index] => _entries![index];

        public IEnumerable<BuildEntry> Entries => _entries ?? [];

        public static HashBuildBuffer? TryCreate(VdbeExecutionMemory memory, bool trackMatches)
        {
            var retainedBytes = checked(
                BufferObjectBytes
                + VdbeManagedFootprint.ListObjectBytes
                + VdbeManagedFootprint.DictionaryObjectBytes);
            if (!memory.TryRetain(retainedBytes, rows: 0))
                return null;
            try
            {
                return new HashBuildBuffer(memory, trackMatches, retainedBytes);
            }
            catch
            {
                memory.Release(retainedBytes, rows: 0);
                throw;
            }
        }

        public bool TryAdd(
            VdbeJoinRow row,
            string? key,
            long ordinal,
            long entryBytes)
        {
            var entries = _entries
                ?? throw new ObjectDisposedException(nameof(HashBuildBuffer));
            var buckets = _buckets
                ?? throw new ObjectDisposedException(nameof(HashBuildBuffer));
            var requiredCount = checked(entries.Count + 1);
            var entriesCapacity = VdbeManagedFootprint.GetListCapacityForCount(
                entries.Capacity,
                requiredCount);
            var currentEntriesBytes =
                VdbeManagedFootprint.EstimateReferenceListStorage(entries.Capacity);
            var growthBytes = VdbeManagedFootprint.EstimateContainerReplacement(
                currentEntriesBytes,
                VdbeManagedFootprint.EstimateReferenceListStorage(entriesCapacity));
            var replacedBytes = growthBytes > 0 ? currentEntriesBytes : 0;

            List<int>? bucket = null;
            var isNewKey = key is not null && !buckets.TryGetValue(key, out bucket);
            var bucketCapacity = 0;
            if (key is not null)
            {
                if (isNewKey)
                {
                    var currentDictionaryBytes =
                        VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count);
                    var dictionaryGrowth = VdbeManagedFootprint.EstimateContainerReplacement(
                        currentDictionaryBytes,
                        VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count + 1));
                    growthBytes = checked(
                        growthBytes
                        + VdbeManagedFootprint.BucketListObjectBytes
                        + dictionaryGrowth);
                    if (dictionaryGrowth > 0)
                        replacedBytes = checked(replacedBytes + currentDictionaryBytes);
                    bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(0, 1);
                    growthBytes = checked(
                        growthBytes
                        + VdbeManagedFootprint.EstimateInt32ListStorage(bucketCapacity));
                }
                else
                {
                    var currentBucketBytes =
                        VdbeManagedFootprint.EstimateInt32ListStorage(bucket!.Capacity);
                    bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(
                        bucket.Capacity,
                        bucket.Count + 1);
                    var bucketGrowth = VdbeManagedFootprint.EstimateContainerReplacement(
                        currentBucketBytes,
                        VdbeManagedFootprint.EstimateInt32ListStorage(bucketCapacity));
                    growthBytes = checked(
                        growthBytes
                        + bucketGrowth);
                    if (bucketGrowth > 0)
                        replacedBytes = checked(replacedBytes + currentBucketBytes);
                }
            }

            if (_trackMatches)
            {
                growthBytes = checked(
                    growthBytes
                    + VdbeManagedFootprint.EstimateBooleanArray(requiredCount)
                    - VdbeManagedFootprint.EstimateBooleanArray(entries.Count));
            }

            var retainedBytes = checked(entryBytes + growthBytes);
            if (!_memory.TryRetain(retainedBytes))
                return false;

            try
            {
                if (entriesCapacity != entries.Capacity)
                    entries.Capacity = entriesCapacity;
                var entry = new BuildEntry(row, key, ordinal, entryBytes);
                var entryIndex = entries.Count;
                entries.Add(entry);
                if (key is not null)
                {
                    if (isNewKey)
                    {
                        bucket = new List<int>(bucketCapacity);
                        buckets.Add(key, bucket);
                    }
                    else if (bucketCapacity != bucket!.Capacity)
                    {
                        bucket.Capacity = bucketCapacity;
                    }
                    bucket!.Add(entryIndex);
                }
                if (replacedBytes > 0)
                    _memory.Release(replacedBytes, rows: 0);
                _retainedBytes = checked(
                    _retainedBytes
                    + retainedBytes
                    - replacedBytes);
                _retainedRows++;
                return true;
            }
            catch
            {
                _memory.Release(retainedBytes);
                throw;
            }
        }

        public bool TryGetCandidates(string key, out List<int> candidates) =>
            _buckets!.TryGetValue(key, out candidates!);

        public bool[] CreateMatchedMap()
        {
            if (!_trackMatches)
                throw new InvalidOperationException("This hash build does not track matches.");
            return new bool[Count];
        }

        public void Dispose()
        {
            if (_entries is null)
                return;
            _entries = null;
            _buckets = null;
            _memory.Release(_retainedBytes, _retainedRows);
            _retainedBytes = 0;
            _retainedRows = 0;
        }
    }

    private sealed class LoadedPartition : IDisposable
    {
        private readonly VdbeExecutionMemory _memory;
        private readonly Dictionary<string, List<BuildEntry>> _buckets;
        private readonly long _retainedBytes;
        private readonly int _retainedRows;
        private bool _disposed;

        public LoadedPartition(
            VdbeExecutionMemory memory,
            Dictionary<string, List<BuildEntry>> buckets,
            long retainedBytes,
            int retainedRows)
        {
            _memory = memory;
            _buckets = buckets;
            _retainedBytes = retainedBytes;
            _retainedRows = retainedRows;
        }

        public IEnumerable<BuildEntry> Find(string key) =>
            _buckets.TryGetValue(key, out var entries) ? entries : [];

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _memory.Release(_retainedBytes, _retainedRows);
        }
    }

    /// <summary>
    /// A lightweight key-&gt;record-offset index over one spill partition, built from a single
    /// header-only pass (record boundaries and keys, never the row payload or rowids). Its
    /// per-partition memory footprint is a small fraction of a fully materialized
    /// <see cref="LoadedPartition"/>, so a memory budget that cannot hold every partition a
    /// cyclic probe order touches as full rows can still hold this cheaper index for many
    /// more of them at once, mitigating the residual thrashing a bounded full-row LRU alone
    /// cannot avoid when the working set exceeds its capacity (see
    /// <see cref="PartitionResidencyCache"/>). Once built it is an exact, complete index of
    /// every key in the partition, so a lookup miss is a definitive no-match, not a hint.
    /// </summary>
    private sealed class PartitionKeyIndex : IDisposable
    {
        private readonly VdbeExecutionMemory _memory;
        private readonly Dictionary<string, List<long>> _offsetsByKey;
        private readonly long _retainedBytes;
        private bool _disposed;

        public PartitionKeyIndex(
            VdbeExecutionMemory memory,
            Dictionary<string, List<long>> offsetsByKey,
            long retainedBytes)
        {
            _memory = memory;
            _offsetsByKey = offsetsByKey;
            _retainedBytes = retainedBytes;
        }

        public IReadOnlyList<long> Find(string key) =>
            _offsetsByKey.TryGetValue(key, out var offsets) ? offsets : [];

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _memory.Release(_retainedBytes, rows: 0);
        }
    }

    /// <summary>
    /// Caches spill-side partitions that have been fully loaded into memory or lightly
    /// indexed across probe rows, so a probe order that interleaves between partitions does
    /// not thrash: entries are evicted least-recently-used only when memory pressure
    /// requires it, mirroring the LRU-bounded residency in
    /// turso-src/core/vdbe/hash_table.rs (loaded_partitions_lru / evict_partitions_to_fit),
    /// instead of unconditionally reloading whenever the probe hash changes. Two tiers share
    /// one LRU order: a full-row <see cref="LoadedPartition"/> (fastest repeat lookups) and,
    /// once that no longer fits, a much cheaper <see cref="PartitionKeyIndex"/> (see that
    /// type's remarks) so a working set that exceeds what full rows can hold at once can
    /// still mostly avoid falling back to raw scans - a bounded LRU alone cannot do this for
    /// a genuinely larger-than-capacity working set, since no recency policy can prevent
    /// thrashing when demand strictly exceeds capacity; shrinking what one entry costs is
    /// what raises the effective capacity instead.
    /// </summary>
    private sealed class PartitionResidencyCache : IDisposable
    {
        public readonly struct Resolution
        {
            public LoadedPartition? Loaded { get; init; }
            public PartitionKeyIndex? Index { get; init; }
            public PartitionKey Id { get; init; }
        }

        private readonly Dictionary<PartitionKey, LoadedPartition> _loaded = [];
        private readonly Dictionary<PartitionKey, PartitionKeyIndex> _indexes = [];
        private readonly LinkedList<PartitionKey> _lruOrder = new();
        private readonly Dictionary<PartitionKey, LinkedListNode<PartitionKey>> _lruNodes = [];
        private readonly HashSet<PartitionKey> _unloadable = [];

        /// <summary>
        /// Resolves the partition that <paramref name="key"/> probes into via whichever tier
        /// (full-row cache, key index, or - on total failure - a required raw scan)
        /// currently answers it, splitting an oversized top-level partition at most once
        /// along the way.
        /// </summary>
        public Resolution Resolve(HashSpill spill, string key, VdbeJoinExecutionContext context)
        {
            var id = spill.GetPartitionKey(key);
            var hit = TryHit(id, context);
            if (hit is { } hitResult)
                return hitResult;
            if (_unloadable.Contains(id))
                return new Resolution { Id = id };

            var established = TryEstablish(spill, id, context);
            if (established is { } establishedResult)
                return establishedResult;

            // Only ever attempt one level of splitting, and only for a still-unsplit
            // top-level partition; a sub-partition that is itself still unaffordable falls
            // straight through to the bounded scan fallback (preserves the skew-bounded
            // guarantee - no amount of re-partitioning helps a single dominant key).
            if (id.SubIndex < 0 && spill.TrySplitPartition(id.TopIndex, context))
            {
                var splitId = spill.GetPartitionKey(key);
                var splitHit = TryHit(splitId, context);
                if (splitHit is { } splitHitResult)
                    return splitHitResult;

                if (!_unloadable.Contains(splitId))
                {
                    var splitEstablished = TryEstablish(spill, splitId, context);
                    if (splitEstablished is { } splitEstablishedResult)
                        return splitEstablishedResult;
                }

                _unloadable.Add(splitId);
                return new Resolution { Id = splitId };
            }

            _unloadable.Add(id);
            return new Resolution { Id = id };
        }

        private Resolution? TryHit(PartitionKey id, VdbeJoinExecutionContext context)
        {
            if (_loaded.TryGetValue(id, out var loaded))
            {
                Touch(id);
                context.Options.Metrics.HashPartitionResidencyReuse();
                return new Resolution { Loaded = loaded, Id = id };
            }
            if (_indexes.TryGetValue(id, out var index))
            {
                Touch(id);
                context.Options.Metrics.HashPartitionResidencyReuse();
                return new Resolution { Index = index, Id = id };
            }
            return null;
        }

        private Resolution? TryEstablish(HashSpill spill, PartitionKey id, VdbeJoinExecutionContext context)
        {
            var loaded = TryLoadWithEviction(spill, id, context);
            if (loaded is not null)
            {
                _loaded[id] = loaded;
                Touch(id);
                return new Resolution { Loaded = loaded, Id = id };
            }

            var index = TryBuildIndexWithEviction(spill, id, context);
            if (index is not null)
            {
                _indexes[id] = index;
                Touch(id);
                return new Resolution { Index = index, Id = id };
            }

            return null;
        }

        private LoadedPartition? TryLoadWithEviction(
            HashSpill spill,
            PartitionKey id,
            VdbeJoinExecutionContext context)
        {
            var loaded = spill.TryLoadPartitionByKey(id, context);
            while (loaded is null && EvictOneLeastRecentlyUsed(id, context))
                loaded = spill.TryLoadPartitionByKey(id, context);
            return loaded;
        }

        private PartitionKeyIndex? TryBuildIndexWithEviction(
            HashSpill spill,
            PartitionKey id,
            VdbeJoinExecutionContext context)
        {
            var index = spill.TryBuildKeyIndexByKey(id, context);
            while (index is null && EvictOneLeastRecentlyUsed(id, context))
                index = spill.TryBuildKeyIndexByKey(id, context);
            return index;
        }

        /// <summary>
        /// Evicts one least-recently-used resident partition (other than <paramref
        /// name="protect"/>) to free memory, for a caller that needs to materialize a
        /// specific row through an already-resident <see cref="PartitionKeyIndex"/> (whose
        /// own footprint, unlike a raw scan's, persists and can leave less headroom than
        /// the old scan-only fallback had for that one row). Returns false once nothing
        /// more is evictable.
        /// </summary>
        public bool EvictOneExcept(PartitionKey protect, VdbeJoinExecutionContext context) =>
            EvictOneLeastRecentlyUsed(protect, context);

        /// <summary>
        /// Releases a specific resident partition's own memory accounting (without
        /// requiring it to be the LRU victim), for a caller that is about to materialize a
        /// row through a <see cref="PartitionKeyIndex"/> it already holds a reference to and
        /// has run out of other, less invasive options: an already-computed offset list
        /// stays valid after this call (this only stops charging its retained bytes against
        /// the budget), so the in-flight read can still complete; only a later probe that
        /// needs this same partition again pays for rebuilding it.
        /// </summary>
        public bool ReleaseSelf(PartitionKey id, VdbeExecutionMetrics metrics)
        {
            if (_loaded.Remove(id, out var loaded))
            {
                loaded.Dispose();
            }
            else if (_indexes.Remove(id, out var index))
            {
                index.Dispose();
            }
            else
            {
                return false;
            }

            if (_lruNodes.Remove(id, out var node))
                _lruOrder.Remove(node);
            metrics.HashPartitionEviction();
            return true;
        }

        private bool EvictOneLeastRecentlyUsed(PartitionKey protect, VdbeJoinExecutionContext context)
        {
            var node = _lruOrder.First;
            while (node is not null && node.Value.Equals(protect))
                node = node.Next;
            if (node is null)
                return false;

            var victim = node.Value;
            _lruOrder.Remove(node);
            _lruNodes.Remove(victim);
            if (_loaded.Remove(victim, out var loadedVictim))
            {
                loadedVictim.Dispose();
                context.Options.Metrics.HashPartitionEviction();
            }
            else if (_indexes.Remove(victim, out var indexVictim))
            {
                indexVictim.Dispose();
                context.Options.Metrics.HashPartitionEviction();
            }
            return true;
        }

        private void Touch(PartitionKey id)
        {
            if (_lruNodes.TryGetValue(id, out var node))
                _lruOrder.Remove(node);
            _lruNodes[id] = _lruOrder.AddLast(id);
        }

        public void Dispose()
        {
            List<Exception>? failures = null;
            foreach (var partition in _loaded.Values)
            {
                try
                {
                    partition.Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
            foreach (var index in _indexes.Values)
            {
                try
                {
                    index.Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            _loaded.Clear();
            _indexes.Clear();
            _lruNodes.Clear();
            _lruOrder.Clear();

            if (failures is [var failure])
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException(failures);
        }
    }

    private sealed class HashSpill : IDisposable
    {
        private readonly VdbeExecutionOptions _options;
        private readonly int _columnCount;
        private readonly int _rowIdCount;
        private readonly Partition?[] _partitions;
        private readonly VdbeMemoryReservation _infrastructureReservation;
        private readonly Dictionary<int, Partition[]> _splits = [];
        private readonly HashSet<int> _splitAttempted = [];
        private readonly Dictionary<int, VdbeMemoryReservation> _splitReservations = [];
        private VdbeTemporaryFile? _buildOrder;
        private VdbeTemporaryFile? _matched;
        private long _buildOrderPosition;
        private int _totalBuildEntries;
        private bool _disposed;

        private HashSpill(
            VdbeExecutionOptions options,
            int columnCount,
            int rowIdCount,
            VdbeMemoryReservation infrastructureReservation)
        {
            _options = options;
            _columnCount = columnCount;
            _rowIdCount = rowIdCount;
            _infrastructureReservation = infrastructureReservation;
            _partitions = new Partition[PartitionCount];
        }

        public static HashSpill Create(
            VdbeJoinExecutionContext context,
            int columnCount,
            int rowIdCount,
            bool trackUnmatchedBuild,
            ref VdbeMemoryReservation? infrastructureReservation)
        {
            var reservation = infrastructureReservation
                ?? throw new InvalidOperationException("Hash spill infrastructure was not reserved.");
            HashSpill spill;
            try
            {
                spill = new HashSpill(
                    context.Options,
                    columnCount,
                    rowIdCount,
                    reservation);
            }
            catch
            {
                reservation.Dispose();
                infrastructureReservation = null;
                throw;
            }

            context.RegisterPendingCleanup(spill);
            infrastructureReservation = null;
            try
            {
                spill.Initialize(trackUnmatchedBuild);
                context.UnregisterPendingCleanup(spill);
                return spill;
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    spill.Dispose();
                    context.UnregisterPendingCleanup(spill);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(primaryFailure, cleanupFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        private void Initialize(bool trackUnmatchedBuild)
        {
            for (var index = 0; index < _partitions.Length; index++)
            {
                var partition = new Partition();
                _partitions[index] = partition;
                partition.SetFile(VdbeTemporaryFile.Create(_options, $"hash-p{index:D3}"));
                partition.Position = VdbeSpillRecordCodec.InitializeFile(
                    partition.File.File,
                    VdbeSpillFileKind.HashPartition,
                    _options.Metrics);
                _options.Metrics.HashPartitionCreated();
            }

            if (!trackUnmatchedBuild)
                return;

            _buildOrder = VdbeTemporaryFile.Create(_options, "hash-build-order");
            _buildOrderPosition = VdbeSpillRecordCodec.InitializeFile(
                _buildOrder.File,
                VdbeSpillFileKind.HashBuildOrder,
                _options.Metrics);
            _matched = VdbeTemporaryFile.Create(_options, "hash-matches");
            VdbeSpillRecordCodec.InitializeFile(
                _matched.File,
                VdbeSpillFileKind.HashMatchMap,
                _options.Metrics);
        }

        public void WriteBuild(BuildEntry entry, VdbeJoinExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            var partition = _partitions[entry.Key is null ? 0 : GetPartition(entry.Key)]!;
            var position = partition.Position;
            WriteEntry(partition.File.File, ref position, entry, context);
            partition.Position = position;
            partition.Count++;
            _totalBuildEntries++;

            if (_buildOrder is not null)
                WriteEntry(_buildOrder.File, ref _buildOrderPosition, entry, context);
        }

        public void CompleteBuild(VdbeJoinExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            foreach (var partition in _partitions)
                partition!.File.File.FlushToDisk();
            _buildOrder?.File.FlushToDisk();
        }

        /// <summary>
        /// Resolves the exact partition (top-level, or one of its lazily-created
        /// sub-partitions) that <paramref name="key"/> currently routes to.
        /// </summary>
        public PartitionKey GetPartitionKey(string key)
        {
            var topIndex = GetPartition(key);
            return _splits.TryGetValue(topIndex, out var subs)
                ? new PartitionKey(topIndex, GetSubPartitionIndex(key, subs.Length))
                : PartitionKey.Top(topIndex);
        }

        private Partition GetPartitionFile(PartitionKey id) =>
            id.SubIndex < 0 ? _partitions[id.TopIndex]! : _splits[id.TopIndex][id.SubIndex];

        // Sub-partitioning re-hashes the same key with the same hash function used for the
        // top-level partition. Every key routed into a given top-level partition already
        // shares the low TopPartitionBits bits of its hash (they equal the partition index),
        // so a sub-index must be drawn from the *next* bits, not the same ones, or every key
        // in the partition would collide into a single sub-partition again.
        private static int GetSubPartitionIndex(string key, int subCount) =>
            (int)((StableHash(key) >> TopPartitionBits) & (ulong)(subCount - 1));

        public IEnumerable<BuildEntryLease> ReadPartitionByKey(
            PartitionKey id,
            VdbeJoinExecutionContext context)
        {
            var partition = GetPartitionFile(id);
            _options.Metrics.HashPartitionScanned();
            VdbeSpillRecordCodec.ValidateFile(
                partition.File.File,
                VdbeSpillFileKind.HashPartition,
                _options.Metrics);
            return ReadEntries(partition.File.File, partition.Count, context);
        }

        public LoadedPartition? TryLoadPartitionByKey(
            PartitionKey id,
            VdbeJoinExecutionContext context) =>
            TryLoadPartitionCore(GetPartitionFile(id), context);

        private LoadedPartition? TryLoadPartitionCore(
            Partition partition,
            VdbeJoinExecutionContext context)
        {
            var dictionaryBytes = VdbeManagedFootprint.DictionaryObjectBytes;
            if (!context.Memory.TryRetain(dictionaryBytes, rows: 0))
                return null;

            var retainedBytes = dictionaryBytes;
            var retainedRows = 0;
            Dictionary<string, List<BuildEntry>>? buckets = null;
            var transferred = false;
            try
            {
                buckets = new Dictionary<string, List<BuildEntry>>(StringComparer.Ordinal);
                _options.Metrics.HashPartitionScanned();
                VdbeSpillRecordCodec.ValidateFile(
                    partition.File.File,
                    VdbeSpillFileKind.HashPartition,
                    _options.Metrics);
                long position = VdbeSpillRecordCodec.FileHeaderSize;
                for (var index = 0; index < partition.Count; index++)
                {
                    using var lease = TryReadEntry(
                        partition.File.File,
                        ref position,
                        context,
                        requireAvailable: false);
                    if (lease is null)
                        return null;

                    var entry = lease.Entry;
                    if (entry.Key is null)
                        continue;

                    List<BuildEntry>? bucket = null;
                    var isNewKey = !buckets.TryGetValue(entry.Key, out bucket);
                    var growthBytes = 0L;
                    var replacedBytes = 0L;
                    var bucketCapacity = 0;
                    if (isNewKey)
                    {
                        var currentDictionaryBytes =
                            VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count);
                        var dictionaryGrowth =
                            VdbeManagedFootprint.EstimateContainerReplacement(
                                currentDictionaryBytes,
                                VdbeManagedFootprint.EstimateDictionaryStorage(
                                    buckets.Count + 1));
                        growthBytes = checked(
                            VdbeManagedFootprint.BucketListObjectBytes
                            + dictionaryGrowth);
                        if (dictionaryGrowth > 0)
                            replacedBytes = currentDictionaryBytes;
                        bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(0, 1);
                        growthBytes = checked(
                            growthBytes
                            + VdbeManagedFootprint.EstimateReferenceListStorage(bucketCapacity));
                    }
                    else
                    {
                        var currentBucketBytes =
                            VdbeManagedFootprint.EstimateReferenceListStorage(bucket!.Capacity);
                        bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(
                            bucket.Capacity,
                            bucket.Count + 1);
                        growthBytes = VdbeManagedFootprint.EstimateContainerReplacement(
                            currentBucketBytes,
                            VdbeManagedFootprint.EstimateReferenceListStorage(bucketCapacity));
                        if (growthBytes > 0)
                            replacedBytes = currentBucketBytes;
                    }

                    if (!context.Memory.TryRetain(growthBytes, rows: 0))
                        return null;

                    try
                    {
                        if (isNewKey)
                        {
                            bucket = new List<BuildEntry>(bucketCapacity);
                            buckets.Add(entry.Key, bucket);
                        }
                        else if (bucketCapacity != bucket!.Capacity)
                        {
                            bucket.Capacity = bucketCapacity;
                        }
                        bucket!.Add(entry);
                        if (replacedBytes > 0)
                            context.Memory.Release(replacedBytes, rows: 0);
                    }
                    catch
                    {
                        context.Memory.Release(growthBytes, rows: 0);
                        throw;
                    }

                    retainedBytes = checked(
                        retainedBytes
                        + growthBytes
                        - replacedBytes
                        + lease.RetainedBytes);
                    retainedRows++;
                    lease.Transfer();
                }

                transferred = true;
                _options.Metrics.HashPartitionLoaded();
                return new LoadedPartition(
                    context.Memory,
                    buckets,
                    retainedBytes,
                    retainedRows);
            }
            finally
            {
                if (!transferred && retainedBytes > 0)
                    context.Memory.Release(retainedBytes, retainedRows);
            }
        }

        /// <summary>
        /// Builds a lightweight key-&gt;record-offset index for the given partition: a single
        /// header-only pass over its file that records where each key's record(s) begin,
        /// without decoding row values or rowids. Much cheaper per entry than
        /// <see cref="TryLoadPartitionCore"/>, so it can serve as a fallback tier when a
        /// partition's full rows do not fit but a bare index of its keys still does.
        /// </summary>
        public PartitionKeyIndex? TryBuildKeyIndexByKey(PartitionKey id, VdbeJoinExecutionContext context) =>
            TryBuildKeyIndexCore(GetPartitionFile(id), context);

        private PartitionKeyIndex? TryBuildKeyIndexCore(Partition partition, VdbeJoinExecutionContext context)
        {
            var dictionaryBytes = VdbeManagedFootprint.DictionaryObjectBytes;
            if (!context.Memory.TryRetain(dictionaryBytes, rows: 0))
                return null;

            var retainedBytes = dictionaryBytes;
            Dictionary<string, List<long>>? offsetsByKey = null;
            var transferred = false;
            try
            {
                offsetsByKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                _options.Metrics.HashPartitionScanned();
                VdbeSpillRecordCodec.ValidateFile(
                    partition.File.File,
                    VdbeSpillFileKind.HashPartition,
                    _options.Metrics);
                long position = VdbeSpillRecordCodec.FileHeaderSize;
                for (var entryIndex = 0; entryIndex < partition.Count; entryIndex++)
                {
                    context.ThrowIfCancellationRequested();
                    var recordStart = position;
                    var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
                        partition.File.File,
                        ref position,
                        _options.Metrics);
                    // Skip the ordinal - the index only needs to locate a record, not its
                    // build-side ordinal, which is re-read from the record itself on a hit.
                    VdbeSpillRecordCodec.ReadInt64(partition.File.File, ref position, _options.Metrics);
                    var hasKey = VdbeSpillRecordCodec.ReadByte(partition.File.File, ref position, _options.Metrics);
                    string? key = hasKey switch
                    {
                        0 => null,
                        1 => VdbeSpillRecordCodec.ReadString(
                            partition.File.File,
                            ref position,
                            recordEnd,
                            _options.Metrics),
                        _ => throw new InvalidDataException($"Unknown hash spill key marker {hasKey}."),
                    };
                    // The whole point of a lightweight index: never decode the row values or
                    // rowids, just jump straight to the next record.
                    position = recordEnd;

                    if (key is null)
                        continue;

                    List<long>? bucket = null;
                    var isNewKey = !offsetsByKey.TryGetValue(key, out bucket);
                    long growthBytes;
                    var replacedBytes = 0L;
                    if (isNewKey)
                    {
                        var currentDictionaryBytes =
                            VdbeManagedFootprint.EstimateDictionaryStorage(offsetsByKey.Count);
                        var dictionaryGrowth = VdbeManagedFootprint.EstimateContainerReplacement(
                            currentDictionaryBytes,
                            VdbeManagedFootprint.EstimateDictionaryStorage(offsetsByKey.Count + 1));
                        var bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(0, 1);
                        growthBytes = checked(
                            VdbeManagedFootprint.BucketListObjectBytes
                            + dictionaryGrowth
                            + VdbeManagedFootprint.EstimateInt64ListStorage(bucketCapacity)
                            + VdbeManagedFootprint.EstimateStringStorage(key.Length));
                        if (dictionaryGrowth > 0)
                            replacedBytes = currentDictionaryBytes;
                    }
                    else
                    {
                        var currentBucketBytes = VdbeManagedFootprint.EstimateInt64ListStorage(bucket!.Capacity);
                        var bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(
                            bucket.Capacity,
                            bucket.Count + 1);
                        growthBytes = VdbeManagedFootprint.EstimateContainerReplacement(
                            currentBucketBytes,
                            VdbeManagedFootprint.EstimateInt64ListStorage(bucketCapacity));
                        if (growthBytes > 0)
                            replacedBytes = currentBucketBytes;
                    }

                    if (!context.Memory.TryRetain(growthBytes, rows: 0))
                        return null;

                    try
                    {
                        if (isNewKey)
                        {
                            bucket = new List<long>(VdbeManagedFootprint.GetListCapacityForCount(0, 1));
                            offsetsByKey.Add(key, bucket);
                        }
                        bucket!.Add(recordStart);
                        if (replacedBytes > 0)
                            context.Memory.Release(replacedBytes, rows: 0);
                    }
                    catch
                    {
                        context.Memory.Release(growthBytes, rows: 0);
                        throw;
                    }

                    retainedBytes = checked(retainedBytes + growthBytes - replacedBytes);
                }

                transferred = true;
                _options.Metrics.HashPartitionIndexBuild();
                return new PartitionKeyIndex(context.Memory, offsetsByKey, retainedBytes);
            }
            finally
            {
                if (!transferred && retainedBytes > 0)
                    context.Memory.Release(retainedBytes, rows: 0);
            }
        }

        /// <summary>
        /// Reads the build entries a resident <see cref="PartitionKeyIndex"/> says match
        /// <paramref name="key"/> by seeking directly to their recorded offsets, instead of
        /// scanning the whole partition file. Unlike a fully materialized
        /// <see cref="LoadedPartition"/> (whose entries are already retained), each match
        /// still needs a fresh memory retention to decode here; since the index's own
        /// footprint persists in <paramref name="residency"/>, a tight budget can leave less
        /// headroom for that one row than a scan-only fallback would have had, so a failed
        /// retention first evicts other resident partitions before giving up.
        /// </summary>
        public IEnumerable<BuildEntryLease> ReadPartitionIndexMatches(
            PartitionKey id,
            PartitionKeyIndex index,
            string key,
            VdbeJoinExecutionContext context,
            PartitionResidencyCache residency)
        {
            var partition = GetPartitionFile(id);
            foreach (var offset in index.Find(key))
            {
                context.Options.Metrics.HashPartitionIndexSeek();
                var position = offset;
                var lease = TryReadEntry(partition.File.File, ref position, context, requireAvailable: false);
                while (lease is null && residency.EvictOneExcept(id, context))
                {
                    position = offset;
                    lease = TryReadEntry(partition.File.File, ref position, context, requireAvailable: false);
                }

                if (lease is null)
                {
                    // Nothing else is evictable; releasing the index's own accounting is
                    // safe here since its offset list is already materialized and does not
                    // depend on that accounting - only a future probe rebuilding this same
                    // partition pays for it again.
                    residency.ReleaseSelf(id, _options.Metrics);
                    position = offset;
                    lease = TryReadEntry(partition.File.File, ref position, context, requireAvailable: false);
                }

                if (lease is null)
                {
                    // Truly out of options; let TryReadEntry throw with its precise,
                    // already-computed byte accounting rather than approximating it here.
                    position = offset;
                    lease = TryReadEntry(partition.File.File, ref position, context, requireAvailable: true)!;
                }

                yield return lease;
            }
        }

        /// <summary>
        /// Lazily splits an oversized top-level partition into finer sub-partitions, once,
        /// so that subsequent probes routing into it can potentially load a much smaller
        /// sub-partition instead of repeatedly falling back to a full scan of the whole
        /// original partition. Mirrors the adaptive fan-out in
        /// turso-src/core/vdbe/hash_table.rs choose_partition_count/MIN_PARTITIONS/
        /// MAX_PARTITIONS, applied here at the point a partition's true on-disk size is
        /// known (build time already completed), rather than speculatively up front.
        /// </summary>
        public bool TrySplitPartition(int topIndex, VdbeJoinExecutionContext context)
        {
            if (_splits.ContainsKey(topIndex))
                return true;
            if (!_splitAttempted.Add(topIndex))
                return false;

            var partition = _partitions[topIndex];
            if (partition is null || partition.Count == 0)
                return false;

            var subCount = ChooseSplitPartitionCount(partition, context);
            if (subCount <= 1)
                return false;

            var infrastructureBytes = VdbeManagedFootprint.EstimateHashPartitionSplitInfrastructure(
                _options.TemporaryDirectory,
                subCount);
            var reservation = VdbeMemoryReservation.TryCreate(context.Memory, infrastructureBytes);
            if (reservation is null)
                return false;

            var subPartitions = new Partition[subCount];
            var created = 0;
            try
            {
                for (var index = 0; index < subCount; index++)
                {
                    var sub = new Partition();
                    sub.SetFile(VdbeTemporaryFile.Create(_options, $"hash-p{topIndex:D3}-s{index:D3}"));
                    subPartitions[index] = sub;
                    // Bump the cleanup watermark as soon as the file handle is live, before
                    // InitializeFile (which writes the header and can itself fail), so a
                    // failure here still disposes every sub-partition file opened so far.
                    created = index + 1;
                    sub.Position = VdbeSpillRecordCodec.InitializeFile(
                        sub.File.File,
                        VdbeSpillFileKind.HashPartition,
                        _options.Metrics);
                    _options.Metrics.HashPartitionCreated();
                }

                VdbeSpillRecordCodec.ValidateFile(
                    partition.File.File,
                    VdbeSpillFileKind.HashPartition,
                    _options.Metrics);
                foreach (var lease in ReadEntries(partition.File.File, partition.Count, context))
                {
                    using (lease)
                    {
                        context.ThrowIfCancellationRequested();
                        var entry = lease.Entry;
                        var subIndex = entry.Key is null ? 0 : GetSubPartitionIndex(entry.Key, subCount);
                        var sub = subPartitions[subIndex];
                        var position = sub.Position;
                        WriteEntry(sub.File.File, ref position, entry, context);
                        sub.Position = position;
                        sub.Count++;
                    }
                }

                foreach (var sub in subPartitions)
                    sub.File.File.FlushToDisk();

                _splits[topIndex] = subPartitions;
                _splitReservations[topIndex] = reservation;
                // The original partition's data now lives entirely in the sub-partitions;
                // release its temporary file. Its share of _infrastructureReservation stays
                // reserved for the remaining lifetime of this HashSpill (a deliberately
                // conservative over-reservation rather than re-deriving a smaller size).
                partition.Dispose();
                _partitions[topIndex] = null;
                _options.Metrics.HashPartitionSplit();
                return true;
            }
            catch
            {
                for (var index = 0; index < created; index++)
                    subPartitions[index]?.Dispose();
                reservation.Dispose();
                throw;
            }
        }

        private int ChooseSplitPartitionCount(Partition partition, VdbeJoinExecutionContext context)
        {
            // Mirrors turso-src/core/vdbe/hash_table.rs choose_partition_count, but sizes the
            // "average entry" against the same in-memory footprint TryLoadPartitionCore
            // actually charges (VdbeManagedFootprint.EstimateHashBuildEntryFromEncodedLength),
            // not raw on-disk bytes: fixed per-entry overhead (array/object headers) can
            // dominate the real retained cost for small payloads, and sizing purely off disk
            // bytes would under-provision the split fan-out for exactly that case.
            var payloadBytes = Math.Max(0L, partition.Position - VdbeSpillRecordCodec.FileHeaderSize);
            var averageOnDiskEntryBytes = payloadBytes / Math.Max(1, partition.Count);
            var averageEntryBytes = Math.Max(
                1L,
                VdbeManagedFootprint.EstimateHashBuildEntryFromEncodedLength(
                    averageOnDiskEntryBytes,
                    _columnCount,
                    _rowIdCount));
            var targetPartitionBytes = Math.Max(context.Memory.LimitBytes / 2, averageEntryBytes);
            var targetEntriesPerPartition = Math.Max(1L, targetPartitionBytes / averageEntryBytes);
            var rawCount = ((long)partition.Count + targetEntriesPerPartition - 1) / targetEntriesPerPartition;
            // Require the computed fan-out to unambiguously clear the clamped minimum
            // before committing to a split: a marginal rawCount of 2 is frequently just
            // noise from a tight overall budget (where splitting cannot help - even a
            // single entry barely fits regardless of how finely the partition is divided),
            // not genuine skew/size that finer partitioning would resolve.
            if (rawCount <= MinSplitPartitionCount)
                return 1; // Not worth splitting: caller (TrySplitPartition) skips when <= 1.
            var clamped = Math.Clamp(rawCount, (long)MinSplitPartitionCount, (long)MaxPartitionCount);
            return (int)NextPowerOfTwo(clamped);
        }

        private static long NextPowerOfTwo(long value)
        {
            var result = 1L;
            while (result < value)
                result <<= 1;
            return result;
        }

        public IEnumerable<BuildEntryLease> ReadBuildOrder(VdbeJoinExecutionContext context)
        {
            if (_buildOrder is null)
                return [];
            VdbeSpillRecordCodec.ValidateFile(
                _buildOrder.File,
                VdbeSpillFileKind.HashBuildOrder,
                _options.Metrics);
            return ReadEntries(_buildOrder.File, _totalBuildEntries, context);
        }

        public void MarkMatched(long ordinal, VdbeJoinExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            if (_matched is null)
                throw new InvalidOperationException("This hash spill does not track unmatched build rows.");
            var position = checked(VdbeSpillRecordCodec.FileHeaderSize + ordinal);
            VdbeSpillRecordCodec.WriteByte(_matched.File, ref position, 1, _options.Metrics);
        }

        public bool IsMatched(long ordinal, VdbeJoinExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            if (_matched is null)
                throw new InvalidOperationException("This hash spill does not track unmatched build rows.");
            var position = checked(VdbeSpillRecordCodec.FileHeaderSize + ordinal);
            if (position >= _matched.File.Length)
                return false;
            return VdbeSpillRecordCodec.ReadBoolean(
                _matched.File,
                ref position,
                _options.Metrics,
                "hash match-map");
        }

        private void WriteEntry(
            IFile file,
            ref long position,
            BuildEntry entry,
            VdbeJoinExecutionContext context)
        {
            var start = position;
            try
            {
                VdbeSpillRecordCodec.BeginRecord(ref position);
                VdbeSpillRecordCodec.WriteInt64(file, ref position, entry.Ordinal, _options.Metrics);
                VdbeSpillRecordCodec.WriteByte(
                    file,
                    ref position,
                    entry.Key is null ? (byte)0 : (byte)1,
                    _options.Metrics);
                if (entry.Key is not null)
                    VdbeSpillRecordCodec.WriteString(file, ref position, entry.Key, _options.Metrics);
                VdbeSpillRecordCodec.WriteValues(file, ref position, entry.Row.Values, _options.Metrics);
                foreach (var rowId in entry.Row.RowIds)
                {
                    VdbeSpillRecordCodec.WriteByte(
                        file,
                        ref position,
                        rowId.HasValue ? (byte)1 : (byte)0,
                        _options.Metrics);
                    if (rowId.HasValue)
                        VdbeSpillRecordCodec.WriteInt64(file, ref position, rowId.Value, _options.Metrics);
                }
                context.ThrowIfCancellationRequested();
                VdbeSpillRecordCodec.CompleteRecord(file, start, position, _options.Metrics);
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    file.SetLength(start);
                    position = start;
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(primaryFailure, rollbackFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        private IEnumerable<BuildEntryLease> ReadEntries(
            IFile file,
            int count,
            VdbeJoinExecutionContext context)
        {
            long position = VdbeSpillRecordCodec.FileHeaderSize;
            for (var index = 0; index < count; index++)
            {
                yield return TryReadEntry(
                    file,
                    ref position,
                    context,
                    requireAvailable: true)!;
            }
        }

        private BuildEntryLease? TryReadEntry(
            IFile file,
            ref long position,
            VdbeJoinExecutionContext context,
            bool requireAvailable)
        {
            context.ThrowIfCancellationRequested();
            var recordStart = position;
            var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
                file,
                ref position,
                _options.Metrics);
            var retainedBytes = VdbeManagedFootprint.EstimateHashBuildEntryFromEncodedLength(
                recordEnd - position,
                _columnCount,
                _rowIdCount);
            if (retainedBytes > context.Memory.LimitBytes)
                throw new VdbeMemoryLimitExceededException(context.Memory.LimitBytes, retainedBytes);
            if (!context.Memory.TryRetain(retainedBytes))
            {
                position = recordStart;
                if (requireAvailable)
                    throw new VdbeMemoryLimitExceededException(context.Memory.LimitBytes, retainedBytes);
                return null;
            }

            try
            {
                var ordinal = VdbeSpillRecordCodec.ReadInt64(file, ref position, _options.Metrics);
                var hasKey = VdbeSpillRecordCodec.ReadByte(file, ref position, _options.Metrics);
                var key = hasKey switch
                {
                    0 => null,
                    1 => VdbeSpillRecordCodec.ReadString(
                        file,
                        ref position,
                        recordEnd,
                        _options.Metrics),
                    _ => throw new InvalidDataException($"Unknown hash spill key marker {hasKey}."),
                };
                var values = VdbeSpillRecordCodec.ReadValues(
                    file,
                    ref position,
                    _columnCount,
                    recordEnd,
                    _options.Metrics,
                    context.CancellationToken);
                var rowIds = new long?[_rowIdCount];
                for (var rowIdIndex = 0; rowIdIndex < rowIds.Length; rowIdIndex++)
                {
                    var hasRowId = VdbeSpillRecordCodec.ReadByte(file, ref position, _options.Metrics);
                    rowIds[rowIdIndex] = hasRowId switch
                    {
                        0 => null,
                        1 => VdbeSpillRecordCodec.ReadInt64(file, ref position, _options.Metrics),
                        _ => throw new InvalidDataException($"Unknown hash spill rowid marker {hasRowId}."),
                    };
                }
                VdbeSpillRecordCodec.RequireRecordEnd(position, recordEnd);
                var entry = new BuildEntry(
                    new VdbeJoinRow(values, rowIds),
                    key,
                    ordinal,
                    retainedBytes);
                return new BuildEntryLease(entry, context.Memory, retainedBytes);
            }
            catch
            {
                position = recordStart;
                context.Memory.Release(retainedBytes);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            List<Exception>? cleanupFailures = null;
            foreach (var partition in _partitions)
            {
                if (partition is null)
                    continue;
                try
                {
                    partition.Dispose();
                }
                catch (Exception exception)
                {
                    (cleanupFailures ??= []).Add(exception);
                }
            }

            try
            {
                _buildOrder?.Dispose();
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }

            try
            {
                _matched?.Dispose();
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }

            foreach (var subPartitions in _splits.Values)
            {
                foreach (var subPartition in subPartitions)
                {
                    try
                    {
                        subPartition.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (cleanupFailures ??= []).Add(exception);
                    }
                }
            }

            foreach (var splitReservation in _splitReservations.Values)
            {
                try
                {
                    splitReservation.Dispose();
                }
                catch (Exception exception)
                {
                    (cleanupFailures ??= []).Add(exception);
                }
            }

            if (cleanupFailures is null)
            {
                _infrastructureReservation.Dispose();
                _disposed = true;
            }
            if (cleanupFailures is [var cleanupFailure])
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            if (cleanupFailures is { Count: > 1 })
                throw new AggregateException(cleanupFailures);
        }

        private sealed class Partition : IDisposable
        {
            private VdbeTemporaryFile? _file;

            public VdbeTemporaryFile File =>
                _file ?? throw new InvalidOperationException("Hash spill partition is not initialized.");

            public long Position { get; set; }

            public int Count { get; set; }

            public void SetFile(VdbeTemporaryFile file) => _file = file;

            public void Dispose() => _file?.Dispose();
        }
    }
}
