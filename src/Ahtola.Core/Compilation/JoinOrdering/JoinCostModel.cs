namespace Ahtola.Core.Compilation.JoinOrdering;

/// <summary>
/// The physical shape a single left-deep join step can take in this engine.
/// </summary>
/// <remarks>
/// Deliberately limited to the shapes <c>VdbeJoinOperatorPlan</c> can actually execute.
/// </remarks>
internal enum JoinStepShape
{
    /// <summary>
    /// No usable equality key: the right input is materialized once and every accumulated left
    /// row is compared against every right row (<c>EquiProbe == null</c>).
    /// </summary>
    NestedLoop,

    /// <summary>The right input is materialized and hashed; the left streams and probes.</summary>
    HashBuildRight,

    /// <summary>
    /// The accumulated left input is materialized and hashed; the right streams and probes.
    /// INNER equijoins only, matching <c>VdbeJoinOperatorPlan.HashBuildRight == false</c>.
    /// </summary>
    HashBuildLeft,

    /// <summary>
    /// The right member is positioned once per accumulated outer row through a durable index's
    /// contiguous equality prefix.
    /// </summary>
    IndexSeekRight,
}

/// <summary>
/// Cost-model constants ported from Turso v0.8.0-pre.7
/// <c>core/translate/optimizer/cost_params.rs</c> (<c>CostModelParams::new</c>, lines 103-141).
/// Only the subset the managed engine can honor is ported.
/// </summary>
internal static class JoinCostParams
{
    /// <summary>cost_params.rs: <c>rows_per_table_fallback</c>.</summary>
    public const double RowsPerTableFallback = 1_000_000.0;

    /// <summary>cost_params.rs: <c>rows_per_table_page</c>.</summary>
    public const double RowsPerTablePage = 50.0;

    /// <summary>cost_params.rs: <c>sel_eq_unindexed</c>.</summary>
    public const double SelectivityEqualityUnindexed = 0.1;

    /// <summary>cost_params.rs: <c>sel_eq_indexed</c>.</summary>
    public const double SelectivityEqualityIndexed = 0.001;

    /// <summary>cost_params.rs: <c>sel_range</c>.</summary>
    public const double SelectivityRange = 0.4;

    /// <summary>cost_params.rs: <c>sel_other</c>.</summary>
    public const double SelectivityOther = 0.9;

    /// <summary>cost_params.rs: <c>cache_reuse_factor</c>.</summary>
    public const double CacheReuseFactor = 0.2;

    /// <summary>cost_params.rs: <c>cpu_cost_per_row</c>.</summary>
    public const double CpuCostPerRow = 0.003;

    /// <summary>cost_params.rs: <c>cpu_cost_per_seek</c>.</summary>
    public const double CpuCostPerSeek = 0.01;

    /// <summary>cost_params.rs: <c>index_bonus</c>.</summary>
    public const double IndexBonus = 0.5;

    /// <summary>cost_params.rs: <c>hash_cpu_cost</c>.</summary>
    public const double HashCpuCost = 0.001;

    /// <summary>cost_params.rs: <c>hash_insert_cost</c>.</summary>
    public const double HashInsertCost = 0.002;

    /// <summary>cost_params.rs: <c>hash_lookup_cost</c>.</summary>
    public const double HashLookupCost = 0.003;

    /// <summary>cost_params.rs: <c>sort_cpu_per_row</c>.</summary>
    public const double SortCpuPerRow = 0.002;
}

/// <summary>
/// The ported subset of Turso's <c>core/translate/optimizer/cost.rs</c> and
/// <c>access_method.rs</c> cost formulas, narrowed to the access paths this engine executes.
/// </summary>
internal static class JoinCostModel
{
    /// <summary>
    /// Port of <c>cost.rs:120-135</c> (<c>estimate_scan_cost</c>). <paramref name="scanCount"/>
    /// is the number of times the input is re-read; the managed join operator materializes each
    /// input once per step, so callers pass <c>1.0</c> unless they model a repeated read.
    /// </summary>
    public static double EstimateFullScanCost(double baseRowCount, double scanCount)
    {
        var rows = Sanitize(baseRowCount);
        var scans = Math.Max(1.0, Sanitize(scanCount));
        var tablePages = Math.Max(rows / JoinCostParams.RowsPerTablePage, 1.0);
        var ioCost = scans <= 1.0
            ? tablePages
            : tablePages + (scans - 1.0) * tablePages * JoinCostParams.CacheReuseFactor;
        var cpuCost = scans * rows * JoinCostParams.CpuCostPerRow;
        return ioCost + cpuCost;
    }

