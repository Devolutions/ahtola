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
internal sealed record JoinSegmentMember(int OriginalIndex, double RowCount, int ColumnWidth);

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
internal sealed record JoinPredicateTerm(
    ulong TableMask,
    bool IsEquality,
    ulong EqualityLeftMask,
    ulong EqualityRightMask,
    double EqualityLeftMatchRows,
    double EqualityRightMatchRows,
    double Selectivity);

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
        double cost,
        double cardinality,
        bool usedDynamicProgramming)
    {
        MemberOrder = memberOrder;
        StepShapes = stepShapes;
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
