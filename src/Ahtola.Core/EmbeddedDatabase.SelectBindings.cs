using Ahtola.Core.Parsing;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    /// <summary>
    /// A single result column of a SELECT after star expansion: the visible name together with
    /// the expression that produces it. Used to resolve GROUP BY ordinals and projection-alias
    /// fallbacks the way SQLite does at prepare time.
    /// </summary>
    private sealed record SelectBindingColumn(string Name, Expression Expression);

    /// <summary>
    /// Applies SQLite's prepare-time binding rules for a single SELECT: GROUP BY ordinal
    /// positions resolve to result columns, bare GROUP BY columns fall back to projection
    /// aliases when they name no source column, HAVING column references rewrite to the
    /// matching projection alias, and WHERE bare columns fall back to projection aliases when
    /// the name is not a source column. The rewrite is idempotent (a resolved column no longer
    /// matches the fallback conditions), so it is safe to run on every execution path.
    /// </summary>
    private SelectStatement ResolveSelectBindings(
        SelectStatement statement,
        QueryContext context,
        SourceRow? outerRow)
    {
        var outputColumns = GetOutputColumns(statement.Source, context);
        var rawOutputColumns = GetRawOutputColumns(statement.Source, context);
        var resultColumns = GetSelectBindingColumns(statement.Projections, outputColumns, rawOutputColumns);

        var groupBy = ResolveGroupByBindings(
            statement.GroupBy,
            statement.Projections,
            resultColumns,
            outputColumns,
            rawOutputColumns,
            outerRow);

        var having = statement.Having is null
            ? null
            : RewriteColumnReferences(
                statement.Having,
                column => ResolveHavingAlias(column, statement.Projections, outputColumns, rawOutputColumns, outerRow));

        var where = statement.Where is null
            ? null
            : RewriteColumnReferences(
                statement.Where,
                column => ResolveWhereAliasFallback(column, statement.Projections, outputColumns, rawOutputColumns, outerRow));

        var orderBy = ResolveOrderByBindings(statement.OrderBy, resultColumns);

        return statement with
        {
            GroupBy = groupBy,
            Having = having,
            Where = where,
            OrderBy = orderBy,
        };
    }

    /// <summary>
    /// ORDER BY ordinal positions resolve to result columns at prepare time the way SQLite
    /// does (Turso's <c>replace_column_number_with_copy_of_column_expr</c>): the literal is
    /// replaced by a copy of the referenced result expression, with the range validated
    /// against the expanded result columns (star projections count once per visible output
    /// column). COLLATE wrappers around the ordinal are re-applied so an explicit collation
    /// governs the sort key. The term keeps its <c>Ordinal</c> marker so downstream
    /// index-order heuristics observe the same shape as before the rewrite.
    /// </summary>
    private static IReadOnlyList<OrderByTerm> ResolveOrderByBindings(
        IReadOnlyList<OrderByTerm> orderBy,
        IReadOnlyList<SelectBindingColumn> resultColumns)
    {
        if (orderBy.Count == 0)
            return orderBy;

        List<OrderByTerm>? result = null;
        for (var index = 0; index < orderBy.Count; index++)
        {
            var term = orderBy[index];
            if (term.Ordinal is not { } ordinal)
                continue;

            // The rewrite must be idempotent: ResolveSelectBindings runs both at the select
            // entry points and again inside ExecuteSelect, so a term whose expression no
            // longer carries an ordinal literal at its core was already resolved and must be
            // left untouched (re-resolving could wrap the projection expression twice).
            var inner = term.Expression;
            List<CollationExpression>? collationWrappers = null;
            while (inner is CollationExpression collationWrapper)
            {
                collationWrappers ??= [];
                collationWrappers.Add(collationWrapper);
                inner = collationWrapper.Expression;
            }

            if (!TryGetOrdinalLiteral(inner, out _))
                continue;

            if (ordinal < 1 || ordinal > resultColumns.Count)
            {
                // Turso hard-codes the "1st" prefix for simple-select range errors
                // regardless of which term carries the ordinal (select.rs:1124).
                throw new EmbeddedSqlException(
                    $"1st ORDER BY term out of range - should be between 1 and {resultColumns.Count}");
            }

            var resolved = resultColumns[(int)ordinal - 1].Expression;
            if (collationWrappers is not null)
            {
                for (var wrapperIndex = collationWrappers.Count - 1; wrapperIndex >= 0; wrapperIndex--)
                    resolved = collationWrappers[wrapperIndex] with { Expression = resolved };
            }

            result ??= new List<OrderByTerm>(orderBy);
            result[index] = term with { Expression = resolved };
        }

        return result ?? orderBy;
    }

    /// <summary>
    /// Expands the projection list into result columns, turning <c>*</c> and qualified
    /// <c>t.*</c> projections into one entry per visible output column.
    /// </summary>
    private static IReadOnlyList<SelectBindingColumn> GetSelectBindingColumns(
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns)
    {
        var result = new List<SelectBindingColumn>();
        foreach (var projection in projections)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    foreach (var column in outputColumns)
                        result.Add(new SelectBindingColumn(column.Name, BuildStarColumnReference(column)));
                    break;
                case QualifiedStarExpression qualifiedStar:
                    var source = rawOutputColumns.Count > 0 ? rawOutputColumns : outputColumns;
                    foreach (var column in source)
                    {
                        if (string.Equals(column.Qualifier, qualifiedStar.Qualifier, StringComparison.OrdinalIgnoreCase))
                            result.Add(new SelectBindingColumn(column.Name, BuildStarColumnReference(column)));
                    }
                    break;
                default:
                    result.Add(new SelectBindingColumn(
                        GetProjectionName(projection),
                        projection.Expression));
                    break;
            }
        }

        return result;
    }

    private static ColumnExpression BuildStarColumnReference(OutputColumn column)
        => column.Qualifier is null
            ? new ColumnExpression(column.Name)
            : new ColumnExpression($"{column.Qualifier}.{column.Name}", column.Qualifier, column.Name);

    /// <summary>
    /// True when <paramref name="expression"/> is an integer literal (optionally unary
    /// plus/negated), i.e. a GROUP BY ordinal position.
    /// </summary>
    private static bool TryGetOrdinalLiteral(Expression expression, out long ordinal)
    {
        ordinal = 0;
        switch (expression)
        {
            case LiteralExpression literal when literal.Value.Kind == SqlValueKind.Integer:
                ordinal = literal.Value.AsInteger();
                return true;
            case UnaryExpression unary
                when (unary.Operator is UnaryOperator.Plus or UnaryOperator.Negate)
                && unary.Operand is LiteralExpression inner
                && inner.Value.Kind == SqlValueKind.Integer:
                ordinal = unary.Operator == UnaryOperator.Negate
                    ? -inner.Value.AsInteger()
                    : inner.Value.AsInteger();
                return true;
            default:
                return false;
        }
    }

    /// <summary>SQLite-style 1st/2nd/3rd/…/11th/12th/13th suffix for GROUP BY range errors.</summary>
    private static string OrdinalSuffix(int zeroBasedIndex)
    {
        var n = zeroBasedIndex + 1L;
        return (n % 100) switch
        {
            11 or 12 or 13 => $"{n}th",
            _ => (n % 10) switch
            {
                1 => $"{n}st",
                2 => $"{n}nd",
                3 => $"{n}rd",
                _ => $"{n}th",
            },
        };
    }

    private IReadOnlyList<Expression> ResolveGroupByBindings(
        IReadOnlyList<Expression> groupBy,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<SelectBindingColumn> resultColumns,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns,
        SourceRow? outerRow)
    {
        if (groupBy.Count == 0)
            return groupBy;

        List<Expression>? result = null;
        for (var index = 0; index < groupBy.Count; index++)
        {
            var term = groupBy[index];
            var resolved = term;

            // An ordinal literal wrapped in COLLATE clauses (GROUP BY 1 COLLATE NOCASE) is
            // still an ordinal position; resolve the inner literal and re-apply the wrappers
            // so the explicit collation governs the grouping key.
            var ordinalExpression = term;
            List<CollationExpression>? collationWrappers = null;
            while (ordinalExpression is CollationExpression collationWrapper)
            {
                collationWrappers ??= [];
                collationWrappers.Add(collationWrapper);
                ordinalExpression = collationWrapper.Expression;
            }

            if (TryGetOrdinalLiteral(ordinalExpression, out var ordinal))
            {
                if (ordinal < 1 || ordinal > resultColumns.Count)
                {
                    throw new EmbeddedSqlException(
                        $"{OrdinalSuffix(index)} GROUP BY term out of range - should be between 1 and {resultColumns.Count}");
                }

                resolved = resultColumns[(int)ordinal - 1].Expression;
                if (collationWrappers is not null)
                {
                    for (var wrapperIndex = collationWrappers.Count - 1; wrapperIndex >= 0; wrapperIndex--)
                        resolved = collationWrappers[wrapperIndex] with { Expression = resolved };
                }
            }
            else if (collationWrappers is null
                && term is ColumnExpression { Qualifier: null } column
                && !ResolvesInLocalSource(column.Name, outputColumns, rawOutputColumns)
                && !ResolvesInOuterRow(column, outerRow)
                && TryFindProjectionAlias(column.Name, projections, out var aliased))
            {
                resolved = aliased;
            }

            if (!ReferenceEquals(resolved, term))
            {
                result ??= new List<Expression>(groupBy);
                result[index] = resolved;
            }
        }

        return result ?? groupBy;
    }

    /// <summary>
    /// HAVING alias rewrite: a bare column resolves to a projection alias only when the name is
    /// not a source column (canonical-first) and does not bind in an enclosing correlated scope,
    /// mirroring SQLite's aggregate-block binding. Ground truth: a name that is both a source
    /// column and an alias keeps the source column in HAVING.
    /// </summary>
    private static Expression? ResolveHavingAlias(
        ColumnExpression column,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns,
        SourceRow? outerRow)
    {
        if (column.Qualifier is not null)
            return null;
        if (ResolvesInLocalSource(column.Name, outputColumns, rawOutputColumns))
            return null;
        if (ResolvesInOuterRow(column, outerRow))
            return null;

        return TryFindProjectionAlias(column.Name, projections, out var expression) ? expression : null;
    }

    /// <summary>
    /// WHERE alias fallback: a bare column is rewritten to its projection alias only when the
    /// name resolves to no source column (canonical-first) and does not bind in an enclosing
    /// correlated scope.
    /// </summary>
    private static Expression? ResolveWhereAliasFallback(
        ColumnExpression column,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns,
        SourceRow? outerRow)
    {
        if (column.Qualifier is not null)
            return null;
        if (ResolvesInLocalSource(column.Name, outputColumns, rawOutputColumns))
            return null;
        if (ResolvesInOuterRow(column, outerRow))
            return null;

        return TryFindProjectionAlias(column.Name, projections, out var expression) ? expression : null;
    }

    private static bool TryFindProjectionAlias(
        string name,
        IReadOnlyList<Projection> projections,
        out Expression expression)
    {
        expression = null!;
        foreach (var projection in projections)
        {
            if (projection.Alias is not null
                && string.Equals(projection.Alias, name, StringComparison.OrdinalIgnoreCase))
            {
                expression = projection.Expression;
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the bare name matches a column exposed by the FROM clause.</summary>
    private static bool ResolvesInLocalSource(
        string name,
        IReadOnlyList<OutputColumn> outputColumns,
        IReadOnlyList<OutputColumn> rawOutputColumns)
    {
        foreach (var column in outputColumns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var column in rawOutputColumns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True when the column binds at any level of an enclosing correlated row.</summary>
    private static bool ResolvesInOuterRow(ColumnExpression column, SourceRow? outerRow)
    {
        for (var row = outerRow; row is not null; row = row.Parent)
        {
            if (TryResolveColumnLocally(row, column))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively rewrites column references. The rewrite callback returns a replacement or
    /// null to keep the reference. Nodes that open a new binding scope (subqueries, window
    /// specifications, aggregate-internal ORDER BY) are not descended into, so their columns
    /// keep their own resolution.
    /// </summary>
    private Expression RewriteColumnReferences(Expression expression, Func<ColumnExpression, Expression?> rewrite)
    {
        switch (expression)
        {
            case ColumnExpression column:
                return rewrite(column) ?? column;
            case CollationExpression collation:
                {
                    var inner = RewriteColumnReferences(collation.Expression, rewrite);
                    return ReferenceEquals(inner, collation.Expression) ? collation : collation with { Expression = inner };
                }
            case CastExpression cast:
                {
                    var inner = RewriteColumnReferences(cast.Expression, rewrite);
                    return ReferenceEquals(inner, cast.Expression) ? cast : cast with { Expression = inner };
                }
            case CaseExpression @case:
                {
                    var operand = @case.Operand is null ? null : RewriteColumnReferences(@case.Operand, rewrite);
                    var clauses = RewriteCaseClauses(@case.Clauses, rewrite);
                    var @else = @case.Else is null ? null : RewriteColumnReferences(@case.Else, rewrite);
                    return ReferenceEquals(operand, @case.Operand)
                        && ReferenceEquals(clauses, @case.Clauses)
                        && ReferenceEquals(@else, @case.Else)
                        ? @case
                        : @case with { Operand = operand, Clauses = clauses, Else = @else };
                }
            case LikeExpression like:
                {
                    var value = RewriteColumnReferences(like.Value, rewrite);
                    var pattern = RewriteColumnReferences(like.Pattern, rewrite);
                    var escape = like.Escape is null ? null : RewriteColumnReferences(like.Escape, rewrite);
                    return ReferenceEquals(value, like.Value)
                        && ReferenceEquals(pattern, like.Pattern)
                        && ReferenceEquals(escape, like.Escape)
                        ? like
                        : like with { Value = value, Pattern = pattern, Escape = escape };
                }
            case GlobExpression glob:
                {
                    var value = RewriteColumnReferences(glob.Value, rewrite);
                    var pattern = RewriteColumnReferences(glob.Pattern, rewrite);
                    return ReferenceEquals(value, glob.Value) && ReferenceEquals(pattern, glob.Pattern)
                        ? glob
                        : glob with { Value = value, Pattern = pattern };
                }
            case InExpression @in:
                {
                    var value = RewriteColumnReferences(@in.Value, rewrite);
                    var values = RewriteExpressionList(@in.Values, rewrite);
                    return ReferenceEquals(value, @in.Value) && ReferenceEquals(values, @in.Values)
                        ? @in
                        : @in with { Value = value, Values = values };
                }
            case InSubqueryExpression inSubquery:
                {
                    // Only the left-hand value binds here; the subquery has its own scope.
                    var value = RewriteColumnReferences(inSubquery.Value, rewrite);
                    return ReferenceEquals(value, inSubquery.Value) ? inSubquery : inSubquery with { Value = value };
                }
            case BetweenExpression between:
                {
                    var value = RewriteColumnReferences(between.Value, rewrite);
                    var lower = RewriteColumnReferences(between.Lower, rewrite);
                    var upper = RewriteColumnReferences(between.Upper, rewrite);
                    return ReferenceEquals(value, between.Value)
                        && ReferenceEquals(lower, between.Lower)
                        && ReferenceEquals(upper, between.Upper)
                        ? between
                        : between with { Value = value, Lower = lower, Upper = upper };
                }
            case UnaryExpression unary:
                {
                    var operand = RewriteColumnReferences(unary.Operand, rewrite);
                    return ReferenceEquals(operand, unary.Operand) ? unary : unary with { Operand = operand };
                }
            case BinaryExpression binary:
                {
                    var left = RewriteColumnReferences(binary.Left, rewrite);
                    var right = RewriteColumnReferences(binary.Right, rewrite);
                    return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                        ? binary
                        : binary with { Left = left, Right = right };
                }
            case FunctionExpression function:
                return RewriteFunctionArguments(function, rewrite);
            case RowValueExpression rowValue:
                {
                    var values = RewriteExpressionList(rowValue.Values, rewrite);
                    return ReferenceEquals(values, rowValue.Values) ? rowValue : rowValue with { Values = values };
                }
            default:
                // Literals, parameters, stars, raise, current-time, scalar subqueries and
                // EXISTS all either contain no column reference or open a new binding scope.
                return expression;
        }
    }

    private Expression RewriteFunctionArguments(FunctionExpression function, Func<ColumnExpression, Expression?> rewrite)
    {
        var arguments = RewriteExpressionList(function.Arguments, rewrite);
        var filter = function.Filter is null ? null : RewriteColumnReferences(function.Filter, rewrite);
        return ReferenceEquals(arguments, function.Arguments) && ReferenceEquals(filter, function.Filter)
            ? function
            : function with { Arguments = arguments, Filter = filter };
    }

    private IReadOnlyList<Expression> RewriteExpressionList(
        IReadOnlyList<Expression> expressions,
        Func<ColumnExpression, Expression?> rewrite)
    {
        List<Expression>? result = null;
        for (var index = 0; index < expressions.Count; index++)
        {
            var rewritten = RewriteColumnReferences(expressions[index], rewrite);
            if (!ReferenceEquals(rewritten, expressions[index]))
            {
                result ??= new List<Expression>(expressions);
                result[index] = rewritten;
            }
        }

        return result ?? expressions;
    }

    private IReadOnlyList<CaseClause> RewriteCaseClauses(
        IReadOnlyList<CaseClause> clauses,
        Func<ColumnExpression, Expression?> rewrite)
    {
        List<CaseClause>? result = null;
        for (var index = 0; index < clauses.Count; index++)
        {
            var when = RewriteColumnReferences(clauses[index].When, rewrite);
            var then = RewriteColumnReferences(clauses[index].Then, rewrite);
            if (!ReferenceEquals(when, clauses[index].When) || !ReferenceEquals(then, clauses[index].Then))
            {
                result ??= new List<CaseClause>(clauses);
                result[index] = new CaseClause(when, then);
            }
        }

        return result ?? clauses;
    }
}
