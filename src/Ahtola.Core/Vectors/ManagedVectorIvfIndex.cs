using Ahtola.Core.Indexing;

namespace Ahtola.Core.Vectors;

/// <summary>One candidate the index reranked: where it sits in the scan, and its reported distance.</summary>
internal readonly record struct ManagedVectorCandidate(int Position, long RowId, double Distance);

/// <summary>The outcome of one certified search.</summary>
/// <param name="Rows">
/// Base rows in scan order that provably contain every row the equivalent scalar scan would have
/// kept, ordered exactly as the scan produced them so a stable ORDER BY resolves ties identically.
/// </param>
/// <param name="ProbedLists">How many lists the certificate needed.</param>
/// <param name="RerankedRows">
/// How many base rows were actually read and scored. This is the honest measure of the work the plan
/// did, and it is what the cost model prices — a probe count says nothing when one list holds most
/// of the table.
/// </param>
/// <param name="Exhaustive">True when the search fell back to reading every live row.</param>
internal readonly record struct ManagedVectorSearchResult(
    IReadOnlyList<ManagedVectorCandidate> Rows,
    int ProbedLists,
    int RerankedRows,
    bool Exhaustive);

/// <summary>
/// The inverted-file (IVF-Flat) structure: durable centroids plus derived assignments, postings and
/// radii, with a search that returns an exact answer or reads everything.
/// </summary>
/// <remarks>
/// <para>
/// Vectors are never copied into the index. A posting is a rowid; reranking reads the base row
/// through <see cref="IManagedIndexSource"/>, which the engine already keeps snapshot isolated, and
/// scores it with <see cref="SqliteVectorFunctions.DistanceExact"/> — the scalar evaluator's own
/// code. Memory is therefore <c>O(rows)</c> at roughly twenty bytes per row plus
/// <c>lists × dims × 4</c> bytes of centroids, not <c>O(rows × dims)</c>.
/// </para>
/// <para>
/// The rowid-to-placement map is the authority for membership, so a deleted rowid that is later
/// reused cannot resurrect its old assignment: the delete removes the map entry and tombstones the
/// posting slot before any reuse can occur.
/// </para>
/// </remarks>
internal sealed class ManagedVectorIvfIndex
{
    /// <summary>The list id used for rows the geometry cannot bound; it is probed on every query.</summary>
    public const int UnboundedList = -1;

    private readonly ManagedVectorIndexOptions _options;
    private readonly List<PostingSlot>[] _postings;
    private readonly List<PostingSlot> _unbounded = [];
    private readonly Dictionary<long, Placement> _placements = [];
    private readonly double[] _radius;
    private readonly HashSet<long> _unindexable = [];

    private float[] _centroids;
    private double[][] _workingCentroids;
    private bool[][] _centroidBits;
    private bool[] _listBoundable;
    private int _holes;

    public ManagedVectorIvfIndex(ManagedVectorIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _postings = new List<PostingSlot>[options.Lists];
        for (var list = 0; list < options.Lists; list++)
            _postings[list] = [];

        _radius = new double[options.Lists];
        _centroids = new float[checked(options.Lists * options.Dimensions)];
        _workingCentroids = [];
        _centroidBits = [];
        _listBoundable = [];
        RebuildWorkingCentroids();
    }

    /// <summary>True once centroids have been trained or restored from the persisted envelope.</summary>
    public bool IsTrained { get; private set; }

    /// <summary>The durable centroid payload, <c>lists × dims</c> float32 components.</summary>
    public float[] Centroids => _centroids;

    /// <summary>How many rows the sample held the last time centroids were trained.</summary>
    /// <remarks>
    /// This is the reservoir size, capped by <c>train_sample</c>. It says how much evidence the
    /// centroids were fitted from; it says nothing at all about how large the table was, which is
    /// why drift is measured against <see cref="TrainedPopulation"/> instead.
    /// </remarks>
    public int TrainedSampleRows { get; private set; }

    /// <summary>
    /// The eligible live-row population the training sample was drawn from.
    /// </summary>
    /// <remarks>
    /// Comparing the live row count against the capped sample size is what made a table larger than
    /// <c>4 × train_sample</c> re-cluster on every single refresh: the sample can never grow past
    /// its cap, so the "grown by a factor of four" test stayed true forever. The population is the
    /// number the drift rule actually means.
    /// </remarks>
    public long TrainedPopulation { get; private set; }

