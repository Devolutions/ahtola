using Ahtola.Core.Compilation.JoinOrdering;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// Cost-based access selection for the inner table of the internal semi/anti joins the
/// correlated-subquery rewrite produces (<c>EmbeddedDatabase.SubqueryRewrites.cs</c>).
/// <para>
/// This ports the part of Turso v0.8.1's join planner that decides how the inner loop of a
/// two-table semi or anti join reads its table: <c>choose_best_btree_candidate</c> and the
/// temporary-index alternative of <c>find_best_access_method_for_btree</c>
/// (optimizer/access_method.rs:195-457, 809-987), the hash-join alternative of
/// <c>join_lhs_and_rhs</c> (optimizer/join.rs:586-1000) with the <c>try_hash_join_access_method</c>
/// / <c>should_not_use_hash_join</c> gates (access_method.rs:1294-1537), and the constraint
/// selectivities of <c>estimate_selectivity</c> (optimizer/constraints.rs:367-437). Every cost
/// is computed with <see cref="TursoCostModel"/>, so the choice is upstream's own.
/// </para>
/// <para>
/// The selected <see cref="SemiAntiInnerAccess"/> is consumed by both
/// <c>GetSemiOrAntiJoinRows</c> (which runs it) and the <c>EXPLAIN QUERY PLAN</c> describer
/// (which prints it), so the plan text always names the strategy that executes. A shape the
/// planner does not model returns <see langword="null"/> and keeps the legacy access path and
/// its legacy description.
/// </para>
/// </summary>
public sealed partial class EmbeddedDatabase
{
    internal enum SemiAntiInnerAccessKind
    {
        /// <summary>A declared index searched by an equality prefix and/or one range.</summary>
        DeclaredIndexSearch,

        /// <summary>A full scan of a declared index (cheaper than the table itself).</summary>
        DeclaredIndexScan,

        /// <summary>An automatic (ephemeral) index built over the inner table once.</summary>
        EphemeralIndex,

        /// <summary>A full scan of the inner table for every outer row.</summary>
        TableScan,

        /// <summary>
        /// A left anti hash join: the outer rows are hashed on the join key, the inner table
        /// is scanned once as the probe, and outer rows no probe row matched are emitted.
        /// </summary>
        HashAnti,
    }

    /// <summary>A comparison operator as seen from the constrained inner column.</summary>
    internal enum TursoConstraintOperator
    {
        Equal,
        Is,
        NotEqual,
        IsNot,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    /// <summary>
    /// One condition conjunct that compares an inner column with an expression that reads no
    /// inner column (Turso's <c>Constraint</c>, constraints.rs:43-104).
    /// </summary>
    private sealed record SemiAntiConstraint(
        int ConjunctIndex,
        Expression Conjunct,
        int ColumnOrdinal,
        TursoConstraintOperator Operator,
        Expression ColumnSide,
        Expression Value,
        bool DependsOnOuter,
        bool NullMatching,
        double Selectivity,
        string ComparisonCollation,
        bool IndexAffinityOk,
        bool UsesCustomCollation);

    /// <summary>One index column of a seek key (Turso's <c>RangeConstraintRef</c>).</summary>
    private sealed record SemiAntiSeekTerm(
        int ColumnOrdinal,
        string ColumnName,
        SemiAntiConstraint? Equality,
        SemiAntiConstraint? Lower,
        SemiAntiConstraint? Upper);

    /// <summary>The access the inner loop of a semi/anti join uses, with its estimates.</summary>
    private sealed record SemiAntiInnerAccess(
        SemiAntiInnerAccessKind Kind,
        NamedTableSource Inner,
        EmbeddedTable Table,
        EmbeddedIndex? Index,
        IReadOnlyList<SemiAntiSeekTerm> SeekTerms,
        IReadOnlyList<SemiAntiConstraint> HashKeys,
        bool Covering,
        double InputRows,
        double RowsPerOuterRow,
        double Cost)
    {
        public IReadOnlyList<string> ConstraintTexts { get; } = BuildConstraintTexts(SeekTerms);

        private static IReadOnlyList<string> BuildConstraintTexts(IReadOnlyList<SemiAntiSeekTerm> terms)
        {
            var texts = new List<string>(terms.Count);
            foreach (var term in terms)
            {
                if (term.Equality is not null)
                {
                    texts.Add($"{term.ColumnName}=?");
                    continue;
                }

                if (term.Lower is { } lower)
                    texts.Add($"{term.ColumnName}{(lower.Operator == TursoConstraintOperator.Greater ? ">" : ">=")}?");
                if (term.Upper is { } upper)
                    texts.Add($"{term.ColumnName}{(upper.Operator == TursoConstraintOperator.Less ? "<" : "<=")}?");
            }

            return texts;
        }
    }

    /// <summary>The outer base table a semi/anti chain filters (its left-most leaf).</summary>
    private static NamedTableSource? GetSemiAntiOuterLeaf(TableSource source)
        => source switch
        {
            NamedTableSource named => named,
            JoinTableSource { Kind: JoinKind.Semi or JoinKind.Anti } join => GetSemiAntiOuterLeaf(join.Left),
            _ => null,
        };

