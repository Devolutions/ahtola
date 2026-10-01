namespace Ahtola.Core.Compilation.JoinOrdering;

/// <summary>
/// Cost-model constants of Turso v0.8.1 <c>core/translate/optimizer/cost_params.rs</c>
/// (<c>CostModelParams::new</c>) that <see cref="JoinCostParams"/> does not already carry.
/// </summary>
internal static class TursoCostParams
{
    /// <summary>cost_params.rs: <c>sel_is_null</c>.</summary>
    public const double SelectivityIsNull = 0.1;

    /// <summary>cost_params.rs: <c>sel_is_not_null</c>.</summary>
    public const double SelectivityIsNotNull = 0.9;

    /// <summary>cost_params.rs: <c>sel_like</c>.</summary>
    public const double SelectivityLike = 0.2;

    /// <summary>cost_params.rs: <c>sel_not_like</c>.</summary>
    public const double SelectivityNotLike = 0.2;

    /// <summary>cost_params.rs: <c>in_subquery_rows</c>.</summary>
    public const double InSubqueryRows = 25.0;

    /// <summary>cost_params.rs: <c>cpu_cost_per_where_step</c>.</summary>
    public const double CpuCostPerWhereStep = 0.003;

    /// <summary>cost_params.rs: <c>hash_bytes_per_row</c>.</summary>
    public const double HashBytesPerRow = 100.0;

    /// <summary>cost_params.rs: <c>closed_range_selectivity_factor</c>.</summary>
    public const double ClosedRangeSelectivityFactor = 0.2;

    /// <summary>
    /// <c>core/vdbe/hash_table.rs</c> <c>DEFAULT_MEM_BUDGET</c> for the debug profile the
    /// upstream sqltest corpus is generated with (32 KiB; release builds use 64 MiB). The
    /// value only decides whether <see cref="TursoCostModel.EstimateHashJoinCost"/> charges the
    /// grace-hash spill term.
    /// </summary>
    public const double HashJoinMemoryBudgetBytes = 32 * 1024;
}

/// <summary>
/// Exact ports of the Turso v0.8.1 cost formulas in <c>core/translate/optimizer/cost.rs</c>
/// and <c>access_method.rs</c> (<c>estimate_hash_join_cost</c>). Unlike
/// <see cref="JoinCostModel"/> (a narrowed, older subset tuned to the managed join operator),
/// these reproduce upstream's arithmetic term for term so a planner decision or an
/// <c>EXPLAIN QUERY PLAN FORMAT=JSON</c> estimate computed from them is upstream's own number.
/// </summary>
internal static class TursoCostModel
{
    /// <summary>cost.rs:120-139 <c>estimate_scan_cost</c>.</summary>
    public static double EstimateScanCost(double baseRowCount, double scanCount)
    {
        var tablePages = Math.Max(baseRowCount / JoinCostParams.RowsPerTablePage, 1.0);
        var ioCost = scanCount <= 1.0
            ? tablePages
            : tablePages + (scanCount - 1.0) * tablePages * JoinCostParams.CacheReuseFactor;
        var cpuCost = scanCount * baseRowCount * JoinCostParams.CpuCostPerRow;
        return ioCost + cpuCost;
    }

    /// <summary>cost.rs:145-151 <c>estimate_ephemeral_index_build_cost</c>.</summary>
    public static double EstimateEphemeralIndexBuildCost(double rowCount)
        => rowCount * Math.Log2(Math.Max(rowCount, 2.0)) * JoinCostParams.CpuCostPerSeek;

    /// <summary>cost.rs:153-155 <c>estimate_sort_cpu_cost</c>.</summary>
    public static double EstimateSortCpuCost(double rowCount)
        => rowCount * Math.Log2(Math.Max(rowCount, 1.0)) * JoinCostParams.SortCpuPerRow;

    /// <summary>cost.rs:158-164 <c>estimate_btree_depth</c>.</summary>
    public static double EstimateBtreeDepth(double rowCount, double rowsPerPage)
        => rowCount <= 1.0
            ? 1.0
            : Math.Max(1.0, Math.Ceiling(Math.Log(rowCount) / Math.Log(rowsPerPage)));

    /// <summary>cost.rs:83-94 <c>index_leaf_rows_per_page</c>.</summary>
    public static double IndexLeafRowsPerPage(
        int indexColumnCount,
        int tableColumnCount,
        bool hasRowIdAlias)
    {
        var tableWidth = tableColumnCount + (hasRowIdAlias ? 0.0 : 1.0);
        var indexWidth = indexColumnCount + 1.0;
        return Math.Max(JoinCostParams.RowsPerTablePage * tableWidth / indexWidth, 1.0);
    }

