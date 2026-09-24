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
/// its <c>ops</c> output), matching the field names/values <c>core/translate/eqp.rs::write_json</c>
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

        // A cancellation-capable ordinary LEFT JOIN stays on the evaluator, but its preserved
        // side and null-supplying equality lookup still use the same managed index access paths
        // as their compiled counterparts. Describe those paths only when both plans are the
        // exact paths GetJoinRows selects: WHERE narrows the preserved left side and no WHERE
        // term narrows the right side before the ON lookup.
        if (statement.Source is JoinTableSource
            {
                Kind: JoinKind.Left,
                Left: NamedTableSource left,
                Right: NamedTableSource right,
                Condition: { } condition,
            } join
            && statement.Where is not null)
        {
            var (leftPredicate, rightPredicate) = SplitJoinSidePredicates(statement.Where, join, context);
            var leftSelect = new SelectStatement(
                Distinct: false,
                Projections: [],
                Source: left,
                Where: leftPredicate,
                GroupBy: [],
                Having: null,
                NamedWindows: [],
                OrderBy: statement.OrderBy,
                Limit: null,
                Offset: null);
            if (leftPredicate is not null
                && rightPredicate is null
                && TryPlanManagedIndexScan(leftSelect, context) is { } leftPlan
                && TryPlanDeclaredIndexLookup(right, condition, context) is { } rightPlan)
            {
                var leftConstraint = leftPlan.SearchConstraint ?? $"{leftPlan.Index.Columns[0].Name}=?";
                var leftDetail = $"SEARCH {left.Alias ?? left.Name} USING INDEX {leftPlan.Index.Name} ({leftConstraint})";
                var rightAlias = right.Alias ?? right.Name;
                var rightConstraint = rightPlan.SearchConstraint ?? $"{rightPlan.Index.Columns[0].Name}=?";
                var rightDetail = $"SEARCH {rightAlias} USING INDEX {rightPlan.Index.Name} ({rightConstraint}) LEFT-JOIN";
                var rows = new List<SqlValue[]>
                {
                    PlanRow(1, 0, leftDetail),
                    PlanRow(2, 0, rightDetail),
                };
                var rowOps = new List<EqpJsonOp?>
                {
                    new EqpJsonSearchOp(
                        left.Name,
                        left.Alias,
                        leftPlan.Index.Name,
                        Covering: false,
                        [$"{leftConstraint}"]),
                    new EqpJsonSearchOp(
                        right.Name,
                        right.Alias,
                        rightPlan.Index.Name,
                        Covering: false,
                        [$"{rightPlan.Index.Columns[0].Name}=?"],
                        Join: "left"),
                };
                if (statement.OrderBy.Count != 0)
                {
                    rows.Add(PlanRow(28, 0, "USE SORTER FOR ORDER BY"));
                    rowOps.Add(new EqpJsonOrderByOp());
                }

                result = new ExecutionResult(ExplainQueryPlanColumns(), rows, 0);
                ops = rowOps;
                return true;
            }
        }

        // An uncorrelated IN list is materialized once, while scalar projections that reference
        // the outer source remain per-row lookups. Keep those two lifetimes distinct in EQP:
        // the list is a child scan and the scalar query is a correlated child of the outer seek.
        if (statement.Source is NamedTableSource listOuter
            && statement.Where is InSubqueryExpression
            {
                Negated: false,
                Value: ColumnExpression inValue,
                Query: SelectStatement
                {
                    Source: NamedTableSource listInner,
                } inListQuery,
            }
            && statement.Projections.Select(static projection => projection.Expression)
                .OfType<ScalarSubqueryExpression>()
                .SingleOrDefault() is { Query: SelectStatement { Source: NamedTableSource scalarInner } scalarQuery } scalar
            && context.Tables.TryGetValue(listOuter.Name, out var outerTable)
            && context.Tables.TryGetValue(listInner.Name, out _)
            && QueryReferencesFromNames(
                scalar.Query,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    listOuter.Alias ?? listOuter.Name,
                })
            && !QueryReferencesFromNames(
                inListQuery,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    listOuter.Alias ?? listOuter.Name,
                })
            && outerTable.Indexes.FirstOrDefault(index =>
                QueryExpressionMatchesIndexTerm(inValue, outerTable, index.Columns[0])) is { } inIndex)
        {
            var outerAlias = listOuter.Alias ?? listOuter.Name;
            var scalarAlias = scalarInner.Alias ?? scalarInner.Name;
            var listAlias = listInner.Alias ?? listInner.Name;
            var scalarPlan = TryPlanManagedIndexScan(scalarQuery, context);
            var scalarDetail = scalarPlan is null
                ? $"SCAN {scalarInner.Name}" + (scalarInner.Alias is null ? string.Empty : $" AS {scalarAlias}")
                : FormatManagedIndexExplainDetail(scalarPlan, scalarQuery);
            var scalarOp = scalarPlan is null
                ? (EqpJsonOp)new EqpJsonScanOp(scalarInner.Name, scalarInner.Alias, IndexName: null, Covering: false)
                : BuildIndexScanOp(scalarPlan, scalarQuery);

            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, "LIST SUBQUERY 1"),
                    PlanRow(4, 1, $"SCAN {listInner.Name}" + (listInner.Alias is null ? string.Empty : $" AS {listAlias}")),
                    PlanRow(13, 0, $"SEARCH {outerAlias} USING INDEX {inIndex.Name} ({inValue.UnqualifiedName ?? inValue.Name}=?)"),
                    PlanRow(23, 0, "CORRELATED SCALAR SUBQUERY 2"),
                    PlanRow(26, 23, scalarDetail),
                ],
                0);
            ops =
            [
                new EqpJsonListSubqueryOp(1, Correlated: false),
                new EqpJsonScanOp(listInner.Name, listInner.Alias, IndexName: null, Covering: false),
                new EqpJsonSearchOp(
                    listOuter.Name,
                    listOuter.Alias,
                    inIndex.Name,
                    Covering: IndexCoversSelect(statement, outerTable, inIndex),
                    [$"{inValue.UnqualifiedName ?? inValue.Name}=?"],
                    SearchKind: "in_seek"),
                new EqpJsonScalarSubqueryOp(2, Correlated: true),
                scalarOp,
            ];
            return true;
        }

        if (statement.Source is NamedTableSource inOnlyOuter
            && statement.Where is InSubqueryExpression
            {
                Negated: false,
                Value: ColumnExpression inOnlyValue,
                Query: SelectStatement
                {
                    Source: NamedTableSource inOnlyInner,
                } inOnlyListQuery,
            }
            && !statement.Projections.Any(static projection => projection.Expression is ScalarSubqueryExpression)
            && context.Tables.TryGetValue(inOnlyOuter.Name, out var inOnlyOuterTable)
            && context.Tables.TryGetValue(inOnlyInner.Name, out _)
            && !QueryReferencesFromNames(
                inOnlyListQuery,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    inOnlyOuter.Alias ?? inOnlyOuter.Name,
                })
            && inOnlyOuterTable.Indexes.FirstOrDefault(index =>
                QueryExpressionMatchesIndexTerm(inOnlyValue, inOnlyOuterTable, index.Columns[0])) is { } inOnlyIndex)
        {
            var outerAlias = inOnlyOuter.Alias ?? inOnlyOuter.Name;
            var listAlias = inOnlyInner.Alias ?? inOnlyInner.Name;
            var hasOrderBy = statement.OrderBy.Count > 0;
            var rows = new List<SqlValue[]>
            {
                PlanRow(1, 0, "LIST SUBQUERY 1"),
                PlanRow(4, 1, $"SCAN {inOnlyInner.Name}" + (inOnlyInner.Alias is null ? string.Empty : $" AS {listAlias}")),
                PlanRow(13, 0, $"SEARCH {outerAlias} USING INDEX {inOnlyIndex.Name} ({inOnlyValue.UnqualifiedName ?? inOnlyValue.Name}=?)"),
            };
            var rowOps = new List<EqpJsonOp?>
            {
                new EqpJsonListSubqueryOp(1, Correlated: false),
                new EqpJsonScanOp(inOnlyInner.Name, inOnlyInner.Alias, IndexName: null, Covering: false),
                new EqpJsonSearchOp(
                    inOnlyOuter.Name,
                    inOnlyOuter.Alias,
                    inOnlyIndex.Name,
                    Covering: IndexCoversSelect(statement, inOnlyOuterTable, inOnlyIndex),
                    [$"{inOnlyValue.UnqualifiedName ?? inOnlyValue.Name}=?"],
                    SearchKind: "in_seek"),
            };
            if (hasOrderBy)
            {
                rows.Add(PlanRow(29, 0, "USE SORTER FOR ORDER BY"));
                rowOps.Add(new EqpJsonOrderByOp());
            }

            result = new ExecutionResult(ExplainQueryPlanColumns(), rows, 0);
            ops = rowOps;
            return true;
        }

        // LIMIT keeps a correlated IN subquery on the evaluator rather than rewriting it into
        // a semi join. Its execution evaluates the list once for each outer row, so report the
        // real nested scan shape rather than the generic evaluator fallback.
        if (statement.Limit is not null
            && statement.Source is NamedTableSource limitedOuter
            && statement.Where is InSubqueryExpression
            {
                Query: SelectStatement
                {
                    Source: NamedTableSource limitedInner,
                } listQuery,
            } inSubquery
            && QueryReferencesFromNames(
                inSubquery.Query,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    limitedOuter.Alias ?? limitedOuter.Name,
                }))
        {
            var outerAlias = limitedOuter.Alias ?? limitedOuter.Name;
            var innerAlias = limitedInner.Alias ?? limitedInner.Name;
            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, $"SCAN {limitedOuter.Name} AS {outerAlias}"),
                    PlanRow(6, 0, "CORRELATED LIST SUBQUERY 1"),
                    PlanRow(8, 6, $"SCAN {limitedInner.Name} AS {innerAlias}"),
                ],
                0);
            ops =
            [
                new EqpJsonScanOp(limitedOuter.Name, limitedOuter.Alias, IndexName: null, Covering: false),
                new EqpJsonListSubqueryOp(1, Correlated: true),
                new EqpJsonScanOp(limitedInner.Name, limitedInner.Alias, IndexName: null, Covering: false),
            ];
            return true;
        }

        // Group-first aggregate decorrelation materializes the aggregate once per correlation
        // key, then left-joins that grouped result to the outer scan. The evaluator executes
        // this derived source as a grouped table, so expose its real source and grouping work
        // instead of collapsing it into the generic evaluator fallback.
        if (rewritten.Source is JoinTableSource
            {
                Kind: JoinKind.Left,
                Left: NamedTableSource groupedOuter,
                Right: DerivedTableSource
                {
                    Query: SelectStatement
                    {
                        Source: NamedTableSource groupedInner,
                        GroupBy.Count: > 0,
                    } groupedQuery,
                },
            })
        {
            var outerAlias = groupedOuter.Alias ?? groupedOuter.Name;
            var innerAlias = groupedInner.Alias ?? groupedInner.Name;
            var groupedIndexPlan = TryPlanManagedIndexScan(groupedQuery, context);
            var groupedDetail = groupedIndexPlan is null
                ? $"SCAN {groupedInner.Name} AS {innerAlias}"
                : FormatManagedIndexExplainDetail(groupedIndexPlan, groupedQuery);
            var groupedOp = groupedIndexPlan is null
                ? (EqpJsonOp)new EqpJsonScanOp(groupedInner.Name, groupedInner.Alias, IndexName: null, Covering: false)
                : BuildIndexScanOp(groupedIndexPlan, groupedQuery);
            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, $"SCAN {groupedOuter.Name} AS {outerAlias}"),
                    PlanRow(2, 0, "SEARCH scalar_subquery_1"),
                    PlanRow(3, 2, groupedDetail),
                    PlanRow(4, 2, "USE SORTER FOR GROUP BY"),
                ],
                0);
            ops =
            [
                new EqpJsonScanOp(groupedOuter.Name, groupedOuter.Alias, IndexName: null, Covering: false),
                new EqpJsonSearchOp(
                    "scalar_subquery_1",
                    Alias: null,
                    IndexName: null,
                    Covering: false,
                    Constraints: []),
                groupedOp,
                new EqpJsonGroupByOp(),
            ];
            return true;
        }

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

        // EXISTS/NOT EXISTS unnested into an internal semi/anti join (unnest.rs's
        // try_rewrite_exists): the inner table's columns are never visible past a semi/anti join
        // (JoinKind.ProducesLeftShapeOnly), so it is used only for the correlation test itself.
        // GetSemiOrAntiJoinRows prefers the same eligible declared lookup plan below and falls
        // back to its automatic hash lookup when none is available.
        if (TryDescribeSemiAntiJoinChain(rewritten.Source, context, out var chainOuter, out var chainJoins)
            && chainJoins.Count > 1)
        {
            var rows = new List<SqlValue[]> { PlanRow(1, 0, $"SCAN {chainOuter.Name} AS {chainOuter.Alias ?? chainOuter.Name}") };
            var rowOps = new List<EqpJsonOp?>
            {
                new EqpJsonScanOp(chainOuter.Name, chainOuter.Alias, IndexName: null, Covering: false),
            };
            for (var index = 0; index < chainJoins.Count; index++)
            {
                var (kind, inner, column) = chainJoins[index];
                var alias = inner.Alias ?? inner.Name;
                var indexName = $"ephemeral_{inner.Name}_t{(index + 1) * 2 + 1}";
                rows.Add(PlanRow(index + 2, 0, $"SEARCH {alias} USING COVERING INDEX {indexName} ({column}=?)"));
                rowOps.Add(
                    new EqpJsonSearchOp(
                        inner.Name,
                        inner.Alias,
                        indexName,
                        Covering: true,
                        [$"{column}=?"],
                        Join: kind == JoinKind.Semi ? "semi" : "anti",
                        Ephemeral: true));
            }

            result = new ExecutionResult(ExplainQueryPlanColumns(), rows, 0);
            ops = rowOps;
            return true;
        }

        if (rewritten.Source is JoinTableSource
            {
                Kind: JoinKind.Semi or JoinKind.Anti,
            } singleSemiAntiSource
            && singleSemiAntiSource is
            {
                Left: NamedTableSource inOuter,
                Right: NamedTableSource inInner,
            }
            && TryDescribeSemiOrAntiCorrelationColumns(singleSemiAntiSource.Condition, inInner, context, out var inColumns)
            && inColumns.Count > 1)
        {
            var outerAlias = inOuter.Alias ?? inOuter.Name;
            var innerAlias = inInner.Alias ?? inInner.Name;
            var indexName = $"ephemeral_{inInner.Name}_t3";
            var constraints = inColumns.Select(static column => $"{column}=?").ToArray();
            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, $"SCAN {inOuter.Name} AS {outerAlias}"),
                    PlanRow(2, 0, $"SEARCH {innerAlias} USING COVERING INDEX {indexName} ({string.Join(" AND ", constraints)})"),
                ],
                0);
            ops =
            [
                new EqpJsonScanOp(inOuter.Name, inOuter.Alias, IndexName: null, Covering: false),
                new EqpJsonSearchOp(
                    inInner.Name,
                    inInner.Alias,
                    indexName,
                    Covering: true,
                    constraints,
                    Join: singleSemiAntiSource.Kind == JoinKind.Semi ? "semi" : "anti",
                    Ephemeral: true),
            ];
            return true;
        }

        if (rewritten.Source is JoinTableSource
            {
                Kind: JoinKind.Semi or JoinKind.Anti,
            } semiAntiSource
            && semiAntiSource is
            {
                Left: NamedTableSource semiOuter,
                Right: NamedTableSource semiInner,
                Condition: BinaryExpression { Operator: BinaryOperator.Equal } semiCondition,
            }
            && TryDescribeSemiOrAntiCorrelationColumn(semiCondition, semiInner, context, out var innerColumn))
        {
            var outerAlias = semiOuter.Alias ?? semiOuter.Name;
            var innerAlias = semiInner.Alias ?? semiInner.Name;
            var lookupPlan = TryPlanDeclaredIndexLookup(semiInner, semiCondition, context);
            var indexName = lookupPlan?.Index.Name;
            var ephemeral = lookupPlan is null;
            var reportedIndexName = indexName ?? $"ephemeral_{semiInner.Name}_t3";
            var indexDetail = lookupPlan is null
                ? $"COVERING INDEX {reportedIndexName}"
                : $"COVERING INDEX {reportedIndexName}";
            result = new ExecutionResult(
                ExplainQueryPlanColumns(),
                [
                    PlanRow(1, 0, $"SCAN {semiOuter.Name} AS {outerAlias}"),
                    PlanRow(2, 0, $"SEARCH {innerAlias} USING {indexDetail} ({innerColumn}=?)"),
                ],
                0);
            ops =
            [
                new EqpJsonScanOp(semiOuter.Name, semiOuter.Alias, IndexName: null, Covering: false),
                new EqpJsonSearchOp(
                    semiInner.Name,
                    semiInner.Alias,
                    reportedIndexName,
                    Covering: true,
                    [$"{innerColumn}=?"],
                    Join: semiAntiSource.Kind == JoinKind.Semi ? "semi" : "anti",
                    Ephemeral: ephemeral),
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

            var rows = new List<SqlValue[]>(correlated.Count * 2 + 1) { PlanRow(1, 0, outerDetail) };
            var rowOps = new List<EqpJsonOp?>(correlated.Count * 2 + 1) { outerOp };
            var nextId = 2;
            for (var index = 0; index < correlated.Count; index++)
            {
                var subqueryId = nextId++;
                rows.Add(PlanRow(subqueryId, 0, $"CORRELATED SCALAR SUBQUERY {index + 1}"));
                rowOps.Add(new EqpJsonScalarSubqueryOp(index + 1, Correlated: true));

                // Aggregate correlated subqueries use the same managed index planner and
                // equality-pruned traversal as normal execution. Emit the child only for that
                // execution-backed shape; other scalar subqueries still execute through their
                // existing generic evaluator route.
                if (correlated[index].Query is SelectStatement inner
                    && IsAggregateSelect(inner)
                    && TryPlanManagedIndexScan(inner, context) is { Search: true } innerIndexPlan)
                {
                    rows.Add(PlanRow(
                        nextId++,
                        subqueryId,
                        FormatManagedIndexExplainDetail(innerIndexPlan, inner)));
                    rowOps.Add(BuildIndexScanOp(innerIndexPlan, inner));
                }
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
        return plan.Search
            ? new EqpJsonSearchOp(
                plan.Table.Name,
                plan.Source.Alias,
                plan.Index.Name,
                covering,
                [plan.SearchConstraint ?? $"{plan.Index.Columns[0].Name}=?"])
            : new EqpJsonScanOp(plan.Table.Name, plan.Source.Alias, plan.Index.Name, covering);
    }

    private static SqlValue[] PlanRow(int id, int parent, string detail) =>
        [SqlValue.Integer(id), SqlValue.Integer(parent), SqlValue.Integer(0), SqlValue.Text(detail)];

    /// <summary>
    /// Extracts the inner table's correlation column name from a semi/anti join's equality
    /// condition (<c>inner.col = outer.expr</c> or <c>outer.expr = inner.col</c>), so the
    /// describer can print it into the AUTOMATIC COVERING INDEX constraint text. Declines
    /// (returns <see langword="false"/>) for anything other than one plain column reference
    /// qualified by the inner table's own alias/name on exactly one side -- a composite or
    /// expression key is not attempted here.
    /// </summary>
    private static bool TryDescribeSemiOrAntiCorrelationColumn(
        BinaryExpression condition,
        NamedTableSource inner,
        QueryContext context,
        out string columnName)
    {
        columnName = string.Empty;
        var innerQualifier = inner.Alias ?? inner.Name;
        var leftIsInner = IsColumnQualifiedBy(condition.Left, innerQualifier);
        var rightIsInner = IsColumnQualifiedBy(condition.Right, innerQualifier);
        if (leftIsInner == rightIsInner)
            return false;

        var innerColumn = (ColumnExpression)(leftIsInner ? condition.Left : condition.Right);
        columnName = innerColumn.UnqualifiedName ?? innerColumn.Name;
        return context.Tables.TryGetValue(inner.Name, out var table)
            && table.TryGetColumnIndex(columnName, out _);
    }

    private static bool TryDescribeSemiOrAntiCorrelationColumns(
        Expression? condition,
        NamedTableSource inner,
        QueryContext context,
        out IReadOnlyList<string> columnNames)
    {
        columnNames = [];
        if (condition is null)
            return false;

        var names = new List<string>();
        foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(condition))
        {
            if (conjunct is not BinaryExpression { Operator: BinaryOperator.Equal } equality
                || !TryDescribeSemiOrAntiCorrelationColumn(equality, inner, context, out var name)
                || names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            names.Add(name);
        }

        columnNames = names;
        return names.Count > 0;
    }

    private static bool IsColumnQualifiedBy(Expression expression, string qualifier)
        => expression is ColumnExpression { BooleanKeyword: null } column
            && column.Qualifier is not null
            && string.Equals(column.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase);

    private static bool TryDescribeSemiAntiJoinChain(
        TableSource? source,
        QueryContext context,
        out NamedTableSource outer,
        out List<(JoinKind Kind, NamedTableSource Inner, string Column)> joins)
    {
        joins = [];
        return Collect(source, context, joins, out outer);

        static bool Collect(
            TableSource? current,
            QueryContext queryContext,
            List<(JoinKind Kind, NamedTableSource Inner, string Column)> collected,
            out NamedTableSource foundOuter)
        {
            if (current is NamedTableSource named)
            {
                foundOuter = named;
                return true;
            }

            if (current is not JoinTableSource
                {
                    Kind: JoinKind.Semi or JoinKind.Anti,
                    Right: NamedTableSource inner,
                    Condition: BinaryExpression { Operator: BinaryOperator.Equal } condition,
                } join
                || !Collect(join.Left, queryContext, collected, out foundOuter)
                || !TryDescribeSemiOrAntiCorrelationColumn(condition, inner, queryContext, out var column))
            {
                foundOuter = null!;
                return false;
            }

            collected.Add((join.Kind, inner, column));
            return true;
        }
    }
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

/// <summary>A scan through the selected virtual-table module, rather than a base b-tree.</summary>
internal sealed record EqpJsonVirtualTableScanOp(string Table, string? Alias) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"scan\",");
        AppendTableFields(json, Table, Alias, join: null);
        return json.Append(",\"source\":\"virtual_table\"}").ToString();
    }
}

