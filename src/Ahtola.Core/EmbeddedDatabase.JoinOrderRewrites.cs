using Ahtola.Core.Compilation.JoinOrdering;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// Cost-based join-order selection for maximal plain-INNER segments of a FROM tree.
/// </summary>
/// <remarks>
/// <para>
/// This is a pure AST→AST rewrite that runs immediately before <c>TryBuildCompiledJoinSource</c>
/// and hands that builder a semantically equivalent, differently-ordered
/// <see cref="JoinTableSource"/> tree. It follows the same fail-closed contract as
/// <c>EmbeddedDatabase.SubqueryRewrites.cs</c>: any shape, predicate, or statistic the rewriter
/// does not explicitly model returns the original tree untouched, so the worst case is exactly
/// today's behavior.
/// </para>
/// <para>
/// <b>Barriers.</b> A join node is a hard partition wall — its whole subtree becomes one opaque
/// member that never interleaves with its siblings — when it is <c>LEFT</c>/<c>RIGHT</c>/
/// <c>FULL</c>, <c>NATURAL</c>, or carries <c>USING</c>. Semi/anti joins decline the rewrite
/// outright. This is deliberately stricter than Turso's <c>required_lhs_by_table</c> /
/// <c>left_join_illegal_map</c> legality bitmask (join.rs:1258-1324): the fine-grained scheme
/// only pays off together with per-table access-method search, which needs index-seek join
/// leaves this engine does not have. Freezing barrier subtrees is a provably safe subset.
/// </para>
/// <para>
/// <b>Projection order.</b> Reordering permutes the physical value slots of a joined row. The
/// rewrite therefore also returns a slot map, and the caller re-points the statement's output
/// columns through it. The output column <em>list order</em> always stays in FROM order, so
/// <c>SELECT *</c> and qualified/unqualified star projections are unaffected by the physical
/// order the executor uses.
/// </para>
/// <para>
/// <b>Statistics.</b> Every member must have a real <c>sqlite_stat1</c> row count. Without
/// <c>ANALYZE</c> the rewrite declines and the FROM order is preserved verbatim, matching the
/// existing two-table nested-loop route's precedent.
/// </para>
/// </remarks>
public sealed partial class EmbeddedDatabase
{
    private long _joinOrderSegmentsConsidered;
    private long _joinOrderSegmentsReordered;
    private long _joinOrderDynamicProgrammingPlans;
    private long _joinOrderGreedyPlans;
    private long _joinOrderDeclines;
    private long _joinOrderPushedWhereTerms;

    /// <summary>
    /// Counters describing what the join-order stage did on this database instance. Test-only
    /// evidence that a segment was actually enumerated (and, just as importantly, that an
    /// ineligible shape declined).
    /// </summary>
    internal JoinOrderDiagnostics JoinOrderDiagnostics => new(
        Interlocked.Read(ref _joinOrderSegmentsConsidered),
        Interlocked.Read(ref _joinOrderSegmentsReordered),
        Interlocked.Read(ref _joinOrderDynamicProgrammingPlans),
        Interlocked.Read(ref _joinOrderGreedyPlans),
        Interlocked.Read(ref _joinOrderDeclines),
        Interlocked.Read(ref _joinOrderPushedWhereTerms));

    internal void ResetJoinOrderDiagnostics()
    {
        Interlocked.Exchange(ref _joinOrderSegmentsConsidered, 0);
        Interlocked.Exchange(ref _joinOrderSegmentsReordered, 0);
        Interlocked.Exchange(ref _joinOrderDynamicProgrammingPlans, 0);
        Interlocked.Exchange(ref _joinOrderGreedyPlans, 0);
        Interlocked.Exchange(ref _joinOrderDeclines, 0);
        Interlocked.Exchange(ref _joinOrderPushedWhereTerms, 0);
    }

