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
/// <para>
/// Each row this method builds also carries a parallel <see cref="EqpJsonOp"/> (via
/// <paramref name="ops"/>), matching the field names/values <c>core/translate/eqp.rs::write_json</c>
/// emits for the equivalent shape (<c>hash_join</c>, <c>scan</c>, <c>group_by</c>,
/// <c>scalar_subquery</c>), so <c>EXPLAIN QUERY PLAN FORMAT=JSON</c> describes every row this
/// describer produces as a typed op instead of falling back to the generic "unmodeled" wrapper.
/// </para>
/// </summary>
public sealed partial class EmbeddedDatabase
{
    private bool TryDescribeCorrelatedAggregateSubqueryPlan(
        SelectStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        out ExecutionResult result,
        out IReadOnlyList<EqpJsonOp?> ops)
    {
        result = null!;
        ops = [];
        if (statement.Source is null)
            return false;

        var rewritten = RewriteSelectSubqueries(statement, context, outerRow: null);

        // Join-first: SubqueryRewrites.cs replaces the subquery with a real LEFT JOIN of the
        // inner table, grouped back to one row per outer row through the outer table's rowid,
        // with the original comparison moved to HAVING. Pattern-matching only the *resulting*
        // shape is not enough to prove this row came from that rewrite: an ordinary hand-written
        // `... LEFT JOIN inner i ON i.k > o.k GROUP BY o.rowid HAVING ...` produces the exact same
        // AST shape without ever going through unnest.rs's rewrite, and a HASH JOIN cannot
        // execute a non-equality condition at all (Ahtola's join-first LEFT JOIN is always built
        // with a plain `=` per unnest.rs:589-687/726-817; a `>`/`<` condition here would in
        // reality take a completely different, unmodelled access path). Guard against that by
        // requiring the *original, pre-rewrite* statement to already be single-table (the
        // rewrite is the only thing that can turn a single-table SELECT into this two-table join
        // -- a hand-written join always starts as a JoinTableSource already) and by requiring
        // every top-level AND-conjunct of the join condition to be a plain equality.
        //
        // Even given a genuine rewrite output, "HASH JOIN" is only provably what executes when
        // ExecuteSelectStatement's real routing decision sends this select to the evaluator: its
        // GetJoinRows/TryBuildJoinHashIndex path always materializes both sides and probes an
        // in-memory hash index for an equi-join condition, regardless of whether a real index
        // exists. The COMPILED route's join builder (TryBuildCompiledJoinSource /
        // TryCreateCompiledJoinIndexScanPlan), by contrast, may instead choose a real or
        // automatic index seek when one is cost-eligible -- a materially different access method
        // this describer does not yet reconstruct. Rather than guess which one a compiled route
        // would pick, decline whenever the real router would actually attempt the compiled route
        // at all, so this only ever describes the query as it is provably guaranteed to run.
        if (statement.Source is not JoinTableSource
            && rewritten.Source is JoinTableSource
            {
                Kind: JoinKind.Left,
                Left: NamedTableSource outerJoined,
                Right: NamedTableSource innerJoined,
                Condition: { } joinCondition,
            }
            && rewritten.GroupBy is [ColumnExpression { UnqualifiedName: "rowid" }]
            && rewritten.Having is not null
            && IndexExpressionSemantics.SplitConjuncts(joinCondition)
                .All(static conjunct => conjunct is BinaryExpression { Operator: BinaryOperator.Equal })
            && !(CanUseCompiledSelectRoute(rewritten, context, outerRow: null)
                && !SourceContainsSemiOrAntiJoin(rewritten.Source)
                && TryCompileSelect(rewritten, parameters, context, outerRow: null, out _)))
        {
            var innerAlias = innerJoined.Alias ?? innerJoined.Name;
            var outerAlias = outerJoined.Alias ?? outerJoined.Name;
            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, $"HASH JOIN {innerJoined.Name} AS {innerAlias}"),
                    PlanRow(2, 0, $"SCAN {outerJoined.Name} AS {outerAlias}"),
                    PlanRow(3, 0, "USE SORTER FOR GROUP BY"),
                ],
                0);
            ops =
            [
                new EqpJsonHashJoinOp(innerJoined.Name, innerJoined.Alias),
                new EqpJsonScanOp(outerJoined.Name, outerJoined.Alias, IndexName: null, Covering: false),
                new EqpJsonGroupByOp(),
            ];
            return true;
        }

        // Nothing became a join: describe the outer table's actual access method plus one tag
        // per correlated scalar subquery this select's own scope still holds (unnest.rs declined
        // every rewrite, or the subquery was never an aggregate candidate to begin with, e.g. a
        // plain correlated `(SELECT … LIMIT 1)`). ExecuteSelect's evaluator route (the route this
        // statement actually runs on) always calls TryPlanManagedIndexScan for its own row
        // production regardless of any correlated subquery elsewhere in the statement, so an
        // indexable term on the outer table's own WHERE genuinely executes as an index
        // SCAN/SEARCH, not a hardcoded full scan -- reuse that same planner rather than assume.
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

            var (outerDetail, outerOp) = TryPlanManagedIndexScan(rewritten, context) is { } outerIndexPlan
                ? (
                    FormatManagedIndexExplainDetail(outerIndexPlan, rewritten),
                    BuildIndexScanOp(outerIndexPlan, rewritten))
                : (
                    $"SCAN {plainOuter.Name} AS {plainOuter.Alias ?? plainOuter.Name}",
                    (EqpJsonOp)new EqpJsonScanOp(plainOuter.Name, plainOuter.Alias, IndexName: null, Covering: false));

            var rows = new List<SqlValue[]>(correlated.Count + 1) { PlanRow(1, 0, outerDetail) };
            var rowOps = new List<EqpJsonOp?>(correlated.Count + 1) { outerOp };
            for (var index = 0; index < correlated.Count; index++)
            {
                rows.Add(PlanRow(index + 2, 0, $"CORRELATED SCALAR SUBQUERY {index + 1}"));
                rowOps.Add(new EqpJsonScalarSubqueryOp(index + 1, Correlated: true));
            }

            result = new ExecutionResult(ExplainQueryPlanColumns(), rows, 0);
            ops = rowOps;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the same typed scan/search op the dedicated single-table index-scan branch of
    /// <c>ExecuteExplainQueryPlanText</c> builds, factored out so the "stays correlated"
    /// describer above can reuse it verbatim instead of re-deriving the covering/constraint
    /// logic.
    /// </summary>
    private static EqpJsonOp BuildIndexScanOp(ManagedIndexScanPlan plan, SelectStatement select)
    {
        var covering = IndexCoversSelect(select, plan.Table, plan.Index);
        return plan.Search && plan.Index.Columns[0].Expression is null
            ? new EqpJsonSearchOp(
                plan.Table.Name,
                plan.Source.Alias,
                plan.Index.Name,
                covering,
                [$"{plan.Index.Columns[0].Name}=?"])
            : new EqpJsonScanOp(plan.Table.Name, plan.Source.Alias, plan.Index.Name, covering);
    }

    private static SqlValue[] PlanRow(int id, int parent, string detail) =>
        [SqlValue.Integer(id), SqlValue.Integer(parent), SqlValue.Integer(0), SqlValue.Text(detail)];
}

