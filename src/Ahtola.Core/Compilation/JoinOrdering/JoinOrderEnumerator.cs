using System.Linq;
using System.Numerics;

namespace Ahtola.Core.Compilation.JoinOrdering;

/// <summary>The outer-join kind of a step costed by the Turso model.</summary>
internal enum TursoOuterJoin
{
    None,
    Left,
    Full,
}

/// <summary>
/// Arbitrary-N join-order enumeration for a single plain-INNER segment.
/// </summary>
/// <remarks>
/// <para>
/// Ports the two-tier strategy of Turso v0.8.0-pre.7
/// <c>core/translate/optimizer/join.rs</c>: a System-R style subset dynamic program
/// (<c>compute_best_join_order_with_context</c>, join.rs:1090-1566) below a member cap, and a
/// linear greedy build-up (<c>compute_greedy_join_order</c>, join.rs:1579) above it, exactly as
/// <c>GREEDY_JOIN_THRESHOLD</c> (join.rs:1569) selects between them upstream.
/// </para>
/// <para>
/// <b>Determinism.</b> join.rs relies on sorted <c>TableMask</c> keys to keep its memo iteration
/// stable (join.rs:1379-1381). The managed port avoids the problem structurally: the memo is a
/// flat array indexed by <c>(subsetMask, lastMember)</c> and every loop walks masks and members
/// in increasing numeric order, so no hash-table enumeration order can leak into the result.
/// Cost ties are then broken by the lexicographically smallest member order, which makes the
/// original FROM order (<c>0,1,2,…</c>) the winner of any exact tie.
/// </para>
/// <para>
/// The enumerator is intentionally free of AST and schema types: it consumes only the numeric
/// <see cref="JoinSegment"/> model, so it can be exercised directly against a brute-force
/// permutation oracle.
/// </para>
/// </remarks>
internal static class JoinOrderEnumerator
{
    /// <summary>
    /// Largest segment the subset DP is allowed to enumerate. This matches Turso's
    /// <c>GREEDY_JOIN_THRESHOLD = 12</c>; larger segments use the deterministic greedy
    /// build-up below.
    /// </summary>
    public const int DynamicProgrammingMemberCap = 12;

    /// <summary>
    /// Hard ceiling on segment size. The managed planner supports the full 64-member SQLite
    /// join mask; only the first twelve members ever enter subset-DP enumeration.
    /// </summary>
    public const int MaximumMembers = 64;

    private const double CostEpsilon = 1e-9;

    /// <summary>join.rs:65-67 <c>MAX_MATERIALIZED_BUILD_ROWS</c>.</summary>
    private const double MaximumMaterializedBuildRows = 200_000.0;

    /// <summary>
    /// Upper bound on how many mutually incomparable partial plans one DP state keeps. Reaching
    /// it is a pathological shape, not a normal one; the cap keeps prepare-time work bounded.
    /// </summary>
    private const int MaximumFrontierEntries = 32;

    /// <summary>
    /// Chooses a left-deep order for <paramref name="segment"/>, or <c>null</c> when the segment
    /// is not enumerable (fewer than two members, or wider than <see cref="MaximumMembers"/>).
    /// </summary>
    public static JoinOrderPlan? Compute(JoinSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var count = segment.Members.Count;
        if (count < 2 || count > MaximumMembers)
            return null;

        // Seed the pruning bound with the plan the engine would run today, exactly as
        // join.rs:1138-1155 seeds `cost_upper_bound` from the natural order instead of +inf.
        var identityOrder = new int[count];
        for (var index = 0; index < count; index++)
            identityOrder[index] = index;
        var identity = EvaluateOrder(segment, identityOrder, usedDynamicProgramming: count <= DynamicProgrammingMemberCap);

        return count <= DynamicProgrammingMemberCap
            ? ComputeDynamicProgramming(segment, identity)
            : ComputeGreedy(segment, identity);
    }

    /// <summary>
    /// Scores one explicit left-deep order under the same per-step model the enumerators use.
    /// Exposed so tests can cross-check the dynamic program against brute-force permutations.
    /// </summary>
    public static JoinOrderPlan EvaluateOrder(
        JoinSegment segment,
        int[] memberOrder,
        bool usedDynamicProgramming = true)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(memberOrder);
        if (memberOrder.Length != segment.Members.Count)
            throw new ArgumentException("Order must cover every member.", nameof(memberOrder));

        var shapes = new JoinStepShape[memberOrder.Length];
        var indexAccesses = new JoinIndexAccessChoice?[memberOrder.Length];
        var first = segment.Members[memberOrder[0]];
        var (firstCost, cardinality) = EvaluateFirstStep(segment, memberOrder[0]);
        var cost = HasForcedIndexCandidate(first)
            ? double.PositiveInfinity
            : firstCost;
        var placed = 1UL << memberOrder[0];

        for (var step = 1; step < memberOrder.Length; step++)
        {
            var member = memberOrder[step];
            var evaluation = EvaluateStep(segment, placed, member, cardinality, memberOrder[step - 1]);
            shapes[step] = evaluation.Shape;
            indexAccesses[step] = evaluation.IndexAccess;
            cost += evaluation.StepCost;
            cardinality = evaluation.OutputCardinality;
            placed |= 1UL << member;
        }