    /// <summary>Live indexed rows.</summary>
    public int IndexedRowCount => _placements.Count;

    /// <summary>
    /// Live rows whose indexed column is not a valid vector of the declared encoding and
    /// dimensionality — including NULL.
    /// </summary>
    /// <remarks>
    /// The scalar form of the query raises an error the moment it reaches such a row, so an index
    /// that silently skipped them would turn an error into a result set. While this is non-zero the
    /// method declines every plan and the ordinary scan answers, errors included.
    /// </remarks>
    public int UnindexableRowCount => _unindexable.Count;

    /// <summary>Rows sitting in the always-probed bucket because no bound applies to them.</summary>
    public int UnboundedRowCount => _unbounded.Count;

    /// <summary>Lists that currently hold at least one live posting.</summary>
    public int OccupiedListCount
    {
        get
        {
            var occupied = 0;
            foreach (var postings in _postings)
            {
                if (postings.Count > 0)
                    occupied++;
            }

            return occupied;
        }
    }

    /// <summary>
    /// The largest list radius. Compaction recomputes radii exactly over the live members, so this
    /// can only ever shrink; growing it would weaken a bound that queries already rely on.
    /// </summary>
    public double MaximumRadius
    {
        get
        {
            var maximum = 0.0;
            foreach (var radius in _radius)
            {
                if (radius > maximum)
                    maximum = radius;
            }

            return maximum;
        }
    }

    /// <summary>True when tombstoned posting slots outnumber the live ones.</summary>
    public bool NeedsCompaction => _holes > 64 && _holes > _placements.Count;

    /// <summary>Publishes freshly trained centroids and drops every derived placement.</summary>
    /// <param name="centroids">The trained centroid payload.</param>
    /// <param name="trainedSampleRows">How many rows the k-means sample held.</param>
    /// <param name="trainedPopulation">
    /// The eligible live-row population the sample was drawn from. It must be at least the sample
    /// size; a caller that only knows the sample passes the same value for both.
    /// </param>
    public void PublishCentroids(float[] centroids, int trainedSampleRows, long trainedPopulation)
    {
        ArgumentNullException.ThrowIfNull(centroids);
        if (centroids.Length != _options.Lists * _options.Dimensions)
            throw new EmbeddedSqlException("vector index centroid payload does not match the index definition");

        _centroids = centroids;
        TrainedSampleRows = trainedSampleRows;
        TrainedPopulation = Math.Max(trainedPopulation, trainedSampleRows);

        // Training over an empty sample produces all-zero centroids that would send every row to
        // list 0 and prune nothing. That is not a trained index, so it must not be recorded as one:
        // leaving it untrained is what makes the first populated refresh actually cluster.
        IsTrained = trainedSampleRows > 0;
        RebuildWorkingCentroids();
        ClearPlacements();
    }

    /// <summary>
    /// True when the live row count has drifted far enough from the population the centroids were
    /// fitted to that they no longer describe the data.
    /// </summary>
    /// <remarks>
    /// Drift never costs recall — the certificate simply stops pruning and the search reads more
    /// rows — but it does cost speed, and the cost model prices that honestly, so an index that has
    /// grown or shrunk by a factor of four re-clusters instead of quietly becoming a slow scan.
    /// The comparison is against the eligible population, never against the capped sample: a table
    /// above <c>4 × train_sample</c> rows would otherwise satisfy the growth test forever and
    /// re-run k-means on every refresh.
    /// </remarks>
    public bool NeedsRetrain(int liveRows)
    {
        if (!IsTrained || liveRows <= 0)
            return false;

        var population = Math.Max(TrainedPopulation, 1L);
        return liveRows >= 4L * population || (long)liveRows * 4L <= population;
    }

    /// <summary>Drops every derived placement, keeping the centroids.</summary>
    public void ClearPlacements()
    {
        for (var list = 0; list < _postings.Length; list++)
            _postings[list].Clear();

        Array.Clear(_radius);
        _unbounded.Clear();
        _placements.Clear();
        _unindexable.Clear();
        _holes = 0;
    }

