using Ahtola.Core.Compilation.JoinOrdering;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;

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
/// only pays off with finer null-extension provenance than the managed row shape currently
/// carries. Freezing barrier subtrees remains the provably safe subset even though eligible
/// inner leaves now support persisted and automatic index seeks.
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
    private readonly VdbeJoinIndexSeekMetrics _joinIndexSeekMetrics = new();

    /// <summary>
    /// First negative "expression group" ordinal <see cref="FindJoinIndexExpressionOrdinal"/>
    /// hands out for a persisted index's expression column. Real column ordinals are always
    /// <c>&gt;= 0</c>, so any value at or below this base unambiguously identifies an expression
    /// group rather than a table column.
    /// </summary>
    private const int JoinIndexExpressionOrdinalBase = -1000;

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

    internal VdbeJoinIndexSeekMetrics JoinIndexSeekMetrics => _joinIndexSeekMetrics;

    internal void ResetJoinOrderDiagnostics()
    {
        Interlocked.Exchange(ref _joinOrderSegmentsConsidered, 0);
        Interlocked.Exchange(ref _joinOrderSegmentsReordered, 0);
        Interlocked.Exchange(ref _joinOrderDynamicProgrammingPlans, 0);
        Interlocked.Exchange(ref _joinOrderGreedyPlans, 0);
        Interlocked.Exchange(ref _joinOrderDeclines, 0);
        Interlocked.Exchange(ref _joinOrderPushedWhereTerms, 0);
        _joinIndexSeekMetrics.Reset();
        _plannerAccessPathMetrics.Reset();
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
                state.HashBuildRight,
                state.IndexSeeks);
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

        // Partial-index eligibility must be proven from conjuncts already known safe for this
        // segment: this segment's own ON conjuncts, plus — only for the root segment, mirroring
        // the existing WHERE-pushing gate below — the top-level WHERE's conjuncts. A conjunct
        // that reaches outside the table under proof is filtered by
        // IndexExpressionSemantics.PredicateImplies itself (UsesOnlySourceColumns), so passing
        // the whole list here is safe even though it is not yet split per member.
        IReadOnlyList<Expression> impliedByConjuncts = isRoot && state.Where is not null
            ? [.. conjuncts, .. SplitJoinOrderConjunction(state.Where)]
            : conjuncts;

        var infos = new JoinOrderMemberInfo[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            if (!TryDescribeJoinOrderMember(sources[index], impliedByConjuncts, state.Context, out var info))
                return false;

            infos[index] = info;
        }

        var terms = new List<JoinPredicateTerm>();
        var placements = new List<JoinOrderTermPlacement>();
        foreach (var conjunct in conjuncts)
        {
            if (!TryCreateJoinOrderTerm(conjunct, infos, state.Context, out var term))
            {
                return false;
            }

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
        {
            var indexCandidates = infos[index].IndexCandidates;
            if (DescribeAutomaticJoinIndexCandidate(
                    sources[index],
                    infos[index],
                    terms,
                    index) is { } automatic)
            {
                indexCandidates = [.. indexCandidates, automatic];
            }

            members[index] = new JoinSegmentMember(
                index,
                infos[index].RowCount,
                infos[index].Width,
                indexCandidates);
        }

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

        var synthesized = SynthesizeJoinOrder(rewrittenMembers, infos, members, placements, plan, state);
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
        JoinOrderMemberInfo[] infos,
        JoinSegmentMember[] planMembers,
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
            if (plan.IndexAccesses[step] is { } indexAccess)
            {
                state.IndexSeeks[joined] = new CompiledJoinIndexSelection(
                    planMembers[member].IndexCandidates![indexAccess.CandidateIndex],
                    indexAccess.EqualityTermIndices.Select(index => placements[index].Expression).ToArray());
            }

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
        IReadOnlyList<Expression> impliedByConjuncts,
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

        var table = TryResolveJoinOrderBaseTable(source, context);
        var candidates = DescribeJoinIndexCandidates(source, table, impliedByConjuncts, context);
        info = new JoinOrderMemberInfo(
            qualifiers,
            columnNames,
            ambiguousColumnNames,
            rows,
            width,
            table,
            candidates);
        return true;
    }

    private IReadOnlyList<JoinIndexCandidate> DescribeJoinIndexCandidates(
        TableSource source,
        EmbeddedTable? table,
        IReadOnlyList<Expression> impliedByConjuncts,
        QueryContext context)
    {
        if (source is not NamedTableSource named
            || table is null
            || named.IndexDirective is NotIndexedDirective)
        {
            return [];
        }

        var candidates = new List<JoinIndexCandidate>();
        foreach (var index in table.Indexes)
        {
            if (named.IndexDirective is IndexedByDirective indexedBy
                && !string.Equals(index.Name, indexedBy.IndexName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index.Columns.Count == 0
                || index.IsMethodIndex
                || IndexUsesRegisteredFunctions(index)
                // A built-in, non-overridden index is always ready. A custom or
                // overridden-built-in index is ready only once every such collation has a
                // registered callback and the index's durable order/content has been proven
                // consistent with it (see IsCustomCollationIndexPlanReady) — this is what lets a
                // clean custom-collation index be offered for direct seeks/plans instead of
                // being unconditionally excluded, while a dirty or callback-less one still falls
                // back to a full scan rather than ever being trusted.
                || !IsCustomCollationIndexPlanReady(table, index))
            {
                continue;
            }

            // A partial index is only a safe candidate once every one of its predicate conjuncts
            // is proven by the segment's own safe plain-INNER ON/WHERE conjuncts. Uncertain or
            // unmatched proofs leave the index out of the candidate list entirely — the predicate
            // then stays a residual filter, never a seek precondition.
            if (index.IsPartial
                && !IndexExpressionSemantics.PredicateImplies(
                    impliedByConjuncts,
                    index.Where,
                    named.Name,
                    named.Alias))
            {
                continue;
            }

            var columns = new JoinIndexColumn[index.Columns.Count];
            var rowsPerPrefix = new List<double>(index.Columns.Count);
            for (var position = 0; position < index.Columns.Count; position++)
            {
                var column = index.Columns[position];
                var collation = IndexExpressionSemantics.GetCollationName(table, column).ToUpperInvariant();
                columns[position] = column.IsExpression
                    ? new JoinIndexColumn(
                        FindJoinIndexExpressionOrdinal(table, column.Expression!)!.Value,
                        collation,
                        column.Descending,
                        column.Expression)
                    : new JoinIndexColumn(column.ColumnIndex, collation, column.Descending);
                if (!TryGetSqliteStat1PrefixAverage(
                        context,
                        table.Name,
                        index.Name,
                        position + 1,
                        out var average))
                {
                    break;
                }

                rowsPerPrefix.Add(Math.Max(1.0, average));
            }

            if (rowsPerPrefix.Count == 0)
            {
                continue;
            }

            if (rowsPerPrefix.Count < columns.Length)
                Array.Resize(ref columns, rowsPerPrefix.Count);

            var indexedColumns = columns.Select(static column => column.ColumnOrdinal).ToHashSet();
            // A WITHOUT ROWID table's secondary-index records always physically carry the table's
            // full primary key as a locator suffix (see GetWithoutRowidIndexStorageColumns), so
            // every primary-key column ordinal is present for the covering check even though it is
            // not one of the index's own declared columns.
            if (table.WithoutRowid && table.PrimaryKeySchema is { } primaryKeySchemaForCovering)
            {
                foreach (var term in primaryKeySchemaForCovering.Terms)
                    indexedColumns.Add(term.ColumnIndex);
            }

            // An expression column never contributes a real table-column ordinal (its identity is
            // a negative synthetic group id), so a covering read would have to re-evaluate the
            // stored expression instead of the row it was derived from. Keep covering strictly
            // conservative for expression indexes rather than risk mis-detecting full coverage
            // when every real column ordinal also happens to be present.
            var covering = columns.All(static column => column.IndexExpression is null)
                && Enumerable.Range(0, table.Columns.Length).All(indexedColumns.Contains);
            candidates.Add(new JoinIndexCandidate(
                index.Name,
                columns,
                rowsPerPrefix,
                index.Unique && columns.Length == index.Columns.Count,
                covering,
                table.Columns.Length,
                table.RowidAliasColumnIndex >= 0,
                named.IndexDirective is IndexedByDirective,
                Automatic: false,
                LazyCursor: CanUseLazyCursorForJoinIndex(table, index, context),
                IsPrimaryKey: false));
        }

        // A WITHOUT ROWID table's own root page is itself an index b-tree keyed by the declared
        // primary key, and the primary key is deliberately never represented as a member of
        // table.Indexes (see CreateWithoutRowidConstraintIndexes). Offer it as an implicit join
        // candidate — reusing its real autoindex name so EXPLAIN QUERY PLAN output matches
        // SQLite's own convention — without fabricating any EmbeddedIndex schema state.
        if (table.WithoutRowid
            && table.PrimaryKeySchema is { Terms.Count: > 0 } primaryKeySchema
            && table.WithoutRowidPrimaryKeyIndexName is { } primaryKeyIndexName
            && (named.IndexDirective is not IndexedByDirective indexedByPrimaryKey
                || string.Equals(indexedByPrimaryKey.IndexName, primaryKeyIndexName, StringComparison.OrdinalIgnoreCase))
            // The table's own root page is never rebuilt/reordered when a collation callback is
            // registered, replaced, or unregistered (CreatePrimaryKeyComparer always compares
            // primary-key terms using pure built-in semantics — see its remarks — so the durable
            // b-tree order can never itself drift away from plain BINARY/NOCASE/RTRIM order).
            // What *can* drift is a live evaluator: per Compare's collation-resolution order, a
            // currently-registered callback for a built-in name takes precedence over the
            // hard-coded fallback that the tree's actual physical order still reflects. Offering
            // this candidate while any term's collation is presently overridden would let the
            // planner compute seek bounds under the override's semantics against a b-tree that is
            // physically ordered by the plain built-in comparison instead — a false-negative (or
            // outright wrong-row) risk indistinguishable from seeking a stale/mismatched index.
            // A genuinely unavailable or non-built-in term is equally unproven here (WITHOUT ROWID
            // primary keys with an actual custom collation are already rejected at CREATE TABLE
            // time, so this is defensive). Unlike a secondary index, there is no revalidation path
            // to make this candidate trustworthy again while overridden — REINDEX has no meaning
            // for the table's own identity b-tree — so removing the override (which immediately
            // restores agreement between the live evaluator and the tree's fixed physical order)
            // is the only way this candidate becomes safe again.
            && !primaryKeySchema.Terms.Any(term =>
                !term.Collation.IsAvailable || IsUnsafeCompiledCollation(term.Collation.Name)))
        {
            var primaryKeyColumns = new JoinIndexColumn[primaryKeySchema.Terms.Count];
            var primaryKeyRowsPerPrefix = new List<double>(primaryKeySchema.Terms.Count);
            for (var position = 0; position < primaryKeySchema.Terms.Count; position++)
            {
                var term = primaryKeySchema.Terms[position];
                primaryKeyColumns[position] = new JoinIndexColumn(
                    term.ColumnIndex,
                    (term.Collation.Name ?? "BINARY").ToUpperInvariant(),
                    term.SortOrder == SqliteKeySortOrder.Descending);
                if (!TryGetSqliteStat1PrefixAverage(
                        context,
                        table.Name,
                        table.Name,
                        position + 1,
                        out var average))
                {
                    break;
                }

                primaryKeyRowsPerPrefix.Add(Math.Max(1.0, average));
            }

            if (primaryKeyRowsPerPrefix.Count > 0)
            {
                if (primaryKeyRowsPerPrefix.Count < primaryKeyColumns.Length)
                    Array.Resize(ref primaryKeyColumns, primaryKeyRowsPerPrefix.Count);

                candidates.Add(new JoinIndexCandidate(
                    primaryKeyIndexName,
                    primaryKeyColumns,
                    primaryKeyRowsPerPrefix,
                    Unique: true,
                    Covering: true,
                    table.Columns.Length,
                    HasRowIdAlias: false,
                    Forced: named.IndexDirective is IndexedByDirective,
                    Automatic: false,
                    LazyCursor: CanUseLazyCursorForJoinIndex(table, index: null, context),
                    IsPrimaryKey: true));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Cost-model hint: can <c>TryCreateCompiledJoinIndexScanPlan</c> seek this candidate's durable
    /// b-tree directly (a "lazy cursor") instead of materializing/sorting the whole index? True
    /// either outside a transaction via the plain committed-snapshot accessor, inside a classic
    /// (non-MVCC) transaction that has pinned a durable snapshot and not yet made a schema change —
    /// see <see cref="TryOpenTransactionIndexAccessor"/> — or inside a BEGIN CONCURRENT/MVCC
    /// transaction that has pinned its own durable snapshot — see <see
    /// cref="TryOpenConcurrentMvccIndexAccessor"/> — which this mirrors so join-order cost
    /// estimation picks the same plan shape <c>TryCreateCompiledJoinIndexScanPlan</c> will actually
    /// choose.
    /// </summary>
    private bool CanUseLazyCursorForJoinIndex(EmbeddedTable table, EmbeddedIndex? index, QueryContext context)
    {
        if (context.ConcurrentMvStore is not null || context.ConcurrentMvccTxId is not null)
        {
            return context.TransactionPinnedSnapshot is not null
                && _fileStore?.CanOpenIndexAccessor(table, index, requireCommittedTableIdentity: false) == true;
        }

        if (context.TransactionOverlay is not null && context.TransactionPinnedSnapshot is not null)
            return _fileStore?.CanOpenIndexAccessor(table, index, requireCommittedTableIdentity: false) == true;

        return !context.InTransaction && _fileStore?.CanOpenIndexAccessor(table, index) == true;
    }

    /// <summary>
    /// A stable per-table identity for a persisted index expression: the negative ordinal shared
    /// by every <see cref="JoinIndexColumn"/> whose expression is structurally equal (via
    /// <see cref="IndexExpressionSemantics.ExpressionsEqual"/>) to <paramref name="expression"/>,
    /// or <see langword="null"/> when no persisted index on <paramref name="table"/> declares a
    /// structurally matching expression column. Recomputed deterministically from
    /// <paramref name="table"/>'s own indexes on every call (first-seen order), so a candidate
    /// column's ordinal (assigned in <see cref="DescribeJoinIndexCandidates"/>) and a later query
    /// conjunct's operand (resolved in <see cref="TryCreateJoinOrderTerm"/>) always agree as long
    /// as the table's index list does not change mid-plan — true within one planning pass. This is
    /// the "stable term identity" that lets <see cref="JoinOrderEnumerator"/> keep binding
    /// expression-index terms with a plain <see cref="int"/> comparison, never touching an AST.
    /// </summary>
    private static int? FindJoinIndexExpressionOrdinal(EmbeddedTable table, Expression expression)
    {
        var seen = new List<Expression>();
        foreach (var candidateIndex in table.Indexes)
        {
            foreach (var candidateColumn in candidateIndex.Columns)
            {
                if (candidateColumn.Expression is not { } candidateExpression)
                    continue;

                var groupIndex = seen.FindIndex(seenExpression =>
                    IndexExpressionSemantics.ExpressionsEqual(seenExpression, candidateExpression));
                if (groupIndex < 0)
                {
                    groupIndex = seen.Count;
                    seen.Add(candidateExpression);
                }

                if (IndexExpressionSemantics.ExpressionsEqual(candidateExpression, expression))
                    return JoinIndexExpressionOrdinalBase - groupIndex;
            }
        }

        return null;
    }


    private static JoinIndexCandidate? DescribeAutomaticJoinIndexCandidate(
        TableSource source,
        JoinOrderMemberInfo info,
        IReadOnlyList<JoinPredicateTerm> terms,
        int member)
    {
        if (source is not NamedTableSource { IndexDirective: null }
            || info.Table is not { } table)
        {
            return null;
        }

        var memberBit = 1UL << member;
        var columns = new List<JoinIndexColumn>();
        var rowsPerPrefix = new List<double>();
        foreach (var term in terms)
        {
            int ordinal;
            double matchRows;
            if (term.EqualityRightMask == memberBit
                && term.EqualityRightColumnOrdinal >= 0
                && !term.EqualityLeftConvertsTextToNumeric
                && !term.EqualityLeftConvertsNumericToText
                && !term.EqualityRightConvertsTextToNumeric
                && !term.EqualityRightConvertsNumericToText)
            {
                ordinal = term.EqualityRightColumnOrdinal;
                matchRows = term.EqualityRightMatchRows;
            }
            else if (term.EqualityLeftMask == memberBit
                     && term.EqualityLeftColumnOrdinal >= 0
                     && !term.EqualityLeftConvertsTextToNumeric
                     && !term.EqualityLeftConvertsNumericToText)
            {
                if (term.EqualityRightConvertsTextToNumeric
                    || term.EqualityRightConvertsNumericToText)
                {
                    continue;
                }

                ordinal = term.EqualityLeftColumnOrdinal;
                matchRows = term.EqualityLeftMatchRows;
            }
            else
            {
                continue;
            }

            if (term.EqualityCollation is not { } collation
                || !IsHashableJoinKeyCollation(collation)
                || !string.Equals(
                    NormalizeDeclaredCollation(table.ColumnDefinitions[ordinal].Collation) ?? "BINARY",
                    collation,
                    StringComparison.OrdinalIgnoreCase)
                || table.Indexes.Any(index => index.Columns.Any(column => column.ColumnIndex == ordinal))
                || columns.Any(column => column.ColumnOrdinal == ordinal))
            {
                continue;
            }

            columns.Add(new JoinIndexColumn(ordinal, collation.ToUpperInvariant(), Descending: false));
            var previous = rowsPerPrefix.Count == 0
                ? Math.Max(1.0, info.RowCount)
                : rowsPerPrefix[^1];
            rowsPerPrefix.Add(Math.Max(
                1.0,
                Math.Min(
                    Math.Max(1.0, matchRows),
                    previous * JoinCostParams.SelectivityEqualityUnindexed)));
        }

        if (columns.Count == 0)
            return null;

        return new JoinIndexCandidate(
            $"automatic_{table.Name}",
            columns,
            rowsPerPrefix,
            Unique: false,
            Covering: true,
            table.Columns.Length,
            table.RowidAliasColumnIndex >= 0,
            Forced: false,
            Automatic: true,
            LazyCursor: false);
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
        var leftColumnOrdinal = -1;
        var rightColumnOrdinal = -1;
        var leftConvertsTextToNumeric = false;
        var leftConvertsNumericToText = false;
        var rightConvertsTextToNumeric = false;
        var rightConvertsNumericToText = false;
        string? collation = null;
        string? seekCollation = null;
        if (conjunct is BinaryExpression { Operator: BinaryOperator.Equal } binary)
        {
            if (UnwrapCollation(binary.Left) is ColumnExpression { BooleanKeyword: null } leftColumn
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

                var leftDefinition = TryResolveJoinOrderColumnDefinition(infos[leftMember], leftColumn, context);
                var rightDefinition = TryResolveJoinOrderColumnDefinition(infos[rightMember], rightColumn, context);
                if (leftDefinition is not null
                    && rightDefinition is not null
                    && infos[leftMember].Table!.TryGetColumnIndex(leftDefinition.Name, out leftColumnOrdinal)
                    && infos[rightMember].Table!.TryGetColumnIndex(rightDefinition.Name, out rightColumnOrdinal))
                {
                    // Ordinals and affinity-conversion flags stay populated as soon as the
                    // equality resolves structurally, regardless of collation hashability — a
                    // custom-collation index seek needs exactly the same binding shape as a
                    // built-in one (CanBindIndexColumn matches against EqualitySeekCollation,
                    // never EqualityCollation). Only the hash-specific EqualityCollation below is
                    // conditionally cleared, so hashing stays strictly gated to built-in
                    // collations while seeking does not.
                    var leftAffinity = GetJoinKeyAffinity(leftDefinition);
                    var rightAffinity = GetJoinKeyAffinity(rightDefinition);
                    // NormalizeDeclaredCollation substitutes "BINARY" for a null collation, so it
                    // must not be chained with ?? here: doing so would short-circuit on the left
                    // column's absent collation and never fall through to the right column's
                    // declared collation. Read the raw declared collation instead and only apply
                    // the BINARY default once, after both operands have been consulted.
                    var resolvedCollation = (GetExplicitCollation(binary.Left)
                        ?? GetExplicitCollation(binary.Right)
                        ?? leftDefinition.Collation
                        ?? rightDefinition.Collation
                        ?? "BINARY").ToUpperInvariant();
                    leftConvertsTextToNumeric =
                        IsNumericAffinity(rightAffinity) && !IsNumericAffinity(leftAffinity);
                    leftConvertsNumericToText =
                        rightAffinity == ColumnAffinity.Text && leftAffinity is null;
                    rightConvertsTextToNumeric =
                        IsNumericAffinity(leftAffinity) && !IsNumericAffinity(rightAffinity);
                    rightConvertsNumericToText =
                        leftAffinity == ColumnAffinity.Text && rightAffinity is null;
                    seekCollation = resolvedCollation;
                    collation = !IsHashableJoinKeyCollation(resolvedCollation) || IsUnsafeCompiledCollation(resolvedCollation)
                        ? null
                        : resolvedCollation;
                }
            }
            else
            {
                var expressionComparisonCollation =
                    GetExplicitCollation(binary.Left)
                    ?? GetExplicitCollation(binary.Right)
                    ?? TryGetJoinOrderInheritedCollation(binary.Left, infos, context)
                    ?? TryGetJoinOrderInheritedCollation(binary.Right, infos, context);
                if (TryMatchExpressionIndexOperand(
                         binary.Left,
                         binary.Right,
                         // SQLite's collation-precedence rule (datatype3.html §7.1 rule 2) is
                         // decided once, from the original left-to-right expression shape, before
                         // either directional match is attempted below. Resolving it inside
                         // TryMatchExpressionIndexOperand from (indexedOperand, outerOperand)
                         // instead would make the winning explicit COLLATE depend on which operand
                         // happens to structurally match a persisted expression index — e.g. a
                         // right-side match would consult binary.Right's explicit COLLATE before
                         // binary.Left's, silently reversing SQLite's left-wins rule whenever the
                         // indexed expression is on the right. Computing it once here, from
                         // binary.Left then binary.Right, and threading the same value into both
                         // directional attempts keeps the outcome independent of which side is
                         // indexed.
                         expressionComparisonCollation,
                         infos,
                         context,
                         out var exprLeftMask,
                         out var exprRightMask,
                         out var exprLeftOrdinal,
                         out var exprRightOrdinal,
                         out var exprLeftMatchRows,
                         out var exprRightMatchRows,
                         out var exprCollation,
                         out var exprSeekCollation)
                     || TryMatchExpressionIndexOperand(
                         binary.Right,
                         binary.Left,
                         expressionComparisonCollation,
                         infos,
                         context,
                         out exprRightMask,
                         out exprLeftMask,
                         out exprRightOrdinal,
                         out exprLeftOrdinal,
                         out exprRightMatchRows,
                         out exprLeftMatchRows,
                         out exprCollation,
                         out exprSeekCollation))
                {
                    isEquality = true;
                    leftMask = exprLeftMask;
                    rightMask = exprRightMask;
                    leftColumnOrdinal = exprLeftOrdinal;
                    rightColumnOrdinal = exprRightOrdinal;
                    leftMatchRows = exprLeftMatchRows;
                    rightMatchRows = exprRightMatchRows;
                    collation = exprCollation;
                    seekCollation = exprSeekCollation;
                }
            }
        }

        term = new JoinPredicateTerm(
            mask,
            isEquality,
            leftMask,
            rightMask,
            leftMatchRows,
            rightMatchRows,
            EstimateJoinOrderSelectivity(conjunct),
            leftColumnOrdinal,
            rightColumnOrdinal,
            leftConvertsTextToNumeric,
            leftConvertsNumericToText,
            rightConvertsTextToNumeric,
            rightConvertsNumericToText,
            collation,
            seekCollation);
        return true;
    }

    /// <summary>
    /// Matches one directional reading of an equality conjunct against a persisted expression
    /// index: <paramref name="indexedOperand"/> must structurally match (via
    /// <see cref="IndexExpressionSemantics.ExpressionsEqual"/>) an expression column declared on
    /// exactly one already-described member's table — that member's <see
    /// cref="FindJoinIndexExpressionOrdinal"/> ordinal becomes the indexed side's column ordinal,
    /// the same synthetic id <c>DescribeJoinIndexCandidates</c> already assigned the matching
    /// <see cref="JoinIndexColumn"/>. <paramref name="outerOperand"/> must resolve to a plain
    /// column of a single, different member (its real table ordinal is kept so an automatic index
    /// can still be built on it) — the task's "opposite operand may come from already placed
    /// outer members" is satisfied structurally here and evaluated later, at seek-key build time,
    /// against the outer row. <paramref name="explicitCollation"/> is resolved once by the
    /// caller from the original BinaryExpression's left operand then right (SQLite's left-wins
    /// rule, datatype3.html §7.1 rule 2) — the same value is passed into both directional
    /// attempts so the winning explicit COLLATE never depends on which operand happens to
    /// structurally match a persisted expression index. Falling back to the outer column's
    /// declared collation, then BINARY, mirrors the plain-column path; the persisted index's
    /// own declared collation for that expression is checked later — by
    /// <c>CanBindIndexColumn</c>'s exact string comparison against <paramref name="seekCollation"/>,
    /// not here — so a mismatched expression/collation naturally leaves the term unable to bind to
    /// that candidate. <paramref name="collation"/> is additionally gated to hashable, non-custom
    /// collations for automatic hash-index building — it is populated only when the resolved
    /// collation is also safe to hash (see <see cref="JoinPredicateTerm.EqualityCollation"/>),
    /// staying <see langword="null"/> for a custom or overridden-built-in collation.
    /// <paramref name="seekCollation"/> is instead populated whenever the operands structurally
    /// match, regardless of hashability, and is safe for index-seek binding only (see
    /// <see cref="JoinPredicateTerm.EqualitySeekCollation"/>).
    /// </summary>
    private bool TryMatchExpressionIndexOperand(
        Expression indexedOperand,
        Expression outerOperand,
        string? explicitCollation,
        JoinOrderMemberInfo[] infos,
        QueryContext context,
        out ulong indexedMask,
        out ulong outerMask,
        out int indexedColumnOrdinal,
        out int outerColumnOrdinal,
        out double indexedMatchRows,
        out double outerMatchRows,
        out string? collation,
        out string? seekCollation)
    {
        indexedMask = 0;
        outerMask = 0;
        indexedColumnOrdinal = -1;
        outerColumnOrdinal = -1;
        indexedMatchRows = 0.0;
        outerMatchRows = 0.0;
        collation = null;
        seekCollation = null;

        var unwrappedOuter = UnwrapCollation(outerOperand);
        if (unwrappedOuter is not ColumnExpression { BooleanKeyword: null } outerColumn
            || !TryResolveJoinOrderMember(outerColumn, infos, out var outerMember)
            || !TryResolveJoinOrderMask(indexedOperand, infos, out var indexedMaskValue)
            || System.Numerics.BitOperations.PopCount(indexedMaskValue) != 1)
        {
            return false;
        }

        var indexedMember = System.Numerics.BitOperations.TrailingZeroCount(indexedMaskValue);
        // A persisted expression-index column's own COLLATE is stored as metadata (see
        // EmbeddedIndexFactory.Create), not retained on its Expression tree, so the stored
        // candidate expression is always bare. An ON/WHERE conjunct's operand, however, may
        // still carry an explicit "(...) COLLATE name" wrapper straight from the parser (that
        // wrapper's name is recovered separately below via GetExplicitCollation). Unwrap it here,
        // the same way outerOperand already is above, so the structural match in
        // FindJoinIndexExpressionOrdinal compares like with like instead of always missing.
        if (indexedMember == outerMember
            || infos[indexedMember].Table is not { } indexedTable)
        {
            return false;
        }
        var unwrappedIndexed = UnwrapCollation(indexedOperand);
        var ordinal = FindJoinIndexExpressionOrdinal(indexedTable, unwrappedIndexed);
        if (ordinal is not { } expressionOrdinal)
        {
            return false;
        }

        var outerDefinition = TryResolveJoinOrderColumnDefinition(infos[outerMember], outerColumn, context);
        var outerAffinity = GetJoinKeyAffinity(outerDefinition);
        // The indexed side is an arbitrary expression: unlike a real column, nothing declares
        // (or otherwise proves) what storage class its persisted values actually hold, so
        // nothing coerces those values before a seek. SQLite's comparison-affinity rules
        // (datatype3.html §7.1) apply the *other* operand's affinity whenever one side is
        // NUMERIC-ish or TEXT and the other has none: a NUMERIC-ish outer probe would need the
        // stored expression value normalized as a number, while a TEXT outer probe would need it
        // normalized as text (e.g. an `x+0` expression storing INTEGER 1 must still compare equal
        // to a TEXT '1' outer probe, exactly as SQLite's rule 2 requires) — and the
        // conversion-flag machinery below never runs for this branch, so there is no seek-key
        // coercion to fall back on for either direction. Only an outer probe that itself carries
        // no affinity signal (STRICT ANY, i.e. null) or BLOB affinity is safe: SqlValue comparison
        // already orders those cross-type without help, exactly as a row-by-row scan of the same
        // predicate would, so a direct seek is safe. Every other affinity (TEXT, INTEGER, REAL,
        // NUMERIC) must decline unless the expression's own representation is statically proven --
        // narrowly, a TEXT outer probe against an expression whose top-level operator is `||`
        // (string concatenation always yields TEXT or NULL, regardless of operand affinities, see
        // IsIndexedExpressionStaticallyText) needs no coercion either way and remains eligible.
        if (outerDefinition is null
            || infos[outerMember].Table is not { } outerTable
            || !outerTable.TryGetColumnIndex(outerDefinition.Name, out var resolvedOuterOrdinal)
            || (outerAffinity is not null
                && outerAffinity != ColumnAffinity.Blob
                && !(outerAffinity == ColumnAffinity.Text
                    && IsIndexedExpressionStaticallyText(unwrappedIndexed))))
        {
            return false;
        }

        // explicitCollation is resolved once by the caller from the original BinaryExpression's
        // left operand then right (SQLite left-wins), independent of which operand this
        // particular directional attempt treats as "indexed" — see the call sites' comment.
        var resolvedCollation = (explicitCollation
            ?? NormalizeDeclaredCollation(outerDefinition.Collation)
            ?? "BINARY").ToUpperInvariant();

        indexedMask = 1UL << indexedMember;
        outerMask = 1UL << outerMember;
        indexedColumnOrdinal = expressionOrdinal;
        outerColumnOrdinal = resolvedOuterOrdinal;
        // No persisted stat exists for an arbitrary expression's result distribution; a
        // conservative per-member rough estimate is enough since this only feeds the enumerator's
        // cost model, never a correctness check.
        indexedMatchRows = Math.Max(1.0, infos[indexedMember].RowCount);
        outerMatchRows = EstimateJoinOrderMatchRows(infos[outerMember], outerColumn, context);
        // Seek binding is safe unconditionally: DescribeJoinIndexCandidates never offers this
        // expression as a candidate column unless the persisted index itself is already proven
        // ready (built-in, or custom-with-validated-callback). Hashing stays strictly gated to
        // built-in collations, since the outer (real-table) ordinal above — unlike the indexed
        // side's synthetic negative ordinal — CAN reach automatic hash-index building.
        seekCollation = resolvedCollation;
        collation = !IsHashableJoinKeyCollation(resolvedCollation) || IsUnsafeCompiledCollation(resolvedCollation)
            ? null
            : resolvedCollation;
        return true;
    }

    /// <summary>
    /// True when <paramref name="expression"/>'s persisted result is provably TEXT (or NULL)
    /// regardless of its operands' runtime values or declared affinities. Narrowly recognizes
    /// SQLite's <c>||</c> string-concatenation operator, which always yields TEXT or NULL
    /// (datatype3.html §3), never NUMERIC/INTEGER/REAL/BLOB -- unlike, say, an arithmetic
    /// expression such as <c>x+0</c>, whose result is NUMERIC even when it happens to hold a
    /// value that also parses as text. This is the one arbitrary-expression shape where a
    /// TEXT-affinity outer probe needs no comparison-affinity coercion against the persisted
    /// value in either direction, so <see cref="TryMatchExpressionIndexOperand"/> can still treat
    /// it as seek-eligible even though the expression's storage class is otherwise unproven.
    /// </summary>
    private static bool IsIndexedExpressionStaticallyText(Expression expression)
        => expression is BinaryExpression { Operator: BinaryOperator.Concatenate };

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

    private static string? TryGetJoinOrderInheritedCollation(
        Expression expression,
        JoinOrderMemberInfo[] infos,
        QueryContext context)
    {
        expression = UnwrapCollation(expression);
        if (expression is CastExpression cast)
            return TryGetJoinOrderInheritedCollation(cast.Expression, infos, context);
        if (expression is not ColumnExpression column
            || !TryResolveJoinOrderMember(column, infos, out var member))
        {
            return null;
        }

        return TryResolveJoinOrderColumnDefinition(infos[member], column, context) is { } definition
            ? NormalizeDeclaredCollation(definition.Collation)
            : null;
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
                rewrite.HashBuildRight,
                rewrite.IndexSeeks)
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

        public Dictionary<JoinTableSource, CompiledJoinIndexSelection> IndexSeeks { get; } =
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
        EmbeddedTable? table,
        IReadOnlyList<JoinIndexCandidate> indexCandidates)
    {
        public HashSet<string> Qualifiers { get; } = qualifiers;

        public HashSet<string> ColumnNames { get; } = columnNames;

        public HashSet<string> AmbiguousColumnNames { get; } = ambiguousColumnNames;

        public double RowCount { get; } = rowCount;

        public int Width { get; } = width;

        public EmbeddedTable? Table { get; } = table;

        public IReadOnlyList<JoinIndexCandidate> IndexCandidates { get; } = indexCandidates;
    }

    private readonly record struct JoinOrderTermPlacement(Expression Expression, ulong Mask);

    private sealed record CompiledJoinIndexSelection(
        JoinIndexCandidate Candidate,
        IReadOnlyList<Expression> EqualityTerms);

    private readonly record struct JoinOrderRewrittenSource(TableSource Source, int[] SlotMap, bool Changed);

    /// <summary>
    /// The rewritten FROM tree plus everything the caller needs to consume it: the slot map that
    /// restores FROM-order projection, and the per-node build-side decisions the cost model made.
    /// </summary>
    private sealed record JoinOrderRewriteResult(
        TableSource Source,
        int[] SlotMap,
        IReadOnlyDictionary<JoinTableSource, bool> HashBuildRight,
        IReadOnlyDictionary<JoinTableSource, CompiledJoinIndexSelection> IndexSeeks);
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