/// <summary>
/// A structured <c>EXPLAIN QUERY PLAN FORMAT=JSON</c> node <c>op</c>, matching the field names
/// and values <c>core/translate/eqp.rs::EqpDetail::write_json</c> (v0.8.0-pre.7) emits for the
/// same shape. Every JSON field is written directly rather than reflected/serialized
/// generically, keeping this trim/AOT-safe. Only the subset of op variants Ahtola's describers
/// can build from data they already have (never fabricated/approximated) is modelled; every
/// other row reports an explicit <c>"unmodeled"</c> op instead (see
/// <c>EmbeddedDatabase.ExecuteExplainQueryPlan</c>).
/// </summary>
internal abstract record EqpJsonOp
{
    public abstract string ToJson();

    /// <summary>
    /// Writes the shared <c>table</c>/<c>alias</c>/<c>join</c> triple <c>write_table_fields</c>
    /// emits first for every table-referencing op (<c>scan</c>, <c>search</c>, <c>hash_join</c>).
    /// <paramref name="join"/> is the upstream <c>EqpJoin::as_str()</c> spelling
    /// ("left"/"full"/"semi"/"anti"/...) or <see langword="null"/> for an ordinary/first
    /// operand.
    /// </summary>
    private protected static void AppendTableFields(
        System.Text.StringBuilder json,
        string table,
        string? alias,
        string? join)
    {
        json.Append("\"table\":").Append(EmbeddedDatabase.JsonEscape(table));
        if (alias is not null)
            json.Append(",\"alias\":").Append(EmbeddedDatabase.JsonEscape(alias));
        if (join is not null)
            json.Append(",\"join\":").Append(EmbeddedDatabase.JsonEscape(join));
    }