    /// <summary>cost.rs:179-250 <c>estimate_index_cost</c>.</summary>
    public static double EstimateIndexCost(
        double baseRowCount,
        double treeDepth,
        TursoIndexInfo indexInfo,
        double inputCardinality,
        double rowsPerSeek)
    {
        var isFullScan = Math.Abs(rowsPerSeek - baseRowCount) < 1.0;
        double seekCost;
        if (isFullScan)
        {
            seekCost = inputCardinality <= 1.0
                ? treeDepth
                : treeDepth + (inputCardinality - 1.0) * treeDepth * JoinCostParams.CacheReuseFactor;
        }
        else if (inputCardinality <= 1.0)
        {
            seekCost = inputCardinality * treeDepth;
        }
        else
        {
            var cachedRootPageCost = JoinCostParams.CacheReuseFactor;
            var leafPageCostedBelow = rowsPerSeek > 1.0 ? 1.0 : 0.0;
            var repeatedTreeSearchCost = Math.Max(
                treeDepth - 1.0 + cachedRootPageCost - leafPageCostedBelow,
                cachedRootPageCost);
            seekCost = treeDepth + (inputCardinality - 1.0) * repeatedTreeSearchCost;
        }

        var indexLeafPagesCount = Math.Max(rowsPerSeek / indexInfo.RowsPerLeafPage, 1.0);
        double leafScanCost;
        if (isFullScan)
        {
            leafScanCost = inputCardinality <= 1.0
                ? indexLeafPagesCount
                : indexLeafPagesCount
                    + (inputCardinality - 1.0) * indexLeafPagesCount * JoinCostParams.CacheReuseFactor;
        }
        else if (rowsPerSeek <= 1.0)
        {
            leafScanCost = 0.0;
        }
        else if (inputCardinality <= 1.0)
        {
            leafScanCost = indexLeafPagesCount;
        }
        else
        {
            leafScanCost = indexLeafPagesCount
                + (inputCardinality - 1.0) * indexLeafPagesCount * JoinCostParams.CacheReuseFactor;
        }

        double tableLookupCost;
        if (indexInfo.Covering)
        {
            tableLookupCost = 0.0;
        }
        else
        {
            var tablePagesCount = Math.Max(baseRowCount / JoinCostParams.RowsPerTablePage, 1.0);
            var selectivity = rowsPerSeek / Math.Max(baseRowCount, 1.0);
            tableLookupCost = inputCardinality * selectivity * tablePagesCount;
        }

        var ioCost = seekCost + leafScanCost + tableLookupCost;
        var totalRows = inputCardinality * rowsPerSeek;
        var cpuCost = inputCardinality * JoinCostParams.CpuCostPerSeek
            + totalRows * JoinCostParams.CpuCostPerRow;
        return Math.Max(ioCost + cpuCost - JoinCostParams.IndexBonus, 0.001);
    }

    /// <summary>
    /// access_method.rs:1242-1290 <c>estimate_hash_join_cost</c>, including the grace-hash
    /// spill term against <see cref="TursoCostParams.HashJoinMemoryBudgetBytes"/>.
    /// </summary>
    public static double EstimateHashJoinCost(
        double buildCardinality,
        double probeCardinality,
        double rowsVisited,
        bool keepsUnmatchedBuildRows,
        double probeMultiplier)
    {
        // `build_cardinality as usize` saturates (and truncates) before the multiply.
        var estimatedHashTableSize = Math.Floor(Math.Max(buildCardinality, 0.0)) * TursoCostParams.HashBytesPerRow;
        var willSpill = estimatedHashTableSize > TursoCostParams.HashJoinMemoryBudgetBytes;
        var buildCost = buildCardinality * (JoinCostParams.HashCpuCost + JoinCostParams.HashInsertCost);
        var probeScanCost = EstimateScanCost(probeCardinality, probeMultiplier);
        var probeHashCost = probeCardinality
            * (JoinCostParams.HashCpuCost + JoinCostParams.HashLookupCost)
            * probeMultiplier;
        var visitCost = rowsVisited * JoinCostParams.CpuCostPerRow;
        var unmatchedBuildCost = keepsUnmatchedBuildRows
            ? buildCardinality * JoinCostParams.CpuCostPerRow
            : 0.0;
        var spillCost = 0.0;
        if (willSpill)
        {
            var buildPages = Math.Ceiling(buildCardinality / JoinCostParams.RowsPerTablePage);
            var probePages = Math.Ceiling(probeCardinality / JoinCostParams.RowsPerTablePage);
            spillCost = (buildPages + probePages) * 2.0 * probeMultiplier;
        }

        return buildCost + probeScanCost + probeHashCost + visitCost + unmatchedBuildCost + spillCost;
    }

    /// <summary>
    /// access_method.rs:687-707 <c>cost_with_where_work</c>: the CPU spent evaluating the
    /// <c>WHERE</c> terms that become ready at one loop. Terms the access path consumes run
    /// once per input or output row (whichever is larger); the rest run once per output row.
    /// </summary>
    public static double EstimateWhereWork(
        double inputCardinality,
        double rowsPerOuterRow,
        int consumedSteps,
        int remainingSteps)
    {
        var outputRows = inputCardinality * rowsPerOuterRow;
        var usedRows = Math.Max(inputCardinality, outputRows);
        var work = usedRows * consumedSteps + outputRows * remainingSteps;
        return work * TursoCostParams.CpuCostPerWhereStep;
    }
}

/// <summary>cost.rs:68-79 <c>IndexInfo</c>.</summary>
internal readonly record struct TursoIndexInfo(
    bool Unique,
    int ColumnCount,
    bool Covering,
    double RowsPerLeafPage);
