using Ahtola.Core.Storage;
using System.Runtime.ExceptionServices;

namespace Ahtola.Core.Execution;

internal sealed class VdbeJoinExecutionContext(
    VdbeExecutionOptions options,
    VdbeExecutionMemory memory)
{
    public VdbeExecutionOptions Options { get; } = options;

    public VdbeExecutionMemory Memory { get; } = memory;

    public CancellationToken CancellationToken { get; private set; }

    public void SetCancellationToken(CancellationToken cancellationToken) =>
        CancellationToken = cancellationToken;

    public void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();

    public Exception? CleanupFailure { get; private set; }

    public void RecordCleanupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        CleanupFailure = CleanupFailure is null
            ? exception
            : new AggregateException(CleanupFailure, exception);
    }

    public Exception? TakeCleanupFailure()
    {
        var failure = CleanupFailure;
        CleanupFailure = null;
        return failure;
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
        var retained = new List<BuildEntry>();
        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        HashSpill? spill = null;
        LoadedPartition? loadedPartition = null;
        var unloadablePartition = -1;
        var trackUnmatchedBuild = buildIsRight && plan.Kind is VdbeJoinKind.Right or VdbeJoinKind.Full;
        var emitted = 0;

        try
        {
            long ordinal = 0;
            foreach (var row in buildNode.Enumerate(maximumRows: null, context))
            {
                context.ThrowIfCancellationRequested();
                var key = buildKey(row);
                var retainedBytes = checked(
                    VdbeSpillRecordCodec.EstimateRetainedBytes(row.Values, key, row.RowIds.Length)
                    + (trackUnmatchedBuild ? 1 : 0));
                var entry = new BuildEntry(row, key, ordinal++, retainedBytes);

                if (spill is null && context.Memory.TryRetain(retainedBytes))
                {
                    var index = retained.Count;
                    retained.Add(entry);
                    if (key is not null)
                    {
                        if (!buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = [];
                            buckets.Add(key, bucket);
                        }
                        bucket.Add(index);
                    }
                    continue;
                }

                if (!context.Options.AllowTemporaryFileSpill)
                    throw new VdbeMemoryLimitExceededException(context.Memory.LimitBytes, retainedBytes);

                if (spill is null)
                {
                    spill = new HashSpill(
                        context.Options,
                        buildNode.ColumnCount,
                        buildNode.SourceCount,
                        trackUnmatchedBuild);
                    foreach (var buffered in retained)
                        spill.WriteBuild(buffered, context);
                    foreach (var buffered in retained)
                        context.Memory.Release(buffered.RetainedBytes);
                    retained.Clear();
                    buckets.Clear();
                }

                spill.WriteBuild(entry, context);
            }

            if (spill is not null)
                spill.CompleteBuild(context);

            bool[]? matched = trackUnmatchedBuild && spill is null ? new bool[retained.Count] : null;
            foreach (var probe in probeNode.Enumerate(maximumRows: null, context))
            {
                context.ThrowIfCancellationRequested();
                var matchedProbe = false;
                var key = probeKey(probe);

                if (key is not null)
                {
                    if (spill is null)
                    {
                        if (buckets.TryGetValue(key, out var candidateIndices))
                        {
                            foreach (var buildIndex in candidateIndices)
                            {
                                context.ThrowIfCancellationRequested();
                                var build = retained[buildIndex];
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

                        IEnumerable<BuildEntry> candidates;
                        if (loadedPartition is not null)
                        {
                            candidates = loadedPartition.Find(key);
                        }
                        else
                        {
                            context.Options.Metrics.HashPartitionFallbackScan();
                            candidates = spill.ReadPartition(partition, context);
                        }

                        foreach (var build in candidates)
                        {
                            context.ThrowIfCancellationRequested();
                            if (!string.Equals(build.Key, key, StringComparison.Ordinal))
                                continue;

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
                    for (var index = 0; index < retained.Count; index++)
                    {
                        context.ThrowIfCancellationRequested();
                        if (matched![index])
                            continue;
                        yield return Combine(nullProbe, retained[index].Row);
                        if (maximumRows is { } maximum && ++emitted >= maximum)
                            yield break;
                    }
                }
                else
                {
                    foreach (var build in spill.ReadBuildOrder(context))
                    {
                        context.ThrowIfCancellationRequested();
                        if (spill.IsMatched(build.Ordinal, context))
                            continue;
                        yield return Combine(nullProbe, build.Row);
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
            foreach (var entry in retained)
            {
                try
                {
                    context.Memory.Release(entry.RetainedBytes);
                }
                catch (Exception exception)
                {
                    context.RecordCleanupFailure(exception);
                }
            }
            try
            {
                spill?.Dispose();
            }
            catch (Exception exception)
            {
                context.RecordCleanupFailure(exception);
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

    private sealed record BuildEntry(
        VdbeJoinRow Row,
        string? Key,
        long Ordinal,
        long RetainedBytes);

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

        public IEnumerable<BuildEntry> ReadPartition(
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
            var retainedBytes = 0L;
            var retainedRows = 0;
            var buckets = new Dictionary<string, List<BuildEntry>>(StringComparer.Ordinal);
            var transferred = false;
            try
            {
                foreach (var entry in ReadPartition(partitionIndex, context))
                {
                    var bytes = VdbeSpillRecordCodec.EstimateRetainedBytes(
                        entry.Row.Values,
                        entry.Key,
                        entry.Row.RowIds.Length);
                    if (!context.Memory.TryRetain(bytes))
                        return null;

                    retainedBytes = checked(retainedBytes + bytes);
                    retainedRows++;
                    if (entry.Key is null)
                        continue;
                    if (!buckets.TryGetValue(entry.Key, out var bucket))
                    {
                        bucket = [];
                        buckets.Add(entry.Key, bucket);
                    }
                    bucket.Add(entry with { RetainedBytes = bytes });
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

        public IEnumerable<BuildEntry> ReadBuildOrder(VdbeJoinExecutionContext context)
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

        private IEnumerable<BuildEntry> ReadEntries(
            IFile file,
            int count,
            VdbeJoinExecutionContext context)
        {
            long position = VdbeSpillRecordCodec.FileHeaderSize;
            for (var index = 0; index < count; index++)
            {
                context.ThrowIfCancellationRequested();
                var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
                    file,
                    ref position,
                    _options.Metrics);
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
                yield return new BuildEntry(
                    new VdbeJoinRow(values, rowIds),
                    key,
                    ordinal,
                    RetainedBytes: 0);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

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