    /// <summary>
    /// Produces a cost-ordered replacement for <paramref name="source"/>, or returns false when
    /// nothing may (or need) change.
    /// </summary>
    /// <param name="source">The FROM tree to reorder.</param>
    /// <param name="where">
    /// The statement's WHERE clause. Single-member comparison conjuncts of the root segment are
    /// additionally attached as ON conditions so the cost model's cardinalities match what the
    /// executor actually filters. Safe only because every segment node is an INNER join, where
    /// ON and WHERE are interchangeable, and only at the root, where no enclosing outer join can
    /// null-extend the filtered columns.
    /// </param>
    /// <param name="context">Query context supplying schema and <c>sqlite_stat1</c> access.</param>
    /// <param name="result">The rewritten tree, slot map and per-node build-side decisions.</param>
    private bool TryRewriteJoinOrderForCostBasedPlanning(
        TableSource source,
        Expression? where,
        QueryContext context,
        out JoinOrderRewriteResult result)
    {
        result = null!;
        if (source is not JoinTableSource || context.SchemaValidation || context.IndexExpression)
            return false;

        // Semi/anti joins have no bytecode lowering at all; TryBuildCompiledJoinSource declines
        // the whole route when it meets one, so there is nothing here worth reordering.
        if (JoinOrderContainsUnsupportedKind(source))
            return false;

        try
        {
            var state = new JoinOrderRewriteState(context, where);
            if (!TryRewriteJoinOrderNode(source, state, isRoot: true, out var rewritten)
                || !rewritten.Changed)
            {
                return false;
            }

            // Defensive: the slot map must describe exactly the joined row the builder will
            // produce. A mismatch means an unmodeled width somewhere, so decline.
            if (rewritten.SlotMap.Length != GetSourceColumns(source, context).Length)
            {
                Interlocked.Increment(ref _joinOrderDeclines);
                return false;
            }

            result = new JoinOrderRewriteResult(
                rewritten.Source,
                rewritten.SlotMap,
                state.HashBuildRight);
            return true;
        }
        catch (EmbeddedSqlException)
        {
            // Schema resolution failed for a shape the rewriter should not have reached. The
            // untouched original path re-runs and reports the diagnostic itself.
            Interlocked.Increment(ref _joinOrderDeclines);
            return false;
        }
    }

    /// <summary>
    /// Re-points a statement's output columns from the original FROM-order slot layout onto the
    /// physical slot layout the reordered plan produces. The list order — and therefore
    /// <c>SELECT *</c> column order — is preserved exactly.
    /// </summary>
    private static IReadOnlyList<OutputColumn> RemapJoinOrderOutputColumns(
        IReadOnlyList<OutputColumn> columns,
        int[] slotMap)
    {
        var remapped = new OutputColumn[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            remapped[index] = column with
            {
                Index = MapSlot(column.Index, slotMap),
                CoalesceIndex = column.CoalesceIndex is { } coalesce ? MapSlot(coalesce, slotMap) : null,
                AdditionalCoalesceIndices = column.AdditionalCoalesceIndices is { } additional
                    ? additional.Select(slot => MapSlot(slot, slotMap)).ToArray()
                    : null,
            };
        }

        return remapped;

        static int MapSlot(int slot, int[] map) => slot >= 0 && slot < map.Length ? map[slot] : slot;
    }

    private static bool JoinOrderContainsUnsupportedKind(TableSource? source)
        => source is JoinTableSource join
            && (join.Kind.ProducesLeftShapeOnly()
                || JoinOrderContainsUnsupportedKind(join.Left)
                || JoinOrderContainsUnsupportedKind(join.Right));

    /// <summary>
    /// A join node whose members may be freely permuted: plain INNER, no coalesced columns.
    /// NATURAL and USING are excluded because <c>BuildJoinPairs</c>/<c>JoinPairMatches</c> resolve
    /// their columns from the specific left/right pair, so changing which tables are adjacent
    /// changes what the join means.
    /// </summary>
    private static bool IsReorderableInnerJoin(JoinTableSource join)
        => join.Kind == JoinKind.Inner && join.UsingColumns is null && !join.Natural;

    private bool TryRewriteJoinOrderNode(
        TableSource node,
        JoinOrderRewriteState state,
        bool isRoot,
        out JoinOrderRewrittenSource result)
    {
        if (node is JoinTableSource join)
        {
            if (IsReorderableInnerJoin(join)
                && TryRewriteJoinOrderSegment(join, state, isRoot, out result))
            {
                return true;
            }

            // Barrier node, or a segment the cost model could not enumerate: freeze this node's
            // shape and optimize each side independently so a reorderable region nested inside a
            // barrier is still improved.
            if (!TryRewriteJoinOrderNode(join.Left, state, isRoot: false, out var left)
                || !TryRewriteJoinOrderNode(join.Right, state, isRoot: false, out var right))
            {
                result = default;
                return false;
            }

            var map = new int[left.SlotMap.Length + right.SlotMap.Length];
            Array.Copy(left.SlotMap, map, left.SlotMap.Length);
            for (var index = 0; index < right.SlotMap.Length; index++)
                map[left.SlotMap.Length + index] = left.SlotMap.Length + right.SlotMap[index];

            var changed = left.Changed || right.Changed;
            result = new JoinOrderRewrittenSource(
                changed ? join with { Left = left.Source, Right = right.Source } : join,
                map,
                changed);
            return true;
        }

        result = new JoinOrderRewrittenSource(
            node,
            JoinOrderIdentityMap(GetSourceColumns(node, state.Context).Length),
            Changed: false);
        return true;
    }

