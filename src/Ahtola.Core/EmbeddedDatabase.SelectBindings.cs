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
        // json_object(*)/jsonb_object(*) must be expanded into explicit column references
        // before RewriteSelectSubqueries's FROM-subquery flattening runs, so a flattened
        // derived table's computed columns and a table-valued function's hidden columns are
        // resolved the same way an ordinary `SELECT *` resolves them. Detect JSON stars
        // without rebuilding the tree first: COUNT(*) alone must not trigger this pass,
        // particularly in correlated subqueries rebound once per outer row.
        var containsJsonStar = ContainsJsonObjectStar(statement);
        var projections = containsJsonStar
            ? RewriteJsonObjectStarInProjections(statement.Projections, outputColumns)
            : statement.Projections;
        var starRewrittenWhere = containsJsonStar && statement.Where is not null
            ? RewriteJsonObjectStarExpressions(statement.Where, outputColumns)
            : statement.Where;
        var starRewrittenHaving = containsJsonStar && statement.Having is not null
            ? RewriteJsonObjectStarExpressions(statement.Having, outputColumns)
            : statement.Having;
        var starRewrittenGroupBy = containsJsonStar
            ? RewriteJsonObjectStarExpressionList(statement.GroupBy, outputColumns)
            : statement.GroupBy;
        var starRewrittenOrderBy = containsJsonStar
            ? RewriteJsonObjectStarInOrderBy(statement.OrderBy, outputColumns)
            : statement.OrderBy;

        var resultColumns = GetSelectBindingColumns(projections, outputColumns, rawOutputColumns);

        var groupBy = ResolveGroupByBindings(
            starRewrittenGroupBy,
            projections,
            resultColumns,
            outputColumns,
            rawOutputColumns,
            outerRow);

        var having = starRewrittenHaving is null
            ? null
            : RewriteColumnReferences(
                starRewrittenHaving,
                column => ResolveHavingAlias(column, projections, outputColumns, rawOutputColumns, outerRow));

        var where = starRewrittenWhere is null
            ? null
            : RewriteColumnReferences(
                starRewrittenWhere,
                column => ResolveWhereAliasFallback(column, projections, outputColumns, rawOutputColumns, outerRow));

        var orderBy = ResolveOrderByBindings(starRewrittenOrderBy, resultColumns);

        return statement with
        {
            Projections = projections,
            GroupBy = groupBy,
            Having = having,
            Where = where,
            OrderBy = orderBy,
            NamedWindows = containsJsonStar
                ? RewriteJsonObjectStarInNamedWindows(statement.NamedWindows, outputColumns)
                : statement.NamedWindows,
        };
    }

    /// <summary>
    /// Allocation-free gate for JSON star expansion within this SELECT's expression scope.
    /// </summary>
    private static bool ContainsJsonObjectStar(SelectStatement statement)
    {
        foreach (var projection in statement.Projections)
        {
            if (ContainsJsonObjectStar(projection.Expression))
                return true;
        }

        if (statement.Where is not null && ContainsJsonObjectStar(statement.Where))
            return true;
        if (statement.Having is not null && ContainsJsonObjectStar(statement.Having))
            return true;

        foreach (var expression in statement.GroupBy)
        {
            if (ContainsJsonObjectStar(expression))
                return true;
        }

        foreach (var term in statement.OrderBy)
        {
            if (ContainsJsonObjectStar(term.Expression))
                return true;
        }

        foreach (var window in statement.NamedWindows)
        {
            if (ContainsJsonObjectStar(window.Specification))
                return true;
        }

        return false;
    }

    private static bool IsJsonObjectStar(FunctionExpression function)
        => function.CountStar
            && (function.Name.Equals("JSON_OBJECT", StringComparison.OrdinalIgnoreCase)
                || function.Name.Equals("JSONB_OBJECT", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsJsonObjectStar(WindowSpecification window)
    {
        foreach (var partition in window.PartitionBy)
        {
            if (ContainsJsonObjectStar(partition))
                return true;
        }

        foreach (var order in window.OrderBy)
        {
            if (ContainsJsonObjectStar(order.Expression))
                return true;
        }

        return window.Frame is { } frame
            && ((frame.Start.Offset is { } start && ContainsJsonObjectStar(start))
                || (frame.End.Offset is { } end && ContainsJsonObjectStar(end)));
    }

    /// <summary>
    /// Mirrors <see cref="RewriteJsonObjectStarExpressions"/>'s traversal shape (including not
    /// descending into a subquery's own scope) but only answers "does a JSON star call exist
    /// anywhere", short-circuiting on the first one found instead of rebuilding the tree.
    /// </summary>
    private static bool ContainsJsonObjectStar(Expression expression)
    {
        switch (expression)
        {
            case FunctionExpression function:
                if (IsJsonObjectStar(function))
                    return true;
                foreach (var argument in function.Arguments)
                {
                    if (ContainsJsonObjectStar(argument))
                        return true;
                }

                if (function.Filter is not null && ContainsJsonObjectStar(function.Filter))
                    return true;
                if (function.AggregateOrderBy is not null)
                {
                    foreach (var term in function.AggregateOrderBy)
                    {
                        if (ContainsJsonObjectStar(term.Expression))
                            return true;
                    }
                }

                return (function.OrderedSetOrderBy is { } ordered && ContainsJsonObjectStar(ordered.Expression))
                    || (function.Window is { } window && ContainsJsonObjectStar(window));
            case CollationExpression collation:
                return ContainsJsonObjectStar(collation.Expression);
            case CastExpression cast:
                return ContainsJsonObjectStar(cast.Expression);
            case CaseExpression @case:
                if (@case.Operand is not null && ContainsJsonObjectStar(@case.Operand))
                    return true;
                foreach (var clause in @case.Clauses)
                {
                    if (ContainsJsonObjectStar(clause.When) || ContainsJsonObjectStar(clause.Then))
                        return true;
                }

                return @case.Else is not null && ContainsJsonObjectStar(@case.Else);
            case LikeExpression like:
                return ContainsJsonObjectStar(like.Value)
                    || ContainsJsonObjectStar(like.Pattern)
                    || (like.Escape is not null && ContainsJsonObjectStar(like.Escape));
            case GlobExpression glob:
                return ContainsJsonObjectStar(glob.Value) || ContainsJsonObjectStar(glob.Pattern);
            case InExpression @in:
                if (ContainsJsonObjectStar(@in.Value))
                    return true;
                foreach (var value in @in.Values)
                {
                    if (ContainsJsonObjectStar(value))
                        return true;
                }

                return false;
            case InSubqueryExpression inSubquery:
                return ContainsJsonObjectStar(inSubquery.Value);
            case BetweenExpression between:
                return ContainsJsonObjectStar(between.Value)
                    || ContainsJsonObjectStar(between.Lower)
                    || ContainsJsonObjectStar(between.Upper);
            case UnaryExpression unary:
                return ContainsJsonObjectStar(unary.Operand);
            case BinaryExpression binary:
                return ContainsJsonObjectStar(binary.Left) || ContainsJsonObjectStar(binary.Right);
            case RowValueExpression rowValue:
                foreach (var value in rowValue.Values)
                {
                    if (ContainsJsonObjectStar(value))
                        return true;
                }

                return false;
            default:
                // Literals, parameters, columns, bare/qualified stars, RAISE, CURRENT_*,
                // DEFAULT, and a scalar-subquery/EXISTS operand (a new query scope) carry no
                // x(*) call of *this* scope to find.
                return false;
        }
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

            // Only an integer that fits a 32-bit int is a column reference, mirroring
            // SQLite's sqlite3ExprIsInteger (select.rs). A literal that fits i32 but
            // is out of the result range errors; a literal beyond i32 (e.g.
            // 6641019685895816357) is a constant expression.
            if (ordinal is < int.MinValue or > int.MaxValue)
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

    /// <summary>
    /// Recursively finds and expands every <c>json_object(*)</c>/<c>jsonb_object(*)</c> call
    /// reachable in a single SELECT's Projections, WHERE, HAVING, GROUP BY, and ORDER BY —
    /// nested inside another call (<c>upper(json_object(*))</c>), inside a WHERE predicate
    /// (<c>WHERE json_object(*) = ...</c>), not just when the star is a projection's entire
    /// top-level expression — into an explicit argument list (alternating column-name literal,
    /// column-value reference) built from the enclosing SELECT's resolved, subquery-aware,
    /// hidden-column-excluding output columns — the same <see cref="GetOutputColumns"/>/
    /// <see cref="BuildStarColumnReference"/> mechanism an ordinary <c>SELECT *</c> uses (see
    /// <see cref="GetSelectBindingColumns"/>).
    /// <para>
    /// Doing this once, up front — before <c>RewriteSelectSubqueries</c>'s FROM-subquery
    /// flattening runs — matters: flattening substitutes derived-table column *references*
    /// (e.g. rewriting a projected <c>double_price</c> back to <c>price * 2</c>) by walking the
    /// expression tree, but a bare <c>json_object(*)</c> has no such references for it to find.
    /// Expanding the star into real <see cref="ColumnExpression"/> arguments here gives the
    /// flattener something to substitute, so a derived-table's computed columns and a
    /// table-valued function's hidden columns are both already correct by the time
    /// <c>EvaluateScalarFunction</c>'s runtime <c>CountStar</c> fallback (still present for a
    /// star this prepare-time pass could not reach, e.g. one that survives inside a rewritten
    /// subquery) would otherwise have to read them straight off the physical row.
    /// </para>
    /// A bare <c>json_object(*)</c> with no FROM clause is left unexpanded so the existing
    /// runtime check still reports "json_object(*) requires a FROM clause".
    /// </summary>
    private static IReadOnlyList<Projection> RewriteJsonObjectStarInProjections(
        IReadOnlyList<Projection> projections,
        IReadOnlyList<OutputColumn> outputColumns)
    {
        List<Projection>? result = null;
        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            var rewritten = RewriteJsonObjectStarExpressions(projection.Expression, outputColumns);
            if (ReferenceEquals(rewritten, projection.Expression))
            {
                result?.Add(projection);
                continue;
            }

            result ??= [.. projections.Take(index)];
            result.Add(projection with { Expression = rewritten });
        }

        return result ?? projections;
    }

    /// <summary>
    /// Rewrites every <c>OrderByTerm</c> in <paramref name="orderBy"/> for a
    /// <c>json_object(*)</c>/<c>jsonb_object(*)</c> star, mirroring
    /// <see cref="RewriteJsonObjectStarInProjections"/>.
    /// </summary>
    private static IReadOnlyList<OrderByTerm> RewriteJsonObjectStarInOrderBy(
        IReadOnlyList<OrderByTerm> orderBy,
        IReadOnlyList<OutputColumn> outputColumns)
    {
        List<OrderByTerm>? result = null;
        for (var index = 0; index < orderBy.Count; index++)
        {
            var term = orderBy[index];
            var rewritten = RewriteJsonObjectStarExpressions(term.Expression, outputColumns);
            if (ReferenceEquals(rewritten, term.Expression))
            {
                result?.Add(term);
                continue;
            }

            result ??= [.. orderBy.Take(index)];
            result.Add(term with { Expression = rewritten });
        }

        return result ?? orderBy;
    }

    private static WindowSpecification RewriteJsonObjectStarInWindow(
        WindowSpecification window,
        IReadOnlyList<OutputColumn> outputColumns)
    {
        var partitions = RewriteJsonObjectStarExpressionList(window.PartitionBy, outputColumns);
        var order = RewriteJsonObjectStarInOrderBy(window.OrderBy, outputColumns);
        var frame = window.Frame;
        if (frame is not null)
        {
            var start = frame.Start.Offset is { } startOffset
                ? RewriteJsonObjectStarExpressions(startOffset, outputColumns)
                : null;
            var end = frame.End.Offset is { } endOffset
                ? RewriteJsonObjectStarExpressions(endOffset, outputColumns)
                : null;
            if (!ReferenceEquals(start, frame.Start.Offset) || !ReferenceEquals(end, frame.End.Offset))
                frame = frame with { Start = frame.Start with { Offset = start }, End = frame.End with { Offset = end } };
        }

        return ReferenceEquals(partitions, window.PartitionBy)
            && ReferenceEquals(order, window.OrderBy)
            && ReferenceEquals(frame, window.Frame)
            ? window
            : window with { PartitionBy = partitions, OrderBy = order, Frame = frame };
    }

    private static IReadOnlyList<NamedWindowDefinition> RewriteJsonObjectStarInNamedWindows(
        IReadOnlyList<NamedWindowDefinition> windows,
        IReadOnlyList<OutputColumn> outputColumns)
    {
        List<NamedWindowDefinition>? result = null;
        for (var index = 0; index < windows.Count; index++)
        {
            var definition = windows[index];
            var rewritten = RewriteJsonObjectStarInWindow(definition.Specification, outputColumns);
            if (ReferenceEquals(rewritten, definition.Specification))
                continue;

            result ??= new List<NamedWindowDefinition>(windows);
            result[index] = definition with { Specification = rewritten };
        }

        return result ?? windows;
    }

    /// <summary>
    /// Recursively rewrites every <c>json_object(*)</c>/<c>jsonb_object(*)</c> call reachable
    /// from <paramref name="expression"/> — including nested inside another function call, a
    /// CASE/CAST/comparison operand, etc. — into an explicit argument list built from
    /// <paramref name="outputColumns"/>. Does not descend into a subquery's own scope
    /// (<see cref="ScalarSubqueryExpression"/>/<see cref="ExistsExpression"/>/the query side of
    /// an <see cref="InSubqueryExpression"/>): that subquery has its own FROM clause and its own
    /// output columns, resolved separately when that inner SELECT is itself bound.
    /// </summary>
    private static Expression RewriteJsonObjectStarExpressions(
        Expression expression,
        IReadOnlyList<OutputColumn> outputColumns)
    {
        switch (expression)
        {
            case FunctionExpression function:
                {
                    if (IsJsonObjectStar(function))
                    {
                        if (outputColumns.Count == 0)
                        {
                            // No FROM clause: leave CountStar set so the scalar-function
                            // evaluator's "json_object(*) requires a FROM clause" check fires.
                            return function;
                        }

                        var starArguments = new List<Expression>(outputColumns.Count * 2);
                        foreach (var column in outputColumns)
                        {
                            starArguments.Add(new LiteralExpression(SqlValue.Text(column.Name)));
                            starArguments.Add(BuildStarColumnReference(column));
                        }

                        return function with { Arguments = starArguments, CountStar = false };
                    }

                    var arguments = RewriteJsonObjectStarExpressionList(function.Arguments, outputColumns);
                    var filter = function.Filter is null
                        ? null
                        : RewriteJsonObjectStarExpressions(function.Filter, outputColumns);
                    var aggregateOrderBy = function.AggregateOrderBy is null
                        ? null
                        : RewriteJsonObjectStarInOrderBy(function.AggregateOrderBy, outputColumns);
                    var orderedSetOrderBy = function.OrderedSetOrderBy;
                    if (orderedSetOrderBy is not null)
                    {
                        var orderedExpression = RewriteJsonObjectStarExpressions(orderedSetOrderBy.Expression, outputColumns);
                        if (!ReferenceEquals(orderedExpression, orderedSetOrderBy.Expression))
                            orderedSetOrderBy = orderedSetOrderBy with { Expression = orderedExpression };
                    }

                    var window = function.Window is null
                        ? null
                        : RewriteJsonObjectStarInWindow(function.Window, outputColumns);
                    return ReferenceEquals(arguments, function.Arguments)
                        && ReferenceEquals(filter, function.Filter)
                        && ReferenceEquals(aggregateOrderBy, function.AggregateOrderBy)
                        && ReferenceEquals(orderedSetOrderBy, function.OrderedSetOrderBy)
                        && ReferenceEquals(window, function.Window)
                        ? function
                        : function with
                        {
                            Arguments = arguments,
                            Filter = filter,
                            AggregateOrderBy = aggregateOrderBy,
                            OrderedSetOrderBy = orderedSetOrderBy,
                            Window = window,
                        };
                }
            case CollationExpression collation:
                {
                    var inner = RewriteJsonObjectStarExpressions(collation.Expression, outputColumns);
                    return ReferenceEquals(inner, collation.Expression) ? collation : collation with { Expression = inner };
                }
            case CastExpression cast:
                {
                    var inner = RewriteJsonObjectStarExpressions(cast.Expression, outputColumns);
                    return ReferenceEquals(inner, cast.Expression) ? cast : cast with { Expression = inner };
                }
            case CaseExpression @case:
                {
                    var operand = @case.Operand is null ? null : RewriteJsonObjectStarExpressions(@case.Operand, outputColumns);
                    List<CaseClause>? clauses = null;
                    for (var index = 0; index < @case.Clauses.Count; index++)
                    {
                        var when = RewriteJsonObjectStarExpressions(@case.Clauses[index].When, outputColumns);
                        var then = RewriteJsonObjectStarExpressions(@case.Clauses[index].Then, outputColumns);
                        if (!ReferenceEquals(when, @case.Clauses[index].When) || !ReferenceEquals(then, @case.Clauses[index].Then))
                        {
                            clauses ??= new List<CaseClause>(@case.Clauses);
                            clauses[index] = new CaseClause(when, then);
                        }
                    }

                    var @else = @case.Else is null ? null : RewriteJsonObjectStarExpressions(@case.Else, outputColumns);
                    return ReferenceEquals(operand, @case.Operand)
                        && clauses is null
                        && ReferenceEquals(@else, @case.Else)
                        ? @case
                        : @case with { Operand = operand, Clauses = clauses ?? @case.Clauses, Else = @else };
                }
            case LikeExpression like:
                {
                    var value = RewriteJsonObjectStarExpressions(like.Value, outputColumns);
                    var pattern = RewriteJsonObjectStarExpressions(like.Pattern, outputColumns);
                    var escape = like.Escape is null ? null : RewriteJsonObjectStarExpressions(like.Escape, outputColumns);
                    return ReferenceEquals(value, like.Value)
                        && ReferenceEquals(pattern, like.Pattern)
                        && ReferenceEquals(escape, like.Escape)
                        ? like
                        : like with { Value = value, Pattern = pattern, Escape = escape };
                }
            case GlobExpression glob:
                {
                    var value = RewriteJsonObjectStarExpressions(glob.Value, outputColumns);
                    var pattern = RewriteJsonObjectStarExpressions(glob.Pattern, outputColumns);
                    return ReferenceEquals(value, glob.Value) && ReferenceEquals(pattern, glob.Pattern)
                        ? glob
                        : glob with { Value = value, Pattern = pattern };
                }
            case InExpression @in:
                {
                    var value = RewriteJsonObjectStarExpressions(@in.Value, outputColumns);
                    var values = RewriteJsonObjectStarExpressionList(@in.Values, outputColumns);
                    return ReferenceEquals(value, @in.Value) && ReferenceEquals(values, @in.Values)
                        ? @in
                        : @in with { Value = value, Values = values };
                }
            case InSubqueryExpression inSubquery:
                {
                    // Only the left-hand value binds in this scope; the subquery has its own.
                    var value = RewriteJsonObjectStarExpressions(inSubquery.Value, outputColumns);
                    return ReferenceEquals(value, inSubquery.Value) ? inSubquery : inSubquery with { Value = value };
                }
            case BetweenExpression between:
                {
                    var value = RewriteJsonObjectStarExpressions(between.Value, outputColumns);
                    var lower = RewriteJsonObjectStarExpressions(between.Lower, outputColumns);
                    var upper = RewriteJsonObjectStarExpressions(between.Upper, outputColumns);
                    return ReferenceEquals(value, between.Value)
                        && ReferenceEquals(lower, between.Lower)
                        && ReferenceEquals(upper, between.Upper)
                        ? between
                        : between with { Value = value, Lower = lower, Upper = upper };
                }
            case UnaryExpression unary:
                {
                    var operand = RewriteJsonObjectStarExpressions(unary.Operand, outputColumns);
                    return ReferenceEquals(operand, unary.Operand) ? unary : unary with { Operand = operand };
                }
            case BinaryExpression binary:
                {
                    var left = RewriteJsonObjectStarExpressions(binary.Left, outputColumns);
                    var right = RewriteJsonObjectStarExpressions(binary.Right, outputColumns);
                    return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                        ? binary
                        : binary with { Left = left, Right = right };
                }
            case RowValueExpression rowValue:
                {
                    var values = RewriteJsonObjectStarExpressionList(rowValue.Values, outputColumns);
                    return ReferenceEquals(values, rowValue.Values) ? rowValue : rowValue with { Values = values };
                }
            default:
                // Literals, parameters, columns, bare/qualified stars, RAISE, CURRENT_*,
                // DEFAULT, and a scalar-subquery/EXISTS operand (a new query scope with its
                // own FROM clause) carry no star of *this* scope's FROM clause to expand.
                return expression;
        }
    }

    private static IReadOnlyList<Expression> RewriteJsonObjectStarExpressionList(
        IReadOnlyList<Expression> expressions,
        IReadOnlyList<OutputColumn> outputColumns)
    {
        List<Expression>? result = null;
        for (var index = 0; index < expressions.Count; index++)
        {
            var rewritten = RewriteJsonObjectStarExpressions(expressions[index], outputColumns);
            if (!ReferenceEquals(rewritten, expressions[index]))
            {
                result ??= new List<Expression>(expressions);
                result[index] = rewritten;
            }
        }

        return result ?? expressions;
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
                // Only an integer that fits a 32-bit int is a column reference, mirroring
                // SQLite's sqlite3ExprIsInteger; a literal beyond i32 is a constant, while
                // an i32-fitting literal below 1 or past the result count errors.
                if (ordinal is >= int.MinValue and <= int.MaxValue)
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