        return new JoinOrderPlan(
            memberOrder,
            shapes,
            indexAccesses,
            cost,
            cardinality,
            usedDynamicProgramming);
    }

    private static JoinOrderPlan ComputeDynamicProgramming(JoinSegment segment, JoinOrderPlan identity)
    {
        var count = segment.Members.Count;
        var maskCount = 1 << count;
        var stateCount = maskCount * count;

        // Flat memo keyed by (subsetMask, lastMember). Each state keeps a Pareto frontier over
        // (cost, cardinality) rather than a single best plan, mirroring join.rs:1210-1216's
        // "don't collapse to one plan per subset" rule. A completion's cost is monotone in both
        // dimensions, so discarding only dominated partial plans cannot discard the optimum.
        var frontiers = new List<JoinOrderDpEntry>?[stateCount];

        for (var member = 0; member < count; member++)
        {
            if (HasForcedIndexCandidate(segment.Members[member]))
                continue;

            var state = ((1 << member) * count) + member;
            var (memberCost, memberCardinality) = EvaluateFirstStep(segment, member);
            frontiers[state] =
            [
                new JoinOrderDpEntry(
                    memberCost,
                    memberCardinality,
                    [member],
                    [JoinStepShape.NestedLoop],
                    [null]),
            ];
        }

        // The identity plan is always a valid answer, so its cost is a sound pruning bound: all
        // step costs are non-negative, making a partial cost a lower bound on any completion.
        var upperBound = identity.Cost;
        var full = maskCount - 1;

        for (var mask = 1; mask < maskCount; mask++)
        {
            if (mask == full)
                continue;

            for (var last = 0; last < count; last++)
            {
                if ((mask & (1 << last)) == 0)
                    continue;
                if (frontiers[(mask * count) + last] is not { } entries)
                    continue;

                // Snapshot the count: extensions only ever write into strictly larger masks.
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    var entry = entries[entryIndex];
                    if (entry.Cost >= upperBound + CostEpsilon)
                        continue;

                    for (var next = 0; next < count; next++)
                    {
                        if ((mask & (1 << next)) != 0)
                            continue;

                        var evaluation = EvaluateStep(segment, (ulong)mask, next, entry.Cardinality, entry.Order[^1]);
                        var nextCost = entry.Cost + evaluation.StepCost;
                        if (nextCost >= upperBound + CostEpsilon)
                            continue;

                        var nextOrder = new int[entry.Order.Length + 1];
                        Array.Copy(entry.Order, nextOrder, entry.Order.Length);
                        nextOrder[^1] = next;
                        var nextShapes = new JoinStepShape[entry.Shapes.Length + 1];
                        Array.Copy(entry.Shapes, nextShapes, entry.Shapes.Length);
                        nextShapes[^1] = evaluation.Shape;
                        var nextIndexAccesses = new JoinIndexAccessChoice?[entry.IndexAccesses.Length + 1];
                        Array.Copy(entry.IndexAccesses, nextIndexAccesses, entry.IndexAccesses.Length);
                        nextIndexAccesses[^1] = evaluation.IndexAccess;

                        var nextMask = mask | (1 << next);
                        var nextState = (nextMask * count) + next;
                        var candidate = new JoinOrderDpEntry(
                            nextCost,
                            evaluation.OutputCardinality,
                            nextOrder,
                            nextShapes,
                            nextIndexAccesses);
                        if (!TryInsertDpEntry(frontiers, nextState, candidate))
                            continue;

                        // Tighten the pruning bound as soon as a complete plan improves on it,
                        // the way join.rs:1138-1155 keeps `cost_upper_bound` current.
                        if (nextMask == full && nextCost < upperBound)
                            upperBound = nextCost;
                    }
                }
            }
        }

        var best = identity;
        for (var last = 0; last < count; last++)
        {
            if (frontiers[(full * count) + last] is not { } entries)
                continue;

            foreach (var entry in entries)
            {
                if (!IsBetter(entry.Cost, entry.Order, best.Cost, best.MemberOrder))
                    continue;

                best = new JoinOrderPlan(
                    entry.Order,
                    entry.Shapes,
                    entry.IndexAccesses,
                    entry.Cost,
                    entry.Cardinality,
                    usedDynamicProgramming: true);
            }
        }

        return best;
    }

    /// <summary>
    /// Adds <paramref name="candidate"/> to a state's Pareto frontier, dropping anything it
    /// dominates. Returns false when an existing entry already dominates it.
    /// </summary>
    private static bool TryInsertDpEntry(
        List<JoinOrderDpEntry>?[] frontiers,
        int state,
        JoinOrderDpEntry candidate)
    {
        var entries = frontiers[state];
        if (entries is null)
        {
            frontiers[state] = [candidate];
            return true;
        }

        foreach (var entry in entries)
        {
            if (Dominates(entry, candidate))
                return false;
        }

        // Bound the frontier so a pathological segment cannot make prepare-time work explode.
        // Dominated entries are already removed, so reaching the cap means many mutually
        // incomparable plans; keeping the first ones is deterministic.
        if (entries.Count >= MaximumFrontierEntries)
            return false;

        entries.RemoveAll(entry => Dominates(candidate, entry));
        entries.Add(candidate);
        return true;

        static bool Dominates(JoinOrderDpEntry left, JoinOrderDpEntry right)
            => left.Cost <= right.Cost + CostEpsilon
                && left.Cardinality <= right.Cardinality + CostEpsilon
                && CompareOrders(left.Order, right.Order) <= 0;
    }

    private static int CompareOrders(int[] left, int[] right)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var index = 0; index < shared; index++)
        {
            if (left[index] != right[index])
                return left[index] < right[index] ? -1 : 1;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static JoinOrderPlan ComputeGreedy(JoinSegment segment, JoinOrderPlan identity)
    {
        var count = segment.Members.Count;
        var order = new int[count];
        var shapes = new JoinStepShape[count];
        var indexAccesses = new JoinIndexAccessChoice?[count];
        var used = new bool[count];

        var firstMember = -1;
        var firstCost = double.PositiveInfinity;
        for (var member = 0; member < count; member++)
        {
            if (HasForcedIndexCandidate(segment.Members[member]))
                continue;

            var candidate = EvaluateFirstStep(segment, member).Cost;
            if (candidate >= firstCost - CostEpsilon)
                continue;

            firstCost = candidate;
            firstMember = member;
        }

        if (firstMember < 0)
            return identity;

        order[0] = firstMember;
        shapes[0] = JoinStepShape.NestedLoop;
        used[firstMember] = true;
        var placed = 1UL << firstMember;
        var cardinality = EvaluateFirstStep(segment, firstMember).Cardinality;
        var cost = firstCost;

        for (var step = 1; step < count; step++)
        {
            var bestMember = -1;
            var bestCost = double.PositiveInfinity;
            var bestShape = JoinStepShape.NestedLoop;
            JoinIndexAccessChoice? bestIndexAccess = null;
            var bestCardinality = cardinality;
            for (var member = 0; member < count; member++)
            {
                if (used[member])
                    continue;

                var evaluation = EvaluateStep(segment, placed, member, cardinality, order[step - 1]);
                // Strict improvement only, so the lowest member index wins every tie.
                if (bestMember >= 0 && evaluation.StepCost >= bestCost - CostEpsilon)
                    continue;

                bestMember = member;
                bestCost = evaluation.StepCost;
                bestShape = evaluation.Shape;
                bestIndexAccess = evaluation.IndexAccess;
                bestCardinality = evaluation.OutputCardinality;
            }

            order[step] = bestMember;
            shapes[step] = bestShape;
            indexAccesses[step] = bestIndexAccess;
            used[bestMember] = true;
            placed |= 1UL << bestMember;
            cardinality = bestCardinality;
            cost += bestCost;
        }

        var greedy = new JoinOrderPlan(
            order,
            shapes,
            indexAccesses,
            cost,
            cardinality,
            usedDynamicProgramming: false);
        return IsBetter(greedy.Cost, greedy.MemberOrder, identity.Cost, identity.MemberOrder)
            ? greedy
            : identity;
    }

    /// <summary>
    /// Cheaper wins; an exact tie goes to the lexicographically smaller order, which makes the
    /// unmodified FROM order the winner and removes every dependency on enumeration order.
    /// </summary>
    private static bool IsBetter(double cost, int[] order, double incumbentCost, int[]? incumbentOrder)
    {
        if (incumbentOrder is null || double.IsPositiveInfinity(incumbentCost))
            return true;
        if (cost < incumbentCost - CostEpsilon)
            return true;
        if (cost > incumbentCost + CostEpsilon)
            return false;

        var shared = Math.Min(order.Length, incumbentOrder.Length);
        for (var index = 0; index < shared; index++)
        {
            if (order[index] != incumbentOrder[index])
                return order[index] < incumbentOrder[index];
        }

        return order.Length < incumbentOrder.Length;
    }

    /// <summary>
    /// The cost and output cardinality of the plan's first (outer-most) member: a full scan.
    /// Under the Turso model this is <c>estimate_scan_cost</c> plus the WHERE work of the
    /// member's own ready filters, producing the rows those filters keep
    /// (join.rs:156-190 rows_after_join, access_method.rs:687-707).
    /// </summary>
    internal static (double Cost, double Cardinality) EvaluateFirstStep(JoinSegment segment, int member)
    {
        var rows = Math.Max(1.0, segment.Members[member].RowCount);
        if (!segment.UseTursoCostModel)
            return (JoinCostModel.EstimateFullScanCost(segment.Members[member].RowCount, scanCount: 1.0), rows);

        var memberBit = 1UL << member;
        var extraSteps = 0;
        var selectivity = 1.0;
        foreach (var term in segment.Terms)
        {
            if (term.TableMask != memberBit)
                continue;
            extraSteps += term.WhereExtraSteps;
            selectivity *= term.TursoLocalSelectivity;
        }

        var cost = TursoCostModel.EstimateScanCost(rows, 1.0)
            + TursoCostModel.EstimateWhereWork(1.0, rows, consumedSteps: 0, extraSteps);
        return (cost, rows * selectivity);
    }

    /// <summary>
    /// Scores adding <paramref name="member"/> onto a partial plan covering
    /// <paramref name="placedMask"/> with cardinality <paramref name="leftCardinality"/>.
    /// </summary>
    private static StepEvaluation EvaluateStep(
        JoinSegment segment,
        ulong placedMask,
        int member,
        double leftCardinality,
        int lastMember = -1)
    {
        if (segment.UseTursoCostModel)
            return EvaluateTursoStep(segment, placedMask, member, leftCardinality, lastMember: lastMember);

        var memberBit = 1UL << member;
        var candidateMask = placedMask | memberBit;
        var rightRows = Math.Max(1.0, segment.Members[member].RowCount);

        var rowsPerOuterRow = rightRows;
        var hasEqualityKey = false;
        var residualSelectivity = 1.0;

        foreach (var term in segment.Terms)
        {
            if ((term.TableMask & ~candidateMask) != 0)
            {
                continue;
            }

            // A term whose mask is already covered was attached at an earlier step; the only
            // exception is the very first step, which is where terms local to the leading member
            // (and constant terms) first become attachable to a join node.
            var alreadyApplied = (term.TableMask & ~placedMask) == 0 && BitOperations.PopCount(placedMask) >= 2;
            if (alreadyApplied)
            {
                continue;
            }

            if (TryGetEqualityMatchRows(term, placedMask, memberBit, out var matchRows, out var hashable))
            {
                // Narrowing the cardinality estimate is safe for any resolved equality, but only
                // a hashable one may make this step consider a hash-build shape below — a custom
                // or overridden-built-in collation still reaches the index-seek-or-nested-loop
                // branch (see the `!hasEqualityKey` check), never the hash cost comparison.
                hasEqualityKey = hasEqualityKey || hashable;
                rowsPerOuterRow = Math.Min(rowsPerOuterRow, Math.Max(matchRows, 0.0));
                continue;
            }

            residualSelectivity *= term.Selectivity;
        }

        residualSelectivity = Math.Clamp(residualSelectivity, 1e-6, 1.0);
        var output = JoinCostModel.RowsAfterStep(leftCardinality, rowsPerOuterRow, residualSelectivity);

        var indexAccess = FindBestIndexAccess(segment, placedMask, member, leftCardinality);
        if (indexAccess is null && HasForcedIndexCandidate(segment.Members[member]))
        {
            return new StepEvaluation(
                JoinStepShape.NestedLoop,
                double.PositiveInfinity,
                output,
                null);
        }

        if (indexAccess is not null)
        {
            rowsPerOuterRow = Math.Min(rowsPerOuterRow, indexAccess.RowsPerSeek);
            output = JoinCostModel.RowsAfterStep(leftCardinality, rowsPerOuterRow, residualSelectivity);
        }

        if (!hasEqualityKey)
        {
            // Without a hash key the executor runs the full cross scan; there is no build-side
            // choice to make, so no cheaper shape may be claimed.
            if (indexAccess is not null)
            {
                return EvaluateIndexSeek(
                    segment.Members[member],
                    leftCardinality,
                    output,
                    indexAccess);
            }

            return new StepEvaluation(
                JoinStepShape.NestedLoop,
                JoinCostModel.EstimateStepCost(JoinStepShape.NestedLoop, leftCardinality, rightRows, output),
                output,
                null);
        }

        var buildRightCost = JoinCostModel.EstimateStepCost(
            JoinStepShape.HashBuildRight,
            leftCardinality,
            rightRows,
            output);
        var buildLeftCost = JoinCostModel.EstimateStepCost(
            JoinStepShape.HashBuildLeft,
            leftCardinality,
            rightRows,
            output);

        // Ties keep hash-build-right, the executor's default shape.
        var best = buildLeftCost < buildRightCost - CostEpsilon
            ? new StepEvaluation(JoinStepShape.HashBuildLeft, buildLeftCost, output, null)
            : new StepEvaluation(JoinStepShape.HashBuildRight, buildRightCost, output, null);
        if (indexAccess is null)
            return best;

        var seek = EvaluateIndexSeek(
            segment.Members[member],
            leftCardinality,
            output,
            indexAccess);
        var candidate = segment.Members[member].IndexCandidates![indexAccess.CandidateIndex];
        if (candidate.Automatic && best.Shape != JoinStepShape.HashBuildRight)
            return best;
        return candidate.Forced || seek.StepCost < best.StepCost - CostEpsilon ? seek : best;
    }

    private static StepEvaluation EvaluateIndexSeek(
        JoinSegmentMember member,
        double leftCardinality,
        double output,
        JoinIndexAccessChoice access)
    {
        var candidate = member.IndexCandidates![access.CandidateIndex];
        var uniquePointLookup = candidate.Unique
            && access.EqualityTermIndices.Length == candidate.Columns.Count;
        var rowsPerSeek = uniquePointLookup ? 1.0 : access.RowsPerSeek;
        var cost = candidate.Automatic
            ? JoinCostModel.EstimateAutomaticIndexCost(
                member.RowCount,
                leftCardinality,
                rowsPerSeek)
            : (candidate.LazyCursor ? 0.0 : JoinCostModel.EstimateManagedIndexViewBuildCost(member.RowCount))
                + JoinCostModel.EstimateIndexSeekCost(
                member.RowCount,
                candidate.Columns.Count,
                candidate.TableColumnCount,
                candidate.HasRowIdAlias,
                candidate.Covering,
                leftCardinality,
                rowsPerSeek);
        return new StepEvaluation(JoinStepShape.IndexSeekRight, cost, output, access);
    }

    private static JoinIndexAccessChoice? FindBestIndexAccess(
        JoinSegment segment,
        ulong placedMask,
        int member,
        double leftCardinality)
    {
        var candidates = segment.Members[member].IndexCandidates;
        if (candidates is null || candidates.Count == 0)
            return null;

        var memberBit = 1UL << member;
        JoinIndexAccessChoice? best = null;
        var bestCost = double.PositiveInfinity;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var termIndices = new List<int>(candidate.Columns.Count);
            for (var columnIndex = 0; columnIndex < candidate.Columns.Count; columnIndex++)
            {
                var column = candidate.Columns[columnIndex];
                var matchedTerm = -1;
                for (var termIndex = 0; termIndex < segment.Terms.Count; termIndex++)
                {
                    if (termIndices.Contains(termIndex)
                        || !CanBindIndexColumn(
                            segment.Terms[termIndex],
                            placedMask,
                            memberBit,
                            column))
                    {
                        continue;
                    }

                    matchedTerm = termIndex;
                    break;
                }

                if (matchedTerm < 0)
                    break;
                termIndices.Add(matchedTerm);
            }

            if (termIndices.Count == 0 || termIndices.Count > candidate.RowsPerPrefix.Count)
                continue;

            var rowsPerSeek = candidate.Unique && termIndices.Count == candidate.Columns.Count
                ? 1.0
                : Math.Max(1.0, candidate.RowsPerPrefix[termIndices.Count - 1]);
            var cost = candidate.Automatic
                ? JoinCostModel.EstimateAutomaticIndexCost(
                    segment.Members[member].RowCount,
                    leftCardinality,
                    rowsPerSeek)
                : (candidate.LazyCursor
                        ? 0.0
                        : JoinCostModel.EstimateManagedIndexViewBuildCost(segment.Members[member].RowCount))
                    + JoinCostModel.EstimateIndexSeekCost(
                        segment.Members[member].RowCount,
                        candidate.Columns.Count,
                        candidate.TableColumnCount,
                        candidate.HasRowIdAlias,
                        candidate.Covering,
                        leftCardinality,
                        rowsPerSeek);
            if (cost < bestCost - CostEpsilon
                || Math.Abs(cost - bestCost) <= CostEpsilon
                    && best is not null
                    && termIndices.Count > best.EqualityTermIndices.Length)
            {
                best = new JoinIndexAccessChoice(candidateIndex, [.. termIndices], rowsPerSeek);
                bestCost = cost;
            }
        }

        return best;
    }

    private static bool HasForcedIndexCandidate(JoinSegmentMember member)
        => member.IndexCandidates?.Any(static candidate => candidate.Forced) == true;

    private static bool CanBindIndexColumn(
        JoinPredicateTerm term,
        ulong placedMask,
        ulong memberBit,
        JoinIndexColumn indexColumn)
    {
        // Seek binding uses EqualitySeekCollation (populated whenever the equality's operands
        // structurally resolve) rather than EqualityCollation (populated only when also safe to
        // hash) — DescribeJoinIndexCandidates already gates which indexes are even offered as
        // candidates, so a custom or overridden-built-in collation can still bind a direct index
        // seek here even though TryBuildAutomaticEquiJoinIndexCandidate will never hash it.
        if (!term.IsEquality
            || !string.Equals(term.EqualitySeekCollation, indexColumn.Collation, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return term.EqualityRightMask == memberBit
                && (term.EqualityLeftMask & ~placedMask) == 0
                && term.EqualityRightColumnOrdinal == indexColumn.ColumnOrdinal
                && !term.EqualityRightConvertsTextToNumeric
                && !term.EqualityRightConvertsNumericToText
            || term.EqualityLeftMask == memberBit
                && (term.EqualityRightMask & ~placedMask) == 0
                && term.EqualityLeftColumnOrdinal == indexColumn.ColumnOrdinal
                && !term.EqualityLeftConvertsTextToNumeric
                && !term.EqualityLeftConvertsNumericToText;
    }

    /// <summary>
    /// True when <paramref name="term"/> can serve as this step's hash key: one operand must be
    /// fully covered by the already-placed members and the other must be exactly the member
    /// being added, which is the same left/right split
    /// <c>EmbeddedDatabase.TryCreateCompiledJoinEquiProbe</c> requires. <paramref name="hashable"/>
    /// additionally reports whether <see cref="JoinPredicateTerm.EqualityCollation"/> is set —
    /// i.e. whether this same equality is also safe to key a hash-build step off of, as opposed
    /// to only being usable for a direct index seek (<see cref="JoinPredicateTerm.EqualitySeekCollation"/>).
    /// A custom or overridden-built-in collation match still narrows the cardinality estimate
    /// (<paramref name="matchRows"/>) but must never make the caller consider a hash shape.
    /// </summary>
    private static bool TryGetEqualityMatchRows(
        JoinPredicateTerm term,
        ulong placedMask,
        ulong memberBit,
        out double matchRows,
        out bool hashable)
    {
        matchRows = 0.0;
        hashable = term.EqualityCollation is not null;
        if (!term.IsEquality)
            return false;

        if ((term.EqualityLeftMask & ~placedMask) == 0 && term.EqualityRightMask == memberBit)
        {
            matchRows = term.EqualityRightMatchRows;
            return true;
        }

        if ((term.EqualityRightMask & ~placedMask) == 0 && term.EqualityLeftMask == memberBit)
        {
            matchRows = term.EqualityLeftMatchRows;
            return true;
        }

        return false;
    }

    /// <summary>
    /// One left-deep step costed with the ported Turso v0.8.1 join planner
    /// (optimizer/join.rs <c>join_lhs_and_rhs</c>, access_method.rs
    /// <c>find_best_access_method_for_btree</c> and <c>try_hash_join_access_method</c>), mapped
    /// onto the shapes the managed operator executes:
    /// <list type="bullet">
    /// <item>a full scan of the member per outer row (<c>estimate_scan_cost</c> with the outer
    /// cardinality as the scan count) → <see cref="JoinStepShape.NestedLoop"/>;</item>
    /// <item>a declared index seek on an outer-bound equality prefix
    /// (<c>estimate_index_cost</c>) → <see cref="JoinStepShape.IndexSeekRight"/>;</item>
    /// <item>Turso's temporary index when the best B-tree access is a full scan
    /// (scan + <c>estimate_ephemeral_index_build_cost</c> + per-seek work) → the operator's hash
    /// of the right input, <see cref="JoinStepShape.HashBuildRight"/>;</item>
    /// <item>a hash join building the previous table and probing this one
    /// (<c>estimate_hash_join_cost</c>, only while the outer side is that single table, the
    /// left-deep build Turso allows without materialization) →
    /// <see cref="JoinStepShape.HashBuildLeft"/>.</item>
    /// </list>
    /// Every alternative carries the WHERE work of the terms that become ready at this step
    /// (<c>cost_with_where_work</c>), and a step with no join term pays Turso's cross-product
    /// penalty.
    /// </summary>
    internal static StepEvaluation EvaluateTursoStep(
        JoinSegment segment,
        ulong placedMask,
        int member,
        double inputCardinality,
        TursoOuterJoin outerJoin = TursoOuterJoin.None,
        int lastMember = -1)
    {
        var memberBit = 1UL << member;
        var candidateMask = placedMask | memberBit;
        var target = segment.Members[member];
        var rows = Math.Max(1.0, target.RowCount);

        // Terms that become ready at this step, and the constraints they put on the member.
        var ready = new List<int>();
        var joinEqualities = new List<(int Term, int Ordinal, double Selectivity, bool Hashable)>();
        var hasJoinTerm = false;
        var memberLocalSelectivity = 1.0;
        for (var index = 0; index < segment.Terms.Count; index++)
        {
            var term = segment.Terms[index];
            if ((term.TableMask & memberBit) == 0 || (term.TableMask & ~candidateMask) != 0)
                continue;
            ready.Add(index);
            if ((term.TableMask & placedMask) != 0)
                hasJoinTerm = true;
            if (term.TableMask == memberBit)
                memberLocalSelectivity *= term.TursoLocalSelectivity;

            if (!term.IsEquality || term.CostOnly)
                continue;
            if (term.EqualityRightMask == memberBit && (term.EqualityLeftMask & ~placedMask) == 0 && term.EqualityLeftMask != 0)
            {
                joinEqualities.Add((index, term.EqualityRightColumnOrdinal, Sanitize(term.TursoRightSelectivity), term.EqualityCollation is not null));
            }
            else if (term.EqualityLeftMask == memberBit && (term.EqualityRightMask & ~placedMask) == 0 && term.EqualityRightMask != 0)
            {
                joinEqualities.Add((index, term.EqualityLeftColumnOrdinal, Sanitize(term.TursoLeftSelectivity), term.EqualityCollation is not null));
            }
        }

        // The selectivity of the ready constraints an access path does not consume
        // (constraint_output_multipliers, join.rs:94-152).
        double RemainingSelectivity(IReadOnlyCollection<int> consumed)
        {
            var selectivity = 1.0;
            foreach (var index in ready)
            {
                if (segment.Terms[index].TableMask == memberBit && !consumed.Contains(index))
                    selectivity *= segment.Terms[index].TursoLocalSelectivity;
            }

            foreach (var equality in joinEqualities)
            {
                if (!consumed.Contains(equality.Term))
                    selectivity *= equality.Selectivity;
            }

            return selectivity;
        }

        double WhereWork(IReadOnlyCollection<int> consumed, double rowsPerOuterRow)
        {
            var consumedSteps = 0;
            var remainingSteps = 0;
            foreach (var index in ready)
            {
                if (consumed.Contains(index))
                    consumedSteps += segment.Terms[index].WhereExtraSteps;
                else
                    remainingSteps += segment.Terms[index].WhereExtraSteps;
            }

            return TursoCostModel.EstimateWhereWork(inputCardinality, rowsPerOuterRow, consumedSteps, remainingSteps);
        }

        var crossPenalty = hasJoinTerm ? 0.0 : inputCardinality * rows * memberLocalSelectivity;

        // An outer join emits every outer row at least once: the Poisson estimate of
        // rows_after_join (join.rs:192-248) keeps a row whose matches all miss.
        double OuterRows(double rowsPerOuterRow, double remainingSelectivity)
            => outerJoin == TursoOuterJoin.None
                ? inputCardinality * rowsPerOuterRow * remainingSelectivity
                : inputCardinality * (rowsPerOuterRow * remainingSelectivity + Math.Exp(-(rowsPerOuterRow * remainingSelectivity)));

        // choose_best_btree_candidate: the plain scan, then each declared index seek.
        IReadOnlyCollection<int> noTerms = [];
        var bestShape = JoinStepShape.NestedLoop;
        JoinIndexAccessChoice? bestIndex = null;
        var bestBtreeCost = TursoCostModel.EstimateScanCost(rows, inputCardinality);
        var bestRowsPerOuter = rows;
        IReadOnlyCollection<int> bestConsumed = noTerms;
        var forcedOnly = HasForcedIndexCandidate(target);
        if (forcedOnly)
            bestBtreeCost = double.PositiveInfinity;

        var candidates = target.IndexCandidates ?? [];
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            if (candidate.Automatic)
                continue;

            var termIndices = new List<int>(candidate.Columns.Count);
            var selectivityProduct = 1.0;
            foreach (var column in candidate.Columns)
            {
                var bound = -1;
                foreach (var equality in joinEqualities)
                {
                    if (!termIndices.Contains(equality.Term)
                        && CanBindIndexColumn(segment.Terms[equality.Term], placedMask, memberBit, column))
                    {
                        bound = equality.Term;
                        selectivityProduct *= equality.Selectivity;
                        break;
                    }
                }

                // A constant equality on the member binds the column as well
                // (constraints.rs: a constraint whose other side reads no table).
                if (bound < 0 && column.IndexExpression is null)
                {
                    foreach (var index in ready)
                    {
                        var term = segment.Terms[index];
                        if (term.TableMask == memberBit
                            && !term.CostOnly
                            && term.ConstantEqualityOrdinal == column.ColumnOrdinal
                            && string.Equals(term.ConstantEqualityCollation, column.Collation, StringComparison.OrdinalIgnoreCase)
                            && !termIndices.Contains(index))
                        {
                            bound = index;
                            selectivityProduct *= term.TursoLocalSelectivity;
                            break;
                        }
                    }
                }

                if (bound < 0)
                    break;
                termIndices.Add(bound);
            }

            // The managed seek is keyed by the outer row, so at least one bound column must come
            // from a join equality; constants may only extend that key.
            if (termIndices.Count == 0
                || !termIndices.Any(index => joinEqualities.Any(equality => equality.Term == index)))
            {
                continue;
            }

            var unique = candidate.Unique && termIndices.Count >= candidate.Columns.Count;
            var rowsPerSeek = unique
                ? 1.0
                : termIndices.Count <= candidate.RowsPerPrefix.Count
                    ? Math.Max(candidate.RowsPerPrefix[termIndices.Count - 1], 1.0)
                    : Math.Max(selectivityProduct * rows, 1.0);
            var info = new TursoIndexInfo(
                candidate.Unique,
                candidate.Columns.Count,
                candidate.Covering,
                TursoCostModel.IndexLeafRowsPerPage(candidate.Columns.Count, candidate.TableColumnCount, candidate.HasRowIdAlias));
            var cost = TursoCostModel.EstimateIndexCost(
                rows,
                TursoCostModel.EstimateBtreeDepth(rows, JoinCostParams.RowsPerTablePage),
                info,
                inputCardinality,
                rowsPerSeek);
            if (cost < bestBtreeCost || candidate.Forced && bestShape != JoinStepShape.IndexSeekRight)
            {
                bestBtreeCost = cost;
                bestShape = JoinStepShape.IndexSeekRight;
                bestIndex = new JoinIndexAccessChoice(candidateIndex, [.. termIndices], rowsPerSeek);
                bestRowsPerOuter = rowsPerSeek;
                bestConsumed = termIndices;
            }
        }

        // The rowid candidate: an outer-bound equality on the INTEGER PRIMARY KEY is a unique
        // point lookup (estimate_cost_for_scan_or_seek with the rowid IndexInfo). The operator
        // serves it by hashing the right input on that key once, i.e. a rowid-keyed lookup.
        var rowidSeek = false;
        if (!forcedOnly && target.RowidAliasOrdinal >= 0)
        {
            foreach (var equality in joinEqualities)
            {
                if (equality.Ordinal != target.RowidAliasOrdinal || !equality.Hashable)
                    continue;

                var cost = TursoCostModel.EstimateIndexCost(
                    rows,
                    TursoCostModel.EstimateBtreeDepth(rows, JoinCostParams.RowsPerTablePage),
                    new TursoIndexInfo(Unique: true, ColumnCount: 1, Covering: true, JoinCostParams.RowsPerTablePage),
                    inputCardinality,
                    1.0);
                if (cost < bestBtreeCost)
                {
                    bestBtreeCost = cost;
                    bestShape = JoinStepShape.HashBuildRight;
                    bestIndex = null;
                    bestRowsPerOuter = 1.0;
                    bestConsumed = [equality.Term];
                    rowidSeek = true;
                }

                break;
            }
        }

        if (double.IsPositiveInfinity(bestBtreeCost))
        {
            return new StepEvaluation(JoinStepShape.NestedLoop, double.PositiveInfinity, inputCardinality * rows, null);
        }

        var bestCost = bestBtreeCost + WhereWork(bestConsumed, bestRowsPerOuter) + crossPenalty;
        var bestOutput = OuterRows(bestRowsPerOuter, RemainingSelectivity(bestConsumed));

        // The temporary index is only considered over a plain full scan and never for a FULL
        // join, which has no nested-loop form (access_method.rs:903-987).
        var hashableKeys = joinEqualities.Where(static equality => equality.Hashable).ToArray();
        if (bestShape == JoinStepShape.NestedLoop && !forcedOnly && hashableKeys.Length != 0
            && outerJoin != TursoOuterJoin.Full)
        {
            var consumed = hashableKeys.Select(static equality => equality.Term).ToArray();
            var rowsPerSeek = Math.Max(
                hashableKeys.Aggregate(1.0, static (product, equality) => product * equality.Selectivity) * rows,
                1.0);
            var cost = TursoCostModel.EstimateScanCost(rows, 1.0)
                + TursoCostModel.EstimateEphemeralIndexBuildCost(rows)
                + inputCardinality * JoinCostParams.CpuCostPerSeek
                + inputCardinality * rowsPerSeek * JoinCostParams.CpuCostPerRow
                + WhereWork(consumed, rowsPerSeek)
                + crossPenalty;
            if (cost < bestCost)
            {
                bestCost = cost;
                bestShape = JoinStepShape.HashBuildRight;
                bestIndex = null;
                bestOutput = OuterRows(rowsPerSeek, RemainingSelectivity(consumed));
            }
        }

        // The left-deep hash join builds the last placed table and probes this one
        // (join.rs:586-1000). With one placed table the build reads it directly; behind a longer
        // prefix Turso materializes the build input from the prefix's rows (the operator hashes
        // that joined prefix), which is only allowed up to MAX_MATERIALIZED_BUILD_ROWS. An index
        // that can seek the probe key keeps the index path (probe_index_can_seek_join_key), and
        // so does a selective declared-index or rowid probe seek.
        var placedCount = BitOperations.PopCount(placedMask);
        var buildMember = placedCount == 1 ? BitOperations.TrailingZeroCount(placedMask) : lastMember;
        var materialize = placedCount > 1;
        var buildKeys = buildMember < 0
            ? []
            : hashableKeys
                .Where(key =>
                {
                    var term = segment.Terms[key.Term];
                    var otherMask = term.EqualityLeftMask == memberBit ? term.EqualityRightMask : term.EqualityLeftMask;
                    return otherMask == 1UL << buildMember;
                })
                .ToArray();
        if (buildMember >= 0
            && buildKeys.Length != 0
            && bestShape != JoinStepShape.IndexSeekRight
            && !rowidSeek
            && !forcedOnly
            && !buildKeys.Any(key => target.IndexLeadingColumnOrdinals.Contains(key.Ordinal))
            && (!materialize || inputCardinality <= MaximumMaterializedBuildRows || outerJoin == TursoOuterJoin.Full))
        {
            var build = segment.Members[buildMember];
            var buildRows = Math.Max(1.0, build.RowCount);
            var buildBit = 1UL << buildMember;
            var buildSelectivity = 1.0;
            foreach (var term in segment.Terms)
            {
                if (term.TableMask == buildBit)
                    buildSelectivity *= term.TursoLocalSelectivity;
            }

            // A plain build column that an index could seek keeps the index path unless the
            // probe side has no constant filter (can_replace_build_index_with_hash).
            var hashCanReplaceBuildIndex = segment.Terms.All(term => term.TableMask != memberBit);
            var buildKeyIndexed = false;
            var buildKeyUnique = false;
            foreach (var key in buildKeys)
            {
                var term = segment.Terms[key.Term];
                var buildOrdinal = term.EqualityLeftMask == buildBit
                    ? term.EqualityLeftColumnOrdinal
                    : term.EqualityRightColumnOrdinal;
                if (buildOrdinal == build.RowidAliasOrdinal && buildOrdinal >= 0)
                {
                    buildKeyIndexed = true;
                    buildKeyUnique = true;
                }
                else if (build.IndexedColumnOrdinals.Contains(buildOrdinal))
                {
                    buildKeyIndexed = true;
                }
            }

            if (hashCanReplaceBuildIndex || !buildKeyIndexed)
            {
                var consumed = buildKeys.Select(static equality => equality.Term).ToArray();
                // should_materialize: the build is the prefix's estimated rows.
                var buildCardinality = materialize
                    ? inputCardinality
                    : buildRows * Math.Clamp(buildSelectivity, 0.0, 1.0);
                var probeSelectivity = buildKeys.Aggregate(1.0, static (product, equality) => product * equality.Selectivity);
                var distinctBuildKeys = materialize ? Math.Min(inputCardinality, buildRows) : buildRows;
                var rowsPerBuildRow = buildKeyUnique
                    ? rows / Math.Max(distinctBuildKeys, 1.0)
                    : rows * probeSelectivity;
                // HashJoinType estimates (access_method.rs:1413-1420): an outer join emits each
                // build row at least once, a FULL join also every unmatched probe row.
                var rowsPerOuterRow = outerJoin switch
                {
                    TursoOuterJoin.Left => Math.Max(rowsPerBuildRow, 1.0),
                    TursoOuterJoin.Full => Math.Max(Math.Max(rowsPerBuildRow, 1.0), rows / Math.Max(buildCardinality, 1.0)),
                    _ => rowsPerBuildRow,
                };
                var cost = TursoCostModel.EstimateHashJoinCost(
                        buildCardinality,
                        rows,
                        buildCardinality * rowsPerOuterRow,
                        keepsUnmatchedBuildRows: outerJoin != TursoOuterJoin.None,
                        probeMultiplier: 1.0)
                    + (materialize ? buildCardinality * JoinCostParams.CpuCostPerRow : 0.0)
                    + WhereWork(consumed, rowsPerOuterRow)
                    + crossPenalty;
                // FULL OUTER requires the hash join for its unmatched-probe scan.
                if (cost < bestCost || outerJoin == TursoOuterJoin.Full)
                {
                    bestCost = cost;
                    bestShape = JoinStepShape.HashBuildLeft;
                    bestIndex = null;
                    bestOutput = outerJoin == TursoOuterJoin.None
                        ? inputCardinality * rowsPerBuildRow * RemainingSelectivity(consumed)
                        : inputCardinality * rowsPerOuterRow;
                }
            }
        }
        return new StepEvaluation(bestShape, bestCost, bestOutput, bestIndex);

        static double Sanitize(double selectivity)
            => double.IsNaN(selectivity) ? JoinCostParams.SelectivityEqualityUnindexed : selectivity;
    }

    internal readonly record struct StepEvaluation(
        JoinStepShape Shape,
        double StepCost,
        double OutputCardinality,
        JoinIndexAccessChoice? IndexAccess);

    private sealed record JoinOrderDpEntry(
        double Cost,
        double Cardinality,
        int[] Order,
        JoinStepShape[] Shapes,
        JoinIndexAccessChoice?[] IndexAccesses);
}