    /// <summary>
    /// Places one base row, or records it as unindexable.
    /// </summary>
    /// <remarks>
    /// Assignment never fails: a row the geometry cannot place goes to the always-probed bucket, and
    /// a row that is not a vector of the declared shape is recorded so the planner can decline. DML
    /// must never fail because of an index method.
    /// </remarks>
    public void Upsert(long rowId, SqlValue columnValue)
    {
        Remove(rowId);
        if (!SqliteVectorFunctions.TryDecodeVector(
                columnValue,
                _options.Encoding,
                _options.Dimensions,
                out var decoded)
            || !decoded.IsFinite)
        {
            _unindexable.Add(rowId);
            return;
        }

        if (!IsTrained || !_options.TryProject(decoded.Values, out var projected))
        {
            Place(rowId, UnboundedList);
            return;
        }

        // A row whose scalar-arithmetic norm collapses — 1e-24 components whose float32 squares
        // underflow to zero, 1e20 components whose squares overflow to infinity — is reported by
        // vector_distance_cos as its degenerate 0/1 constant, not as an angle. Its widened double
        // direction is perfectly well behaved, which is exactly the trap: every angular bound
        // derived from it would be a claim about a number the scalar evaluator never produces. Such
        // a row is placed in the always-probed bucket so it is scored on every query.
        if (_options.Metric == VectorDistanceKind.Cosine
            && !ManagedVectorGeometry.IsCosineScalarUsable(decoded.Values, _options.Encoding))
        {
            Place(rowId, UnboundedList);
            return;
        }

        var list = ManagedVectorTraining.NearestCentroid(projected, _workingCentroids);

        // A list whose centroid carries no usable direction cannot produce a provable bound, so its
        // members are always probed rather than bounded by an inequality that does not hold.
        if (!_listBoundable[list])
        {
            Place(rowId, UnboundedList);
            return;
        }

        var radius = MeasureRadius(projected, list);
        if (!double.IsFinite(radius))
        {
            Place(rowId, UnboundedList);
            return;
        }

        Place(rowId, list);

        // Radii only ever grow here. An upper bound that over-estimates costs extra probes and can
        // never cost recall, which is what lets a delete leave the radius alone; Optimize recomputes
        // it downward from the live members.
        if (radius > _radius[list])
            _radius[list] = radius;
    }

    /// <summary>Removes one base row from its list and from the authority map.</summary>
    public void Remove(long rowId)
    {
        _unindexable.Remove(rowId);
        if (!_placements.Remove(rowId, out var placement))
            return;

        var postings = placement.List == UnboundedList ? _unbounded : _postings[placement.List];
        if (placement.Slot >= 0
            && placement.Slot < postings.Count
            && postings[placement.Slot] is { IsLive: true } slot
            && slot.RowId == rowId)
        {
            postings[placement.Slot] = PostingSlot.Tombstone;
            _holes++;
        }
    }

    /// <summary>Reclaims tombstoned posting slots without changing any membership.</summary>
    public void Compact()
    {
        for (var list = 0; list < _postings.Length; list++)
            CompactList(_postings[list], list);

        CompactList(_unbounded, UnboundedList);
        _holes = 0;
    }

    private void CompactList(List<PostingSlot> postings, int list)
    {
        var write = 0;
        for (var read = 0; read < postings.Count; read++)
        {
            var slot = postings[read];
            if (!slot.IsLive)
                continue;

            postings[write] = slot;
            _placements[slot.RowId] = new Placement(list, write);
            write++;
        }

        postings.RemoveRange(write, postings.Count - write);
    }

