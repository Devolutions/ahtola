using Ahtola.Core.Parsing;

namespace Ahtola.Core.Indexing;

/// <summary>
/// Everything a method's planner adapter may look at when it decides whether it can serve one
/// single-table access path. The core planner never inspects method-specific SQL itself: it hands
/// this context to the adapter the attachment declares, so a vector method can recognize
/// <c>ORDER BY vector_distance_l2(col, ?)</c> exactly the way the FTS method recognizes
/// <c>fts_match</c>, without either shape leaking into the core.
/// </summary>
internal sealed class ManagedIndexMethodPlannerContext
{
    public ManagedIndexMethodPlannerContext(
        string tableName,
        string qualifier,
        IReadOnlyList<ManagedIndexMethodColumn> columns,
        Expression? predicate,
        IReadOnlyList<OrderByTerm>? orderBy,
        long? limit,
        Func<string, bool> isShadowedFunction,
        Func<Expression, bool> isHoistableArgument,
        bool allowsRowTruncation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifier);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(isShadowedFunction);
        ArgumentNullException.ThrowIfNull(isHoistableArgument);

        TableName = tableName;
        Qualifier = qualifier;
        Columns = columns;
        Predicate = predicate;
        OrderBy = orderBy;
        Limit = limit;
        IsShadowedFunction = isShadowedFunction;
        IsHoistableArgument = isHoistableArgument;
        AllowsRowTruncation = allowsRowTruncation;
    }

    /// <summary>The base table the candidate index belongs to, unqualified.</summary>
    public string TableName { get; }

    /// <summary>The alias the query uses for the source, or the table name when there is none.</summary>
    public string Qualifier { get; }

    /// <summary>The index columns in declaration order.</summary>
    public IReadOnlyList<ManagedIndexMethodColumn> Columns { get; }

    public Expression? Predicate { get; }

    public IReadOnlyList<OrderByTerm>? OrderBy { get; }

    /// <summary>A literal LIMIT the planner may push into the method, when there is one.</summary>
    public long? Limit { get; }

    /// <summary>
    /// True when a connection-registered scalar callback shadows the named function. A shadowed
    /// name means the SQL text no longer denotes the method's own function, so the adapter must
    /// decline rather than silently answer with method semantics.
    /// </summary>
    public Func<string, bool> IsShadowedFunction { get; }

    /// <summary>
    /// True when an expression may be evaluated once for the whole scan: it does not depend on
    /// the scanned row, and every call inside it is a deterministic built-in that no registered
    /// callback shadows. A method's query argument must satisfy this before it is hoisted out of
    /// the per-row scalar path.
    /// </summary>
    public Func<Expression, bool> IsHoistableArgument { get; }

    /// <summary>
    /// True when the enclosing statement is a plain projection whose only row-eliminating step is
    /// the pushed-down <see cref="Limit"/>, so a method may return just the rows that survive it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core computes this from the whole statement, not from the single-table access path: a
    /// <c>DISTINCT</c>, a <c>GROUP BY</c>, an aggregate, a window, a non-literal <c>OFFSET</c>, a
    /// compound arm or a join can all reintroduce or re-rank rows a method truncated away, and a
    /// source planned inside any of those shapes receives false.
    /// </para>
    /// <para>
    /// It is a permission, not an instruction: a method that cannot prove its truncated set is a
    /// superset of what the statement keeps must still set
    /// <see cref="ManagedIndexMethodPatternMatch.RetainsUnrankedRows"/>.
    /// </para>
    /// </remarks>
    public bool AllowsRowTruncation { get; }
}

