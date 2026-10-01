using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// IN-list index searches, including the <c>IN</c> filters Turso v0.8.1 infers from
/// <c>OR</c> terms (optimizer/lift_common_subexpressions.rs <c>rewrite_or_terms</c> /
/// <c>in_filters_implied_by_or_branches</c>, commit 70f75beb7).
/// <para>
/// For <c>(x = 1 AND y = 10) OR (x = 2 AND y = 20)</c> every branch pins <c>x</c> to one of
/// <c>1, 2</c>, so <c>x IN (1, 2)</c> holds for every row the <c>OR</c> accepts. Turso adds that
/// implied filter to the WHERE clause (keeping the original <c>OR</c>) so the planner can
/// search an index on <c>x</c>. The managed planner never mutates the statement: it asks for
/// the implied filters while choosing an access path, and the complete WHERE — the original
/// <c>OR</c> included — still filters every row the search returns, so the implied filter can
/// only prune rows no branch accepts.
/// </para>
/// </summary>
public sealed partial class EmbeddedDatabase
{
    /// <summary>
    /// The IN filters implied by the top-level OR conjuncts of <paramref name="where"/> for one
    /// table, ported from <c>in_filters_implied_by_or_branches</c>: a column qualifies when
    /// every branch compares it with at least one copyable literal, the branches name more
    /// than one distinct value, and — when several columns of the table qualify — it is the
    /// single one that leads an index and no compound index starts with two of them.
    /// </summary>
    private static IReadOnlyList<(int ColumnOrdinal, IReadOnlyList<Expression> Values)> GetImpliedOrInFilters(
        Expression? where,
        NamedTableSource source,
        EmbeddedTable table)
    {
        if (where is null)
            return [];

        List<(int ColumnOrdinal, IReadOnlyList<Expression> Values)>? result = null;
        foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(where))
        {
            if (conjunct is not BinaryExpression { Operator: BinaryOperator.Or })
                continue;

            var branches = new List<Expression>();
            CollectTopLevelOrLeaves(conjunct, branches);
            var branchTerms = branches
                .Select(static branch => IndexExpressionSemantics.SplitConjuncts(branch))
                .ToArray();

            var candidates = new List<int>();
            foreach (var term in branchTerms[0])
            {
                if (TryGetColumnLiteralEquality(term, source, table, out var ordinal, out _)
                    && !candidates.Contains(ordinal))
                {
                    candidates.Add(ordinal);
                }
            }

            var filters = new List<(int ColumnOrdinal, IReadOnlyList<Expression> Values)>();
            foreach (var ordinal in candidates)
            {
                var values = new List<Expression>();
                var everyBranch = true;
                foreach (var terms in branchTerms)
                {
                    var found = false;
                    foreach (var term in terms)
                    {
                        if (!TryGetColumnLiteralEquality(term, source, table, out var termOrdinal, out var value)
                            || termOrdinal != ordinal)
                        {
                            continue;
                        }

                        found = true;
                        if (!values.Any(existing => IndexExpressionSemantics.ExpressionsEqual(existing, value)))
                            values.Add(value);
                    }

                    if (!found)
                    {
                        everyBranch = false;
                        break;
                    }
                }

                // One value is the common-term rewrite's case, not an IN filter.
                if (everyBranch && values.Count > 1)
                    filters.Add((ordinal, values));
            }

            if (filters.Count > 1)
            {
                var columns = filters.Select(static filter => filter.ColumnOrdinal).ToArray();
                var sole = SoleIndexedColumn(table, columns);
                filters = sole is { } soleColumn
                    ? [.. filters.Where(filter => filter.ColumnOrdinal == soleColumn)]
                    : [];
            }

            (result ??= []).AddRange(filters);
        }