    /// <summary>
    /// Returns every base row that can belong to the <paramref name="limit"/> nearest neighbours of
    /// <paramref name="queryValue"/>, in scan order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lists are ordered by their provable lower bound and probed cheapest first. After each probe
    /// the k-th best reported distance is compared against the bound of the next unprobed list: when
    /// the bound is strictly greater, every remaining member is provably worse than the current k-th
    /// best and the loop stops. Because the ordering is ascending, that single comparison certifies
    /// every remaining list at once.
    /// </para>
    /// <para>
    /// The result is deliberately a superset: it is the reranked rows that tie with or beat the k-th
    /// best, emitted in scan order. The engine's own ORDER BY then sorts and truncates them, so the
    /// answer is produced by exactly the comparison the scan would have used — ties, collations and
    /// all — rather than by this method's opinion of the ordering.
    /// </para>
    /// </remarks>
    public ManagedVectorSearchResult Search(
        SqlValue queryValue,
        in DecodedVector query,
        int limit,
        IManagedIndexSource source,
        int columnIndex,
        int startingProbes,
        Action? checkInterrupt = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (limit <= 0 || !IsTrained || UnindexableRowCount > 0)
            return Exhaust(source, checkInterrupt);

        var calculator = new ManagedVectorBoundCalculator(
            query,
            _options.Metric,
            _options.ClusteringMetric,
            _options.Encoding);
        var order = OrderListsByBound(calculator, out var bounds);

        // Bounded from the start: the reranked-candidate list is capped, and the cap is what makes
        // the exhaustive fallback a decision rather than a consequence. Sizing the initial capacity
        // against the cap keeps the peak allocation inside the promised bound too.
        var candidates = new List<ManagedVectorCandidate>(
            (int)Math.Min(Math.Min((long)limit * 4, 1024), ManagedVectorIndexLimits.MaxCandidateRows));
        var best = new ManagedVectorTopK(limit);
        var probed = 0;
        var abandoned = false;

        // The always-probed bucket holds rows no inequality covers, so it is never certifiable and
        // is scored before any certificate can be claimed.
        if (!RerankList(_unbounded, queryValue, source, columnIndex, candidates, best, checkInterrupt))
            abandoned = true;

        if (!abandoned)
        {
            for (var position = 0; position < order.Length; position++)
            {
                checkInterrupt?.Invoke();

                // The certificate: every unprobed list from here on has a bound at least this large,
                // so a strictly greater bound than the current k-th best ends the search.
                if (probed >= startingProbes
                    && best.IsFull
                    && bounds[order[position]] > best.Worst)
                {
                    break;
                }

                if (!RerankList(_postings[order[position]], queryValue, source, columnIndex, candidates, best, checkInterrupt))
                {
                    abandoned = true;
                    break;
                }

                probed++;
            }
        }

        if (abandoned)
        {
            // Free the partial working set before the exhaustive pass allocates its own, so the two
            // are never resident at the same time.
            candidates.Clear();
            candidates.TrimExcess();
            return Exhaust(source, checkInterrupt);
        }

        return new ManagedVectorSearchResult(
            SelectSuperset(candidates, limit),
            probed,
            candidates.Count,
            probed >= _postings.Length);
    }

    /// <summary>Every live row in rowid order: the answer whenever no bound can be proven.</summary>
    private static ManagedVectorSearchResult Exhaust(IManagedIndexSource source, Action? checkInterrupt)
    {
        var rows = new List<ManagedVectorCandidate>(source.RowCount);
        for (var position = 0; position < source.RowCount; position++)
        {
            if (position % ManagedVectorTraining.InterruptPollInterval == 0)
                checkInterrupt?.Invoke();

            rows.Add(new ManagedVectorCandidate(position, source.GetRowId(position), double.NaN));
        }

        // An ordinary scan produces rows in ascending rowid order, and this is the result that
        // stands in for one, so it has to arrive in that order too.
        rows.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        return new ManagedVectorSearchResult(rows, ProbedLists: int.MaxValue, rows.Count, Exhaustive: true);
    }

    /// <summary>
    /// The reranked rows that tie with or beat the k-th best, in scan order.
    /// </summary>
    /// <remarks>
    /// Selection uses the same (distance, rowid) ordering an ordinary table scan feeds the engine's
    /// stable sort — a table b-tree cursor produces rows in ascending rowid order, not in storage
    /// order — and then keeps every row tied with the k-th so a boundary tie can never drop a row
    /// the scan would have kept.
    /// </remarks>
    private static List<ManagedVectorCandidate> SelectSuperset(List<ManagedVectorCandidate> candidates, int limit)
    {
        candidates.Sort(static (left, right) =>
        {
            var comparison = left.Distance.CompareTo(right.Distance);
            return comparison != 0 ? comparison : left.RowId.CompareTo(right.RowId);
        });

        var kept = (int)Math.Min((long)limit, candidates.Count);
        while (kept > 0
               && kept < candidates.Count
               && candidates[kept].Distance.Equals(candidates[kept - 1].Distance))
        {
            kept++;
        }

        var selected = candidates.GetRange(0, kept);
        selected.Sort(static (left, right) => left.RowId.CompareTo(right.RowId));
        return selected;
    }