    /// <summary>
    /// Port of <c>access_method.rs:1200-1235</c> (<c>estimate_hash_join_cost</c>), minus the
    /// grace-hash spill term: the managed operator has no memory budget or partition spill to
    /// account against, so charging a spill cost would be fabricated rather than conservative.
    /// </summary>
    public static double EstimateHashJoinCost(
        double buildCardinality,
        double probeCardinality,
        double probeMultiplier)
    {
        var build = Sanitize(buildCardinality);
        var probe = Sanitize(probeCardinality);
        var multiplier = Math.Max(1.0, Sanitize(probeMultiplier));
        var buildCost = build * (JoinCostParams.HashCpuCost + JoinCostParams.HashInsertCost);
        var probeCost = probe * (JoinCostParams.HashCpuCost + JoinCostParams.HashLookupCost) * multiplier;
        return buildCost + probeCost;
    }

    /// <summary>
    /// Analog of <c>join.rs:128-153</c> (<c>rows_after_join</c>): the cardinality after joining
    /// one more member onto a partial left-deep plan. <paramref name="rowsPerOuterRow"/> is the
    /// expected number of matching right rows per accumulated left row and
    /// <paramref name="residualSelectivity"/> folds in the ready non-equality conjuncts.
    /// </summary>
    public static double RowsAfterStep(
        double inputCardinality,
        double rowsPerOuterRow,
        double residualSelectivity)
        => Math.Max(
            1.0,
            Sanitize(inputCardinality) * Sanitize(rowsPerOuterRow) * Sanitize(residualSelectivity));

    /// <summary>
    /// The cost of sorting <paramref name="rowCount"/> rows, used only to decide whether an
    /// order-preserving candidate is worth its extra join cost. Mirrors the role
    /// <c>sort_cpu_per_row</c> plays in <c>order.rs</c>'s ordered-plan comparison.
    /// </summary>
    public static double EstimateSortCost(double rowCount)
    {
        var rows = Sanitize(rowCount);
        return rows * Math.Log2(Math.Max(rows, 2.0)) * JoinCostParams.SortCpuPerRow;
    }

    /// <summary>
    /// One-time incremental cost of reconstructing the managed, MVCC-visible index view used by
    /// join seeks. Turso seeks an already-open B-tree; Ahtola currently projects and sorts the
    /// visible base rows once per statement, so that O(N log N) work must not be hidden.
    /// </summary>
    public static double EstimateManagedIndexViewBuildCost(double rowCount)
    {
        var rows = Sanitize(rowCount);
        return EstimateSortCost(rows) + rows * JoinCostParams.CpuCostPerRow;
    }

    /// <summary>
    /// Port of Turso <c>cost.rs:171-236</c> (<c>estimate_index_cost</c>). The outer cardinality
    /// is the number of B-tree seeks; <paramref name="rowsPerSeek"/> comes from the matching
    /// <c>sqlite_stat1</c> leading-prefix average, or one for a unique full-key equality.
    /// </summary>
    public static double EstimateIndexSeekCost(
        double baseRowCount,
        int indexColumnCount,
        int tableColumnCount,
        bool hasRowIdAlias,
        bool covering,
        double inputCardinality,
        double rowsPerSeek)
    {
        var baseRows = Math.Max(1.0, Sanitize(baseRowCount));
        var seeks = Math.Max(1.0, Sanitize(inputCardinality));
        var matches = Math.Max(0.0, Sanitize(rowsPerSeek));
        var indexRowsPerPage = Math.Max(
            1.0,
            JoinCostParams.RowsPerTablePage
                * (tableColumnCount + (hasRowIdAlias ? 0.0 : 1.0))
                / (indexColumnCount + 1.0));
        var treeDepth = baseRows <= 1.0
            ? 1.0
            : Math.Max(1.0, Math.Ceiling(Math.Log(baseRows) / Math.Log(indexRowsPerPage)));
        var fullScan = Math.Abs(matches - baseRows) < 1.0;
        var seekCost = fullScan
            ? treeDepth + (seeks - 1.0) * treeDepth * JoinCostParams.CacheReuseFactor
            : seeks * treeDepth;
        var leafPages = Math.Max(matches / indexRowsPerPage, 1.0);
        var leafScanCost = fullScan
            ? leafPages + (seeks - 1.0) * leafPages * JoinCostParams.CacheReuseFactor
            : matches <= 1.0
                ? 0.0
                : leafPages + (seeks - 1.0) * leafPages * JoinCostParams.CacheReuseFactor;
        var tableLookupCost = covering
            ? 0.0
            : seeks * (matches / baseRows) * Math.Max(baseRows / JoinCostParams.RowsPerTablePage, 1.0);
        var cpuCost = seeks * JoinCostParams.CpuCostPerSeek
            + seeks * matches * JoinCostParams.CpuCostPerRow;

        return Math.Max(
            0.001,
            seekCost + leafScanCost + tableLookupCost + cpuCost - JoinCostParams.IndexBonus);
    }