        return result ?? (IReadOnlyList<(int, IReadOnlyList<Expression>)>)[];
    }

    /// <summary>lift_common_subexpressions.rs:208-233 <c>sole_indexed_column</c>.</summary>
    private static int? SoleIndexedColumn(EmbeddedTable table, IReadOnlyList<int> columns)
    {
        var indexes = table.Indexes.Where(static index => !index.IsMethodIndex).ToArray();
        if (indexes.Any(index => index.Columns.Count > 1
                && !index.Columns[0].IsExpression
                && !index.Columns[1].IsExpression
                && columns.Contains(index.Columns[0].ColumnIndex)
                && columns.Contains(index.Columns[1].ColumnIndex)))
        {
            return null;
        }

        var indexed = columns
            .Where(column => indexes.Any(index => !index.IsPartial
                && index.Columns.Count > 0
                && !index.Columns[0].IsExpression
                && index.Columns[0].ColumnIndex == column))
            .ToArray();
        return indexed.Length == 1 ? indexed[0] : null;
    }

    /// <summary>
    /// lift_common_subexpressions.rs:239-284 <c>column_literal_equality</c>: <c>column =
    /// literal</c> (either side) where the literal is a number, text, BLOB, TRUE/FALSE, or a
    /// signed number. NULL never satisfies <c>=</c> and is not copied.
    /// </summary>
    private static bool TryGetColumnLiteralEquality(
        Expression term,
        NamedTableSource source,
        EmbeddedTable table,
        out int ordinal,
        out Expression value)
    {
        ordinal = -1;
        value = null!;
        if (term is not BinaryExpression { Operator: BinaryOperator.Equal } equality)
            return false;

        if (IsCopyableLiteral(equality.Right) && TryResolvePlainTableColumn(equality.Left, source, table, out ordinal))
        {
            value = equality.Right;
            return true;
        }

        if (IsCopyableLiteral(equality.Left) && TryResolvePlainTableColumn(equality.Right, source, table, out ordinal))
        {
            value = equality.Left;
            return true;
        }

        return false;

        static bool IsCopyableLiteral(Expression expression)
            => expression switch
            {
                LiteralExpression { Value.Kind: SqlValueKind.Integer or SqlValueKind.Real or SqlValueKind.Text or SqlValueKind.Blob } => true,
                ColumnExpression { BooleanKeyword: not null } => true,
                UnaryExpression
                {
                    Operator: UnaryOperator.Negate or UnaryOperator.Plus,
                    Operand: LiteralExpression { Value.Kind: SqlValueKind.Integer or SqlValueKind.Real },
                } => true,
                _ => false,
            };
    }

    /// <summary>A bare column reference that names a declared column of this table.</summary>
    private static bool TryResolvePlainTableColumn(
        Expression expression,
        NamedTableSource source,
        EmbeddedTable table,
        out int ordinal)
    {
        ordinal = -1;
        if (expression is not ColumnExpression { BooleanKeyword: null } column
            || column.Qualifier is { } qualifier
                && !string.Equals(qualifier, source.Alias ?? source.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return table.TryGetColumnIndex(column.UnqualifiedName ?? column.Name, out ordinal);
    }

    /// <summary>
    /// Whether an index whose leading key is <paramref name="leading"/> can be searched by an
    /// IN list (access_method.rs:505-645 <c>choose_best_in_seek_candidate</c>): an explicit
    /// positive <c>column IN (constant, …)</c> conjunct, or an OR-implied IN filter, on that
    /// plain ascending column, compared under the index's collation.
    /// </summary>
    private static bool WhereAllowsIndexInSearch(
        Expression? where,
        NamedTableSource source,
        EmbeddedTable table,
        EmbeddedIndexColumn leading)
    {
        if (where is null || leading.IsExpression || leading.ColumnIndex < 0 || leading.Descending)
            return false;

        // open_loop copies the index collation onto the IN cursor, so the column's own
        // collation must be the index's (access_method.rs:588-602).
        var columnCollation = table.ColumnDefinitions[leading.ColumnIndex].Collation ?? "BINARY";
        if (!string.Equals(
                columnCollation,
                IndexExpressionSemantics.GetCollationName(table, leading),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(where))
        {
            if (conjunct is InExpression { Negated: false } inList
                && inList.Values.Count > 0
                && TryResolvePlainTableColumn(inList.Value, source, table, out var ordinal)
                && ordinal == leading.ColumnIndex
                && inList.Values.All(IsInSearchConstant))
            {
                return true;
            }
        }

        return GetImpliedOrInFilters(where, source, table)
            .Any(filter => filter.ColumnOrdinal == leading.ColumnIndex);

        static bool IsInSearchConstant(Expression value)
            => value switch
            {
                LiteralExpression or ParameterExpression => true,
                ColumnExpression { BooleanKeyword: not null } => true,
                UnaryExpression { Operator: UnaryOperator.Negate or UnaryOperator.Plus, Operand: var operand }
                    => operand is LiteralExpression,
                _ => false,
            };
    }
}