    /// <summary>
    /// Scores every live member of one list, returning false when exactness is lost.
    /// </summary>
    /// <remarks>
    /// The candidate cap is enforced as the list grows, not after it is built: exceeding it means
    /// the search falls back to an exhaustive scan anyway, so continuing to accumulate would only
    /// buy peak memory this method promised never to use.
    /// </remarks>
    private bool RerankList(
        List<PostingSlot> postings,
        SqlValue queryValue,
        IManagedIndexSource source,
        int columnIndex,
        List<ManagedVectorCandidate> candidates,
        ManagedVectorTopK best,
        Action? checkInterrupt)
    {
        for (var slot = 0; slot < postings.Count; slot++)
        {
            if (slot % ManagedVectorTraining.InterruptPollInterval == 0)
                checkInterrupt?.Invoke();

            var posting = postings[slot];
            if (!posting.IsLive || !source.TryGetPosition(posting.RowId, out var position))
                continue;

            var row = source.GetRow(position);
            var value = columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : SqlValue.Null;
            double distance;
            try
            {
                distance = SqliteVectorFunctions.DistanceExact(value, queryValue, _options.Metric);
            }
            catch (EmbeddedSqlException)
            {
                // The scalar evaluator would have raised here too. Abandoning exactness routes the
                // whole statement through the scan, which raises the error in the right order.
                return false;
            }

            // A non-finite rank has no ordering the certificate can reason about, so the search
            // stops trusting its bounds and lets the scan answer.
            if (!double.IsFinite(distance))
                return false;

            if (candidates.Count >= ManagedVectorIndexLimits.MaxCandidateRows)
                return false;

            var candidate = new ManagedVectorCandidate(position, posting.RowId, distance);
            candidates.Add(candidate);
            best.Offer(candidate);
        }

        return true;
    }