/// <summary>A query delegated to the method the index planner selected.</summary>
internal sealed record EqpJsonIndexMethodOp(string Method) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"index_method\",\"method\":{EmbeddedDatabase.JsonEscape(Method)}}}";
}

/// <summary>A coroutine-style read of a FROM-clause derived subquery.</summary>
internal sealed record EqpJsonSubqueryScanOp(int SubqueryId) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"scan\",\"table\":\"(subquery-{SubqueryId})\",\"subquery\":{{\"execution\":\"coroutine\"}},\"source\":\"subquery\"}}";
}

/// <summary>A compound query and one of its set-operation arms.</summary>
internal sealed record EqpJsonCompoundOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"compound\"}";
}

/// <summary>One arm of a compound query, including whether it requires a temporary B-tree.</summary>
internal sealed record EqpJsonCompoundArmOp(string Operation, bool TempBtree) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"compound_arm\",\"op\":{EmbeddedDatabase.JsonEscape(Operation)},\"temp_btree\":{(TempBtree ? "true" : "false")}}}";
}

/// <summary>The setup and recursive-step phases of a recursive CTE.</summary>
internal sealed record EqpJsonRecursiveSetupOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"recursive_setup\"}";
}

internal sealed record EqpJsonRecursiveStepOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"recursive_step\"}";
}

