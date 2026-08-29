namespace Ahtola.Core.Compilation.JoinOrdering;

/// <summary>
/// One reorderable unit inside a maximal plain-INNER join segment: either a base-table leaf or
/// an opaque barrier subtree (an outer/NATURAL/USING join) that keeps its internal shape.
/// </summary>
/// <param name="OriginalIndex">
/// Position in the original left-to-right FROM sequence. Used for deterministic tie-breaking and
/// to map the projection back into FROM order after a physical reorder.
/// </param>
/// <param name="RowCount">Estimated base cardinality, read from <c>sqlite_stat1</c>.</param>
/// <param name="ColumnWidth">Number of value slots this member contributes to a joined row.</param>
/// <param name="IndexCandidates">Persisted or automatic indexes available to this join step.</param>
internal sealed record JoinSegmentMember(
    int OriginalIndex,
    double RowCount,
    int ColumnWidth,
    IReadOnlyList<JoinIndexCandidate>? IndexCandidates = null);

/// <summary>
/// One persisted or automatic index usable as an outer-bound join access. <c>Forced</c> is true
/// when SQL named a persisted index through mandatory <c>INDEXED BY</c>.
/// </summary>
internal sealed record JoinIndexCandidate(
    string Name,
    IReadOnlyList<JoinIndexColumn> Columns,
    IReadOnlyList<double> RowsPerPrefix,
    bool Unique,
    bool Covering,
    int TableColumnCount,
    bool HasRowIdAlias,
    bool Forced = false,
    bool Automatic = false,
    bool LazyCursor = false);

/// <summary>One key column in persisted index order.</summary>
internal readonly record struct JoinIndexColumn(
    int ColumnOrdinal,
    string Collation,
    bool Descending);

/// <summary>
/// One AND-conjunct available to the segment, already resolved to the members it references.
/// This is the narrow analog of Turso's <c>constraints.rs</c> <c>Constraint</c>: Ahtola has no
/// per-column NDV graph, so a term carries only its member mask, its equality decomposition, and
/// a single fallback selectivity.
/// </summary>
/// <param name="TableMask">Bit <c>i</c> set when the term references member index <c>i</c>.</param>
/// <param name="IsEquality">
/// True when the term is <c>colA = colB</c> with each side resolving to exactly one distinct
/// member, i.e. the exact shape <c>TryCreateCompiledJoinEquiProbe</c> can turn into a hash key.
/// </param>
/// <param name="EqualityLeftMask">Single-member mask of the left operand (0 when not an equality).</param>
/// <param name="EqualityRightMask">Single-member mask of the right operand (0 when not an equality).</param>
/// <param name="EqualityLeftMatchRows">
/// Expected rows of the left operand's member per distinct key value, from <c>sqlite_stat1</c>
/// index averages where available.
/// </param>
/// <param name="EqualityRightMatchRows">Same figure for the right operand's member.</param>
/// <param name="Selectivity">
/// Residual multiplier applied when the term is ready but cannot serve as a hash key.
/// </param>
/// <param name="EqualityLeftColumnOrdinal">Base-table ordinal of the left equality operand.</param>
/// <param name="EqualityRightColumnOrdinal">Base-table ordinal of the right equality operand.</param>
/// <param name="EqualityLeftConvertsTextToNumeric">Whether comparison affinity converts the left value.</param>
/// <param name="EqualityLeftConvertsNumericToText">Whether comparison affinity textifies the left value.</param>
/// <param name="EqualityRightConvertsTextToNumeric">Whether comparison affinity converts the right value.</param>
/// <param name="EqualityRightConvertsNumericToText">Whether comparison affinity textifies the right value.</param>
/// <param name="EqualityCollation">Resolved built-in comparison collation.</param>
internal sealed record JoinPredicateTerm(
    ulong TableMask,
    bool IsEquality,
    ulong EqualityLeftMask,
    ulong EqualityRightMask,
    double EqualityLeftMatchRows,
    double EqualityRightMatchRows,
    double Selectivity,
    int EqualityLeftColumnOrdinal = -1,
    int EqualityRightColumnOrdinal = -1,
    bool EqualityLeftConvertsTextToNumeric = false,
    bool EqualityLeftConvertsNumericToText = false,
    bool EqualityRightConvertsTextToNumeric = false,
    bool EqualityRightConvertsNumericToText = false,
    string? EqualityCollation = null);

/// <summary>
/// Exact index and equality terms selected for one <c>IndexSeekRight</c> step.
/// </summary>
internal sealed record JoinIndexAccessChoice(
    int CandidateIndex,
    int[] EqualityTermIndices,
    double RowsPerSeek);

/// <summary>
/// A maximal plain-INNER join segment: the members that may be freely permuted plus the pool of
/// conjuncts that can be attached wherever they first become evaluable.
/// </summary>
internal sealed record JoinSegment(
    IReadOnlyList<JoinSegmentMember> Members,
    IReadOnlyList<JoinPredicateTerm> Terms);

/// <summary>
/// One enumerated left-deep plan: the member order, the physical shape chosen for each step, and
/// the running cost/cardinality the cost model assigned.
/// </summary>
internal sealed class JoinOrderPlan
{
    public JoinOrderPlan(
        int[] memberOrder,
        JoinStepShape[] stepShapes,
        JoinIndexAccessChoice?[] indexAccesses,
        double cost,
        double cardinality,
        bool usedDynamicProgramming)
    {
        MemberOrder = memberOrder;
        StepShapes = stepShapes;
        IndexAccesses = indexAccesses;
        Cost = cost;
        Cardinality = cardinality;
        UsedDynamicProgramming = usedDynamicProgramming;
    }

    /// <summary>Member indices in left-deep execution order.</summary>
    public int[] MemberOrder { get; }

    /// <summary>
    /// Shape for each step. <c>StepShapes[k]</c> describes the node that adds
    /// <c>MemberOrder[k]</c>; index 0 is unused because the first member is a bare scan.
    /// </summary>
    public JoinStepShape[] StepShapes { get; }

    /// <summary>Selected index metadata for each <see cref="JoinStepShape.IndexSeekRight"/> step.</summary>
    public JoinIndexAccessChoice?[] IndexAccesses { get; }

    public double Cost { get; }

    public double Cardinality { get; }

    /// <summary>False when the greedy fallback produced this plan.</summary>
    public bool UsedDynamicProgramming { get; }

    /// <summary>True when the plan is the unmodified left-to-right FROM order.</summary>
    public bool IsIdentityOrder
    {
        get
        {
            for (var index = 0; index < MemberOrder.Length; index++)
            {
                if (MemberOrder[index] != index)
                    return false;
            }

            return true;
        }
    }
}
