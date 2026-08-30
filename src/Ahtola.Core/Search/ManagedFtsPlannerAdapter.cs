using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core.Search;

/// <summary>
/// The FTS half of query planning: everything that knows what <c>fts_match</c> and <c>fts_score</c>
/// look like in SQL. The core planner calls this through
/// <see cref="IManagedIndexMethodPlannerAdapter"/> and never inspects FTS shapes itself, so a vector
/// method can add <c>ORDER BY vector_distance_l2(col, ?)</c> recognition without touching the core.
/// </summary>
internal sealed class ManagedFtsPlannerAdapter : IManagedIndexMethodPlannerAdapter
{
    public const string MatchFunction = "FTS_MATCH";
    public const string ScoreFunction = "FTS_SCORE";
    public const string HighlightFunction = "FTS_HIGHLIGHT";
    public const string SnippetFunction = "FTS_SNIPPET";

    private static readonly string[] Owned =
    [
        MatchFunction, ScoreFunction, HighlightFunction, SnippetFunction,
    ];

    public static ManagedFtsPlannerAdapter Instance { get; } = new();

    private ManagedFtsPlannerAdapter()
    {
    }

    public IReadOnlyList<string> OwnedFunctionNames => Owned;

    public bool TryMatch(
        ManagedIndexMethodPlannerContext context,
        out ManagedIndexMethodPatternMatch match)
    {
        ArgumentNullException.ThrowIfNull(context);
        match = null!;

        // A connection-registered scalar with one of our names means the SQL no longer denotes this
        // method's function. Answering with index semantics anyway would silently ignore the user's
        // callback, so decline outright.
        foreach (var name in Owned)
        {
            if (context.IsShadowedFunction(name))
                return false;
        }

        var orderedScoreExpression = FindOrderByScore(context, out var descending);
        var projectedScoreExpression = FindProjectedScore(context);
        var scoreExpression = orderedScoreExpression ?? projectedScoreExpression;
        var matchExpression = FindMatchPredicate(
            context,
            out var hasResidualPredicate,
            out var hasPrecedingResidualPredicate);

        if (matchExpression is not null)
        {
            // A preceding conjunct may short-circuit before MATCH evaluates its query argument.
            // Hoisting it into an index scan would surface errors the scalar execution never reaches.
            if (hasPrecedingResidualPredicate)
                return false;

            var sameScore = scoreExpression is not null && scoreExpression.Equals(matchExpression);
            var scoreOrdered = sameScore
                && orderedScoreExpression is not null
                && descending
                && context.OrderBy is { Count: 1 };
            // An unordered MATCH must preserve the base table's rowid scan order. The FTS cursor
            // returns relevance order, so truncating that cursor would choose a different subset
            // than the scalar/NOT INDEXED path. Only a complete score ordering makes top-k
            // truncation part of the query's semantics.
            var canLimit = scoreOrdered
                && context.Limit is not null
                && context.AllowsRowTruncation
                && !hasResidualPredicate;
            var shape = sameScore
                ? scoreOrdered
                    ? canLimit
                        ? ManagedIndexPatternShape.CombinedOrderedLimit
                        : ManagedIndexPatternShape.CombinedOrdered
                    : ManagedIndexPatternShape.Combined
                : ManagedIndexPatternShape.Match;

            match = new ManagedIndexMethodPatternMatch(
                shape,
                matchExpression,
                FiltersRows: true,
                ValidateArgument: ValidateMatchArgument,
                RetainsUnrankedRows: false);
            return true;
        }

        // Pinned pattern 0 is the globally ranked score-only top-k shape. Without a literal LIMIT
        // there is no cheaper access path: SQL must still retain every row at the scalar 0.0
        // fallback, so leave the statement on its ordinary scan.
        if (orderedScoreExpression is null
            || !descending
            || context.Limit is null
            || context.OrderBy is not { Count: 1 }
            || !context.AllowsRowTruncation
            || context.Predicate is not null)
        {
            return false;
        }

        match = new ManagedIndexMethodPatternMatch(
            ManagedIndexPatternShape.Score,
            orderedScoreExpression,
            FiltersRows: false,
            ValidateArgument: ValidateScoreArgument,
            UnrankedMergePolicy: ManagedIndexUnrankedMergePolicy.MergeByDescendingRank,
            UnrankedRank: 0.0);
        return true;
    }

    /// <summary>
    /// Reproduces the exact type error <c>fts_match()</c> raises for the same argument, so choosing
    /// the index can never turn a scalar error into an empty result set.
    /// </summary>
    private static void ValidateMatchArgument(SqlValue value)
    {
        if (value.Kind is SqlValueKind.Null or SqlValueKind.Text)
            return;

        throw new EmbeddedSqlException("fts_match() requires a text query");
    }

    private static void ValidateScoreArgument(SqlValue value)
    {
        if (value.Kind is SqlValueKind.Null or SqlValueKind.Text)
            return;

        throw new EmbeddedSqlException("fts_score() requires a text query");
    }

    /// <summary>Reads a query argument that already passed validation.</summary>
    public static string RequireQueryText(SqlValue value)
        => value.Kind == SqlValueKind.Text
            ? value.AsText()
            : throw new EmbeddedSqlException("fts query must be text");