    /// <summary>
    /// Plans the inner access of one semi/anti join. <paramref name="leftPredicate"/> is the
    /// part of the outer WHERE pushed onto the outer table (the same predicate
    /// <c>GetSemiOrAntiJoinRows</c> receives); it only feeds the outer row estimate. The same
    /// B-tree/temporary-index choice prices the null-supplying side of a two-table LEFT JOIN,
    /// whose projection may read further inner columns (<paramref name="otherInnerColumns"/>,
    /// which decide whether an index covers them); no hash join is considered for it.
    /// </summary>
    private SemiAntiInnerAccess? PlanSemiOrAntiInnerAccess(
        JoinTableSource join,
        Expression? leftPredicate,
        QueryContext context,
        IReadOnlyCollection<int>? otherInnerColumns = null,
        double? inputRowsOverride = null)
    {
        if (join.Kind is not (JoinKind.Semi or JoinKind.Anti or JoinKind.Left)
            || join.Kind == JoinKind.Left && join.Left is not NamedTableSource
            || join.Condition is null
            || context.ConcurrentMvStore is not null
            || !TryGetDecorrelationBaseTable(join.Right, context, out var inner, out var innerQualifier)
            || GetSemiAntiOuterLeaf(join.Left) is not { } outerLeaf
            || !TryGetDecorrelationBaseTable(outerLeaf, context, out _, out var outerQualifier)
            || string.Equals(innerQualifier, outerQualifier, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var table = context.Tables[inner.Name];
        var outerTable = context.Tables[outerLeaf.Name];
        var innerColumns = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
        var conjuncts = IndexExpressionSemantics.SplitConjuncts(join.Condition);
        var constraints = new List<SemiAntiConstraint>();
        var usedColumns = new HashSet<int>();
        var steps = new int[conjuncts.Count];
        for (var index = 0; index < conjuncts.Count; index++)
        {
            var conjunct = conjuncts[index];
            // WhereTermInfo.extra_steps (join.rs:2338-2345): the row cost already includes one
            // simple condition, so a lone comparison adds no WHERE work.
            steps[index] = CountTursoWhereSteps(conjunct) - 1;
            if (!ForEachColumnReference(conjunct, column =>
                {
                    if (!IsInnerColumnReference(column, innerQualifier, innerColumns))
                        return true;
                    var name = column.UnqualifiedName ?? column.Name;
                    if (IsRowIdAlias(name) && !table.TryGetColumnIndex(name, out _))
                        return false;
                    if (table.TryGetColumnIndex(name, out var ordinal))
                        usedColumns.Add(ordinal);
                    return true;
                }))
            {
                // A rowid reference (or a node the walker does not model) is outside what the
                // ported candidate model describes; keep the legacy access path.
                return null;
            }

            if (TryDescribeSemiAntiConstraint(
                    index,
                    conjunct,
                    table,
                    innerQualifier,
                    innerColumns,
                    outerLeaf,
                    outerTable,
                    context,
                    out var constraint))
            {
                constraints.Add(constraint);
            }
        }

        // Turso's rowid candidate would serve an INTEGER PRIMARY KEY equality with a rowid
        // seek, which this planner does not model; leave that shape to the legacy path.
        if (table.HasRowidAlias
            && constraints.Any(constraint => constraint.ColumnOrdinal == table.RowidAliasColumnIndex))
        {
            return null;
        }

        if (otherInnerColumns is not null)
            usedColumns.UnionWith(otherInnerColumns);

        // Stable partition: equalities first (constraints.rs:1165-1171).
        constraints = [.. constraints.Where(static c => IsTursoEqualityOperator(c.Operator)),
            .. constraints.Where(static c => !IsTursoEqualityOperator(c.Operator))];

        var inputRows = inputRowsOverride
            ?? EstimateTursoTableRows(outerLeaf.Name, context)
                * EstimateTursoLocalSelectivity(leftPredicate, outerLeaf, outerTable, context);
        var innerRows = EstimateTursoTableRows(inner.Name, context);
        var covering = usedColumns.Count > 0;

        // choose_best_btree_candidate (access_method.rs:195-457).
        var forcedIndex = inner.IndexDirective is IndexedByDirective indexedBy ? indexedBy.IndexName : null;
        var candidateIndexes = inner.IndexDirective is NotIndexedDirective
            ? []
            : table.Indexes
                .Where(index => !index.IsMethodIndex
                    && !index.IsPartial
                    && index.Columns.Count > 0
                    && (forcedIndex is null
                        || string.Equals(index.Name, forcedIndex, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        if (forcedIndex is not null && candidateIndexes.Length == 0)
            return null;

        SemiAntiInnerAccess best;
        double bestCost;
        double bestAdjustedOutput = double.MaxValue;
        if (forcedIndex is null)
        {
            bestCost = TursoCostModel.EstimateScanCost(innerRows, inputRows);
            best = new SemiAntiInnerAccess(
                SemiAntiInnerAccessKind.TableScan,
                inner,
                table,
                Index: null,
                SeekTerms: [],
                HashKeys: [],
                Covering: false,
                inputRows,
                RowsPerOuterRow: innerRows,
                bestCost);
        }
        else
        {
            bestCost = double.MaxValue;
            best = null!;
        }

        foreach (var index in candidateIndexes)
        {
            var terms = BuildSemiAntiIndexSeekTerms(index, table, constraints);
            var indexCovering = covering
                && index.Columns.All(static column => !column.IsExpression)
                && usedColumns.All(ordinal => index.Columns.Any(column => column.ColumnIndex == ordinal));
            var info = new TursoIndexInfo(
                index.Unique,
                index.Columns.Count,
                indexCovering,
                TursoCostModel.IndexLeafRowsPerPage(index.Columns.Count, table.Columns.Length, table.HasRowidAlias));
            var cost = EstimateTursoScanOrSeekCost(info, terms, inputRows, innerRows, table, index, context);
            var adjustedOutput = constraints
                .Where(constraint => !terms.Any(term => ReferenceEquals(term.Equality, constraint)
                        || ReferenceEquals(term.Lower, constraint)
                        || ReferenceEquals(term.Upper, constraint))
                    && constraint.Operator is TursoConstraintOperator.Equal
                        or TursoConstraintOperator.Less
                        or TursoConstraintOperator.LessOrEqual
                        or TursoConstraintOperator.Greater
                        or TursoConstraintOperator.GreaterOrEqual)
                .Aggregate(1.0, static (product, constraint) => product * constraint.Selectivity);
            var costsEqual = Math.Abs(cost - bestCost) < 1e-9;
            if (cost < bestCost || costsEqual && adjustedOutput < bestAdjustedOutput - 1e-12)
            {
                bestCost = cost;
                bestAdjustedOutput = adjustedOutput;
                best = new SemiAntiInnerAccess(
                    terms.Count == 0 ? SemiAntiInnerAccessKind.DeclaredIndexScan : SemiAntiInnerAccessKind.DeclaredIndexSearch,
                    inner,
                    table,
                    index,
                    terms,
                    HashKeys: [],
                    indexCovering,
                    inputRows,
                    RowsPerOuterRow: terms.Count == 0
                        ? innerRows
                        : EstimateTursoRowsPerSeek(info, terms, innerRows, table, index, context),
                    cost);
            }
        }

        var bestWithWhere = best.Cost + EstimateSemiAntiWhereWork(best, steps, inputRows);

        // The temporary index is only considered over a plain full table scan
        // (access_method.rs:903-987).
        if (best.Kind == SemiAntiInnerAccessKind.TableScan)
        {
            var terms = BuildSemiAntiEphemeralSeekTerms(table, constraints);
            if (terms.Count != 0)
            {
                var columnCount = usedColumns.Count;
                var info = new TursoIndexInfo(
                    Unique: false,
                    columnCount,
                    Covering: true,
                    TursoCostModel.IndexLeafRowsPerPage(columnCount, table.Columns.Length, table.HasRowidAlias));
                var rowsPerSeek = EstimateTursoRowsPerSeek(info, terms, innerRows, table, index: null, context);
                var cost = TursoCostModel.EstimateScanCost(innerRows, 1.0)
                    + TursoCostModel.EstimateEphemeralIndexBuildCost(innerRows)
                    + inputRows * JoinCostParams.CpuCostPerSeek
                    + inputRows * rowsPerSeek * JoinCostParams.CpuCostPerRow;
                var ephemeral = new SemiAntiInnerAccess(
                    SemiAntiInnerAccessKind.EphemeralIndex,
                    inner,
                    table,
                    Index: null,
                    terms,
                    HashKeys: [],
                    Covering: true,
                    inputRows,
                    rowsPerSeek,
                    cost);
                var ephemeralWithWhere = cost + EstimateSemiAntiWhereWork(ephemeral, steps, inputRows);
                if (ephemeralWithWhere < bestWithWhere)
                {
                    best = ephemeral;
                    bestWithWhere = ephemeralWithWhere;
                }
            }
        }

        if (join.Kind == JoinKind.Anti
            && join.Left is NamedTableSource buildSource
            && TryPlanSemiAntiHashAnti(
                join,
                buildSource,
                outerTable,
                leftPredicate,
                inner,
                table,
                constraints,
                candidateIndexes,
                best,
                innerRows,
                inputRows,
                context) is { } hash)
        {
            var hashWithWhere = hash.Cost + EstimateSemiAntiWhereWork(hash, steps, inputRows);
            if (hashWithWhere < bestWithWhere)
            {
                best = hash;
                bestWithWhere = hashWithWhere;
            }
        }

        // add_where_cost folds the loop's WHERE work into the access method's own cost, which
        // is what Turso reports as the node's access_cost.
        return best with { Cost = bestWithWhere };
    }

    /// <summary>
    /// The estimate of the first loop of a semi/anti chain: a full scan of the outer table
    /// (estimate_scan_cost plus the WHERE work of its own filters, access_method.rs:687-707),
    /// producing its row count reduced by those filters' selectivities (rows_after_join).
    /// </summary>
    private (double Rows, double Cost) EstimateSemiAntiOuterScan(
        NamedTableSource outer,
        Expression? predicate,
        QueryContext context)
    {
        var table = context.Tables[outer.Name];
        var baseRows = EstimateTursoTableRows(outer.Name, context);
        var extraSteps = 0;
        if (predicate is not null)
        {
            foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(predicate))
                extraSteps += CountTursoWhereSteps(conjunct) - 1;
        }

        var cost = TursoCostModel.EstimateScanCost(baseRows, 1.0)
            + TursoCostModel.EstimateWhereWork(1.0, baseRows, consumedSteps: 0, extraSteps);
        return (baseRows * EstimateTursoLocalSelectivity(predicate, outer, table, context), cost);
    }

    /// <summary>
    /// The estimate of a first loop that searches a declared index with the table's own
    /// constant constraints (<paramref name="predicate"/>): estimate_cost_for_scan_or_seek with
    /// one input row plus the WHERE work of every term (access_method.rs:687-707), producing the
    /// rows per seek reduced by the constraints the search leaves as filters. Returns
    /// <see langword="null"/> unless the ported seek terms are exactly
    /// <paramref name="constraintTexts"/> — the search the caller describes — and every term is
    /// a modeled column constraint.
    /// </summary>
    private EqpJsonEstimate? EstimateTursoFirstLoopIndexSearch(
        NamedTableSource source,
        EmbeddedIndex index,
        Expression predicate,
        IReadOnlyCollection<int> usedColumns,
        IReadOnlyList<string> constraintTexts,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(source.Name, out var table) || index.IsPartial || index.IsMethodIndex)
            return null;

        var qualifier = source.Alias ?? source.Name;
        var columns = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
        var conjuncts = IndexExpressionSemantics.SplitConjuncts(predicate);
        var steps = new int[conjuncts.Count];
        var constraints = new List<SemiAntiConstraint>();
        for (var position = 0; position < conjuncts.Count; position++)
        {
            steps[position] = CountTursoWhereSteps(conjuncts[position]) - 1;
            if (!TryDescribeSemiAntiConstraint(position, conjuncts[position], table, qualifier, columns, source, table, context, out var constraint)
                || constraint.DependsOnOuter)
            {
                return null;
            }

            constraints.Add(constraint);
        }

        constraints = [.. constraints.Where(static c => IsTursoEqualityOperator(c.Operator)),
            .. constraints.Where(static c => !IsTursoEqualityOperator(c.Operator))];
        var terms = BuildSemiAntiIndexSeekTerms(index, table, constraints);
        if (terms.Count == 0)
            return null;

        var covering = index.Columns.All(static column => !column.IsExpression)
            && usedColumns.All(ordinal => ordinal == table.RowidAliasColumnIndex
                || index.Columns.Any(column => column.ColumnIndex == ordinal));
        var info = new TursoIndexInfo(
            index.Unique,
            index.Columns.Count,
            covering,
            TursoCostModel.IndexLeafRowsPerPage(index.Columns.Count, table.Columns.Length, table.HasRowidAlias));
        var rows = EstimateTursoTableRows(source.Name, context);
        var cost = EstimateTursoScanOrSeekCost(info, terms, 1.0, rows, table, index, context);
        var rowsPerSeek = EstimateTursoRowsPerSeek(info, terms, rows, table, index, context);
        var access = new SemiAntiInnerAccess(
            SemiAntiInnerAccessKind.DeclaredIndexSearch,
            source,
            table,
            index,
            terms,
            HashKeys: [],
            covering,
            InputRows: 1.0,
            rowsPerSeek,
            cost);
        if (!access.ConstraintTexts.SequenceEqual(constraintTexts, StringComparer.OrdinalIgnoreCase))
            return null;

        // constraint_output_multipliers: the constraints the search does not consume still
        // filter its rows, a column bounded on both sides as one closed range.
        var remaining = 1.0;
        var bounds = new Dictionary<int, (bool Lower, bool Upper)>();
        foreach (var constraint in constraints)
        {
            if (terms.Any(term => ReferenceEquals(term.Equality, constraint)
                    || ReferenceEquals(term.Lower, constraint)
                    || ReferenceEquals(term.Upper, constraint)))
            {
                continue;
            }

            remaining *= constraint.Selectivity;
            var isLower = constraint.Operator is TursoConstraintOperator.Greater or TursoConstraintOperator.GreaterOrEqual;
            var isUpper = constraint.Operator is TursoConstraintOperator.Less or TursoConstraintOperator.LessOrEqual;
            if (isLower || isUpper)
            {
                bounds.TryGetValue(constraint.ColumnOrdinal, out var bound);
                bounds[constraint.ColumnOrdinal] = (bound.Lower || isLower, bound.Upper || isUpper);
            }
        }

        foreach (var bound in bounds.Values)
        {
            if (bound.Lower && bound.Upper)
                remaining *= TursoCostParams.ClosedRangeSelectivityFactor;
        }

        var accessCost = cost + EstimateSemiAntiWhereWork(access, steps, 1.0);
        var output = rowsPerSeek * remaining;
        return new EqpJsonEstimate(1, output, output, accessCost, accessCost);
    }

    /// <summary>
    /// The estimate of a first loop that reads a whole declared index in key order (a SCAN …
    /// USING INDEX serving ORDER BY or GROUP BY): estimate_cost_for_scan_or_seek with no seek
    /// terms plus the WHERE work of every filter over each visited row, producing the rows the
    /// table's own column constraints keep. Returns <see langword="null"/> for a filter shape
    /// the ported constraint model does not describe.
    /// </summary>
    private EqpJsonEstimate? EstimateTursoFirstLoopIndexScan(
        NamedTableSource source,
        EmbeddedIndex index,
        Expression? predicate,
        IReadOnlyCollection<int> usedColumns,
        bool isIndexOrdered,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(source.Name, out var table) || index.IsPartial || index.IsMethodIndex)
            return null;

        var steps = 0;
        if (predicate is not null)
        {
            if (ContainsSubqueryExpression(predicate) || GetImpliedOrInFilters(predicate, source, table).Count != 0)
                return null;
            var qualifier = source.Alias ?? source.Name;
            var columns = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
            foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(predicate))
            {
                steps += CountTursoWhereSteps(conjunct) - 1;
                if (conjunct is BinaryExpression { Operator: BinaryOperator.Or })
                    continue;
                if (!TryDescribeSemiAntiConstraint(0, conjunct, table, qualifier, columns, source, table, context, out var constraint)
                    || constraint.DependsOnOuter)
                {
                    return null;
                }
            }
        }

        var covering = index.Columns.All(static column => !column.IsExpression)
            && usedColumns.All(ordinal => ordinal == table.RowidAliasColumnIndex
                || index.Columns.Any(column => column.ColumnIndex == ordinal));
        var info = new TursoIndexInfo(
            index.Unique,
            index.Columns.Count,
            covering,
            TursoCostModel.IndexLeafRowsPerPage(index.Columns.Count, table.Columns.Length, table.HasRowidAlias));
        var rows = EstimateTursoTableRows(source.Name, context);
        var cost = EstimateTursoScanOrSeekCost(info, [], 1.0, rows, table, index, context, isIndexOrdered)
            + TursoCostModel.EstimateWhereWork(1.0, rows, consumedSteps: 0, steps);
        var output = rows * EstimateTursoLocalSelectivity(predicate, source, table, context);
        return new EqpJsonEstimate(1, output, output, cost, cost);
    }

    /// <summary>
    /// The estimate of a multi-index OR union that is the whole WHERE clause
    /// (multi_index.rs:195-235 estimate_multi_index_scan_cost with its branches costed by
    /// choose_multi_index_branch_access): every branch seeks its constraints with a
    /// rowid-only (covering) read, the union deduplicates rowids and fetches the distinct
    /// rows, and the consumed OR term's WHERE work is charged per output row. A branch with
    /// residual conjuncts, or a WHERE with other terms, reports no estimate.
    /// </summary>
    private EqpJsonEstimate? EstimateTursoMultiIndexOrUnion(
        NamedTableSource source,
        EmbeddedTable table,
        Expression where,
        IReadOnlyList<(EmbeddedIndex? Index, Expression Predicate)> branches,
        QueryContext context)
    {
        if (IndexExpressionSemantics.SplitConjuncts(where).Count != 1
            || where is not BinaryExpression { Operator: BinaryOperator.Or }
            || ContainsSubqueryExpression(where))
        {
            return null;
        }

        var qualifier = source.Alias ?? source.Name;
        var columns = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
        var rows = EstimateTursoTableRows(source.Name, context);
        var treeDepth = TursoCostModel.EstimateBtreeDepth(rows, JoinCostParams.RowsPerTablePage);
        var branchCosts = new List<double>(branches.Count);
        var branchRows = new List<double>(branches.Count);
        foreach (var (index, predicate) in branches)
        {
            var constraints = new List<SemiAntiConstraint>();
            var conjuncts = IndexExpressionSemantics.SplitConjuncts(predicate);
            for (var position = 0; position < conjuncts.Count; position++)
            {
                if (!TryDescribeSemiAntiConstraint(position, conjuncts[position], table, qualifier, columns, source, table, context, out var constraint)
                    || constraint.DependsOnOuter)
                {
                    return null;
                }

                constraints.Add(constraint);
            }

            if (index is null)
            {
                // The rowid branch: a unique point lookup on the table b-tree
                // (index_info_for_branch with no index).
                if (constraints is not [{ Operator: TursoConstraintOperator.Equal } rowid]
                    || rowid.ColumnOrdinal != table.RowidAliasColumnIndex
                    || !table.HasRowidAlias)
                {
                    return null;
                }

                branchCosts.Add(TursoCostModel.EstimateIndexCost(
                    rows,
                    treeDepth,
                    new TursoIndexInfo(Unique: true, ColumnCount: 1, Covering: true, JoinCostParams.RowsPerTablePage),
                    1.0,
                    1.0));
                branchRows.Add(1.0);
                continue;
            }

            constraints = [.. constraints.Where(static c => IsTursoEqualityOperator(c.Operator)),
                .. constraints.Where(static c => !IsTursoEqualityOperator(c.Operator))];
            var terms = BuildSemiAntiIndexSeekTerms(index, table, constraints);
            var consumed = terms.Sum(static term => term.Equality is not null ? 1 : (term.Lower is null ? 0 : 1) + (term.Upper is null ? 0 : 1));
            if (terms.Count == 0 || consumed != constraints.Count)
                return null;

            var info = new TursoIndexInfo(
                index.Unique,
                index.Columns.Count,
                Covering: true,
                TursoCostModel.IndexLeafRowsPerPage(index.Columns.Count, table.Columns.Length, table.HasRowidAlias));
            branchCosts.Add(EstimateTursoScanOrSeekCost(info, terms, 1.0, rows, table, index, context));
            branchRows.Add(EstimateTursoRowsPerSeek(info, terms, rows, table, index, context));
        }

        var uniqueRatio = 1.0;
        foreach (var branch in branchRows)
            uniqueRatio *= 1.0 - Math.Min(branch / rows, 1.0);
        var uniqueRows = rows * (1.0 - uniqueRatio);
        var rowsetCost = branchRows.Sum() * JoinCostParams.CpuCostPerRow * 2.0;
        var tablePages = Math.Max(rows / JoinCostParams.RowsPerTablePage, 1.0);
        var fetchCost = uniqueRows / Math.Max(rows, 1.0) * tablePages;
        var cost = branchCosts.Sum() + rowsetCost + fetchCost
            + TursoCostModel.EstimateWhereWork(1.0, uniqueRows, CountTursoWhereSteps(where) - 1, remainingSteps: 0);
        return new EqpJsonEstimate(1, uniqueRows, uniqueRows, cost, cost);
    }

    /// <summary>
    /// The estimate of a first loop driven by an uncorrelated <c>column IN (SELECT …)</c> seek
    /// (access_method.rs:505-645 choose_best_in_seek_candidate, constraints.rs:1068-1160): the
    /// list contributes its body's estimated rows, capped at the square root of the table's
    /// rows (Turso's in_subquery_rows when the body has no estimate), one index seek per value,
    /// and the rows that selectivity keeps.
    /// </summary>
    private static EqpJsonEstimate EstimateTursoInSubquerySeek(
        NamedTableSource source,
        EmbeddedTable table,
        EmbeddedIndex index,
        double? listRows,
        bool covering,
        QueryContext context)
    {
        var rows = EstimateTursoTableRows(source.Name, context);
        var values = listRows is { } planned
            ? Math.Clamp(planned, 0.0, Math.Max(Math.Sqrt(rows), 1.0))
            : Math.Min(TursoCostParams.InSubqueryRows, rows);
        var selectivity = Math.Min(values / rows, 1.0);
        var info = new TursoIndexInfo(
            index.Unique,
            index.Columns.Count,
            covering,
            TursoCostModel.IndexLeafRowsPerPage(index.Columns.Count, table.Columns.Length, table.HasRowidAlias));
        var rowsPerSeek = index.Unique && index.Columns.Count == 1
            ? 1.0
            : Math.Max(Math.Sqrt(rows * JoinCostParams.SelectivityEqualityIndexed), 1.0);
        var cost = TursoCostModel.EstimateIndexCost(
            rows,
            TursoCostModel.EstimateBtreeDepth(rows, JoinCostParams.RowsPerTablePage),
            info,
            values,
            rowsPerSeek);
        var output = Math.Max(selectivity * rows, 1.0);
        return new EqpJsonEstimate(1, output, output, cost, cost);
    }

    /// <summary>
    /// The ordinals of every column of <paramref name="source"/> the statement reads, the input
    /// to Turso's covering-index test. Returns <see langword="false"/> for a <c>*</c>
    /// projection, a subquery, or an unqualified reference that could name this table.
    /// </summary>
    private static bool TryCollectReferencedTableColumns(
        SelectStatement select,
        NamedTableSource source,
        EmbeddedTable table,
        out HashSet<int> ordinals)
    {
        var found = new HashSet<int>();
        ordinals = found;
        var qualifier = source.Alias ?? source.Name;
        foreach (var projection in select.Projections)
        {
            // * reads every column of every source; q.* every column of q.
            if (projection.Expression is StarExpression
                || projection.Expression is QualifiedStarExpression qualified
                    && string.Equals(qualified.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                for (var ordinal = 0; ordinal < table.Columns.Length; ordinal++)
                    found.Add(ordinal);
            }
        }

        var roots = new List<Expression>();
        roots.AddRange(select.Projections.Select(static projection => projection.Expression));
        if (select.Where is not null)
            roots.Add(select.Where);
        roots.AddRange(select.GroupBy);
        if (select.Having is not null)
            roots.Add(select.Having);
        roots.AddRange(select.OrderBy.Select(static term => term.Expression));
        var pendingSources = new Stack<TableSource?>();
        pendingSources.Push(select.Source);
        while (pendingSources.Count > 0)
        {
            if (pendingSources.Pop() is JoinTableSource join)
            {
                if (join.Condition is not null)
                    roots.Add(join.Condition);
                pendingSources.Push(join.Left);
                pendingSources.Push(join.Right);
            }
        }

        // In a single-table statement an unqualified name can only mean this table.
        var soleSource = ReferenceEquals(select.Source, source);
        foreach (var root in roots)
        {
            if (ContainsSubqueryExpression(root))
                return false;
            if (!ForEachColumnReference(root, column =>
                {
                    var name = column.UnqualifiedName ?? column.Name;
                    if (column.Qualifier is null && soleSource)
                    {
                        if (table.TryGetColumnIndex(name, out var soleOrdinal))
                            found.Add(soleOrdinal);
                        return true;
                    }

                    if (column.Qualifier is null)
                        return !table.TryGetColumnIndex(name, out _) && !IsRowIdAlias(name);
                    if (!string.Equals(column.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (table.TryGetColumnIndex(name, out var ordinal))
                    {
                        found.Add(ordinal);
                        return true;
                    }

                    return IsRowIdAlias(name);
                }))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The left anti hash join alternative (join.rs:586-1000 with access_method.rs:1294-1537),
    /// restricted to what the managed executor runs: the build side is the plain outer table
    /// read by a full scan, and every hash key compares an inner column with an outer
    /// expression under a built-in collation.
    /// </summary>
    private SemiAntiInnerAccess? TryPlanSemiAntiHashAnti(
        JoinTableSource join,
        NamedTableSource buildSource,
        EmbeddedTable buildTable,
        Expression? leftPredicate,
        NamedTableSource probeSource,
        EmbeddedTable probeTable,
        IReadOnlyList<SemiAntiConstraint> constraints,
        IReadOnlyList<EmbeddedIndex> probeIndexes,
        SemiAntiInnerAccess bestProbeAccess,
        double probeRows,
        double inputRows,
        QueryContext context)
    {
        // should_not_use_hash_join: both sides need a rowid, a self-join keeps the nested loop,
        // and INDEXED BY / NOT INDEXED on either side is honoured.
        if (!buildTable.HasRowid
            || !probeTable.HasRowid
            || ReferenceEquals(buildTable, probeTable)
            || buildSource.IndexDirective is not null
            || probeSource.IndexDirective is not null)
        {
            return null;
        }

        // A selective declared-index probe seek is preferred (rhs_has_selective_seek).
        if (bestProbeAccess.Kind == SemiAntiInnerAccessKind.DeclaredIndexSearch)
            return null;

        // The build read must be a plain table scan; a filtered index read would need the
        // materialized build input this executor does not implement.
        if (leftPredicate is not null
            && TryPlanManagedIndexScan(
                new SelectStatement(
                    Distinct: false,
                    Projections: [],
                    Source: buildSource,
                    Where: leftPredicate,
                    GroupBy: [],
                    Having: null,
                    NamedWindows: [],
                    OrderBy: [],
                    Limit: null,
                    Offset: null),
                context) is not null)
        {
            return null;
        }

        var keys = constraints
            .Where(constraint => constraint.Operator == TursoConstraintOperator.Equal
                && constraint.DependsOnOuter
                && !constraint.UsesCustomCollation
                && IsHashableJoinKeyCollation(constraint.ComparisonCollation)
                && !IsUnsafeCompiledCollation(constraint.ComparisonCollation)
                && IsSemiAntiHashableOuterValue(constraint.Value))
            .ToArray();
        if (keys.Length == 0)
            return null;

        // probe_index_can_seek_join_key: an index that can seek the probe join key wins.
        foreach (var key in keys)
        {
            if (probeIndexes.Any(index => BuildSemiAntiIndexSeekTerms(index, probeTable, constraints)
                    .Any(term => ReferenceEquals(term.Equality, key))))
            {
                return null;
            }
        }

        // A plain build column that an index could seek keeps the index path unless the
        // probe side has no constant filter the hash would have to give up
        // (can_replace_build_index_with_hash, access_method.rs:1360-1389).
        var hashCanReplaceBuildIndex = !constraints.Any(static constraint => !constraint.DependsOnOuter);
        if (!hashCanReplaceBuildIndex)
        {
            foreach (var key in keys)
            {
                if (key.Value is ColumnExpression { BooleanKeyword: null } buildColumn
                    && buildTable.TryGetColumnIndex(buildColumn.UnqualifiedName ?? buildColumn.Name, out var buildOrdinal)
                    && (buildTable.HasRowidAlias && buildTable.RowidAliasColumnIndex == buildOrdinal
                        || buildTable.Indexes.Any(index => !index.IsMethodIndex
                            && index.Columns.Any(column => !column.IsExpression && column.ColumnIndex == buildOrdinal))))
                {
                    return null;
                }
            }
        }

        var buildRows = EstimateTursoTableRows(buildSource.Name, context)
            * EstimateTursoLocalSelectivity(leftPredicate, buildSource, buildTable, context);
        const double rowsPerOuterRow = 1.0; // HashJoinType::LeftAnti (access_method.rs:1416)
        var cost = TursoCostModel.EstimateHashJoinCost(
            buildRows,
            probeRows,
            rowsVisited: buildRows * rowsPerOuterRow,
            keepsUnmatchedBuildRows: true,
            probeMultiplier: 1.0);
        return new SemiAntiInnerAccess(
            SemiAntiInnerAccessKind.HashAnti,
            probeSource,
            probeTable,
            Index: null,
            SeekTerms: [],
            keys,
            Covering: false,
            inputRows,
            rowsPerOuterRow,
            cost);
    }

    /// <summary>
    /// An outer hash-key expression the managed hash build can canonicalize the same way the
    /// transient equality lookup does: a column, or an expression without CAST/COLLATE, and
    /// never a function call or subquery.
    /// </summary>
    private static bool IsSemiAntiHashableOuterValue(Expression value)
    {
        var stripped = value;
        while (stripped is UnaryExpression { Operator: UnaryOperator.Plus } unary)
            stripped = unary.Operand;
        if (stripped is ColumnExpression { BooleanKeyword: null })
            return true;

        return !ContainsCastOrCollate(stripped) && IsScanConstantExpression(stripped, []);
    }

    /// <summary>cost_with_where_work (access_method.rs:687-707) for the inner loop.</summary>
    private static double EstimateSemiAntiWhereWork(
        SemiAntiInnerAccess access,
        int[] steps,
        double inputRows)
    {
        var consumed = new HashSet<int>();
        foreach (var term in access.SeekTerms)
        {
            if (term.Equality is { } equality)
                consumed.Add(equality.ConjunctIndex);
            if (term.Lower is { } lower)
                consumed.Add(lower.ConjunctIndex);
            if (term.Upper is { } upper)
                consumed.Add(upper.ConjunctIndex);
        }

        foreach (var key in access.HashKeys)
            consumed.Add(key.ConjunctIndex);

        var consumedSteps = 0;
        var remainingSteps = 0;
        for (var index = 0; index < steps.Length; index++)
        {
            if (consumed.Contains(index))
                consumedSteps += steps[index];
            else
                remainingSteps += steps[index];
        }

        return TursoCostModel.EstimateWhereWork(inputRows, access.RowsPerOuterRow, consumedSteps, remainingSteps);
    }

    /// <summary>cost.rs:19-50 <c>where_expr_steps</c>.</summary>
    private static int CountTursoWhereSteps(Expression expression)
    {
        var steps = 0;
        var pending = new Stack<Expression>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case ColumnExpression:
                case LiteralExpression:
                case ParameterExpression:
                    break;
                case CollationExpression collation:
                    pending.Push(collation.Expression);
                    break;
                case BetweenExpression between:
                    steps += 2;
                    pending.Push(between.Value);
                    pending.Push(between.Lower);
                    pending.Push(between.Upper);
                    break;
                case InExpression inList:
                    steps += Math.Max(inList.Values.Count, 1);
                    pending.Push(inList.Value);
                    foreach (var value in inList.Values)
                        pending.Push(value);
                    break;
                case CaseExpression caseExpression:
                    steps += Math.Max(caseExpression.Clauses.Count, 1);
                    if (caseExpression.Operand is not null)
                        pending.Push(caseExpression.Operand);
                    foreach (var clause in caseExpression.Clauses)
                    {
                        pending.Push(clause.When);
                        pending.Push(clause.Then);
                    }

                    if (caseExpression.Else is not null)
                        pending.Push(caseExpression.Else);
                    break;
                case BinaryExpression binary:
                    steps++;
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;
                case UnaryExpression unary:
                    steps++;
                    pending.Push(unary.Operand);
                    break;
                case CastExpression cast:
                    steps++;
                    pending.Push(cast.Expression);
                    break;
                case LikeExpression like:
                    steps++;
                    pending.Push(like.Value);
                    pending.Push(like.Pattern);
                    if (like.Escape is not null)
                        pending.Push(like.Escape);
                    break;
                case FunctionExpression function:
                    steps++;
                    foreach (var argument in function.Arguments)
                        pending.Push(argument);
                    break;
                default:
                    steps++;
                    break;
            }
        }

        return Math.Max(steps, 1);
    }

    private static bool IsTursoEqualityOperator(TursoConstraintOperator op)
        => op is TursoConstraintOperator.Equal or TursoConstraintOperator.Is;

    /// <summary>
    /// Describes one conjunct as a constraint on an inner column
    /// (constraints_from_where_clause, constraints.rs:732-993): a direct comparison with a
    /// bare inner column on one side and an expression that reads no inner column on the
    /// other.
    /// </summary>
    private bool TryDescribeSemiAntiConstraint(
        int conjunctIndex,
        Expression conjunct,
        EmbeddedTable table,
        string innerQualifier,
        HashSet<string> innerColumns,
        NamedTableSource outerLeaf,
        EmbeddedTable outerTable,
        QueryContext context,
        out SemiAntiConstraint constraint)
    {
        constraint = null!;
        if (conjunct is not BinaryExpression
            {
                Operator: BinaryOperator.Equal
                    or BinaryOperator.Is
                    or BinaryOperator.NotEqual
                    or BinaryOperator.IsNot
                    or BinaryOperator.LessThan
                    or BinaryOperator.LessThanOrEqual
                    or BinaryOperator.GreaterThan
                    or BinaryOperator.GreaterThanOrEqual,
            } binary)
        {
            return false;
        }

        // `x IS TRUE` / `x IS FALSE` is a truth test, not a comparison (constraints.rs:743-752).
        if (binary.Operator is BinaryOperator.Is or BinaryOperator.IsNot
            && (binary.Right is ColumnExpression { BooleanKeyword: not null }
                || binary.Left is ColumnExpression { BooleanKeyword: not null }))
        {
            return false;
        }

        Expression columnSide;
        Expression value;
        bool columnOnLeft;
        if (IsBareInnerColumn(binary.Left) && !ReferencesInner(binary.Right))
        {
            columnSide = binary.Left;
            value = binary.Right;
            columnOnLeft = true;
        }
        else if (IsBareInnerColumn(binary.Right) && !ReferencesInner(binary.Left))
        {
            columnSide = binary.Right;
            value = binary.Left;
            columnOnLeft = false;
        }
        else
        {
            return false;
        }

        var column = (ColumnExpression)columnSide;
        if (!table.TryGetColumnIndex(column.UnqualifiedName ?? column.Name, out var ordinal))
            return false;

        var op = binary.Operator switch
        {
            BinaryOperator.Equal => TursoConstraintOperator.Equal,
            BinaryOperator.Is => TursoConstraintOperator.Is,
            BinaryOperator.NotEqual => TursoConstraintOperator.NotEqual,
            BinaryOperator.IsNot => TursoConstraintOperator.IsNot,
            BinaryOperator.LessThan => columnOnLeft ? TursoConstraintOperator.Less : TursoConstraintOperator.Greater,
            BinaryOperator.LessThanOrEqual => columnOnLeft ? TursoConstraintOperator.LessOrEqual : TursoConstraintOperator.GreaterOrEqual,
            BinaryOperator.GreaterThan => columnOnLeft ? TursoConstraintOperator.Greater : TursoConstraintOperator.Less,
            _ => columnOnLeft ? TursoConstraintOperator.GreaterOrEqual : TursoConstraintOperator.LessOrEqual,
        };

        var dependsOnOuter = false;
        ForEachColumnReference(value, _ =>
        {
            dependsOnOuter = true;
            return true;
        });

        // An IS whose value may be NULL matches NULL keys (Constraint::null_matching).
        var nullMatching = op == TursoConstraintOperator.Is
            && value is not LiteralExpression { Value.Kind: not SqlValueKind.Null };
        var selectivityOperator = op == TursoConstraintOperator.Is && !nullMatching
            ? TursoConstraintOperator.Equal
            : op;
        var selectivity = EstimateTursoColumnSelectivity(table, ordinal, selectivityOperator, context);

        var columnDefinition = table.ColumnDefinitions[ordinal];
        var outerDefinition = ResolveSemiAntiOuterColumnDefinition(value, outerLeaf, outerTable);
        var leftExplicit = GetExplicitCollation(binary.Left);
        var rightExplicit = GetExplicitCollation(binary.Right);
        var columnDeclared = NormalizeDeclaredCollation(columnDefinition.Collation) ?? "BINARY";
        var valueDeclared = outerDefinition is null
            ? null
            : NormalizeDeclaredCollation(outerDefinition.Collation) ?? "BINARY";
        var collation = (leftExplicit
            ?? rightExplicit
            ?? (columnOnLeft ? columnDeclared : valueDeclared)
            ?? (columnOnLeft ? valueDeclared : columnDeclared)
            ?? "BINARY").ToUpperInvariant();

        var columnAffinity = GetJoinKeyAffinity(columnDefinition);
        var valueAffinity = value is CastExpression cast
            ? EmbeddedTable.GetAffinity(cast.TypeName)
            : GetJoinKeyAffinity(outerDefinition);
        var comparisonAffinity = (columnAffinity, valueAffinity) switch
        {
            ({ } left, { } right) => IsNumericAffinity(left) || IsNumericAffinity(right)
                ? ColumnAffinity.Numeric
                : ColumnAffinity.Blob,
            ({ } left, null) => left,
            (null, { } right) => right,
            _ => ColumnAffinity.Blob,
        };
        var affinityOk = comparisonAffinity switch
        {
            ColumnAffinity.Blob => true,
            ColumnAffinity.Text => columnAffinity == ColumnAffinity.Text,
            _ => IsNumericAffinity(columnAffinity),
        };

        constraint = new SemiAntiConstraint(
            conjunctIndex,
            conjunct,
            ordinal,
            op,
            columnSide,
            value,
            dependsOnOuter,
            nullMatching,
            selectivity,
            collation,
            affinityOk,
            UsesCustomCollation: (leftExplicit is not null && !IsBuiltInCollation(leftExplicit))
                || (rightExplicit is not null && !IsBuiltInCollation(rightExplicit)));
        return true;

        bool IsBareInnerColumn(Expression expression)
            => expression is ColumnExpression { BooleanKeyword: null } bare
                && IsInnerColumnReference(bare, innerQualifier, innerColumns);

        bool ReferencesInner(Expression expression)
        {
            var referencesInner = false;
            var modelled = ForEachColumnReference(expression, reference =>
            {
                if (IsInnerColumnReference(reference, innerQualifier, innerColumns))
                    referencesInner = true;
                return true;
            });
            return referencesInner || !modelled;
        }

        static string? GetExplicitCollation(Expression expression)
            => expression is CollationExpression collated ? collated.Name.ToUpperInvariant() : null;
    }

    /// <summary>The declared column an outer operand names, when it names one directly.</summary>
    private static EmbeddedColumn? ResolveSemiAntiOuterColumnDefinition(
        Expression value,
        NamedTableSource outerLeaf,
        EmbeddedTable outerTable)
    {
        while (value is UnaryExpression { Operator: UnaryOperator.Plus } unary)
            value = unary.Operand;
        while (value is CollationExpression collation)
            value = collation.Expression;
        if (value is not ColumnExpression { BooleanKeyword: null } column)
            return null;
        if (column.Qualifier is { } qualifier
            && !string.Equals(qualifier, outerLeaf.Alias ?? outerLeaf.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return outerTable.TryGetColumnIndex(column.UnqualifiedName ?? column.Name, out var ordinal)
            ? outerTable.ColumnDefinitions[ordinal]
            : null;
    }

    /// <summary>
    /// Whether a constraint may key an index column: equality or range, a collation that
    /// matches the column (constraints.rs:1180-1214), and <c>sqlite3IndexAffinityOk</c>.
    /// </summary>
    private static bool CanSemiAntiConstraintSeek(SemiAntiConstraint constraint, EmbeddedTable table)
        => constraint.IndexAffinityOk
            && string.Equals(
                constraint.ComparisonCollation,
                NormalizeDeclaredCollation(table.ColumnDefinitions[constraint.ColumnOrdinal].Collation) ?? "BINARY",
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// usable_constraints_for_lhs_mask (constraints.rs:1467-1586) over a declared index: a
    /// contiguous equality prefix followed by at most one range column.
    /// </summary>
    private static IReadOnlyList<SemiAntiSeekTerm> BuildSemiAntiIndexSeekTerms(
        EmbeddedIndex index,
        EmbeddedTable table,
        IReadOnlyList<SemiAntiConstraint> constraints)
    {
        var terms = new List<SemiAntiSeekTerm>();
        foreach (var indexColumn in index.Columns)
        {
            if (indexColumn.IsExpression)
                break;
            var indexCollation = (indexColumn.Collation
                ?? NormalizeDeclaredCollation(table.ColumnDefinitions[indexColumn.ColumnIndex].Collation)
                ?? "BINARY").ToUpperInvariant();
            var tableCollation = (NormalizeDeclaredCollation(table.ColumnDefinitions[indexColumn.ColumnIndex].Collation)
                ?? "BINARY").ToUpperInvariant();
            if (!string.Equals(indexCollation, tableCollation, StringComparison.Ordinal))
                break;

            if (!TryBuildSemiAntiSeekTerm(indexColumn.ColumnIndex, indexColumn.Name, table, constraints, out var term))
                break;
            terms.Add(term);
            if (term.Equality is null)
                break;
        }

        return terms;
    }

    /// <summary>
    /// The automatic-index key (automatic_index_terms / ordered_ephemeral_key_columns,
    /// constraints.rs:292-323, 1608-1637): equality columns in table order, then range-only
    /// columns, consumed with the same prefix rule as a declared index.
    /// </summary>
    private static IReadOnlyList<SemiAntiSeekTerm> BuildSemiAntiEphemeralSeekTerms(
        EmbeddedTable table,
        IReadOnlyList<SemiAntiConstraint> constraints)
    {
        var usable = constraints
            .Where(constraint => !constraint.UsesCustomCollation && CanSemiAntiConstraintSeek(constraint, table))
            .ToArray();
        var equalityColumns = usable
            .Where(static constraint => IsTursoEqualityOperator(constraint.Operator))
            .Select(static constraint => constraint.ColumnOrdinal)
            .Distinct()
            .Order()
            .ToArray();
        var rangeColumns = usable
            .Where(static constraint => constraint.Operator is TursoConstraintOperator.Less
                or TursoConstraintOperator.LessOrEqual
                or TursoConstraintOperator.Greater
                or TursoConstraintOperator.GreaterOrEqual)
            .Select(static constraint => constraint.ColumnOrdinal)
            .Where(ordinal => !equalityColumns.Contains(ordinal))
            .Distinct()
            .Order();

        var terms = new List<SemiAntiSeekTerm>();
        foreach (var ordinal in equalityColumns.Concat(rangeColumns))
        {
            if (!TryBuildSemiAntiSeekTerm(ordinal, table.Columns[ordinal], table, usable, out var term))
                break;
            terms.Add(term);
            if (term.Equality is null)
                break;
        }

        return terms;
    }

    private static bool TryBuildSemiAntiSeekTerm(
        int ordinal,
        string columnName,
        EmbeddedTable table,
        IReadOnlyList<SemiAntiConstraint> constraints,
        out SemiAntiSeekTerm term)
    {
        SemiAntiConstraint? equality = null;
        SemiAntiConstraint? lower = null;
        SemiAntiConstraint? upper = null;
        foreach (var constraint in constraints)
        {
            if (constraint.ColumnOrdinal != ordinal || !CanSemiAntiConstraintSeek(constraint, table))
                continue;

            switch (constraint.Operator)
            {
                case TursoConstraintOperator.Equal or TursoConstraintOperator.Is:
                    equality ??= constraint;
                    break;
                case TursoConstraintOperator.Greater or TursoConstraintOperator.GreaterOrEqual:
                    lower ??= constraint;
                    break;
                case TursoConstraintOperator.Less or TursoConstraintOperator.LessOrEqual:
                    upper ??= constraint;
                    break;
            }
        }

        term = equality is not null
            ? new SemiAntiSeekTerm(ordinal, columnName, equality, null, null)
            : new SemiAntiSeekTerm(ordinal, columnName, null, lower, upper);
        return equality is not null || lower is not null || upper is not null;
    }

    /// <summary>cost.rs:461-519 <c>estimate_cost_for_scan_or_seek</c> for a declared index.</summary>
    private static double EstimateTursoScanOrSeekCost(
        TursoIndexInfo info,
        IReadOnlyList<SemiAntiSeekTerm> terms,
        double inputRows,
        double baseRows,
        EmbeddedTable table,
        EmbeddedIndex index,
        QueryContext context,
        bool isIndexOrdered = false)
    {
        var treeDepth = TursoCostModel.EstimateBtreeDepth(baseRows, JoinCostParams.RowsPerTablePage);
        if (IsTursoUniquePointLookup(info, terms))
            return TursoCostModel.EstimateIndexCost(baseRows, treeDepth, info, inputRows, 1.0);

        var rowsPerSeek = EstimateTursoRowsPerSeek(info, terms, baseRows, table, index, context);
        var cost = TursoCostModel.EstimateIndexCost(baseRows, treeDepth, info, inputRows, rowsPerSeek);
        // A non-covering full index scan that no ORDER BY needs pays for its table lookups.
        return !info.Covering && terms.Count == 0 && !isIndexOrdered ? cost * 2.0 : cost;
    }

    /// <summary>cost.rs:252-264 <c>is_unique_point_lookup</c>.</summary>
    private static bool IsTursoUniquePointLookup(TursoIndexInfo info, IReadOnlyList<SemiAntiSeekTerm> terms)
    {
        var equalityCount = 0;
        foreach (var term in terms)
        {
            if (term.Equality is not { NullMatching: false })
                break;
            equalityCount++;
        }

        return info.Unique && equalityCount >= info.ColumnCount;
    }

    /// <summary>cost.rs:325-407 <c>estimate_rows_per_seek</c>.</summary>
    private static double EstimateTursoRowsPerSeek(
        TursoIndexInfo info,
        IReadOnlyList<SemiAntiSeekTerm> terms,
        double baseRows,
        EmbeddedTable table,
        EmbeddedIndex? index,
        QueryContext context)
    {
        var equalityPrefix = terms.TakeWhile(static term => term.Equality is not null).ToArray();
        var joinProbeConstantSelectivity = equalityPrefix.Any(static term => term.Equality!.DependsOnOuter)
            ? equalityPrefix
                .Where(static term => !term.Equality!.DependsOnOuter)
                .Aggregate(1.0, static (product, term) => product * term.Equality!.Selectivity)
            : 1.0;
        if (IsTursoUniquePointLookup(info, terms))
            return joinProbeConstantSelectivity;

        if (index is not null
            && equalityPrefix.Length > 0
            && equalityPrefix.All(static term => !term.Equality!.NullMatching)
            && TryGetSqliteStat1PrefixAverage(context, table.Name, index.Name, equalityPrefix.Length, out var prefixAverage))
        {
            var rangeSelectivity = terms
                .Skip(equalityPrefix.Length)
                .Aggregate(1.0, static (product, term) => product
                    * (term.Lower?.Selectivity ?? 1.0)
                    * (term.Upper?.Selectivity ?? 1.0));
            return Math.Max(prefixAverage * rangeSelectivity, 1.0) * joinProbeConstantSelectivity;
        }

        var multiplier = terms.Aggregate(1.0, static (product, term) => product * (term.Equality is { } equality
            ? equality.Selectivity
            : (term.Lower?.Selectivity ?? 1.0) * (term.Upper?.Selectivity ?? 1.0)));
        return Math.Max(multiplier * baseRows, 1.0) * joinProbeConstantSelectivity;
    }

    /// <summary>
    /// The row estimate the planner starts from (optimizer/mod.rs:2064-2082
    /// <c>base_row_estimate</c>): the <c>sqlite_stat1</c> row count, else Turso's
    /// 1,000,000-row fallback.
    /// </summary>
    private static double EstimateTursoTableRows(string tableName, QueryContext context)
        => TryGetSqliteStat1TableRowCount(context, tableName, out var rows)
            ? rows
            : JoinCostParams.RowsPerTableFallback;

    /// <summary>
    /// constraints.rs:367-437 <c>estimate_selectivity</c> (with
    /// <c>selectivity_index_for_column</c>, constraints.rs:470-501) for a column constraint.
    /// </summary>
    private static double EstimateTursoColumnSelectivity(
        EmbeddedTable table,
        int ordinal,
        TursoConstraintOperator op,
        QueryContext context)
    {
        switch (op)
        {
            case TursoConstraintOperator.Equal:
                {
                    var hasStats = TryGetSqliteStat1TableRowCount(context, table.Name, out var rowCount);
                    var uniqueSelectivity = hasStats && rowCount > 0
                        ? 1.0 / rowCount
                        : 1.0 / JoinCostParams.RowsPerTableFallback;
                    if (table.HasRowidAlias && table.RowidAliasColumnIndex == ordinal)
                        return uniqueSelectivity;
                    if (table.ColumnDefinitions[ordinal].PrimaryKey)
                        return uniqueSelectivity;

                    var index = table.Indexes.FirstOrDefault(candidate =>
                        !candidate.IsMethodIndex
                        && candidate.Columns.Count > 0
                        && !candidate.Columns[0].IsExpression
                        && candidate.Columns[0].ColumnIndex == ordinal
                        && (candidate.Unique && candidate.Columns.Count == 1
                            || !hasStats
                            || TryGetSqliteStat1PrefixAverage(context, table.Name, candidate.Name, 1, out _)));
                    if (index is null)
                        return JoinCostParams.SelectivityEqualityUnindexed;
                    if (index.Unique && index.Columns.Count == 1)
                        return uniqueSelectivity;
                    if (!hasStats)
                        return JoinCostParams.SelectivityEqualityIndexed;
                    if (TryGetSqliteStat1IndexTotal(context, table.Name, index.Name, out var total)
                        && total > 0
                        && TryGetSqliteStat1PrefixAverage(context, table.Name, index.Name, 1, out var average))
                    {
                        return (double)average / total;
                    }

                    return JoinCostParams.SelectivityEqualityUnindexed;
                }

            case TursoConstraintOperator.Less
                or TursoConstraintOperator.LessOrEqual
                or TursoConstraintOperator.Greater
                or TursoConstraintOperator.GreaterOrEqual:
                return JoinCostParams.SelectivityRange;
            case TursoConstraintOperator.Is:
                return TursoCostParams.SelectivityIsNull;
            case TursoConstraintOperator.IsNot:
                return TursoCostParams.SelectivityIsNotNull;
            default:
                return JoinCostParams.SelectivityOther;
        }
    }

    /// <summary>The first number of an index's <c>sqlite_stat1</c> row (its total rows).</summary>
    private static bool TryGetSqliteStat1IndexTotal(
        QueryContext context,
        string tableName,
        string indexName,
        out long total)
    {
        total = 0;
        return TryFindSqliteStat1Row(context, tableName, indexName, preferAnyIndex: false, out var parts)
            && parts.Length >= 1
            && long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out total);
    }

    /// <summary>
    /// The product of the selectivities of a table's own filters (constraints whose other side
    /// reads no table): Turso's <c>constraint_output_multipliers</c> for the first loop, and
    /// <c>build_self_constraint_selectivity</c> for a hash build (join.rs:72-152, 1187-1208).
    /// </summary>
    private double EstimateTursoLocalSelectivity(
        Expression? predicate,
        NamedTableSource source,
        EmbeddedTable table,
        QueryContext context)
    {
        if (predicate is null)
            return 1.0;

        var qualifier = source.Alias ?? source.Name;
        var columns = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
        var selectivity = 1.0;
        var bounds = new Dictionary<int, (bool Lower, bool Upper)>();
        foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(predicate))
        {
            if (TryDescribeSemiAntiConstraint(
                    conjunctIndex: 0,
                    conjunct,
                    table,
                    qualifier,
                    columns,
                    source,
                    table,
                    context,
                    out var constraint)
                && !constraint.DependsOnOuter)
            {
                selectivity *= constraint.Selectivity;
                var isLower = constraint.Operator is TursoConstraintOperator.Greater or TursoConstraintOperator.GreaterOrEqual;
                var isUpper = constraint.Operator is TursoConstraintOperator.Less or TursoConstraintOperator.LessOrEqual;
                if (isLower || isUpper)
                {
                    bounds.TryGetValue(constraint.ColumnOrdinal, out var bound);
                    bounds[constraint.ColumnOrdinal] = (bound.Lower || isLower, bound.Upper || isUpper);
                }
            }
        }

        // A column bounded from both sides is a closed range (join.rs:141-147).
        foreach (var bound in bounds.Values)
        {
            if (bound.Lower && bound.Upper)
                selectivity *= TursoCostParams.ClosedRangeSelectivityFactor;
        }

        return Math.Clamp(selectivity, 0.0, 1.0);
    }
}
