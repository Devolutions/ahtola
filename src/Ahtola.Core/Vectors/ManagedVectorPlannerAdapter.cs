using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core.Vectors;

/// <summary>
/// The vector half of query planning: everything that knows what <c>vector_distance_*</c> looks like
/// in SQL. The core planner calls this through <see cref="IManagedIndexMethodPlannerAdapter"/> and
/// never inspects vector shapes itself.
/// </summary>
/// <remarks>
/// <para>
/// Recognized: <c>ORDER BY vector_distance_&lt;metric&gt;(col, &lt;row-independent&gt;) [ASC] LIMIT n</c>
/// and its symmetric form with the operands swapped. A result alias (<c>SELECT … AS d … ORDER BY d</c>)
/// reaches this adapter already resolved to the underlying call, so it matches the same way.
/// </para>
/// <para>
/// Declined, so the ordinary scan answers with identical semantics: <c>DESC</c>, a secondary
/// <c>ORDER BY</c> term, an explicit <c>COLLATE</c> on the ordering term, a distance function other
/// than the one the index is bound to, a row-dependent query operand, a shadowed
/// <c>vector_distance_*</c> callback, and any residual <c>WHERE</c> predicate — the last because a
/// pushed-down limit selects the globally nearest rows, which is not the same set as the nearest
/// rows that also satisfy a filter.
/// </para>
/// </remarks>
internal sealed class ManagedVectorPlannerAdapter : IManagedIndexMethodPlannerAdapter
{
    public const string L2Function = "VECTOR_DISTANCE_L2";
    public const string CosineFunction = "VECTOR_DISTANCE_COS";
    public const string DotFunction = "VECTOR_DISTANCE_DOT";
    public const string JaccardFunction = "VECTOR_DISTANCE_JACCARD";

    private static readonly string[] Owned =
    [
        L2Function, CosineFunction, DotFunction, JaccardFunction,
    ];

    private readonly ManagedVectorIndexOptions _options;

    public ManagedVectorPlannerAdapter(ManagedVectorIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Every <c>vector_distance_*</c> name, not just the one this index serves.
    /// </summary>
    /// <remarks>
    /// Shadowing any of them suppresses method planning: a user callback named
    /// <c>vector_distance_cos</c> changes what a cosine ordering means even for an L2 index, and the
    /// engine has to agree with the scalar evaluator about every call in the statement.
    /// </remarks>
    public IReadOnlyList<string> OwnedFunctionNames => Owned;

    /// <summary>The SQL function name bound to one metric.</summary>
    public static string FunctionFor(VectorDistanceKind metric)
        => metric switch
        {
            VectorDistanceKind.L2 => L2Function,
            VectorDistanceKind.Cosine => CosineFunction,
            VectorDistanceKind.Dot => DotFunction,
            VectorDistanceKind.Jaccard => JaccardFunction,
            _ => throw new InvalidOperationException($"Unknown vector distance kind {metric}."),
        };

    public bool TryMatch(ManagedIndexMethodPlannerContext context, out ManagedIndexMethodPatternMatch match)
    {
        ArgumentNullException.ThrowIfNull(context);
        match = null!;

        // A connection-registered scalar with any name this method owns means the SQL no longer
        // denotes the built-in, so answering with index semantics would ignore the user's callback.
        foreach (var name in Owned)
        {
            if (context.IsShadowedFunction(name))
                return false;
        }

        if (context.OrderBy is not { Count: > 0 })
            return false;

        var term = context.OrderBy[0];

        // A COLLATE on the ordering term changes the comparison the engine applies; the index has no
        // way to reproduce that, so it declines rather than assuming a REAL is unaffected.
        if (term.Descending || term.Expression is CollationExpression)
            return false;
        if (term.Expression is not FunctionExpression function)
            return false;
        if (!TryReadQueryOperand(function, context, out var queryExpression))
            return false;

        // A pushed-down limit truncates. That is only sound when nothing downstream can reintroduce
        // a row the method left out: no residual predicate, no secondary ordering term, and the core
        // itself has proven the statement shape is a plain projection.
        var limited = context.Limit is not null && context.OrderBy is { Count: 1 };
        var truncatable = limited && context.Predicate is null && context.AllowsRowTruncation;

        match = new ManagedIndexMethodPatternMatch(
            limited ? ManagedIndexPatternShape.KnnLimit : ManagedIndexPatternShape.Knn,
            queryExpression,
            FiltersRows: false,
            ValidateArgument: ValidateQueryArgument,
            RetainsUnrankedRows: !truncatable);
        return true;
    }

    /// <summary>
    /// Reproduces the exact error <c>vector_distance_*</c> raises for this query operand, so choosing
    /// the index can never turn a scalar error into a row set.
    /// </summary>
    /// <remarks>
    /// The index is only planned once every live row decodes to the declared encoding and
    /// dimensionality (<c>EstimateCost</c> declines otherwise) and only for a non-empty table, so the
    /// column operand can never be the operand that fails and the first error is always one of the
    /// three checks the scalar path performs, in the order it performs them.
    /// </remarks>
    private void ValidateQueryArgument(SqlValue value)
        => SqliteVectorFunctions.ValidateVectorQueryArgument(value, _options.Encoding, _options.Dimensions);

    /// <summary>
    /// True when a call names this index's distance function over this index's column and one
    /// row-independent operand, in either argument order.
    /// </summary>
    private bool TryReadQueryOperand(
        FunctionExpression function,
        ManagedIndexMethodPlannerContext context,
        out Expression queryExpression)
    {
        queryExpression = null!;
        if (!string.Equals(function.Name, _options.DistanceFunctionName, StringComparison.OrdinalIgnoreCase)
            || function.Window is not null
            || function.Filter is not null
            || function.Distinct
            || function.Arguments.Count != 2)
        {
            return false;
        }

        var columnFirst = IsIndexedColumn(function.Arguments[0], context);
        var columnSecond = IsIndexedColumn(function.Arguments[1], context);

        // Both operands naming the column makes the distance a constant rather than a ranking, and
        // neither naming it means this index has nothing to do with the call.
        if (columnFirst == columnSecond)
            return false;

        var candidate = columnFirst ? function.Arguments[1] : function.Arguments[0];
        if (!context.IsHoistableArgument(candidate))
            return false;

        queryExpression = candidate;
        return true;
    }

    private bool IsIndexedColumn(Expression expression, ManagedIndexMethodPlannerContext context)
    {
        if (expression is not ColumnExpression { BooleanKeyword: null } column)
            return false;

        // An unqualified column in a multi-source query could belong to any of them; when the
        // qualifier is present it has to agree so `other.embedding` never binds to this index.
        if (column.Qualifier is { } qualifier
            && !string.Equals(qualifier, context.Qualifier, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
            column.UnqualifiedName ?? column.Name,
            _options.ColumnName,
            StringComparison.OrdinalIgnoreCase);
    }
}