    /// <summary>
    /// The cost of one left-deep step under a given <paramref name="shape"/>.
    /// <paramref name="leftCardinality"/> is the accumulated cardinality produced so far and
    /// <paramref name="rightRowCount"/> the base row count of the member being added.
    /// </summary>
    /// <remarks>
    /// Turso separates the build side's memory pressure into <c>estimate_hash_join_cost</c>'s
    /// grace-hash spill term, which needs a memory budget the managed operator does not have.
    /// The port replaces it with an explicit buffering charge on whichever side is materialized
    /// into a list (<c>EnumerateHashBuildRight</c> buffers the right input,
    /// <c>EnumerateHashBuildLeft</c> buffers the accumulated left input). Without that charge
    /// the ported constants would rank a large build side as cheap, because
    /// <c>hash_lookup_cost</c> exceeds <c>hash_insert_cost</c>.
    /// </remarks>
    public static double EstimateStepCost(
        JoinStepShape shape,
        double leftCardinality,
        double rightRowCount,
        double outputCardinality)
    {
        var left = Sanitize(leftCardinality);
        var right = Sanitize(rightRowCount);
        var output = Sanitize(outputCardinality);

        // Every shape reads the right input exactly once (VdbeJoinOperatorPlan enumerates it
        // with maximumRows: null before any row is emitted), and every emitted row costs one
        // combine plus one condition evaluation.
        var rightScan = EstimateFullScanCost(right, scanCount: 1.0);
        var emitCost = output * JoinCostParams.CpuCostPerRow;

        return shape switch
        {
            // EnumerateHashBuildRight with EquiProbe == null: the right list is buffered once
            // and then walked in full for every accumulated left row.
            JoinStepShape.NestedLoop =>
                rightScan
                + BufferCost(right)
                + left * right * JoinCostParams.CpuCostPerRow
                + emitCost,

            // EnumerateHashBuildRight with a probe: buffer and hash the right, stream the left.
            JoinStepShape.HashBuildRight =>
                rightScan
                + BufferCost(right)
                + EstimateHashJoinCost(buildCardinality: right, probeCardinality: left, probeMultiplier: 1.0)
                + emitCost,

            // EnumerateHashBuildLeft: buffer and hash the accumulated left, stream the right.
            // The left rows were already produced by earlier steps, so only the buffering and
            // hash-build work is charged here.
            JoinStepShape.HashBuildLeft =>
                rightScan
                + BufferCost(left)
                + EstimateHashJoinCost(buildCardinality: left, probeCardinality: right, probeMultiplier: 1.0)
                + emitCost,

            JoinStepShape.IndexSeekRight => throw new ArgumentException(
                "Index seeks require EstimateIndexSeekCost.",
                nameof(shape)),

            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>Cost of copying one join input into the in-memory list the operator probes.</summary>
    private static double BufferCost(double rowCount) => Sanitize(rowCount) * JoinCostParams.CpuCostPerRow;

    private static double Sanitize(double value)
        => double.IsNaN(value) || value < 0.0
            ? 0.0
            : double.IsPositiveInfinity(value)
                ? JoinCostParams.RowsPerTableFallback
                : value;
}