/// <summary>
/// One pattern an adapter recognized, plus the guarantees the core planner needs to stay correct.
/// </summary>
/// <param name="Shape">The declared pattern shape being served.</param>
/// <param name="QueryExpression">The row-independent argument evaluated once per scan.</param>
/// <param name="FiltersRows">
/// True when the pattern is itself a filter, so rows the method does not return are provably
/// excluded by the original predicate. False for ordering-only patterns (score/KNN ranking without
/// a matching predicate): those must still produce every base row, because ranking never removes
/// rows from a result set.
/// </param>
/// <param name="ValidateArgument">
/// Invoked on the evaluated query argument before the scan runs, so the indexed path raises exactly
/// the type errors the scalar evaluator would have raised for the same call.
/// </param>
/// <param name="RetainsUnrankedRows">
/// True (the default) when the engine must still emit every base row the method did not rank, after
/// the ranked ones. Ranking never removes rows from a result set, so this is the only safe default.
/// <para>
/// A method may set it false only for a shape carrying a pushed-down LIMIT, and only when it
/// guarantees the rows it returns are a superset of the rows the statement's own ORDER BY and LIMIT
/// will keep. The core additionally refuses to honor false unless the shape really carries a limit
/// and <see cref="ManagedIndexMethodPlannerContext.AllowsRowTruncation"/> proved the statement shape
/// cannot reintroduce a truncated row, so a method mistake degrades to extra rows rather than to
/// missing ones.
/// </para>
/// </param>
/// <param name="UnrankedMergePolicy">
/// How to combine ranked hits with the rows <see cref="RetainsUnrankedRows"/> forces the scan to
/// keep. The default, <see cref="ManagedIndexUnrankedMergePolicy.Append"/>, is correct for methods
/// whose unranked rows never need to interleave with ranked ones before the statement's own ORDER BY
/// re-sorts everything downstream. A method that pushes its own score down as the statement's actual
/// ordering — so nothing downstream will re-sort the rows the scan produced — must instead choose
/// <see cref="ManagedIndexUnrankedMergePolicy.MergeByDescendingRank"/> and supply
/// <see cref="UnrankedRank"/>, or a LIMIT can truncate the merged set in the wrong order.
/// </param>
/// <param name="UnrankedRank">
/// The rank an unranked row is assigned under <see cref="ManagedIndexUnrankedMergePolicy.MergeByDescendingRank"/>.
/// Meaningless under <see cref="ManagedIndexUnrankedMergePolicy.Append"/>. Must equal whatever the
/// scalar path's own scoring function returns for a row the method never ranked, or the merge order
/// would diverge from what an unindexed scan of the same statement produces.
/// </param>
internal sealed record ManagedIndexMethodPatternMatch(
    ManagedIndexPatternShape Shape,
    Expression QueryExpression,
    bool FiltersRows,
    Action<SqlValue>? ValidateArgument = null,
    bool RetainsUnrankedRows = true,
    ManagedIndexUnrankedMergePolicy UnrankedMergePolicy = ManagedIndexUnrankedMergePolicy.Append,
    double UnrankedRank = 0.0);

/// <summary>
/// The method-specific half of query planning. Implemented next to the method (FTS today, vector
/// next), never in the core planner.
/// </summary>
internal interface IManagedIndexMethodPlannerAdapter
{
    /// <summary>
    /// SQL function names this method owns. A connection-registered scalar callback with any of
    /// these names suppresses method planning and index-aware scalar behavior entirely.
    /// </summary>
    IReadOnlyList<string> OwnedFunctionNames { get; }

    /// <summary>
    /// Attempts to recognize one access path. Adapters return the most specific shape they can
    /// serve; the core planner then checks it against the method's declared patterns and prices it.
    /// </summary>
    bool TryMatch(ManagedIndexMethodPlannerContext context, out ManagedIndexMethodPatternMatch match);
}

/// <summary>
/// Pattern-shape helpers shared by every adapter, so limit-pushdown and ordering-only
/// classification cannot drift between the FTS method and the vector method.
/// </summary>
internal static class ManagedIndexPatternShapes
{
    /// <summary>True when the shape is an ordering-only ranking pattern rather than a filter.</summary>
    public static bool IsRankingOnly(ManagedIndexPatternShape shape)
        => shape is ManagedIndexPatternShape.Score
            or ManagedIndexPatternShape.ScoreOrdered
            or ManagedIndexPatternShape.ScoreOrderedLimit
            or ManagedIndexPatternShape.Knn
            or ManagedIndexPatternShape.KnnLimit;

    /// <summary>True when the shape carries a pushed-down LIMIT.</summary>
    public static bool HasLimit(ManagedIndexPatternShape shape)
        => shape is ManagedIndexPatternShape.MatchLimit
            or ManagedIndexPatternShape.ScoreOrderedLimit
            or ManagedIndexPatternShape.KnnLimit;

    /// <summary>The shape without its LIMIT, used when a secondary ORDER BY term blocks pushdown.</summary>
    public static ManagedIndexPatternShape WithoutLimit(ManagedIndexPatternShape shape)
        => shape switch
        {
            ManagedIndexPatternShape.MatchLimit => ManagedIndexPatternShape.Match,
            ManagedIndexPatternShape.ScoreOrderedLimit => ManagedIndexPatternShape.ScoreOrdered,
            ManagedIndexPatternShape.KnnLimit => ManagedIndexPatternShape.Knn,
            _ => shape,
        };
}