    /// <summary>List ids ordered by ascending provable bound, ties by list id.</summary>
    private int[] OrderListsByBound(ManagedVectorBoundCalculator calculator, out double[] bounds)
    {
        bounds = new double[_postings.Length];
        var order = new int[_postings.Length];
        for (var list = 0; list < _postings.Length; list++)
        {
            order[list] = list;
            bounds[list] = _postings[list].Count == 0
                ? double.PositiveInfinity
                : _listBoundable[list]
                    ? calculator.LowerBound(
                        _workingCentroids[list],
                        _centroidBits.Length == 0 ? [] : _centroidBits[list],
                        _radius[list])
                    : ManagedVectorGeometry.Unprovable;
        }

        var keys = bounds;
        Array.Sort(order, (left, right) =>
        {
            var comparison = keys[left].CompareTo(keys[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });

        return order;
    }

    /// <summary>The clustering-space distance from a projected vector to one centroid.</summary>
    private double MeasureRadius(double[] projected, int list)
    {
        if (_options.Encoding == VectorEncodingKind.Float1Bit)
            return ManagedVectorGeometry.Hamming(ManagedVectorGeometry.ToBits(projected), _centroidBits[list]);

        return _options.ClusteringMetric == VectorDistanceKind.Cosine
            ? ManagedVectorGeometry.UnitAngle(projected, _workingCentroids[list])
            : ManagedVectorGeometry.ClusterDistance(projected, _workingCentroids[list]);
    }

    private void Place(long rowId, int list)
    {
        var postings = list == UnboundedList ? _unbounded : _postings[list];
        _placements[rowId] = new Placement(list, postings.Count);
        postings.Add(new PostingSlot(rowId, IsLive: true));
    }

    /// <summary>
    /// Materializes the query-time centroid view: unit length for cosine (so the inner product is a
    /// cosine), binarized for <c>float1bit</c> (so the bound is an exact Hamming count).
    /// </summary>
    /// <remarks>
    /// A cosine centroid that cannot be normalized — the exact cancellation of opposing unit members
    /// leaves a zero mean — has no direction, so the inner product against it is not a cosine and
    /// the angular inequality does not hold. The list is marked unboundable rather than bounded by a
    /// formula whose premise is false; its members are routed to the always-probed bucket.
    /// </remarks>
    private void RebuildWorkingCentroids()
    {
        var lists = _options.Lists;
        var dimensions = _options.Dimensions;
        _workingCentroids = new double[lists][];
        _centroidBits = _options.Encoding == VectorEncodingKind.Float1Bit ? new bool[lists][] : [];
        _listBoundable = new bool[lists];
        for (var list = 0; list < lists; list++)
        {
            var centroid = new double[dimensions];
            var finite = true;
            for (var component = 0; component < dimensions; component++)
            {
                centroid[component] = _centroids[(list * dimensions) + component];
                finite &= double.IsFinite(centroid[component]);
            }

            if (_options.Encoding == VectorEncodingKind.Float1Bit)
                _centroidBits[list] = ManagedVectorGeometry.BinarizeCentroid(centroid);

            var boundable = finite;
            if (_options.ClusteringMetric == VectorDistanceKind.Cosine)
            {
                if (finite && ManagedVectorGeometry.TryNormalize(centroid, out var unit))
                    centroid = unit;
                else
                    boundable = false;
            }

            _listBoundable[list] = boundable;
            _workingCentroids[list] = centroid;
        }
    }

    /// <summary>Where one rowid lives: which list, and which slot inside that list's postings.</summary>
    private readonly record struct Placement(int List, int Slot);

    /// <summary>
    /// One posting slot: a row id plus whether the slot still refers to a live row.
    /// </summary>
    /// <remarks>
    /// Liveness is a field of its own, never a sentinel row id. Every 64-bit value is a legal SQLite
    /// rowid — <see cref="long.MinValue"/> included — so a magic value would make a real row
    /// invisible to search, silently dropped by compaction, and double-counted as a hole when it was
    /// deleted.
    /// </remarks>
    private readonly record struct PostingSlot(long RowId, bool IsLive)
    {
        public static PostingSlot Tombstone { get; } = new(0, false);
    }
}

/// <summary>
/// A bounded worst-at-the-root max-heap keyed by the same (distance, rowid) ordering an ordinary
/// table scan feeds the engine's stable sort, so the certificate compares against exactly the value
/// the scan would have at the cut.
/// </summary>
/// <remarks>
/// A binary heap rather than a sorted list: a pushed-down limit can legitimately be large, and an
/// insertion-sorted tracker would make one query cost O(rows × limit) comparisons.
/// </remarks>
internal sealed class ManagedVectorTopK(int capacity)
{
    private readonly List<ManagedVectorCandidate> _heap = new(Math.Min(Math.Max(capacity, 1), 1024));

    /// <summary>True once k candidates have been seen, which is when a certificate becomes possible.</summary>
    public bool IsFull => _heap.Count >= capacity;

    /// <summary>The k-th best reported distance so far, or infinity before the heap fills.</summary>
    public double Worst => IsFull ? _heap[0].Distance : double.PositiveInfinity;

    public void Offer(in ManagedVectorCandidate candidate)
    {
        if (_heap.Count < capacity)
        {
            _heap.Add(candidate);
            SiftUp(_heap.Count - 1);
            return;
        }

        if (_heap.Count == 0 || Compare(candidate, _heap[0]) >= 0)
            return;

        _heap[0] = candidate;
        SiftDown(0);
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (Compare(_heap[index], _heap[parent]) <= 0)
                return;

            (_heap[index], _heap[parent]) = (_heap[parent], _heap[index]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            var left = (2 * index) + 1;
            if (left >= _heap.Count)
                return;

            var largest = left;
            var right = left + 1;
            if (right < _heap.Count && Compare(_heap[right], _heap[left]) > 0)
                largest = right;
            if (Compare(_heap[largest], _heap[index]) <= 0)
                return;

            (_heap[index], _heap[largest]) = (_heap[largest], _heap[index]);
            index = largest;
        }
    }

    private static int Compare(in ManagedVectorCandidate left, in ManagedVectorCandidate right)
    {
        var comparison = left.Distance.CompareTo(right.Distance);
        return comparison != 0 ? comparison : left.RowId.CompareTo(right.RowId);
    }
}
