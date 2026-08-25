using Ahtola.Core.Storage;
using System.Runtime.ExceptionServices;

namespace Ahtola.Core.Execution;

internal sealed class VdbeJoinExecutionContext(
    VdbeExecutionOptions options,
    VdbeExecutionMemory memory)
{
    private List<IDisposable>? _pendingCleanup;

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
        if (retryable is not null
            && !(_pendingCleanup?.Contains(retryable) ?? false))
        {
            (_pendingCleanup ??= []).Add(retryable);
        }
    }

    public Exception? TakeCleanupFailure()
    {
        var failure = CleanupFailure;
        CleanupFailure = null;
        return failure;
    }

    public bool HasPendingCleanup => _pendingCleanup is { Count: > 0 };

    public void RetryPendingCleanup()
    {
        if (_pendingCleanup is not { Count: > 0 } pending)
            return;

        List<Exception>? failures = null;
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            try
            {
                pending[index].Dispose();
                pending.RemoveAt(index);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is [var failure])
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);
    }

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
    private const int PartitionCount = 16;
    private const ulong HashOffsetBasis = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;

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
        LoadedPartition? loadedPartition = null;
        var unloadablePartition = -1;
        var trackUnmatchedBuild = buildIsRight && plan.Kind is VdbeJoinKind.Right or VdbeJoinKind.Full;
        HashBuildBuffer? buffered = HashBuildBuffer.TryCreate(context.Memory, trackUnmatchedBuild);
        var emitted = 0;

        try
        {
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
                    spill = new HashSpill(
                        context.Options,
                        buildNode.ColumnCount,
                        buildNode.SourceCount,
                        trackUnmatchedBuild);
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
                ? buffered!.CreateMatchedMap()
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
                        if (buffered!.TryGetCandidates(key, out var candidateIndices))
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
                        var partition = GetPartition(key);
                        if (loadedPartition?.Index != partition)
                        {
                            loadedPartition?.Dispose();
                            loadedPartition = null;
                            if (unloadablePartition != partition)
                            {
                                loadedPartition = spill.TryLoadPartition(partition, context);
                                unloadablePartition = loadedPartition is null ? partition : -1;
                            }
                        }

                        if (loadedPartition is not null)
                        {
                            foreach (var build in loadedPartition.Find(key))
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
                        else
                        {
                            context.Options.Metrics.HashPartitionFallbackScan();
                            foreach (var lease in spill.ReadPartition(partition, context))
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
                loadedPartition?.Dispose();
                loadedPartition = null;
                var nullProbe = NullRow(probeNode);
                if (spill is null)
                {
                    for (var index = 0; index < buffered!.Count; index++)
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
                loadedPartition?.Dispose();
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
            var growthBytes = checked(
                VdbeManagedFootprint.EstimateReferenceListStorage(entriesCapacity)
                - VdbeManagedFootprint.EstimateReferenceListStorage(entries.Capacity));

            List<int>? bucket = null;
            var isNewKey = key is not null && !buckets.TryGetValue(key, out bucket);
            var bucketCapacity = 0;
            if (key is not null)
            {
                if (isNewKey)
                {
                    growthBytes = checked(
                        growthBytes
                        + VdbeManagedFootprint.BucketListObjectBytes
                        + VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count + 1)
                        - VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count));
                    bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(0, 1);
                    growthBytes = checked(
                        growthBytes
                        + VdbeManagedFootprint.EstimateInt32ListStorage(bucketCapacity));
                }
                else
                {
                    bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(
                        bucket!.Capacity,
                        bucket.Count + 1);
                    growthBytes = checked(
                        growthBytes
                        + VdbeManagedFootprint.EstimateInt32ListStorage(bucketCapacity)
                        - VdbeManagedFootprint.EstimateInt32ListStorage(bucket.Capacity));
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
                _retainedBytes = checked(_retainedBytes + retainedBytes);
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
            int index,
            VdbeExecutionMemory memory,
            Dictionary<string, List<BuildEntry>> buckets,
            long retainedBytes,
            int retainedRows)
        {
            Index = index;
            _memory = memory;
            _buckets = buckets;
            _retainedBytes = retainedBytes;
            _retainedRows = retainedRows;
        }

        public int Index { get; }

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

    private sealed class HashSpill : IDisposable
    {
        private readonly VdbeExecutionOptions _options;
        private readonly int _columnCount;
        private readonly int _rowIdCount;
        private readonly Partition[] _partitions;
        private readonly VdbeTemporaryFile? _buildOrder;
        private readonly VdbeTemporaryFile? _matched;
        private long _buildOrderPosition;
        private bool _disposed;

        public HashSpill(
            VdbeExecutionOptions options,
            int columnCount,
            int rowIdCount,
            bool trackUnmatchedBuild)
        {
            _options = options;
            _columnCount = columnCount;
            _rowIdCount = rowIdCount;
            _partitions = new Partition[PartitionCount];
            try
            {
                for (var index = 0; index < _partitions.Length; index++)
                {
                    _partitions[index] = CreatePartition(options, index);
                    options.Metrics.HashPartitionCreated();
                }

                if (trackUnmatchedBuild)
                {
                    _buildOrder = VdbeTemporaryFile.Create(options, "hash-build-order");
                    _buildOrderPosition = VdbeSpillRecordCodec.InitializeFile(
                        _buildOrder.File,
                        VdbeSpillFileKind.HashBuildOrder,
                        options.Metrics);
                    _matched = VdbeTemporaryFile.Create(options, "hash-matches");
                    VdbeSpillRecordCodec.InitializeFile(
                        _matched.File,
                        VdbeSpillFileKind.HashMatchMap,
                        options.Metrics);
                }
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(primaryFailure, cleanupFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        public void WriteBuild(BuildEntry entry, VdbeJoinExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            var partition = _partitions[entry.Key is null ? 0 : GetPartition(entry.Key)];
            var position = partition.Position;
            WriteEntry(partition.File.File, ref position, entry, context);
            partition.Position = position;
            partition.Count++;

            if (_buildOrder is not null)
                WriteEntry(_buildOrder.File, ref _buildOrderPosition, entry, context);
        }

        public void CompleteBuild(VdbeJoinExecutionContext context)
        {
            context.ThrowIfCancellationRequested();
            foreach (var partition in _partitions)
                partition.File.File.FlushToDisk();
            _buildOrder?.File.FlushToDisk();
        }

        public IEnumerable<BuildEntryLease> ReadPartition(
            int partitionIndex,
            VdbeJoinExecutionContext context)
        {
            var partition = _partitions[partitionIndex];
            _options.Metrics.HashPartitionScanned();
            VdbeSpillRecordCodec.ValidateFile(
                partition.File.File,
                VdbeSpillFileKind.HashPartition,
                _options.Metrics);
            return ReadEntries(partition.File.File, partition.Count, context);
        }

        public LoadedPartition? TryLoadPartition(
            int partitionIndex,
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
                var partition = _partitions[partitionIndex];
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
                    var bucketCapacity = 0;
                    if (isNewKey)
                    {
                        growthBytes = checked(
                            VdbeManagedFootprint.BucketListObjectBytes
                            + VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count + 1)
                            - VdbeManagedFootprint.EstimateDictionaryStorage(buckets.Count));
                        bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(0, 1);
                        growthBytes = checked(
                            growthBytes
                            + VdbeManagedFootprint.EstimateReferenceListStorage(bucketCapacity));
                    }
                    else
                    {
                        bucketCapacity = VdbeManagedFootprint.GetListCapacityForCount(
                            bucket!.Capacity,
                            bucket.Count + 1);
                        growthBytes = checked(
                            VdbeManagedFootprint.EstimateReferenceListStorage(bucketCapacity)
                            - VdbeManagedFootprint.EstimateReferenceListStorage(bucket.Capacity));
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
                    }
                    catch
                    {
                        context.Memory.Release(growthBytes, rows: 0);
                        throw;
                    }

                    retainedBytes = checked(
                        retainedBytes
                        + growthBytes
                        + lease.RetainedBytes);
                    retainedRows++;
                    lease.Transfer();
                }

                transferred = true;
                _options.Metrics.HashPartitionLoaded();
                return new LoadedPartition(
                    partitionIndex,
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

        public IEnumerable<BuildEntryLease> ReadBuildOrder(VdbeJoinExecutionContext context)
        {
            if (_buildOrder is null)
                return [];
            VdbeSpillRecordCodec.ValidateFile(
                _buildOrder.File,
                VdbeSpillFileKind.HashBuildOrder,
                _options.Metrics);
            return ReadEntries(
                _buildOrder.File,
                _partitions.Sum(static partition => partition.Count),
                context);
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
            return VdbeSpillRecordCodec.ReadByte(_matched.File, ref position, _options.Metrics) != 0;
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
                    1 => VdbeSpillRecordCodec.ReadString(file, ref position, _options.Metrics),
                    _ => throw new InvalidDataException($"Unknown hash spill key marker {hasKey}."),
                };
                var values = VdbeSpillRecordCodec.ReadValues(
                    file,
                    ref position,
                    _columnCount,
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
                    partition.File.Dispose();
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

            if (cleanupFailures is null)
                _disposed = true;
            if (cleanupFailures is [var cleanupFailure])
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            if (cleanupFailures is { Count: > 1 })
                throw new AggregateException(cleanupFailures);
        }

        private static Partition CreatePartition(VdbeExecutionOptions options, int index)
        {
            var file = VdbeTemporaryFile.Create(options, $"hash-p{index:D3}");
            try
            {
                var position = VdbeSpillRecordCodec.InitializeFile(
                    file.File,
                    VdbeSpillFileKind.HashPartition,
                    options.Metrics);
                return new Partition(file, position);
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    file.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(primaryFailure, cleanupFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        private sealed class Partition(VdbeTemporaryFile file, long position)
        {
            public VdbeTemporaryFile File { get; } = file;

            public long Position { get; set; } = position;

            public int Count { get; set; }
        }
    }
}