    private protected static void AppendIndexField(
        System.Text.StringBuilder json,
        string? indexName,
        bool covering,
        bool ephemeral)
    {
        if (indexName is null)
            return;

        json.Append(",\"index\":{\"name\":")
            .Append(EmbeddedDatabase.JsonEscape(indexName))
            .Append(",\"covering\":")
            .Append(covering ? "true" : "false")
            .Append(",\"ephemeral\":")
            .Append(ephemeral ? "true" : "false")
            .Append('}');
    }
}

/// <summary>A FROM-less SELECT reading its one synthesized row of literal/computed values.</summary>
internal sealed record EqpJsonConstantRowOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"constant_row\"}";
}

/// <summary>Mirrors <c>EqpDetail::Scan</c>: a full read of a base table, with no key constraint.</summary>
internal sealed record EqpJsonScanOp(
    string Table,
    string? Alias,
    string? IndexName,
    bool Covering,
    string? Join = null,
    bool Ephemeral = false) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"scan\",");
        AppendTableFields(json, Table, Alias, Join);
        json.Append(",\"source\":\"table\"");
        AppendIndexField(json, IndexName, Covering, Ephemeral);
        return json.Append('}').ToString();
    }
}

/// <summary>
/// Mirrors <c>EqpDetail::Search</c>: a table read bounded by a key constraint. <paramref
/// name="Constraints"/> holds each constraint exactly as printed inside the TEXT detail's
/// parentheses (e.g. <c>"age&gt;?"</c>), matching <c>core/translate/eqp.rs</c>'s own
/// already-formatted constraint strings.
/// </summary>
internal sealed record EqpJsonSearchOp(
    string Table,
    string? Alias,
    string? IndexName,
    bool Covering,
    IReadOnlyList<string> Constraints,
    string? Join = null,
    bool Ephemeral = false,
    string SearchKind = "seek") : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"search\",");
        AppendTableFields(json, Table, Alias, Join);
        json.Append(",\"search_kind\":").Append(EmbeddedDatabase.JsonEscape(SearchKind));
        AppendIndexField(json, IndexName, Covering, Ephemeral);
        if (IndexName is null)
            json.Append(",\"integer_primary_key\":true");
        json.Append(",\"constraints\":[")
            .Append(string.Join(",", Constraints.Select(static c => EmbeddedDatabase.JsonEscape(c))))
            .Append(']');
        return json.Append('}').ToString();
    }
}

/// <summary>Mirrors <c>EqpDetail::MultiIndex</c>: an OR of independently-searched indexes.</summary>
internal sealed record EqpJsonMultiIndexOp(string Table, IReadOnlyList<string> Indexes) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"multi_index\",\"table\":")
            .Append(EmbeddedDatabase.JsonEscape(Table))
            .Append(",\"set_op\":\"or\",\"indexes\":[")
            .Append(string.Join(",", Indexes.Select(static name => EmbeddedDatabase.JsonEscape(name))))
            .Append(']');
        return json.Append('}').ToString();
    }
}

/// <summary>
/// Mirrors <c>EqpDetail::HashJoin</c>: the build side of a hash join reads the whole table once
/// before any probe.
/// </summary>
internal sealed record EqpJsonHashJoinOp(string Table, string? Alias, string? Join = null) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"hash_join\",");
        AppendTableFields(json, Table, Alias, Join);
        return json.Append('}').ToString();
    }
}

/// <summary>Mirrors <c>EqpDetail::OrderBy</c> with <c>method: "sorter"</c> ("USE SORTER FOR ORDER BY").</summary>
internal sealed record EqpJsonOrderByOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"order_by\",\"method\":\"sorter\"}";
}

/// <summary>Mirrors <c>EqpDetail::GroupBy</c> ("USE SORTER FOR GROUP BY"); Ahtola only builds this via a sorter.</summary>
internal sealed record EqpJsonGroupByOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"group_by\",\"method\":\"sorter\"}";
}

/// <summary>Mirrors <c>EqpDetail::ScalarSubquery</c>: a scalar-valued (non-EXISTS/IN) subquery.</summary>
internal sealed record EqpJsonScalarSubqueryOp(int SubqueryId, bool Correlated) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"scalar_subquery\",\"subquery_id\":{SubqueryId},\"correlated\":{(Correlated ? "true" : "false")}}}";
}

/// <summary>Mirrors <c>EqpDetail::ListSubquery</c>: an IN/EXISTS-style multi-row subquery.</summary>
internal sealed record EqpJsonListSubqueryOp(int SubqueryId, bool Correlated) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"list_subquery\",\"subquery_id\":{SubqueryId},\"correlated\":{(Correlated ? "true" : "false")}}}";
}