    private bool TryRewriteJoinOrderSegment(
        JoinTableSource join,
        JoinOrderRewriteState state,
        bool isRoot,
        out JoinOrderRewrittenSource result)
    {
        result = default;
        var sources = new List<TableSource>();
        var conjuncts = new List<Expression>();
        FlattenJoinOrderSegment(join, sources, conjuncts);
        if (sources.Count < 2 || sources.Count > JoinOrderEnumerator.MaximumMembers)
            return false;

        var infos = new JoinOrderMemberInfo[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            if (!TryDescribeJoinOrderMember(sources[index], state.Context, out var info))
                return false;

            infos[index] = info;
        }

        var terms = new List<JoinPredicateTerm>();
        var placements = new List<JoinOrderTermPlacement>();
        foreach (var conjunct in conjuncts)
        {
            if (!TryCreateJoinOrderTerm(conjunct, infos, state.Context, out var term))
                return false;

            terms.Add(term);
            placements.Add(new JoinOrderTermPlacement(conjunct, term.TableMask));
        }

        var pushedWhereTerms = 0;
        if (isRoot && state.Where is not null)
        {
            foreach (var conjunct in SplitJoinOrderConjunction(state.Where))
            {
                if (!TryCreatePushableJoinOrderWhereTerm(conjunct, infos, state.Context, out var term))
                    continue;

                terms.Add(term);
                placements.Add(new JoinOrderTermPlacement(conjunct, term.TableMask));
                pushedWhereTerms++;
            }
        }

        var members = new JoinSegmentMember[infos.Length];
        for (var index = 0; index < infos.Length; index++)
            members[index] = new JoinSegmentMember(index, infos[index].RowCount, infos[index].Width);

        Interlocked.Increment(ref _joinOrderSegmentsConsidered);
        var plan = JoinOrderEnumerator.Compute(new JoinSegment(members, terms));
        if (plan is null)
        {
            Interlocked.Increment(ref _joinOrderDeclines);
            return false;
        }

        // Recurse into each member only once the segment is committed, so a rejected segment
        // never double-counts a nested rewrite.
        var rewrittenMembers = new JoinOrderRewrittenSource[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            if (!TryRewriteJoinOrderNode(sources[index], state, isRoot: false, out rewrittenMembers[index]))
                return false;
        }

        var synthesized = SynthesizeJoinOrder(rewrittenMembers, placements, plan, state);
        var slotMap = BuildJoinOrderSlotMap(rewrittenMembers, infos, plan.MemberOrder);

        Interlocked.Increment(ref _joinOrderSegmentsReordered);
        if (plan.UsedDynamicProgramming)
            Interlocked.Increment(ref _joinOrderDynamicProgrammingPlans);
        else
            Interlocked.Increment(ref _joinOrderGreedyPlans);
        if (pushedWhereTerms > 0)
            Interlocked.Add(ref _joinOrderPushedWhereTerms, pushedWhereTerms);

        result = new JoinOrderRewrittenSource(synthesized, slotMap, Changed: true);
        return true;
    }

    /// <summary>
    /// Collects the maximal plain-INNER run rooted at <paramref name="join"/> in left-to-right
    /// FROM order together with every ON conjunct it contributes. A non-qualifying child becomes
    /// exactly one member and its own condition stays inside it, so a barrier is never split and
    /// never contributes candidate predicates to the surrounding segment.
    /// </summary>
    private static void FlattenJoinOrderSegment(
        JoinTableSource join,
        List<TableSource> sources,
        List<Expression> conjuncts)
    {
        AddSide(join.Left);
        AddSide(join.Right);
        if (join.Condition is not null)
            conjuncts.AddRange(SplitJoinOrderConjunction(join.Condition));

        void AddSide(TableSource side)
        {
            if (side is JoinTableSource nested && IsReorderableInnerJoin(nested))
                FlattenJoinOrderSegment(nested, sources, conjuncts);
            else
                sources.Add(side);
        }
    }

