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

        var scoreExpression = FindOrderByScore(context, out var descending);
        var matchExpression = FindMatchPredicate(
            context,
            out var hasResidualPredicate,
            out var hasPrecedingResidualPredicate);

        // The one shape whose truncation order is the statement's own order: filter by match, rank
        // by relevance, and stop at the limit. It needs the ORDER BY to be relevance on exactly the
        // same call, no residual conjunct to shrink the truncated set afterwards, and a statement
        // shape that cannot reintroduce or re-rank a truncated row.
        if (matchExpression is not null
            && scoreExpression is not null
            && descending
            && context.Limit is not null
            && context.OrderBy is { Count: 1 }
            && context.AllowsRowTruncation
            && !hasResidualPredicate
            && scoreExpression.Equals(matchExpression))
        {
            match = new ManagedIndexMethodPatternMatch(
                ManagedIndexPatternShape.MatchLimit,
                matchExpression,
                FiltersRows: true,
                ValidateArgument: ValidateMatchArgument,
                RetainsUnrankedRows: false);
            return true;
        }

        // ORDER BY runs after WHERE has already decided which rows survive at all, so it is a
        // separate evaluation phase rather than another conjunct that short-circuiting can skip
        // over. That means ANY residual predicate — before or after a match call in the source
        // text, it makes no difference here — can filter out every row before this hoisted score
        // argument would ever be reached by ORDER BY on the scalar path, leaving an error-prone
        // argument unevaluated there. Only two shapes are provably safe from that: no predicate at
        // all, or a predicate that IS this exact argument's own fts_match call and nothing else, so
        // WHERE itself evaluates the argument (as part of deciding whether the row survives) before
        // ORDER BY could ever be reached — an error in it surfaces through that match evaluation
        // regardless of whether the row would have survived.
        if (scoreExpression is not null
            && descending
            && (context.Predicate is null
                || (matchExpression is not null
                    && !hasResidualPredicate
                    && scoreExpression.Equals(matchExpression))))
        {
            // Ranking never removes rows, so a score-ordered plan must still produce every base row.
            // Only a pushed-down LIMIT makes it worth taking, and a secondary ORDER BY term blocks
            // that pushdown because it could reorder rows the method already truncated away.
            var shape = context.Limit is not null && context.OrderBy is { Count: 1 }
                ? ManagedIndexPatternShape.ScoreOrderedLimit
                : ManagedIndexPatternShape.ScoreOrdered;
            match = new ManagedIndexMethodPatternMatch(
                shape,
                scoreExpression,
                FiltersRows: false,
                ValidateArgument: ValidateScoreArgument,
                // The scalar path scores a row the index did not rank as 0.0 (see
                // EvaluateFtsScore), so merging on that same fallback rank reproduces the scalar
                // evaluator's own (score DESC, rowid ASC) tie-break instead of the index's ranked
                // hits winning ties against unranked rows purely by having been listed first.
                UnrankedMergePolicy: ManagedIndexUnrankedMergePolicy.MergeByDescendingRank,
                UnrankedRank: 0.0);
            return true;
        }

        if (matchExpression is null)
            return false;

        // A residual conjunct that precedes the match call in left-to-right order can short-circuit
        // the scalar path past it entirely — `AND` never evaluates its right operand once the left
        // one is false — so the match's own query argument is never evaluated and never raises.
        // Planning here would evaluate that argument unconditionally up front and could raise an
        // error the scalar path would have silently avoided by never reaching the call. Declining
        // leaves the whole predicate to the ordinary per-row pipeline, which reproduces that same
        // short-circuit. A residual that only follows the match call cannot cause this: the scalar
        // path always reaches the match call first, so the pattern below still filters correctly.
        if (hasPrecedingResidualPredicate)
            return false;

        // Any other LIMIT is left to the ordinary pipeline. A pushed-down LIMIT on a filtering
        // pattern truncates the row set itself, and the rows it keeps are the best-scoring ones:
        //   * `... WHERE fts_match(…) LIMIT 5` wants the first five matches in scan order, not the
        //     five best-scoring ones, and a scan produces rows in ascending rowid order;
        //   * `... ORDER BY id LIMIT 5` wants the five lowest ids among *all* matches;
        //   * a residual conjunct applied after truncation can only shrink the truncated set, so it
        //     would return fewer rows than the LIMIT asked for while further matches still exist.
        // The unlimited Match pattern still filters; only the truncation is given up.
        match = new ManagedIndexMethodPatternMatch(
            ManagedIndexPatternShape.Match,
            matchExpression,
            FiltersRows: true,
            ValidateArgument: ValidateMatchArgument);
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
                && MatchesIndexCall(function, MatchFunction, context))
            {
                matched = function.Arguments[^1];
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

    /// <summary>Finds a leading <c>ORDER BY fts_score(cols…, query) DESC</c> term.</summary>
    private static Expression? FindOrderByScore(ManagedIndexMethodPlannerContext context, out bool descending)
    {
        descending = false;
        if (context.OrderBy is not { Count: > 0 })
            return null;

        var term = context.OrderBy[0];
        if (term.Expression is not FunctionExpression function
            || !MatchesIndexCall(function, ScoreFunction, context))
        {
            return null;
        }

        descending = term.Descending;
        return function.Arguments[^1];
    }

    /// <summary>
    /// True when a call names the index's method function with exactly the index's columns, in
    /// declaration order, all resolving to this source. Anything else (a different column set, a
    /// different table, an expression argument) leaves the call to the scalar evaluator.
    /// </summary>
    private static bool MatchesIndexCall(
        FunctionExpression function,
        string expectedName,
        ManagedIndexMethodPlannerContext context)
    {
        if (!string.Equals(function.Name, expectedName, StringComparison.OrdinalIgnoreCase)
            || function.Window is not null
            || function.Filter is not null
            || function.Distinct
            || function.Arguments.Count != context.Columns.Count + 1)
        {
            return false;
        }

        for (var position = 0; position < context.Columns.Count; position++)
        {
            if (function.Arguments[position] is not ColumnExpression { BooleanKeyword: null } column)
                return false;

            // An unqualified column in a multi-source query could belong to any of them; the caller
            // only offers this adapter single-table access paths, but the qualifier still has to
            // agree when it is present so `other.title` never binds to this source's index.
            if (column.Qualifier is { } columnQualifier
                && !string.Equals(columnQualifier, context.Qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(
                    column.UnqualifiedName ?? column.Name,
                    context.Columns[position].Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // The query argument must not depend on the scanned row, and must be safe to evaluate once
        // for the whole scan, or the method could not hoist it out of the per-row scalar path.
        return context.IsHoistableArgument(function.Arguments[^1]);
    }
}