    /// <summary>
    /// Finds <c>fts_match(cols…, query)</c> among the conjuncts of a WHERE predicate, and reports
    /// whether any other conjunct survives alongside it — and, separately, whether one of those
    /// conjuncts sits *before* the match call in the statement's own left-to-right source order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The residual flag is what makes limit pushdown safe to reason about: a conjunct the method
    /// does not evaluate is applied to whatever the method returned, so truncating first would drop
    /// rows that the residual would have kept.
    /// </para>
    /// <para>
    /// The preceding-residual flag exists because the scalar evaluator short-circuits <c>AND</c>
    /// left-to-right: `a AND b AND c` parses left-associatively as `((a AND b) AND c)`, and at every
    /// `AND` node the left operand is evaluated first and the right operand runs only once the left
    /// one is true. A conjunct that sits before the match call in that order can therefore be false
    /// for a row and keep the scalar path from ever calling <c>fts_match</c> at all — so an
    /// error-prone query argument is never evaluated, and never raises. A conjunct after the match
    /// call cannot do that: by the time it is reached the match call has already run.
    /// </para>
    /// <para>
    /// Walking the tree with an explicit stack (rather than recursing) avoids a stack frame per
    /// conjunct for a pathologically long AND chain. Visiting in true left-to-right order out of a
    /// LIFO stack means pushing the right child before the left one, so the left child is what pops
    /// — and gets visited — first.
    /// </para>
    /// </remarks>
    private static Expression? FindMatchPredicate(
        ManagedIndexMethodPlannerContext context,
        out bool hasResidualPredicate,
        out bool hasPrecedingResidualPredicate)
    {
        hasResidualPredicate = false;
        hasPrecedingResidualPredicate = false;
        if (context.Predicate is null)
            return null;

        Expression? matched = null;
        var pending = new Stack<Expression>();
        pending.Push(context.Predicate);
        while (pending.Count > 0)
        {
            var conjunct = pending.Pop();
            if (conjunct is BinaryExpression { Operator: BinaryOperator.And } and)
            {
                pending.Push(and.Right);
                pending.Push(and.Left);
                continue;
            }

            if (matched is null
                && conjunct is FunctionExpression function
                && TryGetIndexCallQuery(function, MatchFunction, context, out var query))
            {
                matched = query;
                continue;
            }

            hasResidualPredicate = true;

            // Not yet matched at this point in the left-to-right walk means this conjunct is
            // evaluated strictly before the match call would be.
            if (matched is null)
                hasPrecedingResidualPredicate = true;
        }

        return matched;
    }

    private static Expression? FindProjectedScore(ManagedIndexMethodPlannerContext context)
    {
        if (context.ResultExpressions is null)
            return null;

        foreach (var expression in context.ResultExpressions)
        {
            if (expression is FunctionExpression function
                && TryGetIndexCallQuery(function, ScoreFunction, context, out var query))
            {
                return query;
            }
        }

        return null;
    }

    /// <summary>Finds a leading <c>ORDER BY fts_score(cols…, query) DESC</c> term.</summary>
    private static Expression? FindOrderByScore(ManagedIndexMethodPlannerContext context, out bool descending)
    {
        descending = false;
        if (context.OrderBy is not { Count: > 0 })
            return null;

        var term = context.OrderBy[0];
        if (term.Expression is not FunctionExpression function
            || !TryGetIndexCallQuery(function, ScoreFunction, context, out var query))
        {
            return null;
        }

        descending = term.Descending;
        return query;
    }

    /// <summary>
    /// True when a call names the index's method function with exactly the index's columns as an
    /// unordered set, all resolving to this source. Anything else (a different column set, a
    /// different table, an expression argument) leaves the call to the scalar evaluator.
    /// </summary>
    private static bool TryGetIndexCallQuery(
        FunctionExpression function,
        string expectedName,
        ManagedIndexMethodPlannerContext context,
        out Expression query)
    {
        query = null!;
        if (function.Window is not null
            || function.Filter is not null
            || function.Distinct)
        {
            return false;
        }

        IReadOnlyList<Expression> columns;
        if (string.Equals(function.Name, expectedName, StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == context.Columns.Count + 1)
        {
            columns = function.Arguments.Take(function.Arguments.Count - 1).ToArray();
            query = function.Arguments[^1];
        }
        else if (string.Equals(expectedName, MatchFunction, StringComparison.OrdinalIgnoreCase)
            && string.Equals(function.Name, "MATCH", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2)
        {
            columns = function.Arguments[1] is RowValueExpression tuple
                ? tuple.Values
                : [function.Arguments[1]];
            query = function.Arguments[0];
        }
        else
        {
            return false;
        }

        if (columns.Count != context.Columns.Count)
            return false;

        var matchedColumns = new bool[context.Columns.Count];
        foreach (var argument in columns)
        {
            if (argument is not ColumnExpression { BooleanKeyword: null } column)
                return false;

            // An unqualified column in a multi-source query could belong to any of them; the caller
            // only offers this adapter single-table access paths, but the qualifier still has to
            // agree when it is present so `other.title` never binds to this source's index.
            if (column.Qualifier is { } columnQualifier
                && !string.Equals(columnQualifier, context.Qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var name = column.UnqualifiedName ?? column.Name;
            var found = -1;
            for (var position = 0; position < context.Columns.Count; position++)
            {
                if (string.Equals(name, context.Columns[position].Name, StringComparison.OrdinalIgnoreCase))
                {
                    found = position;
                    break;
                }
            }

            if (found < 0 || matchedColumns[found])
                return false;
            matchedColumns[found] = true;
        }

        // The query argument must not depend on the scanned row, and must be safe to evaluate once
        // for the whole scan, or the method could not hoist it out of the per-row scalar path.
        return context.IsHoistableArgument(query);
    }
}