    private static IEnumerable<Expression> SplitJoinOrderConjunction(Expression expression)
    {
        var pending = new Stack<Expression>();
        var ordered = new List<Expression>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is BinaryExpression { Operator: BinaryOperator.And } and)
            {
                pending.Push(and.Right);
                pending.Push(and.Left);
                continue;
            }

            ordered.Add(current);
        }

        return ordered;
    }

    /// <summary>
    /// Builds a left-deep tree in the chosen order, attaching each pooled conjunct at the first
    /// step where every member it references is present. That is exactly the one-condition-per-node
    /// shape <c>TryBuildCompiledJoinSource</c> already expects, so no builder change is needed.
    /// </summary>
    private static TableSource SynthesizeJoinOrder(
        JoinOrderRewrittenSource[] members,
        List<JoinOrderTermPlacement> placements,
        JoinOrderPlan plan,
        JoinOrderRewriteState state)
    {
        var attached = new bool[placements.Count];
        var placed = 1UL << plan.MemberOrder[0];
        var node = members[plan.MemberOrder[0]].Source;

        for (var step = 1; step < plan.MemberOrder.Length; step++)
        {
            var member = plan.MemberOrder[step];
            var candidate = placed | (1UL << member);
            Expression? condition = null;
            for (var index = 0; index < placements.Count; index++)
            {
                if (attached[index] || (placements[index].Mask & ~candidate) != 0)
                    continue;

                attached[index] = true;
                condition = condition is null
                    ? placements[index].Expression
                    : new BinaryExpression(condition, BinaryOperator.And, placements[index].Expression);
            }

            var joined = new JoinTableSource(node, members[member].Source, condition, JoinKind.Inner);
            state.HashBuildRight[joined] = plan.StepShapes[step] != JoinStepShape.HashBuildLeft;
            node = joined;
            placed = candidate;
        }

        return node;
    }

    /// <summary>
    /// Maps every original FROM-order value slot onto the slot the reordered plan puts it in.
    /// Members keep their internal layout, so the map is a block permutation composed with each
    /// member's own (possibly already permuted) map.
    /// </summary>
    private static int[] BuildJoinOrderSlotMap(
        JoinOrderRewrittenSource[] members,
        JoinOrderMemberInfo[] infos,
        int[] memberOrder)
    {
        var total = 0;
        var originalOffsets = new int[members.Length];
        for (var index = 0; index < members.Length; index++)
        {
            originalOffsets[index] = total;
            total += infos[index].Width;
        }

        var physicalOffsets = new int[members.Length];
        var running = 0;
        foreach (var member in memberOrder)
        {
            physicalOffsets[member] = running;
            running += infos[member].Width;
        }

        var map = new int[total];
        for (var member = 0; member < members.Length; member++)
        {
            var inner = members[member].SlotMap;
            for (var slot = 0; slot < infos[member].Width; slot++)
            {
                var innerSlot = slot < inner.Length ? inner[slot] : slot;
                map[originalOffsets[member] + slot] = physicalOffsets[member] + innerSlot;
            }
        }

        return map;
    }

    private static int[] JoinOrderIdentityMap(int width)
    {
        var map = new int[width];
        for (var index = 0; index < width; index++)
            map[index] = index;
        return map;
    }

    /// <summary>
    /// Describes one segment member: the qualifiers and column names it exposes, its value width,
    /// and its <c>sqlite_stat1</c> cardinality. Returns false — declining the whole segment — when
    /// any of that is unavailable, which is what keeps un-<c>ANALYZE</c>d databases on the
    /// unmodified FROM order.
    /// </summary>
    private bool TryDescribeJoinOrderMember(
        TableSource source,
        QueryContext context,
        out JoinOrderMemberInfo info)
    {
        info = null!;
        if (!TryEstimateJoinOrderRows(source, context, out var rows))
            return false;

        var width = GetSourceColumns(source, context).Length;
        if (width <= 0)
            return false;

        var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in GetOutputColumns(source, context))
        {
            if (column.Qualifier is { } qualifier)
                qualifiers.Add(qualifier);
            if (!columnNames.Add(column.Name))
                ambiguousColumnNames.Add(column.Name);
        }

        if (qualifiers.Count == 0)
            return false;

        info = new JoinOrderMemberInfo(
            qualifiers,
            columnNames,
            ambiguousColumnNames,
            rows,
            width,
            TryResolveJoinOrderBaseTable(source, context));
        return true;
    }

    /// <summary>
    /// The base table behind a leaf member, or null when the member is a barrier subtree or any
    /// source whose statistics the rewriter cannot read. Mirrors the guard set
    /// <c>TryPlanManagedIndexScan</c> uses so a CTE/view/virtual table sharing a base table's
    /// name can never borrow that table's statistics.
    /// </summary>
    private static EmbeddedTable? TryResolveJoinOrderBaseTable(TableSource source, QueryContext context)
    {
        if (source is not NamedTableSource named
            || IsSchemaTable(named.Name)
            || context.CommonTableExpressions.ContainsKey(named.Name)
            || context.Views?.ContainsKey(named.Name) == true
            || TryGetVirtualTable(context, named, out _)
            || TryBindBareTableValuedFunction(named, context, out _)
            || !context.Tables.TryGetValue(named.Name, out var table))
        {
            return null;
        }

        return table;
    }

    /// <summary>
    /// Cardinality for a member. Base tables read <c>sqlite_stat1</c>; a frozen barrier subtree
    /// uses <c>max(left, right)</c>, the same naive equijoin residual
    /// <c>EstimateJoinNodeRows</c> applies to the compiled plan, so the enumerator and the
    /// builder's own build-side check never disagree.
    /// </summary>
    private static bool TryEstimateJoinOrderRows(TableSource source, QueryContext context, out double rows)
    {
        rows = 0.0;
        switch (source)
        {
            case JoinTableSource join:
                if (!TryEstimateJoinOrderRows(join.Left, context, out var left)
                    || !TryEstimateJoinOrderRows(join.Right, context, out var right))
                {
                    return false;
                }

                rows = Math.Max(1.0, Math.Max(left, right));
                return true;
            default:
                if (TryResolveJoinOrderBaseTable(source, context) is not { } table
                    || !TryGetSqliteStat1TableRowCount(context, table.Name, out var count))
                {
                    return false;
                }

                rows = Math.Max(1.0, count);
                return true;
        }
    }

    private bool TryCreateJoinOrderTerm(
        Expression conjunct,
        JoinOrderMemberInfo[] infos,
        QueryContext context,
        out JoinPredicateTerm term)
    {
        term = null!;
        if (ContainsAggregate(conjunct)
            || ContainsWindowFunction(conjunct)
            || !IsScanPredicate(conjunct)
            || !TryResolveJoinOrderMask(conjunct, infos, out var mask))
        {
            return false;
        }

        var isEquality = false;
        ulong leftMask = 0;
        ulong rightMask = 0;
        var leftMatchRows = 0.0;
        var rightMatchRows = 0.0;
        if (conjunct is BinaryExpression { Operator: BinaryOperator.Equal } binary
            && UnwrapCollation(binary.Left) is ColumnExpression { BooleanKeyword: null } leftColumn
            && UnwrapCollation(binary.Right) is ColumnExpression { BooleanKeyword: null } rightColumn
            && TryResolveJoinOrderMember(leftColumn, infos, out var leftMember)
            && TryResolveJoinOrderMember(rightColumn, infos, out var rightMember)
            && leftMember != rightMember)
        {
            isEquality = true;
            leftMask = 1UL << leftMember;
            rightMask = 1UL << rightMember;
            leftMatchRows = EstimateJoinOrderMatchRows(infos[leftMember], leftColumn, context);
            rightMatchRows = EstimateJoinOrderMatchRows(infos[rightMember], rightColumn, context);
        }

        term = new JoinPredicateTerm(
            mask,
            isEquality,
            leftMask,
            rightMask,
            leftMatchRows,
            rightMatchRows,
            EstimateJoinOrderSelectivity(conjunct));
        return true;
    }

    /// <summary>
    /// Accepts a WHERE conjunct as an extra ON condition of the root segment. Restricted to a
    /// comparison between one member's column and a literal or parameter, which keeps the pushed
    /// term free of user functions (so the duplicate evaluation the surviving WHERE performs is
    /// unobservable) and inside the shape <c>IsSafeCompiledJoinPredicate</c> already admits.
    /// </summary>
    private bool TryCreatePushableJoinOrderWhereTerm(
        Expression conjunct,
        JoinOrderMemberInfo[] infos,
        QueryContext context,
        out JoinPredicateTerm term)
    {
        term = null!;
        if (conjunct is not BinaryExpression comparison
            || !IsComparisonOperator(comparison.Operator)
            || !IsPushableJoinOrderWhereOperand(comparison.Left, infos, context)
            || !IsPushableJoinOrderWhereOperand(comparison.Right, infos, context)
            || !TryResolveJoinOrderMask(conjunct, infos, out var mask)
            || System.Numerics.BitOperations.PopCount(mask) > 1)
        {
            return false;
        }

        term = new JoinPredicateTerm(
            mask,
            IsEquality: false,
            EqualityLeftMask: 0,
            EqualityRightMask: 0,
            EqualityLeftMatchRows: 0.0,
            EqualityRightMatchRows: 0.0,
            EstimateJoinOrderSelectivity(conjunct));
        return true;
    }

    private bool IsPushableJoinOrderWhereOperand(
        Expression expression,
        JoinOrderMemberInfo[] infos,
        QueryContext context)
    {
        while (expression is CollationExpression collation)
        {
            if (!IsStreamingSafeDistinctCollation(collation.Name))
                return false;
            expression = collation.Expression;
        }

        if (expression is LiteralExpression or ParameterExpression)
            return true;
        if (expression is not ColumnExpression column
            || !TryResolveJoinOrderMember(column, infos, out var member))
        {
            return false;
        }

        var declared = TryResolveJoinOrderColumnDefinition(infos[member], column, context)?.Collation;
        return IsStreamingSafeDistinctCollation(declared);
    }

    /// <summary>
    /// Expected rows of <paramref name="info"/> matching one key value of
    /// <paramref name="column"/>. A rowid alias or unique index yields one row; otherwise the
    /// per-index leading average from <c>sqlite_stat1</c> is used, falling back to Turso's
    /// <c>sel_eq_unindexed</c>. This describes the data distribution only — no seek discount is
    /// implied, because the executor still hashes or scans the whole input.
    /// </summary>
    private static double EstimateJoinOrderMatchRows(
        JoinOrderMemberInfo info,
        ColumnExpression column,
        QueryContext context)
    {
        var fallback = Math.Max(1.0, info.RowCount * JoinCostParams.SelectivityEqualityUnindexed);
        if (info.Table is not { } table)
            return fallback;

        var name = column.UnqualifiedName ?? column.Name;
        if (table.HasRowid && EmbeddedTable.IsRowidAliasName(name))
            return 1.0;
        if (table.RowidAliasColumnIndex >= 0
            && string.Equals(table.Columns[table.RowidAliasColumnIndex], name, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var best = double.NaN;
        foreach (var index in table.Indexes)
        {
            if (index.Columns.Count == 0
                || index.IsPartial
                || index.Columns[0].IsExpression
                || !string.Equals(index.Columns[0].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index.Unique && index.Columns.Count == 1)
                return 1.0;
            if (TryGetSqliteStat1PrefixAverage(context, table.Name, index.Name, prefixLength: 1, out var average)
                && (double.IsNaN(best) || average < best))
            {
                best = average;
            }
        }

        return double.IsNaN(best) ? fallback : Math.Max(1.0, best);
    }

    private static EmbeddedColumn? TryResolveJoinOrderColumnDefinition(
        JoinOrderMemberInfo info,
        ColumnExpression column,
        QueryContext context)
    {
        _ = context;
        if (info.Table is not { } table)
            return null;

        var name = column.UnqualifiedName ?? column.Name;
        foreach (var definition in table.ColumnDefinitions)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
                return definition;
        }

        return null;
    }

    private static double EstimateJoinOrderSelectivity(Expression conjunct)
        => conjunct switch
        {
            BinaryExpression { Operator: BinaryOperator.Equal or BinaryOperator.Is }
                => JoinCostParams.SelectivityEqualityUnindexed,
            BinaryExpression
            {
                Operator: BinaryOperator.LessThan
                    or BinaryOperator.LessThanOrEqual
                    or BinaryOperator.GreaterThan
                    or BinaryOperator.GreaterThanOrEqual,
            } => JoinCostParams.SelectivityRange,
            _ => JoinCostParams.SelectivityOther,
        };

    private static bool TryResolveJoinOrderMask(
        Expression expression,
        JoinOrderMemberInfo[] infos,
        out ulong mask)
    {
        mask = 0;
        var columns = new List<ColumnExpression>();
        if (!TryCollectJoinOrderColumns(expression, columns))
            return false;

        foreach (var column in columns)
        {
            if (!TryResolveJoinOrderMember(column, infos, out var member))
                return false;

            mask |= 1UL << member;
        }

        return true;
    }

    /// <summary>
    /// Resolves one column reference to the member that owns it. A qualified reference matches by
    /// qualifier; an unqualified one must match exactly one member and must not be ambiguous
    /// inside that member, otherwise the segment declines. This is what keeps a reorder from
    /// silently changing which table an unqualified duplicate name resolves to.
    /// </summary>
    private static bool TryResolveJoinOrderMember(
        ColumnExpression column,
        JoinOrderMemberInfo[] infos,
        out int member)
    {
        member = -1;
        if (column.Schema is { } schema
            && !schema.Equals("main", StringComparison.OrdinalIgnoreCase)
            && !schema.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (column.Qualifier is { } qualifier)
        {
            for (var index = 0; index < infos.Length; index++)
            {
                if (!infos[index].Qualifiers.Contains(qualifier))
                    continue;
                if (member >= 0)
                    return false;

                member = index;
            }

            return member >= 0;
        }

        for (var index = 0; index < infos.Length; index++)
        {
            if (!infos[index].ColumnNames.Contains(column.Name))
                continue;
            if (member >= 0 || infos[index].AmbiguousColumnNames.Contains(column.Name))
                return false;

            member = index;
        }

        return member >= 0;
    }

    /// <summary>
    /// Collects every column reference in an expression, returning false for any node shape the
    /// rewriter does not model. The allowed set mirrors <c>AllCompiledJoinColumnsResolve</c>, so a
    /// predicate this accepts is one the compiled join builder can also evaluate.
    /// </summary>
    private static bool TryCollectJoinOrderColumns(Expression expression, List<ColumnExpression> columns)
    {
        switch (expression)
        {
            case LiteralExpression or ParameterExpression or CurrentTimeExpression:
                return true;
            case ColumnExpression column:
                columns.Add(column);
                return true;
            case CollationExpression collation:
                return TryCollectJoinOrderColumns(collation.Expression, columns);
            case CastExpression cast:
                return TryCollectJoinOrderColumns(cast.Expression, columns);
            case UnaryExpression unary:
                return TryCollectJoinOrderColumns(unary.Operand, columns);
            case BinaryExpression binary:
                return TryCollectJoinOrderColumns(binary.Left, columns)
                    && TryCollectJoinOrderColumns(binary.Right, columns);
            case BetweenExpression between:
                return TryCollectJoinOrderColumns(between.Value, columns)
                    && TryCollectJoinOrderColumns(between.Lower, columns)
                    && TryCollectJoinOrderColumns(between.Upper, columns);
            case LikeExpression like:
                return TryCollectJoinOrderColumns(like.Value, columns)
                    && TryCollectJoinOrderColumns(like.Pattern, columns)
                    && (like.Escape is null || TryCollectJoinOrderColumns(like.Escape, columns));
            case GlobExpression glob:
                return TryCollectJoinOrderColumns(glob.Value, columns)
                    && TryCollectJoinOrderColumns(glob.Pattern, columns);
            case InExpression @in:
                return TryCollectJoinOrderColumns(@in.Value, columns)
                    && @in.Values.All(value => TryCollectJoinOrderColumns(value, columns));
            case CaseExpression @case:
                return (@case.Operand is null || TryCollectJoinOrderColumns(@case.Operand, columns))
                    && @case.Clauses.All(clause =>
                        TryCollectJoinOrderColumns(clause.When, columns)
                        && TryCollectJoinOrderColumns(clause.Then, columns))
                    && (@case.Else is null || TryCollectJoinOrderColumns(@case.Else, columns));
            case FunctionExpression function:
                return function.Filter is null
                    && function.Window is null
                    && function.Arguments.All(argument => TryCollectJoinOrderColumns(argument, columns));
            default:
                return false;
        }
    }

    /// <summary>
    /// Builds the compiled join source for a general N-way select, first attempting the
    /// cost-based order. Any failure — including a redistributed ON conjunct the compiled-join
    /// validator rejects at its new node — falls back to building the untouched FROM tree, so
    /// the reorder can only ever improve a plan, never block one.
    /// </summary>
    private bool TryBuildCostOrderedJoinSource(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledJoinSource compiled)
    {
        var source = select.Source!;
        if (TryRewriteJoinOrderForCostBasedPlanning(source, select.Where, context, out var rewrite)
            && TryBuildCompiledJoinSource(
                rewrite.Source,
                parameters,
                context,
                outerRow,
                out var reordered,
                rewrite.HashBuildRight)
            && reordered.Columns.Length == rewrite.SlotMap.Length)
        {
            // The physical row layout is permuted, but the projection metadata keeps FROM order
            // so SELECT * and qualified stars are unchanged by the reorder. The reordered tree
            // travels with it: the remapped indexes address that tree, not the FROM one.
            compiled = reordered.WithProjectionColumns(
                RemapJoinOrderOutputColumns(GetOutputColumns(source, context), rewrite.SlotMap),
                RemapJoinOrderOutputColumns(GetRawOutputColumns(source, context), rewrite.SlotMap),
                rewrite.Source);
            return true;
        }

        return TryBuildCompiledJoinSource(source, parameters, context, outerRow, out compiled);
    }

    /// <summary>Per-statement state threaded through the join-order rewrite.</summary>
    private sealed class JoinOrderRewriteState(QueryContext context, Expression? where)
    {
        public QueryContext Context { get; } = context;

        public Expression? Where { get; } = where;

        /// <summary>
        /// Build-side decision for each synthesized node, keyed by reference so it can never be
        /// confused with an equal-by-value node elsewhere in the tree.
        /// </summary>
        public Dictionary<JoinTableSource, bool> HashBuildRight { get; } =
            new(JoinOrderNodeIdentityComparer.Instance);
    }

    /// <summary>
    /// Reference identity for synthesized join nodes. <see cref="JoinTableSource"/> is a record,
    /// so structural equality would let two distinct steps that happen to look alike share one
    /// build-side decision.
    /// </summary>
    private sealed class JoinOrderNodeIdentityComparer : IEqualityComparer<JoinTableSource>
    {
        public static JoinOrderNodeIdentityComparer Instance { get; } = new();

        public bool Equals(JoinTableSource? x, JoinTableSource? y) => ReferenceEquals(x, y);

        public int GetHashCode(JoinTableSource obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class JoinOrderMemberInfo(
        HashSet<string> qualifiers,
        HashSet<string> columnNames,
        HashSet<string> ambiguousColumnNames,
        double rowCount,
        int width,
        EmbeddedTable? table)
    {
        public HashSet<string> Qualifiers { get; } = qualifiers;

        public HashSet<string> ColumnNames { get; } = columnNames;

        public HashSet<string> AmbiguousColumnNames { get; } = ambiguousColumnNames;

        public double RowCount { get; } = rowCount;

        public int Width { get; } = width;

        public EmbeddedTable? Table { get; } = table;
    }

    private readonly record struct JoinOrderTermPlacement(Expression Expression, ulong Mask);

    private readonly record struct JoinOrderRewrittenSource(TableSource Source, int[] SlotMap, bool Changed);

    /// <summary>
    /// The rewritten FROM tree plus everything the caller needs to consume it: the slot map that
    /// restores FROM-order projection, and the per-node build-side decisions the cost model made.
    /// </summary>
    private sealed record JoinOrderRewriteResult(
        TableSource Source,
        int[] SlotMap,
        IReadOnlyDictionary<JoinTableSource, bool> HashBuildRight);
}

/// <summary>Join-order stage counters. Test-only evidence, not a public API.</summary>
/// <param name="SegmentsConsidered">Segments handed to the enumerator.</param>
/// <param name="SegmentsReordered">
/// Segments the stage re-synthesized. Includes a segment whose FROM order was already optimal,
/// because the rebuild is still what installs the chosen per-step build sides.
/// </param>
/// <param name="DynamicProgrammingPlans">Plans produced by the subset dynamic program.</param>
/// <param name="GreedyPlans">Plans produced by the greedy fallback above the DP cap.</param>
/// <param name="Declines">Segments the enumerator refused (outside its member range).</param>
/// <param name="PushedWhereTerms">Single-member WHERE comparisons attached as ON conditions.</param>
internal sealed record JoinOrderDiagnostics(
    long SegmentsConsidered,
    long SegmentsReordered,
    long DynamicProgrammingPlans,
    long GreedyPlans,
    long Declines,
    long PushedWhereTerms);
