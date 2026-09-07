using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// Describes the correlated-aggregate-subquery shapes <c>EmbeddedDatabase.SubqueryRewrites.cs</c>
/// rewrites (or declines to rewrite) for <c>EXPLAIN QUERY PLAN</c>, mirroring the plan
/// <c>core/translate/optimizer/unnest.rs</c> reports for the same shapes.
/// <para>
/// <c>EXPLAIN QUERY PLAN</c> previously described the un-rewritten statement (see the type-level
/// remarks on <c>EmbeddedDatabase.SubqueryRewrites.cs</c>), so a query whose aggregate subquery
/// became a join-first or stayed correlated for a good reason both fell back to the generic
/// "MANAGED EVALUATOR FALLBACK"/"MANAGED COMPILED VDBE" rows. This runs the exact same rewrite
/// pipeline execution uses and reports the actual resulting shape:
/// <list type="bullet">
/// <item>Join-first (<see cref="TryRewriteAggregateJoinFirst"/>) introduces a plain
/// <see cref="JoinKind.Left"/> join grouped by the outer table's rowid; described as a hash join
/// against the inner table, a scan of the outer table, and a final sort for the grouping.</item>
/// <item>A subquery every gate declined (unsafe for group-first, and either not eligible for or
/// declined by join-first) stays exactly as written; described as a scan of the outer table plus
/// one "CORRELATED SCALAR SUBQUERY" tag per correlated scalar subquery still in scope.</item>
/// </list>
/// Group-first (a joined derived <c>GROUP BY</c> table) and the cost-based choice between staying
/// correlated and rewriting an eligible aggregate are not modelled here yet: this method declines
/// (returns <see langword="false"/>) rather than describe them, so the existing fallback rows
/// keep reporting an admittedly incomplete plan instead of a wrong one.
/// </para>
/// </summary>
public sealed partial class EmbeddedDatabase
{
    private bool TryDescribeCorrelatedAggregateSubqueryPlan(
        SelectStatement statement,
        QueryContext context,
        out ExecutionResult result)
    {
        result = null!;
        if (statement.Source is null)
            return false;

        var rewritten = RewriteSelectSubqueries(statement, context, outerRow: null);

        // Join-first: SubqueryRewrites.cs replaces the subquery with a real LEFT JOIN of the
        // inner table, grouped back to one row per outer row through the outer table's rowid,
        // with the original comparison moved to HAVING.
        if (rewritten.Source is JoinTableSource
            {
                Kind: JoinKind.Left,
                Left: NamedTableSource outerJoined,
                Right: NamedTableSource innerJoined,
            }
            && rewritten.GroupBy is [ColumnExpression { UnqualifiedName: "rowid" }]
            && rewritten.Having is not null)
        {
            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, $"HASH JOIN {innerJoined.Name} AS {innerJoined.Alias ?? innerJoined.Name}"),
                    PlanRow(2, 0, $"SCAN {outerJoined.Name} AS {outerJoined.Alias ?? outerJoined.Name}"),
                    PlanRow(3, 0, "USE SORTER FOR GROUP BY"),
                ],
                0);
            return true;
        }

        // Nothing became a join: describe the outer scan plus one tag per correlated scalar
        // subquery this select's own scope still holds (unnest.rs declined every rewrite, or the
        // subquery was never an aggregate candidate to begin with, e.g. a plain correlated
        // `(SELECT … LIMIT 1)`).
        if (rewritten.Source is NamedTableSource plainOuter)
        {
            var subqueries = new List<ScalarSubqueryExpression>();
            if (!TryCollectScopedScalarSubqueries(rewritten, subqueries))
                return false;

            var outerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                plainOuter.Alias ?? plainOuter.Name,
            };
            var correlated = subqueries
                .Where(subquery => QueryReferencesFromNames(subquery.Query, outerNames))
                .ToList();
            if (correlated.Count == 0)
                return false;

            var rows = new List<SqlValue[]>(correlated.Count + 1)
            {
                PlanRow(1, 0, $"SCAN {plainOuter.Name} AS {plainOuter.Alias ?? plainOuter.Name}"),
            };
            for (var index = 0; index < correlated.Count; index++)
                rows.Add(PlanRow(index + 2, 0, $"CORRELATED SCALAR SUBQUERY {index + 1}"));

            result = new ExecutionResult(ExplainQueryPlanColumns(), rows, 0);
            return true;
        }

        return false;
    }

    private static SqlValue[] PlanRow(int id, int parent, string detail) =>
        [SqlValue.Integer(id), SqlValue.Integer(parent), SqlValue.Integer(0), SqlValue.Text(detail)];
}

/// <summary>
/// A structured <c>EXPLAIN QUERY PLAN FORMAT=JSON</c> node <c>op</c>, matching the small subset
/// of the typed contract in <c>turso-src/docs/eqp-json.md</c> / <c>core/translate/eqp.rs</c> that
/// <see cref="EmbeddedDatabase.TryBuildEqpJsonOp"/> currently models. Every JSON field is written
/// directly rather than reflected/serialized generically, keeping this trim/AOT-safe.
/// </summary>
internal abstract record EqpJsonOp
{
    public abstract string ToJson();
}

internal sealed record EqpJsonScanOp(string Table, string? Alias, string? IndexName, bool Covering) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"scan\",\"table\":")
            .Append(EmbeddedDatabase.JsonEscape(Table));
        if (Alias is not null)
            json.Append(",\"alias\":").Append(EmbeddedDatabase.JsonEscape(Alias));
        json.Append(",\"source\":\"table\"");
        if (IndexName is not null)
        {
            json.Append(",\"index\":{\"name\":")
                .Append(EmbeddedDatabase.JsonEscape(IndexName))
                .Append(",\"covering\":")
                .Append(Covering ? "true" : "false")
                .Append(",\"ephemeral\":false}");
        }

        return json.Append('}').ToString();
    }
}