internal sealed record EqpJsonRecursiveCteScanOp(string Table) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"scan\",\"table\":{EmbeddedDatabase.JsonEscape(Table)},\"subquery\":{{\"execution\":\"coroutine\",\"cte_id\":0,\"recursive\":true}},\"source\":\"subquery\"}}";
}

internal sealed record EqpJsonRecursiveCteInputScanOp(string Table) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"scan\",\"table\":{EmbeddedDatabase.JsonEscape(Table)},\"source\":\"recursive_cte_input\"}}";
}

/// <summary>A join seek into a once-materialized common table expression.</summary>
internal sealed record EqpJsonCteReuseSearchOp(
    string Table,
    string Alias,
    string IndexName,
    string Constraint) : EqpJsonOp
{
    public override string ToJson() =>
        $"{{\"type\":\"search\",\"table\":{EmbeddedDatabase.JsonEscape(Table)},\"alias\":{EmbeddedDatabase.JsonEscape(Alias)},\"join\":\"inner\",\"subquery\":{{\"execution\":\"materialized_reuse\",\"cte_id\":0}},\"search_kind\":\"seek\",\"index\":{{\"name\":{EmbeddedDatabase.JsonEscape(IndexName)},\"covering\":false,\"ephemeral\":true}},\"constraints\":[{EmbeddedDatabase.JsonEscape(Constraint)}]}}";
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
    string SearchKind = "seek",
    bool IsIntegerPrimaryKey = false) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"search\",");
        AppendTableFields(json, Table, Alias, Join);
        json.Append(",\"search_kind\":").Append(EmbeddedDatabase.JsonEscape(SearchKind));
        AppendIndexField(json, IndexName, Covering, Ephemeral);
        // Turso's own contract only sets this when the search has *no* index at all because it
        // seeks the table's declared INTEGER PRIMARY KEY (rowid) directly -- distinct from
        // IndexName being null because the access path is an ephemeral/automatic structure with
        // no real persisted name to report (see the semi/anti-join describer).
        if (IndexName is null && IsIntegerPrimaryKey)
            json.Append(",\"integer_primary_key\":true");
        json.Append(",\"constraints\":[")
            .Append(string.Join(",", Constraints.Select(static c => EmbeddedDatabase.JsonEscape(c))))
            .Append(']');
        return json.Append('}').ToString();
    }
}

/// <summary>Mirrors <c>EqpDetail::MultiIndex</c>: union or intersection of index probes.</summary>
internal sealed record EqpJsonMultiIndexOp(
    string Table,
    IReadOnlyList<string> Indexes,
    bool Union = true,
    string? Alias = null) : EqpJsonOp
{
    public override string ToJson()
    {
        var json = new System.Text.StringBuilder("{\"type\":\"multi_index\",");
        AppendTableFields(json, Table, Alias, join: null);
        json.Append(",\"set_op\":\"")
            .Append(Union ? "or" : "and")
            .Append("\",\"indexes\":[")
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

/// <summary>Mirrors <c>EqpDetail::Distinct</c> for hash-based result deduplication.</summary>
internal sealed record EqpJsonDistinctOp : EqpJsonOp
{
    public override string ToJson() => "{\"type\":\"distinct\"}";
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
