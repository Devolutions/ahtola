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

    // Upper bound on how many probe rows are grouped into one partition-major processing
    // pass (see FlushProbeBatch). This is a genuine probe-side scheduling mechanism, not
    // just a cache-tier decision: every probe in a batch that targets the same spill
    // partition is answered by loading/building/scanning that partition exactly once for
    // the whole group, regardless of how the probe order interleaves between partitions -
    // mirroring the intent (not the async chunked-IO mechanics) of turso-src/core/vdbe/
    // hash_table.rs's probe-side buffering (ProbeSpillState/buffer_probe_row/grace_begin),
    // scoped here to an in-memory batch with strict output-order preservation rather than a
    // full two-phase grace pass that would reorder emission to partition order. A larger
    // value amortizes reload/rescan cost over more probes per partition touch (the actual
    // fix for cyclic/interleaved thrashing); memory for the batch itself is still bounded by
    // the shared execution budget (see the TryRetain/flush-and-retry logic in EnumerateCore),
    // so this cap only limits per-partition-group bookkeeping overhead, not correctness.
    private const int MaxProbeBatchRows = 256;

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

    /// <summary>
    /// One probe row buffered for grouped, partition-major processing (see
    /// <see cref="MaxProbeBatchRows"/> and <c>FlushProbeBatch</c>). <see cref="RetainedBytes"/>
    /// is 0 for a probe processed via a singleton batch that bypassed real buffering because
    /// even an empty batch could not afford it (see the fallback path in EnumerateCore); a
    /// zero value signals the cleanup path to skip releasing memory that was never retained.
    /// </summary>
    private readonly record struct ProbeBatchEntry(VdbeJoinRow Probe, string? Key, long RetainedBytes);


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
        // Tracks the largest single build-row retained-bytes estimate observed during the
        // build phase (regardless of whether that row ends up buffered or spilled). Used as
        // a reserve when deciding how many probes may accumulate into one probe-scheduling
        // batch (see the accumulation loop below): no single spilled build-side entry decode
        // can ever need more than this many bytes (every build row was itself required to
        // fit within the shared budget before being written), so keeping at least this much
        // headroom free whenever a SECOND-OR-LATER probe joins a batch guarantees that at
        // least one such decode can always succeed later, without ever needing to release
        // any probe's own accounting early. This is a genuine, unavoidable cost of grouping
        // (the pre-batching design never needed it, since it never held more than one probe
        // at a time): TryProcessGroup's own graceful fallback (see its remarks) handles
        // pressure this reserve does not anticipate (e.g. several buffered output rows from
        // duplicate-key fanout), so this reserve only needs to cover the single-decode case.
        var maxBuildEntryBytes = 0L;

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
                maxBuildEntryBytes = Math.Max(maxBuildEntryBytes, retainedBytes);
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

            // Buffered, partition-major probe scheduling for the spilled path only (the
            // small in-memory HashBuildBuffer path below never thrashes, since its
            // dictionary is never evicted). Probe rows are consumed from the single-pass
            // probeNode enumerator exactly once, in order, and accumulated into a bounded
            // batch (see ProbeBatchEntry) rather than answered immediately one at a time.
            // Once a batch is flushed, every probe in it that targets the same spill
            // partition is normally answered together - loading/building/scanning that
            // partition exactly once for the whole group, not once per probe - which is the
            // actual structural fix for repeated reload/rescan under a cyclic or interleaved
            // probe order, not merely a cheaper fallback tier. Batching never changes
            // emission order or drops/duplicates a match: FlushProbeBatch always replays
            // results in original within-batch probe order (buffering a group's results,
            // with their own accounted memory, only until every earlier probe in the batch
            // is also ready), and gracefully falls back to unbuffered, immediately-streamed
            // per-probe resolution - identical in memory profile to this file's original,
            // pre-batching design - for whatever it cannot afford to buffer (see
            // FlushProbeBatch remarks for the full accounting and fallback contract).
            // Batches themselves are flushed and yielded strictly in probe-arrival order, so
            // the final sequence is always identical to fully streaming per-probe processing.
            List<ProbeBatchEntry>? pendingBatch = null;

            IEnumerable<VdbeJoinRow> DrainPendingBatch()
            {
                if (pendingBatch is not { Count: > 0 } batch)
                    yield break;

                // Ownership of each entry's retained-memory accounting transfers to
                // FlushProbeBatch here: it releases each probe's bytes itself, exactly when
                // that probe's row data is genuinely no longer needed (after it is answered,
                // whether via a successful group or the individual streaming fallback),
                // never earlier. This is deliberate: an early bulk release here (before the
                // row data is actually done being used) would underreport real memory
                // usage for as long as FlushProbeBatch is still working with these rows.
                pendingBatch = null;

                foreach (var row in FlushProbeBatch(
                    batch, spill!, residency, plan, buildNode, buildIsRight, trackUnmatchedBuild, context))
                    yield return row;
            }

            foreach (var probe in probeNode.Enumerate(maximumRows: null, context))
            {
                context.ThrowIfCancellationRequested();

                if (spill is null)
                {
                    var matchedProbe = false;
                    var key = probeKey(probe);
                    if (key is not null && buffered?.TryGetCandidates(key, out var candidateIndices) == true)
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

                    if (!matchedProbe && buildIsRight && plan.Kind is VdbeJoinKind.Left or VdbeJoinKind.Full)
                    {
                        yield return Combine(probe, NullRow(buildNode));
                        if (maximumRows is { } maximum && ++emitted >= maximum)
                            yield break;
                    }
                    continue;
                }

                var probeKeyValue = probeKey(probe);

                if (pendingBatch is { Count: >= MaxProbeBatchRows })
                {
                    foreach (var row in DrainPendingBatch())
                    {
                        yield return row;
                        if (maximumRows is { } maximum && ++emitted >= maximum)
                            yield break;
                    }
                }

                if (pendingBatch is not { Count: > 0 })
                {
                    // First probe of a fresh batch: exactly like the pre-batching per-probe
                    // design, one lone buffered probe costs nothing extra to retain -
                    // nothing else is competing for headroom yet, and it may end up being
                    // the ONLY entry in this batch (no grouping benefit either way). Only
                    // once a SECOND probe wants to join it (below) does this become genuine
                    // extra concurrent memory pressure the original design never had, and
                    // that is where honest retention applies.
                    pendingBatch = [new ProbeBatchEntry(probe, probeKeyValue, RetainedBytes: 0)];
                    continue;
                }

                var entryBytes = VdbeManagedFootprint.EstimateHashBuildEntry(
                    probe.Values,
                    probeKeyValue,
                    probe.RowIds.Length);

                // Joining an already-started batch additionally requires that enough
                // headroom remains, after retaining this probe, to cover BOTH a transient
                // build-entry decode (maxBuildEntryBytes - see that field's remarks) AND the
                // residency cache's own need to establish (load or index) at least one
                // partition concurrently with the batch's retained input/output: a reserve
                // of only the former lets batch accumulation greedily consume nearly the
                // entire budget regardless of how large it is (since a single-row decode is
                // typically tiny relative to the budget), starving the residency cache down
                // to zero admissible partitions and forcing every probe through the raw
                // fallback-scan tier - defeating grouping's whole purpose even when the
                // overall budget is otherwise generous. Reserving a fraction of the total
                // budget in addition keeps that headroom scaling WITH the budget.
                var residencyReserve = context.Memory.LimitBytes / 4;
                var reserve = Math.Max(maxBuildEntryBytes, residencyReserve);
                if (context.Memory.AvailableBytes - entryBytes >= reserve
                    && context.Memory.TryRetain(entryBytes))
                {
                    pendingBatch.Add(new ProbeBatchEntry(probe, probeKeyValue, entryBytes));
                    continue;
                }

                // Not enough reserve headroom to add a second probe to the current batch:
                // flush what is pending (its entries release their own accounting, if any, as
                // FlushProbeBatch finishes with them) and let this probe become the fresh,
                // again cost-free first entry of a new batch.
                foreach (var row in DrainPendingBatch())
                {
                    yield return row;
                    if (maximumRows is { } maximum && ++emitted >= maximum)
                        yield break;
                }

                pendingBatch = [new ProbeBatchEntry(probe, probeKeyValue, RetainedBytes: 0)];
            }

            if (spill is not null)
            {
                foreach (var row in DrainPendingBatch())
                {
                    yield return row;
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

    /// <summary>
    /// Answers every buffered probe in <paramref name="batch"/> that targets the spilled
    /// build side, grouping probes by the distinct partition they touch so that partition is
    /// resolved (loaded/index-built/scanned) exactly once for the whole group instead of once
    /// per probe - the actual probe-side scheduling fix for repeated reload/rescan under an
    /// interleaved or cyclic probe order, not merely a cheaper fallback tier. Also serves the
    /// unbatched (<see cref="ProbeBatchEntry.RetainedBytes"/> == 0) singleton fallback path in
    /// EnumerateCore, so both share one correctness-checked implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grouping happens in two passes because splitting a top-level partition (see
    /// <c>HashSpill.TrySplitPartition</c>) can only be decided the first time any probe in
    /// this batch (or an earlier one) touches it, and a split reroutes different keys within
    /// the same top-level partition to different sub-partitions. Pass 1 groups by the stable
    /// top-level index (a pure hash computation, never affected by splitting) and resolves
    /// one representative key per top-level group purely to let any pending split decision
    /// finalize as a side effect (the resolution result itself is discarded). Pass 2 then
    /// re-derives each probe's final (possibly post-split) <see cref="PartitionKey"/> and
    /// groups by that, so every probe is matched against the exact partition it actually
    /// belongs to, regardless of when the split happened.
    /// </para>
    /// <para>
    /// Output-order preservation and memory accounting: a probe's own input row bytes stay
    /// retained for as long as that probe's data is genuinely still needed (through whichever
    /// group answers it, or through the individual streaming path below), never released
    /// early. Because grouping necessarily answers probes out of original order (by
    /// partition, not by arrival), a group's *results* must be buffered until every earlier
    /// probe's results are also ready, so they can be replayed strictly in original batch
    /// order; every buffered result row is itself retained via the shared memory budget
    /// (<see cref="EstimateCombinedRowBytes"/>), and released the moment it is replayed. A
    /// group's results are committed all-or-nothing: if retaining any one of its result rows
    /// fails, every result already retained for that group is rolled back and discarded (see
    /// <c>TryProcessGroup</c>), and every probe in that group - plus every probe in every
    /// group not yet processed - falls back to individual, unbuffered, immediately-streamed
    /// resolution (see <c>EmitOneIndex</c>'s unsettled branch), exactly mirroring the
    /// unbatched per-probe design and its memory profile, so a memory-constrained batch can
    /// never drop, reorder, or fail a match that streaming alone could always answer; it only
    /// loses the cross-probe reload/rescan-avoidance benefit for whatever could not be
    /// buffered.
    /// </para>
    /// </remarks>
    private static IEnumerable<VdbeJoinRow> FlushProbeBatch(
        IReadOnlyList<ProbeBatchEntry> batch,
        HashSpill spill,
        PartitionResidencyCache residency,
        VdbeJoinOperatorPlan plan,
        VdbeJoinPlanNode buildNode,
        bool buildIsRight,
        bool trackUnmatchedBuild,
        VdbeJoinExecutionContext context)
    {
        context.Options.Metrics.HashProbeBatchFlushed();

        var matchesByLocalIndex = new List<VdbeJoinRow>?[batch.Count];
        var outputRetainedBytes = new long[batch.Count];
        var settled = new bool[batch.Count];
        var inputReleased = new bool[batch.Count];
        var nextEmitIndex = 0;

        // NULL join keys never match anything (SQL semantics), so they never join a group;
        // they are "settled" from the start and only ever eligible for outer null-extension.
        for (var localIndex = 0; localIndex < batch.Count; localIndex++)
        {
            if (batch[localIndex].Key is null)
                settled[localIndex] = true;
        }

        void ReleaseInput(int localIndex)
        {
            if (inputReleased[localIndex])
                return;
            inputReleased[localIndex] = true;
            if (batch[localIndex].RetainedBytes > 0)
                context.Memory.Release(batch[localIndex].RetainedBytes);
        }

        IEnumerable<VdbeJoinRow> EmitOneIndex(int localIndex)
        {
            if (settled[localIndex])
            {
                var matches = matchesByLocalIndex[localIndex];
                if (matches is not null)
                {
                    foreach (var row in matches)
                        yield return row;
                    if (outputRetainedBytes[localIndex] > 0)
                    {
                        context.Memory.Release(outputRetainedBytes[localIndex], rows: matches.Count);
                        outputRetainedBytes[localIndex] = 0;
                    }
                }
                else if (buildIsRight && plan.Kind is VdbeJoinKind.Left or VdbeJoinKind.Full)
                {
                    yield return Combine(batch[localIndex].Probe, NullRow(buildNode));
                }
            }
            else
            {
                // Not answered via grouping - its own group's buffered output could not be
                // retained (see TryProcessGroup's remarks), so this index alone falls back
                // to individual streaming while every other group still attempts (and often
                // succeeds at) real grouping. Resolve and stream matches immediately, exactly
                // like the pre-batching per-probe design: nothing here is buffered beyond
                // the one match being yielded, so it carries no extra memory risk versus
                // that original design.
                var key = batch[localIndex].Key;
                var probe = batch[localIndex].Probe;
                var matchedProbe = false;
                if (key is not null)
                {
                    var resolution = residency.Resolve(spill, key, context);
                    foreach (var combined in MatchAgainstResolution(
                        resolution, spill, key, probe, plan, buildIsRight, trackUnmatchedBuild, context, residency))
                    {
                        matchedProbe = true;
                        yield return combined;
                    }
                }

                if (!matchedProbe && buildIsRight && plan.Kind is VdbeJoinKind.Left or VdbeJoinKind.Full)
                    yield return Combine(probe, NullRow(buildNode));
            }

            ReleaseInput(localIndex);
        }

        IEnumerable<VdbeJoinRow> EmitReadyPrefix()
        {
            while (nextEmitIndex < batch.Count && settled[nextEmitIndex])
            {
                foreach (var row in EmitOneIndex(nextEmitIndex))
                    yield return row;
                nextEmitIndex++;
            }
        }

        // Attempts to answer every probe in localIndices against resolution, retaining each
        // result row's memory as it is produced. All-or-nothing: if any single row's
        // retention fails, every row already retained for THIS group is rolled back
        // (released) and discarded, and this returns false without committing anything -
        // only THIS group's own probes fall back to the individual/streaming path (see
        // EmitOneIndex's unsettled branch), which never needs this buffering (and thus never
        // fails this same way); every OTHER group in the batch still attempts real grouping
        // (see the caller's remarks for why one group's failure must not be treated as
        // permanent for the rest of the batch). The finally below guarantees this rollback
        // happens even when a DIFFERENT exception (cancellation, a user predicate fault, an
        // unrelated I/O fault) propagates out mid-group: such exceptions are never swallowed
        // - only their in-progress retained bytes are still released before they continue
        // propagating - unlike VdbeMemoryLimitExceededException, which IS deliberately
        // absorbed here (see
        // the catch below) to trigger the graceful streaming fallback instead of faulting
        // the whole statement.
        bool TryProcessGroup(IReadOnlyList<int> localIndices, PartitionResidencyCache.Resolution resolution)
        {
            var groupMatches = new List<VdbeJoinRow>?[localIndices.Count];
            var totalRetainedBytes = 0L;
            var totalRetainedRows = 0L;
            var committed = false;

            bool TryAddMatch(int slot, VdbeJoinRow combined)
            {
                var bytes = EstimateCombinedRowBytes(combined);
                if (!context.Memory.TryRetain(bytes))
                    return false;
                (groupMatches[slot] ??= []).Add(combined);
                totalRetainedBytes = checked(totalRetainedBytes + bytes);
                totalRetainedRows = checked(totalRetainedRows + 1);
                return true;
            }

            try
            {
                if (resolution.Loaded is not null)
                {
                    for (var slot = 0; slot < localIndices.Count; slot++)
                    {
                        var localIndex = localIndices[slot];
                        var key = batch[localIndex].Key!;
                        var probe = batch[localIndex].Probe;
                        foreach (var build in resolution.Loaded.Find(key))
                        {
                            context.ThrowIfCancellationRequested();
                            var combined = Combine(build.Row, probe, buildIsRight);
                            if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                                continue;

                            if (!TryAddMatch(slot, combined))
                                return false;
                            if (trackUnmatchedBuild)
                                spill.MarkMatched(build.Ordinal, context);
                        }
                    }
                }
                else if (resolution.Index is not null)
                {
                    for (var slot = 0; slot < localIndices.Count; slot++)
                    {
                        var localIndex = localIndices[slot];
                        var key = batch[localIndex].Key!;
                        var probe = batch[localIndex].Probe;
                        foreach (var lease in spill.ReadPartitionIndexMatches(
                            resolution.Id, resolution.Index, key, context, residency))
                        {
                            VdbeJoinRow? combined = null;
                            long? matchedOrdinal = null;
                            using (lease)
                            {
                                context.ThrowIfCancellationRequested();
                                var build = lease.Entry;
                                var candidate = Combine(build.Row, probe, buildIsRight);
                                if (Matches(plan, build.Row, probe, candidate, buildIsRight))
                                {
                                    combined = candidate;
                                    if (trackUnmatchedBuild)
                                        matchedOrdinal = build.Ordinal;
                                }
                            }

                            if (combined is null)
                                continue;
                            if (!TryAddMatch(slot, combined))
                                return false;
                            if (matchedOrdinal is { } ordinal)
                                spill.MarkMatched(ordinal, context);
                        }
                    }
                }
                else
                {
                    // Neither tier fit this partition: answer every probe in this group
                    // with ONE sequential scan instead of one scan per probe - the actual,
                    // measurable I/O reduction for a partition too large for even the
                    // lightweight index, and the direct fix for "oversized partitions
                    // rescan per probe" when several such probes share a batch.
                    context.Options.Metrics.HashPartitionFallbackScan();
                    var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                    for (var slot = 0; slot < localIndices.Count; slot++)
                    {
                        var key = batch[localIndices[slot]].Key!;
                        if (!byKey.TryGetValue(key, out var slotsForKey))
                        {
                            slotsForKey = [];
                            byKey.Add(key, slotsForKey);
                        }
                        slotsForKey.Add(slot);
                    }

                    foreach (var lease in spill.ReadPartitionByKey(resolution.Id, context, residency))
                    {
                        using (lease)
                        {
                            context.ThrowIfCancellationRequested();
                            var build = lease.Entry;
                            if (build.Key is null || !byKey.TryGetValue(build.Key, out var slotsForKey))
                                continue;

                            foreach (var slot in slotsForKey)
                            {
                                var localIndex = localIndices[slot];
                                var probe = batch[localIndex].Probe;
                                var combined = Combine(build.Row, probe, buildIsRight);
                                if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                                    continue;

                                if (!TryAddMatch(slot, combined))
                                    return false;
                                if (trackUnmatchedBuild)
                                    spill.MarkMatched(build.Ordinal, context);
                            }
                        }
                    }
                }

                for (var slot = 0; slot < localIndices.Count; slot++)
                {
                    var localIndex = localIndices[slot];
                    var matchList = groupMatches[slot];
                    if (matchList is null && buildIsRight && plan.Kind is VdbeJoinKind.Left or VdbeJoinKind.Full)
                    {
                        // Eagerly materialize the null-extension now, while this probe's row
                        // data is still safely available, so its input bytes can be released
                        // immediately below exactly like a genuine match. Deferring this to
                        // emission time (as EmitOneIndex's unsettled branch does for probes
                        // that never join a group) would require keeping this probe's own
                        // accounting alive until then - reintroducing the same early-release
                        // risk the immediate release below exists to avoid. This still goes
                        // through TryAddMatch's own accounting/rollback, so a null-extension
                        // that cannot be retained fails this group exactly like any other
                        // match would.
                        if (!TryAddMatch(slot, Combine(batch[localIndex].Probe, NullRow(buildNode))))
                            return false;
                        matchList = groupMatches[slot];
                    }

                    matchesByLocalIndex[localIndex] = matchList;
                    settled[localIndex] = true;

                    if (matchList is not null)
                    {
                        var bytes = 0L;
                        foreach (var row in matchList)
                            bytes = checked(bytes + EstimateCombinedRowBytes(row));
                        outputRetainedBytes[localIndex] = bytes;
                    }

                    // Release this probe's OWN input bytes now, decoupled from when it is
                    // finally emitted: every combined output row this probe could ever
                    // produce (real matches, or the eagerly-materialized null-extension
                    // above) has already been copied into its own, separately-accounted
                    // output row, so the original probe row's bytes are genuinely no longer
                    // needed once this group commits. Waiting for EmitReadyPrefix to reach
                    // this index in strict original-order would be wrong here: a cyclic
                    // probe order spreads one group's members across non-contiguous indices
                    // (e.g. every 8th probe for an 8-key round-robin), so nothing would ever
                    // become emit-ready until EVERY group in the batch has committed -
                    // holding the ENTIRE batch's input retained throughout, which starves
                    // the residency cache of the very headroom grouping is supposed to free
                    // up. Releasing here instead bounds concurrent retention to
                    // "not-yet-committed groups plus committed-but-not-yet-emitted output",
                    // never "the whole original batch".
                    ReleaseInput(localIndex);
                }
                committed = true;
                return true;
            }
            catch (VdbeMemoryLimitExceededException)
            {
                // A transient build-side entry read (not one of THIS group's own buffered
                // output rows - those fail gracefully via TryAddMatch above) could not be
                // retained, because the batch's own buffered probes plus whatever this
                // group has already accumulated as output consumed the remaining budget.
                // Treat it exactly like a graceful TryAddMatch failure: fall back to
                // individual, unbuffered streaming for this group and everything after it
                // (never accumulates output, so it never competes with the budget this
                // way). Actual rollback happens in the finally below, uniformly with every
                // other early-return path.
                return false;
            }
            finally
            {
                if (!committed && (totalRetainedBytes > 0 || totalRetainedRows > 0))
                    context.Memory.Release(totalRetainedBytes, totalRetainedRows);
            }
        }

        try
        {
            var topLevelGroups = new Dictionary<int, List<int>>();
            var orderedTopLevelIndices = new List<int>();
            for (var localIndex = 0; localIndex < batch.Count; localIndex++)
            {
                var key = batch[localIndex].Key;
                if (key is null)
                    continue;

                var topIndex = GetPartition(key);
                if (!topLevelGroups.TryGetValue(topIndex, out var indices))
                {
                    indices = [];
                    topLevelGroups.Add(topIndex, indices);
                    orderedTopLevelIndices.Add(topIndex);
                }
                indices.Add(localIndex);
            }

            foreach (var topIndex in orderedTopLevelIndices)
            {
                context.ThrowIfCancellationRequested();
                var topLevelLocalIndices = topLevelGroups[topIndex];

                // Trigger any pending split decision for this top-level partition exactly
                // once (HashSpill.TrySplitPartition itself only ever attempts a split the
                // first time any caller touches a given top-level index); the resolution
                // this call returns is discarded; it only serves to finalize routing before
                // the real regroup below.
                _ = residency.Resolve(spill, batch[topLevelLocalIndices[0]].Key!, context);

                var finalGroups = new Dictionary<PartitionKey, List<int>>();
                var orderedFinalIds = new List<PartitionKey>();
                foreach (var localIndex in topLevelLocalIndices)
                {
                    var id = spill.GetPartitionKey(batch[localIndex].Key!);
                    if (!finalGroups.TryGetValue(id, out var indices))
                    {
                        indices = [];
                        finalGroups.Add(id, indices);
                        orderedFinalIds.Add(id);
                    }
                    indices.Add(localIndex);
                }

                foreach (var id in orderedFinalIds)
                {
                    context.ThrowIfCancellationRequested();
                    var localIndices = finalGroups[id];
                    var resolution = residency.Resolve(spill, batch[localIndices[0]].Key!, context);

                    // A failure here (see TryProcessGroup's remarks) leaves only THIS
                    // group's own indices unsettled - handled later via individual streaming
                    // in the catch-up loop below - and does not abandon grouping for the
                    // REST of the batch: memory pressure that defeats one group (e.g. a
                    // transient spike from duplicate-key output fanout) does not imply every
                    // other, unrelated partition's group will also fail, and treating one
                    // failure as permanent for the whole batch would force the remaining,
                    // potentially large tail through individual streaming - which keeps that
                    // whole tail's input bytes retained throughout (see EnumerateCore's
                    // per-probe retention) and can starve the residency cache down to a
                    // single resident entry, causing exactly the reload thrashing this
                    // batching mechanism exists to avoid.
                    if (!TryProcessGroup(localIndices, resolution))
                        continue;

                    foreach (var row in EmitReadyPrefix())
                        yield return row;
                }
            }

            while (nextEmitIndex < batch.Count)
            {
                foreach (var row in EmitOneIndex(nextEmitIndex))
                    yield return row;
                nextEmitIndex++;
            }
        }
        finally
        {
            // Safety net for any exception that unwinds this iterator before every entry
            // reached its normal release point above (cancellation, a user predicate fault,
            // an unrelated I/O fault, or an early Dispose() from the caller's own
            // maximumRows early-exit): release whatever input/output accounting is still
            // outstanding so a fault never leaks retained bytes, matching the same
            // exception-safety contract EnumerateCore's own outer try/finally already
            // provides for the rest of this join operator.
            for (var localIndex = 0; localIndex < batch.Count; localIndex++)
            {
                if (outputRetainedBytes[localIndex] > 0)
                {
                    var matches = matchesByLocalIndex[localIndex];
                    context.Memory.Release(outputRetainedBytes[localIndex], rows: matches?.Count ?? 0);
                    outputRetainedBytes[localIndex] = 0;
                }
                ReleaseInput(localIndex);
            }
        }
    }

    /// <summary>Approximates the retained-memory footprint of one buffered join-result row,
    /// for accounting output rows produced during grouped probe-batch processing (see
    /// <c>FlushProbeBatch</c>). Reuses the build-entry estimator (no join key applies to an
    /// already-combined row) since its array/value-payload shape is the same.</summary>
    private static long EstimateCombinedRowBytes(VdbeJoinRow combined) =>
        VdbeManagedFootprint.EstimateHashBuildEntry(combined.Values, key: null, combined.RowIds.Length);

    /// <summary>
    /// Streams every build-side match for a single probe against an already-resolved
    /// partition (<paramref name="resolution"/>), marking matched build ordinals as it goes.
    /// Used only by the individual/unbuffered path (<c>FlushProbeBatch.EmitOneIndex</c>'s
    /// unsettled branch): each yielded row is transient, never buffered, so this carries no
    /// memory-accounting risk of its own - identical in shape to the pre-batching per-probe
    /// design.
    /// </summary>
    private static IEnumerable<VdbeJoinRow> MatchAgainstResolution(
        PartitionResidencyCache.Resolution resolution,
        HashSpill spill,
        string key,
        VdbeJoinRow probe,
        VdbeJoinOperatorPlan plan,
        bool buildIsRight,
        bool trackUnmatchedBuild,
        VdbeJoinExecutionContext context,
        PartitionResidencyCache residency)
    {
        if (resolution.Loaded is not null)
        {
            foreach (var build in resolution.Loaded.Find(key))
            {
                context.ThrowIfCancellationRequested();
                var combined = Combine(build.Row, probe, buildIsRight);
                if (!Matches(plan, build.Row, probe, combined, buildIsRight))
                    continue;
                if (trackUnmatchedBuild)
                    spill.MarkMatched(build.Ordinal, context);
                yield return combined;
            }
        }
        else if (resolution.Index is not null)
        {
            foreach (var lease in spill.ReadPartitionIndexMatches(resolution.Id, resolution.Index, key, context, residency))
            {
                VdbeJoinRow? combined = null;
                long? matchedOrdinal = null;
                using (lease)
                {
                    context.ThrowIfCancellationRequested();
                    var build = lease.Entry;
                    var candidate = Combine(build.Row, probe, buildIsRight);
                    if (Matches(plan, build.Row, probe, candidate, buildIsRight))
                    {
                        combined = candidate;
                        if (trackUnmatchedBuild)
                            matchedOrdinal = build.Ordinal;
                    }
                }

                if (combined is null)
                    continue;
                if (matchedOrdinal is { } ordinal)
                    spill.MarkMatched(ordinal, context);
                yield return combined;
            }
        }
        else
        {
            context.Options.Metrics.HashPartitionFallbackScan();
            foreach (var lease in spill.ReadPartitionByKey(resolution.Id, context, residency))
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

                    if (trackUnmatchedBuild)
                        spill.MarkMatched(build.Ordinal, context);
                    combinedResult = combined;
                }

                yield return combinedResult;
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
    /// A lightweight key-&gt;record-offset index over one spill partition, built from a
    /// single sequential pass that reads every record's length/key bytes but never decodes
    /// row values or rowids (see <c>HashSpill.TryBuildKeyIndexCore</c> remarks for why this
    /// still reads the whole partition's bytes - it reduces retained MEMORY, not I/O volume,
    /// per rebuild). Its per-partition memory footprint is a small fraction of a fully
    /// materialized <see cref="LoadedPartition"/>, so a memory budget that cannot hold every
    /// partition a cyclic probe order touches as full rows can still hold this cheaper index
    /// for many more of them at once, mitigating the residual thrashing a bounded full-row
    /// LRU alone cannot avoid when the working set exceeds its capacity (see
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
            VdbeJoinExecutionContext context,
            PartitionResidencyCache residency)
        {
            var partition = GetPartitionFile(id);
            _options.Metrics.HashPartitionScanned();
            VdbeSpillRecordCodec.ValidateFile(
                partition.File.File,
                VdbeSpillFileKind.HashPartition,
                _options.Metrics);
            return ReadEntriesWithEviction(partition.File.File, partition.Count, context, id, residency);
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
        /// pass over its file that records where each key's record(s) begin, decoding only
        /// record-length prefixes and key bytes and never decoding row values or rowids.
        /// </summary>
        /// <remarks>
        /// This still performs a full sequential read of the whole partition file end to
        /// end - its I/O volume is the same order as a raw fallback scan of that partition,
        /// not "header-only" in the sense of touching only a fixed-size prefix. What it
        /// actually reduces is retained MEMORY (no row-value/rowid arrays are ever
        /// materialized), which is what lets a shared memory budget hold many more resident
        /// partitions of this cheaper shape than of the fully materialized
        /// <see cref="TryLoadPartitionCore"/> shape, raising effective cache capacity for a
        /// probe order that revisits partitions. On its own, rebuilding this index on every
        /// cache miss for a genuinely over-capacity cyclic working set still re-reads that
        /// partition's full bytes each time (see
        /// HashJoinSpillExecutionTests.CyclicProbesExceedingResidentCapacityStillAvoidRawRescans,
        /// which documents this as a real, not eliminated, limitation). The batched,
        /// partition-major probe scheduling in FlushProbeBatch is what actually amortizes
        /// this I/O across every probe in a batch that shares the same partition, rather than
        /// re-paying it once per probe.
        /// </remarks>
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

        /// <summary>
        /// Like <see cref="ReadEntries"/>, but for a raw partition scan that may run
        /// alongside other still-resident cache entries and buffered probe rows a caller
        /// legitimately needs to keep alive (see <c>VdbeHashJoinRuntime.EnumerateCore</c>'s
        /// probe-batch scheduling): before giving up on a single entry's decode, this evicts
        /// other resident partitions (never the probe-side data this scan itself is
        /// answering) to make room, exactly mirroring <see cref="ReadPartitionIndexMatches"/>'s
        /// existing degradation, so a tight-but-not-truly-impossible budget never hard-faults
        /// here purely because of a scan that could have succeeded moments later once
        /// something else was evicted.
        /// </summary>
        private IEnumerable<BuildEntryLease> ReadEntriesWithEviction(
            IFile file,
            int count,
            VdbeJoinExecutionContext context,
            PartitionKey protectId,
            PartitionResidencyCache residency)
        {
            long position = VdbeSpillRecordCodec.FileHeaderSize;
            for (var index = 0; index < count; index++)
            {
                // TryReadEntry rolls its own `position` argument back to this record's start
                // whenever it fails (requireAvailable: false), so retrying with the SAME
                // `position` variable after an eviction always re-attempts the same record,
                // and a successful attempt always leaves `position` correctly advanced to
                // the next one - no separate bookkeeping is needed here.
                var lease = TryReadEntry(file, ref position, context, requireAvailable: false);
                while (lease is null && residency.EvictOneExcept(protectId, context))
                    lease = TryReadEntry(file, ref position, context, requireAvailable: false);

                // Truly out of options; let TryReadEntry throw with its precise,
                // already-computed byte accounting rather than approximating it here.
                lease ??= TryReadEntry(file, ref position, context, requireAvailable: true)!;

                yield return lease;
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
