using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// A pure AST rewrite stage that runs between prepare-time validation and select planning.
/// It ports the two conservative shapes of Turso's
/// <c>core/translate/optimizer/unnest.rs</c> (v0.8.0-pre.7) that survive translation to a
/// tree-walking evaluator:
/// <list type="bullet">
/// <item>
/// FROM-clause subquery flattening — SQLite's <c>flattenSubquery()</c>. A single derived
/// table whose SELECT adds nothing but a projection and a filter is hoisted into the
/// enclosing SELECT, so the enclosing WHERE runs against the base scan instead of against a
/// fully materialized intermediate row set.
/// </item>
/// <item>
/// Correlated <c>EXISTS</c> / <c>NOT EXISTS</c> / direct <c>IN</c> unnesting into the
/// internal semi/anti joins (<see cref="JoinKind.Semi"/>, <see cref="JoinKind.Anti"/>),
/// mirroring <c>try_rewrite_exists</c> / <c>try_rewrite_in</c> /
/// <c>rewrite_as_semi_or_anti_join</c>. The inner table is then scanned once for the whole
/// statement instead of once per outer row.
/// </item>
/// <item>
/// Correlated single-value aggregate decorrelation, mirroring
/// <c>try_rewrite_single_value_aggregate</c> and <c>rewrite_aggregate_as_join_then_group</c>.
/// <c>o.v &lt; (SELECT avg(i.v) FROM i WHERE i.k = o.k)</c> becomes either a
/// <b>group-first</b> LEFT JOIN against a <c>GROUP BY</c> derived table keyed by the
/// correlation columns, or — when computing the aggregate for a key no outer row asks for
/// could fail (<c>sum</c> overflow, a fallible input expression) — a <b>join-first</b>
/// LEFT JOIN of the inner table grouped by the outer rowid, with the comparison moved to
/// <c>HAVING</c> and every aggregate guarded by
/// <c>FILTER (WHERE i.rowid IS NOT NULL)</c> so the NULL-padded row a left join invents is
/// not counted.
/// </item>
/// </list>
/// <para>
/// Everything here is fail-closed: any AST node, clause or reference the rewriter does not
/// explicitly model declines the rewrite and leaves the original statement untouched. A
/// declined aggregate subquery keeps the original per-outer-row
/// <c>ExecuteSubquery</c> behavior.
/// </para>
/// <para>
/// The stage runs inside <c>ExecuteSelectStatement</c>, after <c>ValidateQuerySchema</c>, so
/// name-resolution diagnostics are always produced from the <em>original</em> statement and a
/// rewrite can never mask a "no such column" error or invent scope. For the same reason it is
/// skipped inside trigger bodies, where that validation does not run. <c>EXPLAIN</c> and
/// <c>EXPLAIN QUERY PLAN</c> use their own routes and therefore describe the un-rewritten
/// statement.
/// </para>
/// </summary>
public sealed partial class EmbeddedDatabase
{
    private long _flattenedFromSubqueries;
    private long _semiJoinRewrites;
    private long _antiJoinRewrites;
    private long _aggregateGroupFirstRewrites;
    private long _aggregateJoinFirstRewrites;
    private long _aggregateDecorrelationDeclines;

    /// <summary>
    /// Counts of the rewrites this database instance has applied. Test-only evidence that an
    /// eligible shape actually took the rewritten route (and, just as importantly, that an
    /// excluded shape did not).
    /// </summary>
    internal SelectRewriteDiagnostics RewriteDiagnostics => new(
        Interlocked.Read(ref _flattenedFromSubqueries),
        Interlocked.Read(ref _semiJoinRewrites),
        Interlocked.Read(ref _antiJoinRewrites),
        Interlocked.Read(ref _aggregateGroupFirstRewrites),
        Interlocked.Read(ref _aggregateJoinFirstRewrites),
        Interlocked.Read(ref _aggregateDecorrelationDeclines));

    internal void ResetRewriteDiagnostics()
    {
        Interlocked.Exchange(ref _flattenedFromSubqueries, 0);
        Interlocked.Exchange(ref _semiJoinRewrites, 0);
        Interlocked.Exchange(ref _antiJoinRewrites, 0);
        Interlocked.Exchange(ref _aggregateGroupFirstRewrites, 0);
        Interlocked.Exchange(ref _aggregateJoinFirstRewrites, 0);
        Interlocked.Exchange(ref _aggregateDecorrelationDeclines, 0);
    }

    /// <summary>
    /// Applies the rewrite stage to one SELECT. Flattening runs first so a flattened FROM
    /// subquery can still become the outer side of a semi/anti join in the same pass.
    /// <para>
    /// <paramref name="outerRow"/> is the enclosing correlated row, if any. Flattening needs it
    /// to reproduce the inner SELECT's own WHERE name resolution (source column, then enclosing
    /// correlation, then projection alias) before that clause is hoisted into a scope with a
    /// different alias list.
    /// </para>
    /// </summary>
    private SelectStatement RewriteSelectSubqueries(
        SelectStatement select,
        QueryContext context,
        SourceRow? outerRow)
    {
        // Trigger bodies skip ValidateQuerySchema, so a rewrite there could resolve a
        // reference the original statement would have rejected. Never rewrite in that scope.
        if (context.InsideTrigger || context.SchemaValidation || context.IndexExpression)
            return select;

        // Flattening can expose another derived table (`FROM (SELECT … FROM (SELECT …))`), so
        // repeat until it reaches a fixed point. The bound is a guard, not a limit: each pass
        // strictly removes one FROM-clause SELECT.
        var rewritten = select;
        for (var pass = 0; pass < 32; pass++)
        {
            var flattened = TryFlattenFromSubquery(rewritten, context, outerRow);
            if (ReferenceEquals(flattened, rewritten))
                break;

            rewritten = flattened;
        }

        return RewriteCorrelatedAggregateSubqueries(
            RewriteCorrelatedSubqueriesAsJoins(rewritten, context),
            context);
    }

    // ---------------------------------------------------------------------------------------
    // Rewrite 1: FROM-clause derived-table flattening.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Hoists <c>SELECT … FROM (SELECT &lt;proj&gt; FROM &lt;src&gt; WHERE &lt;p&gt;) AS d
    /// WHERE &lt;q&gt;</c> into <c>SELECT … FROM &lt;src&gt; WHERE &lt;p&gt; AND &lt;q'&gt;</c>,
    /// where every reference to a <c>d</c> column is replaced by the inner projection
    /// expression that produced it.
    /// <para>
    /// Supported only for the narrow shape where the derived table is the entire FROM clause.
    /// The inner SELECT must add nothing that changes cardinality, ordering, grouping or row
    /// identity, because the enclosing clauses would then apply at a different point in the
    /// pipeline. See <see cref="CanFlattenInnerSelect"/> for the exact exclusion list.
    /// </para>
    /// </summary>
    private SelectStatement TryFlattenFromSubquery(
        SelectStatement select,
        QueryContext context,
        SourceRow? outerRow)
    {
        if (select.Source is not DerivedTableSource derived
            || derived.Query is not SelectStatement inner
            || !CanFlattenInnerSelect(inner)
            || !CanFlattenOuterSelect(select))
        {
            return select;
        }

        // The visible shape of the derived table: name -> the inner expression that produces
        // it. Star projections expand exactly the way the binding stage expands them, so
        // `SELECT * FROM (SELECT * FROM t)` flattens with the same column identity and order.
        IReadOnlyList<SelectBindingColumn> derivedColumns;
        IReadOnlyList<OutputColumn> derivedOutputColumns;
        IReadOnlyList<OutputColumn> derivedRawOutputColumns;
        IReadOnlyList<OutputColumn> innerOutputColumns;
        IReadOnlyList<OutputColumn> innerRawOutputColumns;
        string[] outerColumnNames;
        string[] innerSourceColumns;
        try
        {
            innerOutputColumns = GetOutputColumns(inner.Source, context);
            innerRawOutputColumns = GetRawOutputColumns(inner.Source, context);
            derivedColumns = GetSelectBindingColumns(
                inner.Projections,
                innerOutputColumns,
                innerRawOutputColumns);
            derivedOutputColumns = GetOutputColumns(select.Source, context);
            derivedRawOutputColumns = GetRawOutputColumns(select.Source, context);
            outerColumnNames = GetColumnNames(
                select.Projections,
                derivedOutputColumns,
                derivedRawOutputColumns);
            innerSourceColumns = GetSourceColumns(inner.Source, context);
        }
        catch (EmbeddedSqlException)
        {
            // Shape discovery failed (unknown table, ambiguous star, …). The unrewritten
            // statement reproduces the same diagnostic on its own execution path.
            return select;
        }

        if (derivedColumns.Count == 0)
            return select;

        // A star projection over a USING/NATURAL join whose left side can be NULL-extended
        // exposes COALESCE(left, right), which the star expansion cannot express as a plain
        // column reference. Hoisting it would silently downgrade the coalesced column to the
        // NULL-padded left one.
        if (!CanFlattenInnerStarOutput(inner, innerOutputColumns))
            return select;

        var byName = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in derivedColumns)
        {
            // Two visible columns with one name (`SELECT x.a, y.a FROM x, y`) make the
            // substitution positional rather than by name. Decline instead of guessing.
            if (!byName.TryAdd(column.Name, column.Expression))
                return select;
        }

        var alias = derived.Alias;
        var innerNames = new HashSet<string>(innerSourceColumns, StringComparer.OrdinalIgnoreCase);

        // The names the inner FROM clause brings into scope once it is hoisted. Before the
        // rewrite they live inside the derived table and are invisible to the enclosing SELECT,
        // so `d.x` is the only qualifier that resolves here and every other one is an
        // enclosing-scope correlation. Hoisting makes them visible, which would silently
        // re-point a correlated `x.v` at the hoisted `u AS x` instead of the outer `t AS x`.
        var innerSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromSourceNames(inner.Source, innerSourceNames);
        if (alias is not null)
            innerSourceNames.Remove(alias);

        // Any reference to a derived column from inside a nested subquery would have to be
        // rewritten within that subquery's own scope, where an unqualified name may bind
        // locally instead. Decline rather than model nested scopes here.
        if (!IsFreeOfNestedDerivedReferences(select, alias, byName, innerSourceNames))
            return select;

        var referenceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var declined = false;

        Expression? Substitute(ColumnExpression column)
        {
            if (declined)
                return null;

            var name = column.UnqualifiedName ?? column.Name;
            if (column.Qualifier is { } qualifier)
            {
                if (alias is null || !string.Equals(qualifier, alias, StringComparison.OrdinalIgnoreCase))
                {
                    // Qualified by something else: either an enclosing-scope correlation (which
                    // keeps resolving through the outer row chain) or a reference the original
                    // statement already rejected during validation. Leave it alone — unless the
                    // hoisted FROM clause would start answering to that qualifier.
                    if (innerSourceNames.Contains(qualifier))
                        declined = true;

                    return null;
                }

                if (!byName.TryGetValue(name, out var replacement))
                {
                    // `d.rowid` and friends: a derived table exposes no rowid, so the original
                    // statement fails validation. Decline defensively.
                    declined = true;
                    return null;
                }

                Count(name);
                return replacement;
            }

            if (byName.TryGetValue(name, out var unqualified))
            {
                Count(name);
                return unqualified;
            }

            // Not a derived column. After flattening the inner source's own columns become
            // visible here, so a name that used to resolve in an enclosing scope (or fail)
            // could silently bind to the base table instead.
            if (innerNames.Contains(name) || IsRowIdAlias(name))
                declined = true;

            return null;
        }

        void Count(string name)
            => referenceCounts[name] = referenceCounts.TryGetValue(name, out var count) ? count + 1 : 1;

        var substitution = AstSubstitution.ForColumns(Substitute);
        var projections = RewriteFlattenedProjections(
            select,
            derivedColumns,
            outerColumnNames,
            alias,
            substitution,
            Count,
            ref declined);
        if (declined || projections is null)
            return select;

        if (!TryRewriteClause(select.Where, substitution, ref declined, out var outerWhere)
            || !TryRewriteClause(select.Having, substitution, ref declined, out var having)
            || !TryRewriteClause(select.Limit, substitution, ref declined, out var limit)
            || !TryRewriteClause(select.Offset, substitution, ref declined, out var offset)
            || !TryRewriteExpressionList(select.GroupBy, substitution, ref declined, out var groupBy)
            || !TryRewriteOrderBy(select.OrderBy, substitution, ref declined, out var orderBy)
            || declined)
        {
            return select;
        }

        // The inner WHERE is about to be evaluated in the enclosing SELECT's scope, whose
        // projection aliases are a different list. Bind its alias references here — against the
        // inner projection list — so the hoisted clause keeps the meaning the derived table
        // gave it.
        if (!TryBindFlattenedInnerWhere(
                inner,
                innerOutputColumns,
                innerRawOutputColumns,
                projections,
                outerRow,
                Count,
                out var innerWhere))
        {
            return select;
        }

        // A nondeterministic projection may be substituted at most once in total: duplicating
        // `random()` into both the result column and the WHERE would draw two different values
        // where the derived table computed one. (unnest.rs rejects the same hazard through
        // expr_contains_nondeterministic_scalar_function.)
        foreach (var column in derivedColumns)
        {
            if (IsDuplicationSafeExpression(column.Expression))
                continue;

            if (!referenceCounts.TryGetValue(column.Name, out var count) || count > 1)
                return select;
        }

        Interlocked.Increment(ref _flattenedFromSubqueries);
        return select with
        {
            Source = inner.Source,
            Projections = projections,
            // The inner filter is placed first so a guard such as `WHERE b <> 0` still runs
            // before an outer term that depends on it under left-to-right AND evaluation.
            Where = CombineConjuncts(innerWhere, outerWhere),
            Having = having,
            GroupBy = groupBy,
            OrderBy = orderBy,
            Limit = limit,
            Offset = offset,
        };
    }

    /// <summary>
    /// The inner-SELECT exclusion list. Each excluded clause changes which rows, in which
    /// order, or how many times the enclosing clauses would see them, and none of them can be
    /// preserved by simply moving the enclosing WHERE next to the inner one.
    /// </summary>
    private bool CanFlattenInnerSelect(SelectStatement inner)
    {
        if (inner.Source is null
            || inner.Distinct                        // duplicate elimination happens before the outer WHERE
            || inner.GroupBy.Count != 0              // grouping changes row identity
            || inner.Having is not null
            || inner.NamedWindows.Count != 0
            || inner.OrderBy.Count != 0              // ordering feeds an outer LIMIT
            || inner.Limit is not null               // cardinality-limiting
            || inner.Offset is not null)
        {
            return false;
        }

        foreach (var projection in inner.Projections)
        {
            if (projection.Expression is StarExpression or QualifiedStarExpression)
                continue;

            // An aggregate or window call collapses/annotates rows before the outer clauses
            // run; a subquery in the select list would move to a different scope.
            if (ContainsAggregate(projection.Expression)
                || ContainsWindowFunction(projection.Expression)
                || ContainsSubqueryExpression(projection.Expression)
                // SQLite strips the JSON subtype at the FROM-clause co-routine boundary
                // (conformance/sqlite-sqltests/json/json_subtype_strip.sqltest), and the
                // derived-table path reproduces that in MaterializeQueryResult. Flattening
                // removes the boundary, so the value would keep a subtype it must lose.
                || ContainsSubtypeSensitiveExpression(projection.Expression))
            {
                return false;
            }
        }

        // The inner WHERE stays a WHERE over the same source, so it may keep subqueries; an
        // aggregate or window there is already illegal SQL.
        return inner.Where is null
            || (!ContainsAggregate(inner.Where) && !ContainsWindowFunction(inner.Where));
    }

    /// <summary>
    /// The enclosing-SELECT exclusion list. Window machinery carries expressions this rewriter
    /// does not rewrite (partition/order/frame specifications), and a compound or VALUES body
    /// never reaches here.
    /// </summary>
    private bool CanFlattenOuterSelect(SelectStatement select)
    {
        if (select.NamedWindows.Count != 0)
            return false;

        foreach (var projection in select.Projections)
        {
            if (projection.Expression is not (StarExpression or QualifiedStarExpression)
                && ContainsWindowFunction(projection.Expression))
            {
                return false;
            }
        }

        foreach (var term in select.OrderBy)
        {
            if (ContainsWindowFunction(term.Expression))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when the inner SELECT's star expansion can be re-expressed as plain column
    /// references without losing anything.
    /// <para>
    /// A USING/NATURAL join publishes one coalesced output column per joined name, whose value
    /// is <c>COALESCE(left, right)</c>, and both <c>*</c> and <c>t.*</c> report that coalesced
    /// value. <see cref="GetSelectBindingColumns"/> expands a star into <em>qualified</em>
    /// references, and a qualified reference written by hand deliberately reads the raw left
    /// slot (SQLite: <c>a.k</c> is not coalesced). That substitution is exact while the left
    /// slot can never be NULL-extended — INNER and LEFT joins, where an unmatched left <c>k</c>
    /// is NULL on both sides anyway — but under a RIGHT or FULL join the left slot is
    /// NULL-padded exactly where the coalesced column must report the surviving right value.
    /// Decline instead of publishing the padded slot.
    /// </para>
    /// <para>
    /// A projection that names the joined column directly (<c>SELECT k, …</c>) is unaffected: it
    /// stays an unqualified reference, which <c>SourceRow</c> resolves through the coalesced
    /// column both before and after the hoist.
    /// </para>
    /// </summary>
    private static bool CanFlattenInnerStarOutput(
        SelectStatement inner,
        IReadOnlyList<OutputColumn> innerOutputColumns)
    {
        var hasStar = false;
        foreach (var projection in inner.Projections)
        {
            if (projection.Expression is StarExpression or QualifiedStarExpression)
            {
                hasStar = true;
                break;
            }
        }

        if (!hasStar || !SourceCanNullExtendLeftSide(inner.Source))
            return true;

        foreach (var column in innerOutputColumns)
        {
            if (column.CoalesceIndex is not null || column.AdditionalCoalesceIndices is { Count: > 0 })
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="source"/> contains a RIGHT or FULL join anywhere, i.e. a join
    /// that can NULL-extend the columns of its own left side.
    /// </summary>
    private static bool SourceCanNullExtendLeftSide(TableSource? source)
        => source is JoinTableSource join
            && (join.Kind is JoinKind.Right or JoinKind.Full
                || SourceCanNullExtendLeftSide(join.Left)
                || SourceCanNullExtendLeftSide(join.Right));

    /// <summary>
    /// Re-binds the inner SELECT's WHERE clause the way <c>ResolveWhereAliasFallback</c> would
    /// have bound it had the derived table executed on its own, before the clause is hoisted
    /// into the enclosing SELECT.
    /// <para>
    /// SQLite resolves a bare WHERE name canonical-first: a source column wins, then an
    /// enclosing correlated row, and only then a projection alias of the <em>same</em> SELECT.
    /// The first two resolve identically after the hoist — the inner FROM clause becomes the
    /// enclosing FROM clause and the correlation chain is unchanged — but the alias list does
    /// not, so <c>SELECT a AS x FROM t WHERE x &gt; 0</c> would start reading the enclosing
    /// SELECT's <c>x</c>. Bind those references to the inner projection expression here.
    /// </para>
    /// <para>
    /// A name that resolves to none of the three (a <c>rowid</c> alias, or a reference the
    /// derived table itself rejects) keeps its own meaning after the hoist <em>unless</em> the
    /// enclosing projection list aliases that same name, in which case the enclosing alias would
    /// capture it. Decline that case rather than model it.
    /// </para>
    /// </summary>
    private bool TryBindFlattenedInnerWhere(
        SelectStatement inner,
        IReadOnlyList<OutputColumn> innerOutputColumns,
        IReadOnlyList<OutputColumn> innerRawOutputColumns,
        IReadOnlyList<Projection> outerProjections,
        SourceRow? outerRow,
        Action<string> count,
        out Expression? bound)
    {
        bound = inner.Where;
        if (inner.Where is null)
            return true;

        var outerAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projection in outerProjections)
        {
            if (projection.Alias is { } alias)
                outerAliases.Add(alias);
        }

        var declined = false;
        bound = RewriteColumnReferences(inner.Where, column =>
        {
            if (declined || column.Qualifier is not null)
                return null;

            if (ResolvesInLocalSource(column.Name, innerOutputColumns, innerRawOutputColumns)
                || ResolvesInOuterRow(column, outerRow))
            {
                return null;
            }

            if (TryFindProjectionAlias(column.Name, inner.Projections, out var expression))
            {
                // A substituted alias is one more use of that derived column, so it counts
                // against the nondeterministic-duplication budget just like a reference from
                // the enclosing clauses does.
                count(column.Name);
                return expression;
            }

            if (outerAliases.Contains(column.Name))
                declined = true;

            return null;
        });

        return !declined;
    }

    private IReadOnlyList<Projection>? RewriteFlattenedProjections(
        SelectStatement select,
        IReadOnlyList<SelectBindingColumn> derivedColumns,
        string[] outerColumnNames,
        string? alias,
        AstSubstitution substitute,
        Action<string> count,
        ref bool declined)
    {
        var result = new List<Projection>(outerColumnNames.Length);
        var nameIndex = 0;
        foreach (var projection in select.Projections)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    foreach (var column in derivedColumns)
                    {
                        if (nameIndex >= outerColumnNames.Length)
                            return null;

                        // A star expansion substitutes the inner expression too, so it counts
                        // toward the duplication budget just like a named reference.
                        count(column.Name);
                        result.Add(new Projection(column.Expression, outerColumnNames[nameIndex++]));
                    }

                    break;
                case QualifiedStarExpression qualifiedStar:
                    if (alias is null
                        || !string.Equals(qualifiedStar.Qualifier, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    foreach (var column in derivedColumns)
                    {
                        if (nameIndex >= outerColumnNames.Length)
                            return null;

                        count(column.Name);
                        result.Add(new Projection(column.Expression, outerColumnNames[nameIndex++]));
                    }

                    break;
                default:
                    {
                        if (nameIndex >= outerColumnNames.Length)
                            return null;

                        if (!TryRewriteExpression(projection.Expression, substitute, ref declined, out var rewritten))
                            return null;

                        var visibleName = outerColumnNames[nameIndex++];
                        result.Add(ReferenceEquals(rewritten, projection.Expression)
                            ? projection
                            // Substituting `x` with `t.a` would otherwise rename the result
                            // column. Pin the original visible name as an explicit alias.
                            : projection with { Expression = rewritten, Alias = projection.Alias ?? visibleName });
                        break;
                    }
            }
        }

        return nameIndex == outerColumnNames.Length ? result : null;
    }

    /// <summary>
    /// True when no nested subquery anywhere in <paramref name="select"/>'s clauses could bind
    /// a reference to the derived table being flattened, and none of them correlates through a
    /// qualifier the hoisted inner FROM clause would start answering to. A qualified <c>d.x</c>
    /// would need rewriting inside the subquery, an unqualified name matching a derived column
    /// could bind either locally or to <c>d</c> depending on the subquery's own FROM clause, and
    /// a correlated <c>x.v</c> would silently re-point at the hoisted source once it is named
    /// <c>x</c> in this scope.
    /// </summary>
    private static bool IsFreeOfNestedDerivedReferences(
        SelectStatement select,
        string? alias,
        Dictionary<string, Expression> derivedColumns,
        IReadOnlySet<string> innerSourceNames)
    {
        var subqueries = new List<QueryStatement>();
        foreach (var projection in select.Projections)
        {
            if (!TryCollectSubqueries(projection.Expression, subqueries))
                return false;
        }

        if (!TryCollectSubqueries(select.Where, subqueries)
            || !TryCollectSubqueries(select.Having, subqueries)
            || !TryCollectSubqueries(select.Limit, subqueries)
            || !TryCollectSubqueries(select.Offset, subqueries))
        {
            return false;
        }

        foreach (var expression in select.GroupBy)
        {
            if (!TryCollectSubqueries(expression, subqueries))
                return false;
        }

        foreach (var term in select.OrderBy)
        {
            if (!TryCollectSubqueries(term.Expression, subqueries))
                return false;
        }

        foreach (var query in subqueries)
        {
            var safe = ForEachColumnReference(query, column =>
            {
                var name = column.UnqualifiedName ?? column.Name;
                if (column.Qualifier is { } qualifier)
                {
                    if (innerSourceNames.Contains(qualifier))
                        return false;

                    return alias is null
                        || !string.Equals(qualifier, alias, StringComparison.OrdinalIgnoreCase);
                }

                return !derivedColumns.ContainsKey(name);
            });

            if (!safe)
                return false;
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Rewrite 2: correlated EXISTS / NOT EXISTS / IN -> semi / anti join.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Turns each eligible top-level conjunct of the WHERE clause into a semi or anti join.
    /// Mirrors <c>rewrite_correlated_subqueries</c>: a positive <c>EXISTS</c> becomes a
    /// semi-join, <c>NOT EXISTS</c> an anti-join, and a direct positive <c>IN</c> a semi-join
    /// with one extra equality. <c>NOT IN</c> is never rewritten because a NULL on either side
    /// changes its answer, and the rewrite never produces an inner join because that would
    /// emit one outer row per inner match.
    /// </summary>
    private SelectStatement RewriteCorrelatedSubqueriesAsJoins(SelectStatement select, QueryContext context)
    {
        if (select.Where is null || select.Source is null)
            return select;

        // A term moved out of the subquery runs in the outer WHERE, where it would filter rows
        // an outer join is required to null-pad. unnest.rs rejects only the terms that touch a
        // null-supplying table; declining the whole statement is the conservative superset.
        if (SourceContainsOuterJoin(select.Source))
            return select;

        var terms = IndexExpressionSemantics.SplitConjuncts(select.Where);
        if (!IsShortCircuitSafeToReorder(terms))
            return select;

        var source = select.Source;
        List<Expression>? remaining = null;
        var changed = false;

        for (var index = 0; index < terms.Count; index++)
        {
            if (!TryRewriteSubqueryTermAsJoin(terms[index], source, context, out var rewritten))
                continue;

            source = rewritten;
            remaining ??= [.. terms];
            remaining[index] = null!;
            changed = true;
        }

        if (!changed)
            return select;

        Expression? where = null;
        foreach (var term in remaining!)
        {
            if (term is not null)
                where = CombineConjuncts(where, term);
        }

        return select with { Source = source, Where = where };
    }

    /// <summary>
    /// True when turning a WHERE conjunct into a join cannot change which errors the statement
    /// raises.
    /// <para>
    /// <c>AND</c> evaluates left to right and stops at the first false, so a WHERE clause is
    /// also an error guard: in <c>WHERE o.k = 1 AND EXISTS (… json_extract(o.j,'$.a') …)</c>
    /// the subquery never runs for a row the first term rejects, and in
    /// <c>WHERE json_extract(o.j,'$.a') = 1 AND EXISTS (…)</c> the malformed row raises before
    /// the subquery is reached. A join runs before every remaining WHERE term and for every
    /// outer row, which moves both boundaries: the first shape gains an error, the second
    /// loses one.
    /// </para>
    /// <para>
    /// A single conjunct has nothing to short-circuit against, so it is always safe. With more
    /// than one, the rewrite is declined as soon as <em>any</em> term can raise on its input —
    /// the same strict <c>expression_can_fail_on_input</c> classification unnest.rs uses, where
    /// every function call counts because no list of provably total functions exists, extended
    /// with the collation callbacks this engine lets an application register.
    /// </para>
    /// </summary>
    private bool IsShortCircuitSafeToReorder(IReadOnlyList<Expression> terms)
    {
        if (terms.Count < 2)
            return true;

        foreach (var term in terms)
        {
            if (ExpressionCanFail(term))
                return false;
        }

        return true;
    }

    private bool TryRewriteSubqueryTermAsJoin(
        Expression term,
        TableSource outerSource,
        QueryContext context,
        out TableSource rewritten)
    {
        rewritten = null!;

        QueryStatement query;
        JoinKind kind;
        Expression? inValue = null;
        switch (term)
        {
            case ExistsExpression { Negated: false } exists:
                query = exists.Query;
                kind = JoinKind.Semi;
                break;
            case ExistsExpression { Negated: true } notExists:
                query = notExists.Query;
                kind = JoinKind.Anti;
                break;
            case UnaryExpression { Operator: UnaryOperator.Not, Operand: ExistsExpression { Negated: false } negatedExists }:
                query = negatedExists.Query;
                kind = JoinKind.Anti;
                break;
            case InSubqueryExpression { Negated: false } inSubquery:
                query = inSubquery.Query;
                kind = JoinKind.Semi;
                inValue = inSubquery.Value;
                break;
            default:
                // NOT IN stays a subquery: `x NOT IN (SELECT y …)` yields NULL when any y is
                // NULL, which an anti-join's "no matching row" answer cannot reproduce.
                return false;
        }

        if (query is not SelectStatement inner)
            return false;

        if (!TryGetSemiJoinInnerTable(inner, context, out var innerTable, out var innerQualifier))
            return false;

        if (!CanRewriteAsSemiJoin(inner, context))
            return false;

        var innerColumns = new HashSet<string>(
            GetSourceColumns(innerTable, context),
            StringComparer.OrdinalIgnoreCase);

        var condition = inner.Where;
        Expression? inEquality = null;
        if (inValue is not null)
        {
            if (inner.Projections.Count != 1
                || inner.Projections[0].Expression is StarExpression or QualifiedStarExpression)
            {
                return false;
            }

            var innerValue = inner.Projections[0].Expression;

            // IN runs its subquery to completion and evaluates its left side once per outer
            // row; a semi-join stops at the first match and re-evaluates the left side per
            // inner row. Both differences are only observable when an operand can raise, so
            // require operands that cannot (unnest.rs:291-314).
            //
            // The inner WHERE is part of that contract: `1 IN (SELECT v FROM t WHERE
            // json_extract(t.j,'$.a') IS NOT NULL)` must still fail on a malformed row that
            // sits *after* the matching one, and the semi-join would never reach it.
            if (ContainsAggregate(innerValue)
                || ContainsWindowFunction(innerValue)
                || ContainsSubqueryExpression(innerValue)
                || ContainsSubqueryExpression(inValue)
                || ExpressionCanFail(innerValue)
                || ExpressionCanFail(inValue)
                || ExpressionCanFail(inner.Where)
                || ContainsNonDeterministicFunction(innerValue)
                || ContainsNonDeterministicFunction(inValue))
            {
                return false;
            }

            // `x IN (SELECT y …)` is exactly `EXISTS (SELECT 1 … WHERE x = y)` in a WHERE
            // context: the operator's three-valued NULL result and its false result are both
            // filtered out there, so a plain equality reproduces it.
            inEquality = new BinaryExpression(inValue, BinaryOperator.Equal, innerValue);
            condition = CombineConjuncts(condition, inEquality);
        }
        else if (ContainsParameterExpression(inner.Projections))
        {
            // EXISTS ignores its select list, so the rewrite drops it. Turso keeps the
            // parameters as phantoms; declining is the equivalent fail-closed answer.
            return false;
        }

        // EXISTS needs the same guard the IN branch applies to its inner WHERE, for the same
        // reason. A correlated `EXISTS (SELECT 1 FROM t WHERE t.k = o.k AND
        // json_extract(t.j,'$.a') = 1)` evaluates its predicate over whichever inner rows the
        // subquery plan visits; as a semi-join the join condition drives the inner scan, so rows
        // that would have raised are skipped — or, for an outer row a later WHERE term rejects,
        // rows that never ran now do. Both directions change which errors the statement raises,
        // so any inner predicate that can fail on its input declines the rewrite.
        if (ExpressionCanFail(inner.Where)
            || ContainsNonDeterministicFunction(inner.Where))
        {
            return false;
        }

        if (condition is null)
            return false;

        var outerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromSourceNames(outerSource, outerNames);
        if (outerNames.Count == 0)
            return false;

        var conjuncts = IndexExpressionSemantics.SplitConjuncts(condition);
        var correlated = false;
        foreach (var conjunct in conjuncts)
        {
            if (!TryClassifyJoinTerm(
                    conjunct,
                    innerQualifier,
                    innerColumns,
                    out var referencesInner,
                    out var referencesOuter))
            {
                return false;
            }

            // Every term that reaches out of the subquery must be a plain equality with all
            // inner references on one side and all outer references on the other, so it can
            // run as a join condition (unnest.rs:1423-1478).
            if (referencesOuter)
            {
                // The equality synthesized for IN always spans both scopes; it is what makes
                // the join, not evidence that the subquery itself was correlated.
                if (!ReferenceEquals(conjunct, inEquality))
                    correlated = true;
                if (!IsInnerOuterEquality(conjunct, innerQualifier, innerColumns))
                    return false;
            }

            // An anti-join drops an outer row when the inner side matches. A term that never
            // reads the inner table is a constant filter on the outer row; moving it into the
            // join would reject rows that NOT EXISTS keeps (unnest.rs:372-381).
            if (kind == JoinKind.Anti && !referencesInner)
                return false;
        }

        // Only a correlated subquery benefits: an uncorrelated one already evaluates once per
        // statement through the subquery memo, and a semi-join would replace that single
        // result with a per-row scan.
        if (!correlated)
            return false;

        rewritten = new JoinTableSource(outerSource, innerTable, condition, kind);
        if (kind == JoinKind.Semi)
            Interlocked.Increment(ref _semiJoinRewrites);
        else
            Interlocked.Increment(ref _antiJoinRewrites);

        return true;
    }

    /// <summary>
    /// The subquery must read exactly one ordinary base table. The semi/anti loop probes a
    /// single materialized row set, so a joined, derived, virtual, view, CTE or table-valued
    /// inner source declines (unnest.rs:1300-1341 keeps the same one-table restriction).
    /// </summary>
    private bool TryGetSemiJoinInnerTable(
        SelectStatement inner,
        QueryContext context,
        out NamedTableSource innerTable,
        out string innerQualifier)
        => TryGetDecorrelationBaseTable(inner.Source, context, out innerTable, out innerQualifier);

    /// <summary>
    /// Clause-level exclusions for a semi/anti join candidate, mirroring
    /// <c>can_rewrite_as_semi_join</c>. The loop stops at the first matching inner row, so any
    /// clause that changes which rows exist, how many there are, or in what order they arrive
    /// would change the answer.
    /// </summary>
    private bool CanRewriteAsSemiJoin(SelectStatement inner, QueryContext context)
    {
        _ = context;
        if (inner.Distinct
            || inner.GroupBy.Count != 0
            || inner.Having is not null
            || inner.OrderBy.Count != 0
            || inner.NamedWindows.Count != 0
            || inner.Limit is not null
            || inner.Offset is not null)
        {
            return false;
        }

        foreach (var projection in inner.Projections)
        {
            if (projection.Expression is StarExpression or QualifiedStarExpression)
                continue;

            // An aggregate returns a row even when the inner table is empty, so EXISTS is
            // always true and no join can express that.
            if (ContainsAggregate(projection.Expression) || ContainsWindowFunction(projection.Expression))
                return false;
        }

        if (inner.Where is null)
            return false;

        return !ContainsAggregate(inner.Where)
            && !ContainsWindowFunction(inner.Where)
            // A nested subquery in the moved WHERE would have to keep its own correlation
            // scope; the join condition runs in a different one.
            && !ContainsSubqueryExpression(inner.Where)
            // The condition runs once per (outer, inner) pair instead of once per inner row,
            // so a value that changes between calls would change the answer.
            && !ContainsNonDeterministicFunction(inner.Where);
    }

    /// <summary>
    /// Classifies one moved WHERE conjunct by which side its column references come from. A
    /// reference is "inner" when it is qualified by the subquery's table (or alias) or is
    /// unqualified and names one of its columns — SQLite's own shadowing rule — and "outer"
    /// otherwise. Returns false for any expression node the classifier does not model.
    /// </summary>
    private static bool TryClassifyJoinTerm(
        Expression expression,
        string innerQualifier,
        HashSet<string> innerColumns,
        out bool referencesInner,
        out bool referencesOuter)
    {
        var inner = false;
        var outer = false;
        var modelled = ForEachColumnReference(expression, column =>
        {
            if (IsInnerColumnReference(column, innerQualifier, innerColumns))
                inner = true;
            else
                outer = true;

            return true;
        });

        referencesInner = inner;
        referencesOuter = outer;
        return modelled;
    }

    private static bool IsInnerColumnReference(
        ColumnExpression column,
        string innerQualifier,
        HashSet<string> innerColumns)
    {
        var name = column.UnqualifiedName ?? column.Name;
        return column.Qualifier is { } qualifier
            ? string.Equals(qualifier, innerQualifier, StringComparison.OrdinalIgnoreCase)
            : innerColumns.Contains(name) || IsRowIdAlias(name);
    }

    /// <summary>
    /// True when the conjunct is <c>inner = outer</c> or <c>outer = inner</c> with each side
    /// referencing exactly one of the two scopes (unnest.rs:1448-1478). Any other operator, or
    /// a side mixing both scopes, cannot be evaluated as a join key.
    /// </summary>
    private static bool IsInnerOuterEquality(
        Expression expression,
        string innerQualifier,
        HashSet<string> innerColumns)
    {
        if (expression is not BinaryExpression { Operator: BinaryOperator.Equal } equality)
            return false;

        if (!TryClassifyJoinTerm(equality.Left, innerQualifier, innerColumns, out var leftInner, out var leftOuter)
            || !TryClassifyJoinTerm(equality.Right, innerQualifier, innerColumns, out var rightInner, out var rightOuter))
        {
            return false;
        }

        var leftIsInnerOnly = leftInner && !leftOuter;
        var leftIsOuterOnly = leftOuter && !leftInner;
        var rightIsInnerOnly = rightInner && !rightOuter;
        var rightIsOuterOnly = rightOuter && !rightInner;
        return (leftIsInnerOnly && rightIsOuterOnly) || (leftIsOuterOnly && rightIsInnerOnly);
    }

    private static bool SourceContainsOuterJoin(TableSource? source)
        => source is JoinTableSource join
            && (join.Kind is JoinKind.Left or JoinKind.Right or JoinKind.Full
                || SourceContainsOuterJoin(join.Left)
                || SourceContainsOuterJoin(join.Right));

    // ---------------------------------------------------------------------------------------
    // Rewrite 3: correlated single-value aggregate subquery decorrelation.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Decorrelates every eligible correlated single-value aggregate subquery of one SELECT,
    /// mirroring <c>rewrite_correlated_subqueries</c>' aggregate arm
    /// (turso-src/core/translate/optimizer/unnest.rs:171-215) through
    /// <c>try_rewrite_single_value_aggregate</c> (unnest.rs:500-688).
    /// <para>
    /// Group-first runs when computing the aggregate for a correlation key that no outer row
    /// asks for cannot fail. It computes each key once, adds one grouped table per subquery, and
    /// keeps every outer clause intact. Otherwise join-first is tried, which never touches an
    /// unused key but restructures the whole statement and therefore only runs when it is the
    /// single candidate. When neither applies the statement is returned unchanged and the
    /// subquery keeps its per-outer-row evaluation.
    /// </para>
    /// <para>
    /// Every candidate is analysed against the <em>original</em> FROM clause, before any grouped
    /// table is appended, so a second subquery is judged by the same scope the first one was.
    /// </para>
    /// </summary>
    private SelectStatement RewriteCorrelatedAggregateSubqueries(
        SelectStatement select,
        QueryContext context)
    {
        if (select.Source is null)
            return select;

        var candidates = new List<ScalarSubqueryExpression>();
        if (!TryCollectScopedScalarSubqueries(select, candidates) || candidates.Count == 0)
            return select;

        // Cheap pre-filter so an ordinary scalar subquery never pays for the outer column
        // description the correlation-key analysis needs.
        List<ScalarSubqueryExpression>? plausible = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Query is SelectStatement { Projections.Count: 1 } inner
                && inner.Projections[0].Expression is not (StarExpression or QualifiedStarExpression)
                && ContainsAggregate(inner.Projections[0].Expression))
            {
                (plausible ??= []).Add(candidate);
            }
        }

        if (plausible is null)
            return select;

        var outerColumns = GetOutputColumns(select.Source, context);
        var shapes = new List<AggregateSubqueryShape>();
        var declines = 0L;
        foreach (var candidate in plausible)
        {
            switch (TryAnalyzeAggregateSubquery(candidate, outerColumns, context, out var shape))
            {
                case true:
                    shapes.Add(shape);
                    break;
                case false:
                    declines++;
                    break;
            }
        }

        // A pre-existing outer join already decides which rows get NULL-padded; appending
        // another LEFT JOIN in front of that decision, or moving one of its terms, would change
        // the padding. unnest.rs declines the individual terms (unnest.rs:515-519, 570-572);
        // declining the whole statement is the conservative superset.
        if (shapes.Count == 0 || SourceContainsOuterJoin(select.Source))
        {
            CountDeclines(declines + shapes.Count);
            return select;
        }

        // Join-first replaces the statement's grouping and its WHERE clause, so it cannot be
        // combined with another rewrite of the same SELECT.
        if (shapes is [{ CanRunForUnusedKeys: false } only])
        {
            if (TryRewriteAggregateJoinFirst(select, context, only, out var joined))
            {
                CountDeclines(declines);
                _ = Interlocked.Increment(ref _aggregateJoinFirstRewrites);
                return joined;
            }

            CountDeclines(declines + 1);
            return select;
        }

        var rewritten = select;
        var applied = 0L;
        foreach (var shape in shapes)
        {
            if (shape.CanRunForUnusedKeys
                && TryRewriteAggregateGroupFirst(rewritten, context, shape, out var grouped))
            {
                rewritten = grouped;
                applied++;
                continue;
            }

            declines++;
        }

        CountDeclines(declines);
        if (applied > 0)
            _ = Interlocked.Add(ref _aggregateGroupFirstRewrites, applied);

        return rewritten;

        void CountDeclines(long count)
        {
            if (count > 0)
                _ = Interlocked.Add(ref _aggregateDecorrelationDeclines, count);
        }
    }

    /// <summary>
    /// Collects the scalar subqueries that belong to <paramref name="select"/>'s own scope: the
    /// ones reachable from its clauses without descending through another subquery body or a
    /// FROM-clause derived table, both of which are separate scopes that get their own rewrite
    /// pass. Returns false when an unmodelled node is reached, so the caller fails closed.
    /// </summary>
    private static bool TryCollectScopedScalarSubqueries(
        SelectStatement select,
        List<ScalarSubqueryExpression> found)
    {
        var clauses = new List<Expression>();
        foreach (var projection in select.Projections)
            clauses.Add(projection.Expression);
        if (select.Where is not null)
            clauses.Add(select.Where);
        clauses.AddRange(select.GroupBy);
        if (select.Having is not null)
            clauses.Add(select.Having);
        foreach (var term in select.OrderBy)
            clauses.Add(term.Expression);
        if (select.Limit is not null)
            clauses.Add(select.Limit);
        if (select.Offset is not null)
            clauses.Add(select.Offset);

        foreach (var clause in clauses)
        {
            if (!ForEachScopedExpression(clause, candidate =>
                {
                    if (candidate is ScalarSubqueryExpression scalar)
                        found.Add(scalar);

                    return true;
                }))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Like <see cref="ForEachExpression"/>, but treats a subquery operand as a leaf instead of
    /// walking its body: everything inside belongs to another scope.
    /// </summary>
    private static bool ForEachScopedExpression(Expression expression, Func<Expression, bool> visit)
    {
        var pending = new Stack<object>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            if (pending.Pop() is not Expression current)
                return false;

            if (!visit(current))
                return true;

            if (current is ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression)
            {
                // The value operand of `x IN (SELECT …)` is evaluated in this scope, so it can
                // still hold a scalar subquery of its own.
                if (current is InSubqueryExpression inSubquery)
                    pending.Push(inSubquery.Value);

                continue;
            }

            if (!PushExpressionChildren(current, pending))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Classifies one correlated aggregate subquery against the shared rules of
    /// <c>can_rewrite_single_value_aggregate</c> (unnest.rs:934-979), the correlation-term
    /// analysis of <c>try_rewrite_single_value_aggregate</c> (unnest.rs:560-583), the
    /// empty-input value analysis of <c>result_on_empty_input</c> (unnest.rs:1070-1130), and the
    /// unused-key safety analysis of <c>aggregate_can_run_for_unused_rows</c>
    /// (unnest.rs:997-1020).
    /// <para>
    /// Returns <see langword="null"/> when the subquery is not even a candidate (it is not a
    /// correlated aggregate over one base table), and <see langword="false"/> when it is a
    /// candidate that one of the semantic gates rejected. The distinction only feeds the decline
    /// counter; both answers leave the statement untouched.
    /// </para>
    /// </summary>
    private bool? TryAnalyzeAggregateSubquery(
        ScalarSubqueryExpression subquery,
        IReadOnlyList<OutputColumn> outerColumns,
        QueryContext context,
        out AggregateSubqueryShape shape)
    {
        shape = null!;
        if (subquery.Query is not SelectStatement inner
            || inner.Projections.Count != 1
            || inner.Projections[0].Expression is StarExpression or QualifiedStarExpression
            || !ContainsAggregate(inner.Projections[0].Expression))
        {
            return null;
        }

        if (!TryGetDecorrelationBaseTable(inner.Source, context, out var innerTable, out var innerQualifier))
            return false;

        // Without a WHERE clause there is nothing to correlate through, and an uncorrelated
        // aggregate subquery already evaluates once for the whole statement.
        if (inner.Where is null)
            return null;

        var value = inner.Projections[0].Expression;
        var innerColumns = new HashSet<string>(
            GetSourceColumns(innerTable, context),
            StringComparer.OrdinalIgnoreCase);

        var pairs = new List<AggregateCorrelationPair>();
        List<Expression>? innerOnly = null;
        var correlated = false;
        foreach (var conjunct in IndexExpressionSemantics.SplitConjuncts(inner.Where))
        {
            if (!TryClassifyJoinTerm(conjunct, innerQualifier, innerColumns, out _, out var referencesOuter))
                return false;

            if (!referencesOuter)
            {
                (innerOnly ??= []).Add(conjunct);
                continue;
            }

            correlated = true;

            // Only a plain `inner = outer` equality can become a grouping key and a join
            // condition; anything else stays a per-row filter (unnest.rs:1133-1172).
            if (!TryReadCorrelationPair(conjunct, innerQualifier, innerColumns, out var pair))
                return false;

            pairs.Add(pair);
        }

        if (!correlated)
            return null;

        // Each excluded clause changes which inner rows the aggregate sees, in what order, or
        // how many results the subquery produces. Both rewrites replace the subquery's own
        // grouping, so none of them survives (unnest.rs:934-953). Ahtola's AST carries no
        // synthesized `LIMIT 1`, so the upstream "limit must be exactly 1" test becomes "no
        // limit at all".
        if (inner.Distinct
            || inner.GroupBy.Count != 0
            || inner.Having is not null
            || inner.NamedWindows.Count != 0
            || inner.OrderBy.Count != 0
            || inner.Limit is not null
            || inner.Offset is not null)
        {
            return false;
        }

        // A nested subquery keeps its own correlation scope, which neither rewrite moves; a
        // nondeterministic scalar call would be evaluated a different number of times
        // (unnest.rs:944, 968-977).
        if (ContainsWindowFunction(value)
            || ContainsSubqueryExpression(value)
            || ContainsSubqueryExpression(inner.Where)
            || ContainsNonDeterministicScalarFunction(value)
            || ContainsNonDeterministicScalarFunction(inner.Where)
            || ContainsAggregate(inner.Where)
            || ContainsWindowFunction(inner.Where))
        {
            return false;
        }

        if (!TryCollectAggregateCalls(value, innerQualifier, innerColumns, out var aggregates))
            return false;

        object? outerOrigin = null;
        foreach (var pair in pairs)
        {
            if (!TryDescribeInnerKey(pair.Inner, innerTable, context, out var innerKey)
                || !TryDescribeOuterKey(pair.Outer, outerColumns, context, out var outerKey, out var origin))
            {
                return false;
            }

            // An inner BINARY key can split `A` and `a` into two groups that a NOCASE outer key
            // then joins to both, and an affinity mismatch can do the same for `1` and `'1'`.
            // Either would emit the outer row twice (unnest.rs:1182-1192).
            if (innerKey.Affinity != outerKey.Affinity
                || !string.Equals(innerKey.Collation, outerKey.Collation, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Linking the grouped table to more than one outer table would let the join-order
            // rewriter move it in front of one of them (unnest.rs:542-551).
            if (outerOrigin is not null && !ReferenceEquals(outerOrigin, origin))
                return false;

            outerOrigin = origin;
        }

        if (!TryGetAggregateEmptyInputValue(value, aggregates, out var empty))
            return false;

        shape = new AggregateSubqueryShape(
            subquery,
            inner,
            innerTable,
            innerQualifier,
            value,
            pairs,
            innerOnly is null ? null : innerOnly.Aggregate((Expression?)null, CombineConjuncts),
            empty,
            AggregateCanRunForUnusedKeys(aggregates, inner.Where, pairs, innerTable, context));
        return true;
    }

    /// <summary>
    /// Group-first: the whole inner table is grouped by the correlation columns once, and the
    /// resulting one-row-per-key table is LEFT JOINed to the outer rows (unnest.rs:589-687).
    /// <para>
    /// The join can match at most one grouped row per outer row — that is exactly what the
    /// affinity/collation compatibility gate buys — so the outer row count, and with it every
    /// outer clause, is preserved untouched.
    /// </para>
    /// </summary>
    private bool TryRewriteAggregateGroupFirst(
        SelectStatement select,
        QueryContext context,
        AggregateSubqueryShape shape,
        out SelectStatement rewritten)
    {
        rewritten = select;

        // `SELECT *` would start publishing the grouped table's columns. Expanding the star here
        // would mean reimplementing SQLite's result-column naming, so decline instead. `t.*`
        // names one source and is unaffected.
        foreach (var projection in select.Projections)
        {
            if (projection.Expression is StarExpression)
                return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromSourceNames(select.Source, names);
        var alias = MakeUniqueSourceAlias(names, "ahtola_group_first");

        var keys = new List<ColumnExpression>();
        var keyIndices = new int[shape.Pairs.Count];
        for (var index = 0; index < shape.Pairs.Count; index++)
        {
            var key = shape.Pairs[index].Inner;
            var position = keys.FindIndex(existing => existing == key);
            if (position < 0)
            {
                keys.Add(key);
                position = keys.Count - 1;
            }

            keyIndices[index] = position;
        }

        var projections = new List<Projection>(keys.Count + 1)
        {
            new(shape.Value, AggregateValueAlias),
        };
        for (var index = 0; index < keys.Count; index++)
            projections.Add(new Projection(keys[index], CorrelationKeyAlias(index)));

        var grouped = new SelectStatement(
            Distinct: false,
            Projections: projections,
            Source: shape.Inner.Source,
            Where: shape.InnerFilter,
            GroupBy: keys,
            Having: null,
            NamedWindows: [],
            OrderBy: [],
            Limit: null,
            Offset: null);

        Expression? condition = null;
        for (var index = 0; index < shape.Pairs.Count; index++)
        {
            var pair = shape.Pairs[index];
            var keyReference = QualifiedColumn(alias, CorrelationKeyAlias(keyIndices[index]));

            // Keep the operand order the subquery wrote. SQLite resolves a comparison's
            // collation from the left operand first, and the grouped column carries none.
            var equality = pair.InnerWasLeft
                ? new BinaryExpression(keyReference, BinaryOperator.Equal, pair.Outer)
                : new BinaryExpression(pair.Outer, BinaryOperator.Equal, keyReference);
            condition = CombineConjuncts(condition, equality);
        }

        var value = QualifiedColumn(alias, AggregateValueAlias);

        // An outer row with no matching key reads the left join's NULL padding, which is what
        // the subquery returns for `avg`/`min`/`max`. `count` and `total` answer 0 and 0.0 for
        // an empty input, so those keep their identity through COALESCE (unnest.rs:658-662).
        Expression replacement = shape.EmptyInputValue switch
        {
            AggregateEmptyInputValue.IntegerZero => CoalesceWithZero(value, SqlValue.Integer(0)),
            AggregateEmptyInputValue.RealZero => CoalesceWithZero(value, SqlValue.Real(0.0)),
            _ => value,
        };

        if (!TryReplaceScalarSubqueryValue(select, shape.Subquery, replacement, out var replaced))
            return false;

        rewritten = replaced with
        {
            Source = new JoinTableSource(
                select.Source!,
                new DerivedTableSource(grouped, alias),
                condition,
                JoinKind.Left),
        };
        return true;
    }

    /// <summary>
    /// Join-first: the inner table is LEFT JOINed directly, the joined rows are grouped back to
    /// one group per outer row through the outer rowid, and the single WHERE comparison that
    /// consumed the subquery moves to HAVING (unnest.rs:726-817).
    /// <para>
    /// The join only reaches inner rows whose key some outer row asks for, so this form never
    /// evaluates the aggregate over an unused key — the reason it exists. Each aggregate gains a
    /// <c>FILTER (WHERE inner.rowid IS NOT NULL)</c> guard so the NULL-padded row a left join
    /// invents for an unmatched outer row is not counted; without it <c>count(*)</c> would
    /// answer 1 where the subquery answers 0.
    /// </para>
    /// </summary>
    private bool TryRewriteAggregateJoinFirst(
        SelectStatement select,
        QueryContext context,
        AggregateSubqueryShape shape,
        out SelectStatement rewritten)
    {
        rewritten = select;

        // Grouping by the outer rowid is what keeps outer rows apart, and none of these clauses
        // survives being pushed around that new grouping step (unnest.rs:732-743).
        if (select.Distinct
            || select.GroupBy.Count != 0
            || select.Having is not null
            || select.NamedWindows.Count != 0
            || select.OrderBy.Count != 0
            || select.Limit is not null
            || select.Offset is not null
            || select.Where is null)
        {
            return false;
        }

        foreach (var projection in select.Projections)
        {
            if (projection.Expression is StarExpression
                || ContainsAggregate(projection.Expression)
                || ContainsWindowFunction(projection.Expression))
            {
                return false;
            }
        }

        // `GROUP BY o.rowid` needs one group per original outer row, and
        // `i.rowid IS NOT NULL` needs to mean "a real inner row matched", so both sides must be
        // ordinary rowid B-tree tables whose rowid spelling is not shadowed by a declared
        // column (unnest.rs:745-761).
        if (!TryGetDecorrelationBaseTable(select.Source, context, out var outerTable, out var outerQualifier)
            || !TryGetRowidTable(outerTable, context, out _)
            || !TryGetRowidTable(shape.InnerTable, context, out _)
            || string.Equals(outerQualifier, shape.InnerQualifier, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var outerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromSourceNames(select.Source, outerNames);
        if (outerNames.Contains(shape.InnerQualifier))
            return false;

        // An inner-only WHERE term moves into the LEFT JOIN's ON condition. The join executor is
        // free to test the equality keys before that residual, while the original subquery
        // evaluates its WHERE conjuncts in source order for every inner row. Moving a fallible
        // term could therefore hide an error from a non-matching row. Keep the per-row subquery
        // whenever that evaluation order is observable.
        if (ExpressionCanFail(shape.InnerFilter))
            return false;

        // Unlike group-first, whose grouped table publishes two synthetic names, join-first
        // moves a real table into the enclosing scope. An unqualified outer reference that
        // names one of its columns would become ambiguous, and a reference the rewrite carries
        // out of the subquery would resolve against the outer table instead — neither is a
        // diagnostic or a binding the original statement had.
        var innerColumns = new HashSet<string>(
            GetSourceColumns(shape.InnerTable, context),
            StringComparer.OrdinalIgnoreCase);
        if (!JoinFirstNamesStayUnambiguous(select, shape, innerColumns))
            return false;

        foreach (var projection in select.Projections)
        {
            if (ExpressionReferencesScalarSubquery(projection.Expression, shape.Subquery))
                return false;
        }

        var terms = IndexExpressionSemantics.SplitConjuncts(select.Where);
        var comparisonIndex = -1;
        Expression? having = null;
        for (var index = 0; index < terms.Count; index++)
        {
            var term = terms[index];
            if (!ExpressionReferencesScalarSubquery(term, shape.Subquery))
            {
                // After the rewrite an outer row appears once per matching inner row, so every
                // surviving WHERE term runs once per copy. A term whose value can differ
                // between copies could keep some and drop others, and the aggregate would then
                // see only part of the outer row's inner rows (unnest.rs:858-865).
                if (ContainsNonDeterministicFunction(term)
                    || ExpressionContainsCorrelatedSubquery(term, outerNames))
                {
                    return false;
                }

                continue;
            }

            // The rewrite deletes the subquery, so this one comparison must be its only use.
            if (comparisonIndex >= 0
                || !TryMoveAggregateComparisonToHaving(term, shape, out having))
            {
                return false;
            }

            comparisonIndex = index;
        }

        if (comparisonIndex < 0 || having is null)
            return false;

        Expression? where = null;
        for (var index = 0; index < terms.Count; index++)
        {
            if (index != comparisonIndex)
                where = CombineConjuncts(where, terms[index]);
        }

        // The inner WHERE becomes the join condition: its correlation equalities are the join
        // keys, and its inner-only filters must not reject rows before the left join decides
        // whether to null-pad (unnest.rs:783-787).
        Expression? condition = shape.InnerFilter;
        foreach (var pair in shape.Pairs)
        {
            condition = CombineConjuncts(
                condition,
                pair.InnerWasLeft
                    ? new BinaryExpression(pair.Inner, BinaryOperator.Equal, pair.Outer)
                    : new BinaryExpression(pair.Outer, BinaryOperator.Equal, pair.Inner));
        }

        rewritten = select with
        {
            Source = new JoinTableSource(
                select.Source!,
                shape.InnerTable,
                condition,
                JoinKind.Left),
            Where = where,
            GroupBy = [QualifiedColumn(outerQualifier, "rowid")],
            Having = having,
        };
        return true;
    }

    /// <summary>
    /// True when join-first can move the inner table into the enclosing FROM clause without
    /// changing what any name means.
    /// <para>
    /// Two directions have to hold. An <em>unqualified</em> reference already in the enclosing
    /// query must not name a column of the inner table (or a rowid spelling), because the extra
    /// table would make it ambiguous. And every reference the rewrite carries out of the
    /// subquery — the aggregate expression, the inner-only filters and the inner side of each
    /// correlation equality — must be qualified by the inner table, because an unqualified
    /// <c>sum(v)</c> that resolved to the subquery's only table would suddenly have two
    /// candidates.
    /// </para>
    /// </summary>
    private static bool JoinFirstNamesStayUnambiguous(
        SelectStatement select,
        AggregateSubqueryShape shape,
        HashSet<string> innerColumns)
    {
        var safe = true;
        bool VisitEnclosing(Expression expression)
            => ForEachScopedExpression(expression, candidate =>
            {
                if (candidate is ScalarSubqueryExpression scalar)
                {
                    // The aggregate subquery itself is the value this rewrite removes. Any
                    // other nested query has its own name-resolution scope, but an unqualified
                    // name in that scope may still fall back to this enclosing FROM clause.
                    // Moving the inner table here could therefore make a previously unique
                    // correlation ambiguous. Decline rather than duplicate the binder's scope
                    // and shadowing rules in this safety check.
                    if (!ReferenceEquals(scalar, shape.Subquery))
                        safe = false;
                }
                else if (candidate is ExistsExpression or InSubqueryExpression)
                {
                    safe = false;
                }

                if (candidate is ColumnExpression { BooleanKeyword: null } column)
                {
                    if (column.Qualifier is null)
                    {
                        var name = column.UnqualifiedName ?? column.Name;
                        if (innerColumns.Contains(name) || IsRowIdAlias(name))
                            safe = false;
                    }
                    else if (string.Equals(
                                 column.Qualifier,
                                 shape.InnerQualifier,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        // The enclosing FROM does not currently answer to this qualifier (that
                        // was checked before this walk), so the reference resolves through an
                        // outer scope today. Moving the inner table here would capture it.
                        safe = false;
                    }
                }

                return safe;
            });

        bool VisitMoved(Expression expression)
            => ForEachExpression(expression, candidate =>
            {
                if (candidate is ColumnExpression { BooleanKeyword: null } column
                    && !string.Equals(column.Qualifier, shape.InnerQualifier, StringComparison.OrdinalIgnoreCase))
                {
                    safe = false;
                }

                return safe;
            });

        foreach (var projection in select.Projections)
        {
            if (!VisitEnclosing(projection.Expression) || !safe)
                return false;
        }

        if (select.Where is not null && (!VisitEnclosing(select.Where) || !safe))
            return false;

        if (!VisitMoved(shape.Value) || !safe)
            return false;

        if (shape.InnerFilter is not null && (!VisitMoved(shape.InnerFilter) || !safe))
            return false;

        foreach (var pair in shape.Pairs)
        {
            if (!string.Equals(pair.Inner.Qualifier, shape.InnerQualifier, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return safe;
    }

    /// <summary>
    /// Rebuilds one WHERE comparison as the HAVING clause of the join-first form: the subquery
    /// operand becomes the inner aggregate expression, with every aggregate call in it guarded
    /// against the row a left join invents when nothing matched (unnest.rs:843-897).
    /// <para>
    /// Only a comparison whose whole operand is the subquery qualifies. <c>1 + (SELECT …)</c>
    /// and <c>(SELECT sum(…)) &gt; (SELECT max(…))</c> are rejected: moving one of them would
    /// leave another use of a removed subquery behind, or evaluate an aggregate outside the
    /// grouping the rewrite just created.
    /// </para>
    /// </summary>
    private bool TryMoveAggregateComparisonToHaving(
        Expression term,
        AggregateSubqueryShape shape,
        out Expression? having)
    {
        having = null;
        if (term is not BinaryExpression binary || !IsComparisonOperator(binary.Operator))
            return false;

        var leftIsSubquery = ReferenceEquals(binary.Left, shape.Subquery);
        var rightIsSubquery = ReferenceEquals(binary.Right, shape.Subquery);
        if (leftIsSubquery == rightIsSubquery)
            return false;

        var other = leftIsSubquery ? binary.Right : binary.Left;
        if (ContainsSubqueryExpression(other))
            return false;

        var innerRowExists = new BinaryExpression(
            QualifiedColumn(shape.InnerQualifier, "rowid"),
            BinaryOperator.IsNot,
            new LiteralExpression(SqlValue.Null));

        var declined = false;
        var substitution = new AstSubstitution(
            Column: null,
            Function: function => IsAggregateFunctionCall(function) && function.Window is null
                ? function with
                {
                    Filter = function.Filter is null
                        ? innerRowExists
                        : new BinaryExpression(innerRowExists, BinaryOperator.And, function.Filter),
                }
                : null);

        if (!TryRewriteExpression(shape.Value, substitution, ref declined, out var guarded) || declined)
            return false;

        having = leftIsSubquery
            ? new BinaryExpression(guarded, binary.Operator, other)
            : new BinaryExpression(other, binary.Operator, guarded);
        return true;
    }

    /// <summary>
    /// Replaces every use of one scalar subquery's value with <paramref name="replacement"/>,
    /// mirroring <c>replace_subquery_value</c> (unnest.rs:1235-1297). Returns false when the
    /// subquery was not found or a clause holds a node the rewriter does not model.
    /// </summary>
    private static bool TryReplaceScalarSubqueryValue(
        SelectStatement select,
        ScalarSubqueryExpression subquery,
        Expression replacement,
        out SelectStatement rewritten)
    {
        rewritten = select;
        var found = false;
        var declined = false;
        var substitution = new AstSubstitution(
            Column: null,
            ScalarSubquery: candidate =>
            {
                if (!ReferenceEquals(candidate, subquery))
                    return null;

                found = true;
                return replacement;
            });

        List<Projection>? projections = null;
        for (var index = 0; index < select.Projections.Count; index++)
        {
            var projection = select.Projections[index];
            if (!TryRewriteExpression(projection.Expression, substitution, ref declined, out var expression))
                return false;

            if (ReferenceEquals(expression, projection.Expression))
                continue;

            projections ??= [.. select.Projections];

            // Substituting the subquery renames an unaliased result column, so pin the name the
            // original statement published.
            projections[index] = projection with
            {
                Expression = expression,
                Alias = projection.Alias ?? projection.SourceText,
            };
        }

        if (!TryRewriteClause(select.Where, substitution, ref declined, out var where)
            || !TryRewriteClause(select.Having, substitution, ref declined, out var having)
            || !TryRewriteClause(select.Limit, substitution, ref declined, out var limit)
            || !TryRewriteClause(select.Offset, substitution, ref declined, out var offset)
            || !TryRewriteExpressionList(select.GroupBy, substitution, ref declined, out var groupBy)
            || !TryRewriteOrderBy(select.OrderBy, substitution, ref declined, out var orderBy)
            || declined
            || !found)
        {
            return false;
        }

        rewritten = select with
        {
            Projections = projections ?? select.Projections,
            Where = where,
            Having = having,
            GroupBy = groupBy,
            OrderBy = orderBy,
            Limit = limit,
            Offset = offset,
        };
        return true;
    }

    /// <summary>
    /// Collects the aggregate calls of the subquery's single result expression and checks that
    /// the expression is self-contained: every column it reads must come from the inner table
    /// and must sit inside an aggregate's arguments, FILTER or ORDER BY.
    /// <para>
    /// A column read outside an aggregate makes the result depend on which row of the group the
    /// engine happens to keep, and an outer reference outside the WHERE clause cannot move into
    /// a grouped table at all (unnest.rs:1195-1214). Extension aggregates decline: their value
    /// for an empty input is unknown, so neither rewrite can reproduce it (unnest.rs:97-99).
    /// </para>
    /// </summary>
    private bool TryCollectAggregateCalls(
        Expression value,
        string innerQualifier,
        HashSet<string> innerColumns,
        out List<FunctionExpression> aggregates)
    {
        aggregates = [];
        var calls = aggregates;
        if (!ForEachExpression(value, candidate =>
            {
                if (candidate is FunctionExpression { Window: null } function && IsAggregateFunctionCall(function))
                    calls.Add(function);

                return true;
            }))
        {
            return false;
        }

        if (aggregates.Count == 0)
            return false;

        var covered = new HashSet<object>(AstReferenceComparer.Instance);
        foreach (var aggregate in aggregates)
        {
            // Ordered-set and ORDER BY aggregates carry an ordering the grouped form would have
            // to reproduce, and a non-built-in aggregate has no known empty-input value.
            if (aggregate.OrderedSet
                || aggregate.AggregateOrderBy is { Count: > 0 }
                || !IsBuiltInAggregate(aggregate))
            {
                return false;
            }

            foreach (var argument in aggregate.Arguments)
            {
                if (!ForEachExpression(argument, node => covered.Add(node) || true))
                    return false;
            }

            if (aggregate.Filter is not null
                && !ForEachExpression(aggregate.Filter, node => covered.Add(node) || true))
            {
                return false;
            }
        }

        var selfContained = true;
        if (!ForEachExpression(value, candidate =>
            {
                if (candidate is not ColumnExpression { BooleanKeyword: null } column)
                    return true;

                if (!covered.Contains(column) || !IsInnerColumnReference(column, innerQualifier, innerColumns))
                    selfContained = false;

                return selfContained;
            }))
        {
            return false;
        }

        return selfContained;
    }

    /// <summary>
    /// True when group-first may evaluate the aggregate for every key of the inner table,
    /// including keys that no outer row asks for (unnest.rs:997-1020).
    /// <para>
    /// The original subquery only ever reads the keys the outer rows ask for, so group-first must
    /// not be able to raise an error those keys never produce. <c>sum</c> can overflow and the
    /// string aggregates can outgrow the largest SQL value, so only <c>avg</c>, <c>count</c>,
    /// <c>min</c>, <c>max</c> and <c>total</c> qualify, and only when no aggregate input, no
    /// aggregate FILTER and no inner WHERE term can fail on its input. Ahtola adds the grouping
    /// key itself: a group is formed with the key's declared collation, so an
    /// application-registered sequence would run for unused keys too.
    /// </para>
    /// </summary>
    private bool AggregateCanRunForUnusedKeys(
        List<FunctionExpression> aggregates,
        Expression? innerWhere,
        List<AggregateCorrelationPair> pairs,
        NamedTableSource innerTable,
        QueryContext context)
    {
        foreach (var aggregate in aggregates)
        {
            if (aggregate.Name is not ("AVG" or "COUNT" or "MIN" or "MAX" or "TOTAL"))
                return false;

            foreach (var argument in aggregate.Arguments)
            {
                if (ExpressionCanFail(argument))
                    return false;
            }

            if (ExpressionCanFail(aggregate.Filter))
                return false;
        }

        if (ExpressionCanFail(innerWhere))
            return false;

        if (!_hasCustomCollations)
            return true;

        foreach (var pair in pairs)
        {
            if (!TryDescribeInnerKey(pair.Inner, innerTable, context, out var key)
                || key.Collation is not ("BINARY" or "NOCASE" or "RTRIM"))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The value the subquery produces when no inner row matches, if it is known
    /// (unnest.rs:1070-1130). <c>count</c> answers integer 0 and <c>total</c> real 0.0; the
    /// null-producing aggregates answer NULL, and so does any arithmetic, concatenation, cast,
    /// collate or unary expression built over one of them.
    /// </summary>
    private static bool TryGetAggregateEmptyInputValue(
        Expression value,
        List<FunctionExpression> aggregates,
        out AggregateEmptyInputValue empty)
    {
        empty = AggregateEmptyInputValue.Null;
        if (value is FunctionExpression function && aggregates.Contains(function))
        {
            switch (function.Name)
            {
                case "COUNT":
                    empty = AggregateEmptyInputValue.IntegerZero;
                    return true;
                case "TOTAL":
                    empty = AggregateEmptyInputValue.RealZero;
                    return true;
                case "AVG" or "GROUP_CONCAT" or "MAX" or "MIN" or "STRING_AGG" or "SUM":
                    empty = AggregateEmptyInputValue.Null;
                    return true;
                default:
                    return false;
            }
        }

        return IsNullOnEmptyInput(value, aggregates);
    }

    private static bool IsNullOnEmptyInput(Expression expression, List<FunctionExpression> aggregates)
    {
        switch (expression)
        {
            case FunctionExpression function when aggregates.Contains(function):
                return function.Name is "AVG" or "GROUP_CONCAT" or "MAX" or "MIN" or "STRING_AGG" or "SUM";
            case BinaryExpression
            {
                Operator: BinaryOperator.Add
                    or BinaryOperator.Subtract
                    or BinaryOperator.Multiply
                    or BinaryOperator.Divide
                    or BinaryOperator.Modulo
                    or BinaryOperator.BitwiseAnd
                    or BinaryOperator.BitwiseOr
                    or BinaryOperator.ShiftLeft
                    or BinaryOperator.ShiftRight
                    or BinaryOperator.Concatenate,
            } binary:
                return IsNullOnEmptyInput(binary.Left, aggregates)
                    || IsNullOnEmptyInput(binary.Right, aggregates);
            case UnaryExpression unary:
                return IsNullOnEmptyInput(unary.Operand, aggregates);
            case CastExpression cast:
                return IsNullOnEmptyInput(cast.Expression, aggregates);
            case CollationExpression collation:
                return IsNullOnEmptyInput(collation.Expression, aggregates);
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads one <c>inner = outer</c> (or <c>outer = inner</c>) correlation term, keeping which
    /// side the inner column was on so the rewritten comparison resolves its collation from the
    /// same operand (unnest.rs:1133-1172).
    /// </summary>
    private static bool TryReadCorrelationPair(
        Expression expression,
        string innerQualifier,
        HashSet<string> innerColumns,
        out AggregateCorrelationPair pair)
    {
        pair = null!;
        if (expression is not BinaryExpression { Operator: BinaryOperator.Equal } equality
            || equality.Left is not ColumnExpression { BooleanKeyword: null } left
            || equality.Right is not ColumnExpression { BooleanKeyword: null } right)
        {
            return false;
        }

        var leftIsInner = IsInnerColumnReference(left, innerQualifier, innerColumns);
        var rightIsInner = IsInnerColumnReference(right, innerQualifier, innerColumns);
        if (leftIsInner == rightIsInner)
            return false;

        pair = leftIsInner
            ? new AggregateCorrelationPair(left, right, InnerWasLeft: true)
            : new AggregateCorrelationPair(right, left, InnerWasLeft: false);
        return true;
    }

    /// <summary>
    /// Resolves the declared affinity and collation of a correlation column of the subquery's
    /// own table. A rowid spelling declines: grouping by the rowid would put every inner row in
    /// its own group, which is never worth a rewrite.
    /// </summary>
    private static bool TryDescribeInnerKey(
        ColumnExpression column,
        NamedTableSource innerTable,
        QueryContext context,
        out CorrelationKeyDescription key)
    {
        key = default;
        if (column.Schema is not null || !context.Tables.TryGetValue(innerTable.Name, out var table))
            return false;

        return TryDescribeTableColumn(table, column.UnqualifiedName ?? column.Name, out key);
    }

    /// <summary>
    /// Resolves the declared affinity and collation of a correlation column of the enclosing
    /// query, and the FROM source it came from. A name that resolves to no source, to more than
    /// one, or to anything other than an ordinary base table declines: it may belong to a scope
    /// further out, which neither rewrite can reach (unnest.rs:531-541).
    /// </summary>
    private static bool TryDescribeOuterKey(
        ColumnExpression column,
        IReadOnlyList<OutputColumn> outerColumns,
        QueryContext context,
        out CorrelationKeyDescription key,
        out object? origin)
    {
        key = default;
        origin = null;
        if (column.Schema is not null)
            return false;

        var name = column.UnqualifiedName ?? column.Name;
        OutputColumn? match = null;
        foreach (var candidate in outerColumns)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (column.Qualifier is { } qualifier
                && !string.Equals(candidate.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
                return false;

            match = candidate;
        }

        if (match?.Origin is not NamedTableSource named
            || !context.Tables.TryGetValue(named.Name, out var table)
            || !TryDescribeTableColumn(table, name, out key))
        {
            return false;
        }

        origin = match.Origin;
        return true;
    }

    private static bool TryDescribeTableColumn(
        EmbeddedTable table,
        string columnName,
        out CorrelationKeyDescription key)
    {
        key = default;
        if (!table.TryGetColumnIndex(columnName, out var index))
            return false;

        var definition = table.ColumnDefinitions[index];

        // A STRICT ANY column keeps whatever it was given, so no single affinity describes how
        // it compares.
        if (definition.StrictAny)
            return false;

        key = new CorrelationKeyDescription(
            table.GetColumnAffinity(definition),
            NormalizeDeclaredCollation(definition.Collation));
        return true;
    }

    /// <summary>
    /// The FROM clause must be exactly one ordinary base table. Both rewrites move that table
    /// into the enclosing query (join-first) or group it in place (group-first), which a view,
    /// CTE, virtual table, table-valued function or joined source cannot express
    /// (unnest.rs:754-761, 954-967).
    /// </summary>
    private bool TryGetDecorrelationBaseTable(
        TableSource? source,
        QueryContext context,
        out NamedTableSource table,
        out string qualifier)
    {
        table = null!;
        qualifier = string.Empty;

        if (source is not NamedTableSource named
            || named.IsSchemaQualified
            || IsSchemaTable(named.Name)
            || IsCommonTableExpression(named, context)
            || TryGetView(context, named.Name, out _)
            || TryGetVirtualTable(context, named, out _)
            || TryBindBareTableValuedFunction(named, context, out _)
            || !context.Tables.ContainsKey(named.Name))
        {
            return false;
        }

        table = named;
        qualifier = named.Alias ?? named.Name;
        return true;
    }

    /// <summary>
    /// True when the source is a rowid B-tree table whose rowid spelling is not shadowed by a
    /// declared column, so <c>t.rowid</c> really is the one-per-row identity join-first groups
    /// by and tests for NULL.
    /// </summary>
    private static bool TryGetRowidTable(
        NamedTableSource source,
        QueryContext context,
        out EmbeddedTable table)
    {
        table = null!;
        if (!context.Tables.TryGetValue(source.Name, out var candidate) || !candidate.HasRowid)
            return false;

        foreach (var column in candidate.Columns)
        {
            if (IsRowIdAlias(column))
                return false;
        }

        table = candidate;
        return true;
    }

    private static bool ExpressionReferencesScalarSubquery(
        Expression expression,
        ScalarSubqueryExpression subquery)
    {
        var found = false;
        var modelled = ForEachScopedExpression(expression, candidate =>
        {
            if (ReferenceEquals(candidate, subquery))
                found = true;

            return !found;
        });

        return found || !modelled;
    }

    private static Expression CoalesceWithZero(Expression value, SqlValue zero)
        => new FunctionExpression("COALESCE", [value, new LiteralExpression(zero)], CountStar: false);

    /// <summary>
    /// Builds a qualified column reference exactly the way the parser builds <c>t.c</c>: the
    /// canonical <see cref="ColumnExpression.Name"/> is the dotted spelling and
    /// <see cref="ColumnExpression.UnqualifiedName"/> carries the column on its own. Binding
    /// looks at both, so a synthesized reference that sets only one of them fails to resolve.
    /// </summary>
    private static ColumnExpression QualifiedColumn(string qualifier, string name)
        => new(
            Name: string.Concat(qualifier, ".", name),
            Qualifier: qualifier,
            UnqualifiedName: name);

    private const string AggregateValueAlias = "ahtola_aggregate_value";

    private static string CorrelationKeyAlias(int index)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"ahtola_correlation_key_{index}");

    private static string MakeUniqueSourceAlias(HashSet<string> taken, string prefix)
    {
        if (!taken.Contains(prefix))
            return prefix;

        for (var suffix = 0; ; suffix++)
        {
            var candidate = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{prefix}_{suffix}");
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Shared expression predicates and the fail-closed rewriting walker.
    // ---------------------------------------------------------------------------------------

    private static bool IsRowIdAlias(string name)
        => string.Equals(name, "rowid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "_rowid_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "oid", StringComparison.OrdinalIgnoreCase);

    private static Expression? CombineConjuncts(Expression? left, Expression? right)
        => left is null
            ? right
            : right is null
                ? left
                : new BinaryExpression(left, BinaryOperator.And, right);

    private static bool ContainsParameterExpression(IReadOnlyList<Projection> projections)
    {
        foreach (var projection in projections)
        {
            var found = false;
            var modelled = ForEachExpression(projection.Expression, expression =>
            {
                if (expression is ParameterExpression)
                    found = true;
                return !found;
            });

            if (found || !modelled)
                return true;
        }

        return false;
    }

    private static bool ContainsSubqueryExpression(Expression? expression)
    {
        if (expression is null)
            return false;

        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            if (candidate is ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression)
                found = true;
            return !found;
        });

        return found || !modelled;
    }

    /// <summary>
    /// True when the expression can carry a value subtype (today: the JSON subtype) out of the
    /// derived table. Used as the JSON-subtype guard for flattening.
    /// <para>
    /// A subtype originates in a function result (<c>json_object()</c>, <c>json_array()</c>, …)
    /// or in the <c>-&gt;</c> operator, and can travel through any expression containing one.
    /// Table columns never persist a subtype, so a projection free of both is safe to hoist.
    /// <c>-&gt;&gt;</c> is treated the same way even though it yields a plain SQL value: it is
    /// the same JSON path machinery, and declining costs an optimization while guessing costs
    /// a wrong answer.
    /// </para>
    /// </summary>
    private static bool ContainsSubtypeSensitiveExpression(Expression? expression)
    {
        if (expression is null)
            return false;

        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            switch (candidate)
            {
                case FunctionExpression:
                case BinaryExpression { Operator: BinaryOperator.JsonArrow or BinaryOperator.JsonArrowText }:
                    found = true;
                    break;
            }

            return !found;
        });

        return found || !modelled;
    }

    /// <summary>
    /// True when the expression calls something whose value can change between invocations for
    /// the same inputs. A function this engine does not recognize as a deterministic built-in
    /// (any application-registered function, for example) counts as non-deterministic: the
    /// classifier cannot see through it, so a rewrite that changes the call count is unsafe.
    /// </summary>
    private static bool ContainsNonDeterministicFunction(Expression? expression)
    {
        if (expression is null)
            return false;

        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            switch (candidate)
            {
                case FunctionExpression function
                    when !SqliteBuiltinFunctions.Contains(function.Name)
                        || !SqliteBuiltinFunctions.IsDeterministic(function.Name):
                    found = true;
                    break;
                case CurrentTimeExpression:
                    found = true;
                    break;
            }

            return !found;
        });

        return found || !modelled;
    }

    /// <summary>
    /// True when evaluating the expression can raise instead of producing a value, using the
    /// same strict rules as <c>expression_can_fail_on_input</c>: every function call counts
    /// (an application-defined one can throw), <c>LIKE</c> and <c>GLOB</c> count because they
    /// can dispatch to a registered implementation, <c>RAISE</c> exists to fail, and the JSON
    /// operators reject malformed input.
    /// </summary>
    private static bool ExpressionCanFailOnInput(Expression? expression)
    {
        if (expression is null)
            return false;

        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            switch (candidate)
            {
                case FunctionExpression:
                case LikeExpression:
                case GlobExpression:
                case RaiseExpression:
                case BinaryExpression { Operator: BinaryOperator.JsonArrow or BinaryOperator.JsonArrowText }:
                    found = true;
                    break;
            }

            return !found;
        });

        return found || !modelled;
    }

    /// <summary>
    /// The instance-aware fallibility test used by the semi/anti-join rewrite: everything
    /// <see cref="ExpressionCanFailOnInput"/> models, plus any comparison that could dispatch to
    /// a registered collation callback.
    /// </summary>
    /// <remarks>
    /// A collation registered through <c>RegisterCollation</c> is ordinary managed code invoked
    /// per comparison, so it can throw exactly like a scalar function — and, like a function, the
    /// rewrite would change how many times and for which rows it runs. Which sequence a
    /// comparison uses depends on the declared collation of the columns involved, which this
    /// decision cannot see, so once any custom sequence exists every comparison counts. An
    /// explicit <c>COLLATE</c> naming a registered sequence always counts, including one that
    /// shadows a built-in name.
    /// </remarks>
    private bool ExpressionCanFail(Expression? expression)
    {
        if (ExpressionCanFailOnInput(expression))
            return true;

        return expression is not null
               && _hasCustomCollations
               && ExpressionCanInvokeCollationCallback(expression);
    }

    /// <summary>
    /// True when any part of the expression could dispatch a comparison to a collation sequence.
    /// Only consulted once a custom sequence is actually registered.
    /// </summary>
    private static bool ExpressionCanInvokeCollationCallback(Expression expression)
    {
        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            switch (candidate)
            {
                case CollationExpression:
                case BetweenExpression:
                case InExpression:
                case CaseExpression { Operand: not null }:
                case BinaryExpression
                {
                    Operator: BinaryOperator.Equal
                        or BinaryOperator.NotEqual
                        or BinaryOperator.LessThan
                        or BinaryOperator.LessThanOrEqual
                        or BinaryOperator.GreaterThan
                        or BinaryOperator.GreaterThanOrEqual
                        or BinaryOperator.Is
                        or BinaryOperator.IsNot,
                }:
                    found = true;
                    break;
            }

            return !found;
        });

        return found || !modelled;
    }

    /// <summary>
    /// True when the expression calls a <em>scalar</em> function whose value can change between
    /// invocations, mirroring <c>expr_contains_nondeterministic_scalar_function</c>.
    /// <para>
    /// A built-in aggregate call is exempt from the test applied to itself — it is a fold over a
    /// set of input rows, not a per-call value — while its arguments are still examined. Anything
    /// else this engine does not recognize as a deterministic built-in counts, including an
    /// application-registered aggregate, whose fold the rewriter cannot reason about.
    /// </para>
    /// </summary>
    private static bool ContainsNonDeterministicScalarFunction(Expression? expression)
    {
        if (expression is null)
            return false;

        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            switch (candidate)
            {
                case FunctionExpression { Window: null } aggregate when IsBuiltInAggregate(aggregate):
                    break;
                case FunctionExpression function
                    when !SqliteBuiltinFunctions.Contains(function.Name)
                        || !SqliteBuiltinFunctions.IsDeterministic(function.Name):
                    found = true;
                    break;
                case CurrentTimeExpression:
                    found = true;
                    break;
            }

            return !found;
        });

        return found || !modelled;
    }

    /// <summary>
    /// True when substituting the expression into more than one place preserves meaning: it
    /// must be free of calls whose result can change and of subqueries (which carry their own
    /// evaluation and memoization lifetime).
    /// </summary>
    private static bool IsDuplicationSafeExpression(Expression expression)
        => !ContainsNonDeterministicFunction(expression)
            && !ContainsSubqueryExpression(expression)
            && !ExpressionContainsRaise(expression);

    private static bool ExpressionContainsRaise(Expression expression)
    {
        var found = false;
        var modelled = ForEachExpression(expression, candidate =>
        {
            if (candidate is RaiseExpression)
                found = true;
            return !found;
        });

        return found || !modelled;
    }

    private static bool TryRewriteClause(
        Expression? expression,
        AstSubstitution substitute,
        ref bool declined,
        out Expression? result)
    {
        if (expression is null)
        {
            result = null;
            return true;
        }

        var rewrote = TryRewriteExpression(expression, substitute, ref declined, out var rewritten);
        result = rewritten;
        return rewrote;
    }

    private static bool TryRewriteExpressionList(
        IReadOnlyList<Expression> expressions,
        AstSubstitution substitute,
        ref bool declined,
        out IReadOnlyList<Expression> result)
    {
        result = expressions;
        if (expressions.Count == 0)
            return true;

        List<Expression>? rewritten = null;
        for (var index = 0; index < expressions.Count; index++)
        {
            if (!TryRewriteExpression(expressions[index], substitute, ref declined, out var item))
                return false;

            if (ReferenceEquals(item, expressions[index]))
                continue;

            rewritten ??= [.. expressions];
            rewritten[index] = item;
        }

        result = rewritten ?? expressions;
        return true;
    }

    private static bool TryRewriteOrderBy(
        IReadOnlyList<OrderByTerm> orderBy,
        AstSubstitution substitute,
        ref bool declined,
        out IReadOnlyList<OrderByTerm> result)
    {
        result = orderBy;
        if (orderBy.Count == 0)
            return true;

        List<OrderByTerm>? rewritten = null;
        for (var index = 0; index < orderBy.Count; index++)
        {
            if (!TryRewriteExpression(orderBy[index].Expression, substitute, ref declined, out var expression))
                return false;

            if (ReferenceEquals(expression, orderBy[index].Expression))
                continue;

            rewritten ??= [.. orderBy];
            rewritten[index] = orderBy[index] with { Expression = expression };
        }

        result = rewritten ?? orderBy;
        return true;
    }

    /// <summary>
    /// Structural expression rewrite that replaces nodes through <paramref name="substitute"/>.
    /// Any node type not listed declines the rewrite rather than being copied through
    /// unexamined.
    /// <para>
    /// A subquery operand is an opaque leaf unless the substitution asks for scalar subqueries
    /// by name: the flattening rewrite vets them separately, while the aggregate decorrelation
    /// rewrite replaces exactly the node it decorrelated. Neither descends into a subquery body,
    /// which belongs to its own scope and gets its own rewrite pass.
    /// </para>
    /// </summary>
    private static bool TryRewriteExpression(
        Expression expression,
        AstSubstitution substitute,
        ref bool declined,
        out Expression result)
    {
        result = expression;
        if (declined)
            return false;

        switch (expression)
        {
            case ColumnExpression column:
                {
                    if (substitute.Column is null)
                        return true;

                    var replacement = substitute.Column(column);
                    if (declined)
                        return false;

                    result = replacement ?? column;
                    return true;
                }
            case ScalarSubqueryExpression scalarSubquery:
                {
                    if (substitute.ScalarSubquery is null)
                        return true;

                    var replacement = substitute.ScalarSubquery(scalarSubquery);
                    if (declined)
                        return false;

                    result = replacement ?? scalarSubquery;
                    return true;
                }
            case LiteralExpression:
            case ParameterExpression:
            case CurrentTimeExpression:
            case ExistsExpression:
            case InSubqueryExpression:
            case StarExpression:
            case QualifiedStarExpression:
                return true;
            case CollationExpression collation:
                {
                    if (!TryRewriteExpression(collation.Expression, substitute, ref declined, out var inner))
                        return false;

                    result = ReferenceEquals(inner, collation.Expression) ? collation : collation with { Expression = inner };
                    return true;
                }
            case CastExpression cast:
                {
                    if (!TryRewriteExpression(cast.Expression, substitute, ref declined, out var inner))
                        return false;

                    result = ReferenceEquals(inner, cast.Expression) ? cast : cast with { Expression = inner };
                    return true;
                }
            case UnaryExpression unary:
                {
                    if (!TryRewriteExpression(unary.Operand, substitute, ref declined, out var operand))
                        return false;

                    result = ReferenceEquals(operand, unary.Operand) ? unary : unary with { Operand = operand };
                    return true;
                }
            case BinaryExpression binary:
                {
                    if (!TryRewriteExpression(binary.Left, substitute, ref declined, out var left)
                        || !TryRewriteExpression(binary.Right, substitute, ref declined, out var right))
                    {
                        return false;
                    }

                    result = ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                        ? binary
                        : binary with { Left = left, Right = right };
                    return true;
                }
            case BetweenExpression between:
                {
                    if (!TryRewriteExpression(between.Value, substitute, ref declined, out var value)
                        || !TryRewriteExpression(between.Lower, substitute, ref declined, out var lower)
                        || !TryRewriteExpression(between.Upper, substitute, ref declined, out var upper))
                    {
                        return false;
                    }

                    result = ReferenceEquals(value, between.Value)
                        && ReferenceEquals(lower, between.Lower)
                        && ReferenceEquals(upper, between.Upper)
                            ? between
                            : between with { Value = value, Lower = lower, Upper = upper };
                    return true;
                }
            case LikeExpression like:
                {
                    if (!TryRewriteExpression(like.Value, substitute, ref declined, out var value)
                        || !TryRewriteExpression(like.Pattern, substitute, ref declined, out var pattern))
                    {
                        return false;
                    }

                    Expression? escape = null;
                    if (like.Escape is not null
                        && !TryRewriteExpression(like.Escape, substitute, ref declined, out escape!))
                    {
                        return false;
                    }

                    result = ReferenceEquals(value, like.Value)
                        && ReferenceEquals(pattern, like.Pattern)
                        && ReferenceEquals(escape, like.Escape)
                            ? like
                            : like with { Value = value, Pattern = pattern, Escape = escape };
                    return true;
                }
            case GlobExpression glob:
                {
                    if (!TryRewriteExpression(glob.Value, substitute, ref declined, out var value)
                        || !TryRewriteExpression(glob.Pattern, substitute, ref declined, out var pattern))
                    {
                        return false;
                    }

                    result = ReferenceEquals(value, glob.Value) && ReferenceEquals(pattern, glob.Pattern)
                        ? glob
                        : glob with { Value = value, Pattern = pattern };
                    return true;
                }
            case InExpression @in:
                {
                    if (!TryRewriteExpression(@in.Value, substitute, ref declined, out var value)
                        || !TryRewriteExpressionList(@in.Values, substitute, ref declined, out var values))
                    {
                        return false;
                    }

                    result = ReferenceEquals(value, @in.Value) && ReferenceEquals(values, @in.Values)
                        ? @in
                        : @in with { Value = value, Values = values };
                    return true;
                }
            case RowValueExpression rowValue:
                {
                    if (!TryRewriteExpressionList(rowValue.Values, substitute, ref declined, out var values))
                        return false;

                    result = ReferenceEquals(values, rowValue.Values) ? rowValue : rowValue with { Values = values };
                    return true;
                }
            case CaseExpression @case:
                {
                    Expression? operand = null;
                    if (@case.Operand is not null
                        && !TryRewriteExpression(@case.Operand, substitute, ref declined, out operand!))
                    {
                        return false;
                    }

                    List<CaseClause>? clauses = null;
                    for (var index = 0; index < @case.Clauses.Count; index++)
                    {
                        var clause = @case.Clauses[index];
                        if (!TryRewriteExpression(clause.When, substitute, ref declined, out var when)
                            || !TryRewriteExpression(clause.Then, substitute, ref declined, out var then))
                        {
                            return false;
                        }

                        if (ReferenceEquals(when, clause.When) && ReferenceEquals(then, clause.Then))
                            continue;

                        clauses ??= [.. @case.Clauses];
                        clauses[index] = new CaseClause(when, then);
                    }

                    Expression? otherwise = null;
                    if (@case.Else is not null
                        && !TryRewriteExpression(@case.Else, substitute, ref declined, out otherwise!))
                    {
                        return false;
                    }

                    result = ReferenceEquals(operand, @case.Operand)
                        && clauses is null
                        && ReferenceEquals(otherwise, @case.Else)
                            ? @case
                            : @case with
                            {
                                Operand = operand,
                                Clauses = clauses ?? @case.Clauses,
                                Else = otherwise,
                            };
                    return true;
                }
            case FunctionExpression function:
                {
                    // Window specifications carry their own partition/order/frame expressions;
                    // the flattening gate rejects them before this point.
                    if (function.Window is not null)
                        return false;

                    if (!TryRewriteExpressionList(function.Arguments, substitute, ref declined, out var arguments))
                        return false;

                    Expression? filter = null;
                    if (function.Filter is not null
                        && !TryRewriteExpression(function.Filter, substitute, ref declined, out filter!))
                    {
                        return false;
                    }

                    IReadOnlyList<OrderByTerm>? aggregateOrderBy = function.AggregateOrderBy;
                    if (aggregateOrderBy is not null
                        && !TryRewriteOrderBy(aggregateOrderBy, substitute, ref declined, out aggregateOrderBy))
                    {
                        return false;
                    }

                    result = ReferenceEquals(arguments, function.Arguments)
                        && ReferenceEquals(filter, function.Filter)
                        && ReferenceEquals(aggregateOrderBy, function.AggregateOrderBy)
                            ? function
                            : function with
                            {
                                Arguments = arguments,
                                Filter = filter,
                                AggregateOrderBy = aggregateOrderBy,
                            };

                    if (substitute.Function is not null)
                    {
                        var replacement = substitute.Function((FunctionExpression)result);
                        if (declined)
                            return false;

                        result = replacement ?? result;
                    }

                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Visits every expression node reachable from <paramref name="expression"/>, including the
    /// bodies of nested subqueries. Returns false when an unmodelled node is reached, so
    /// predicates built on it fail closed. The callback returns false to stop early.
    /// </summary>
    private static bool ForEachExpression(Expression expression, Func<Expression, bool> visit)
    {
        var pending = new Stack<object>();
        var seen = new HashSet<object>(AstReferenceComparer.Instance);
        pending.Push(expression);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Expression current)
            {
                if (!visit(current))
                    return true;

                if (!PushExpressionChildren(current, pending))
                    return false;

                continue;
            }

            if (!PushQueryChildren(node, pending, seen))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Visits every column reference reachable from a statement or expression, subqueries
    /// included. Returns false when the callback rejects a reference or an unmodelled node is
    /// reached.
    /// </summary>
    private static bool ForEachColumnReference(object root, Func<ColumnExpression, bool> visit)
    {
        var pending = new Stack<object>();
        var seen = new HashSet<object>(AstReferenceComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Expression current)
            {
                if (current is ColumnExpression { BooleanKeyword: null } column && !visit(column))
                    return false;

                if (!PushExpressionChildren(current, pending))
                    return false;

                continue;
            }

            if (!PushQueryChildren(node, pending, seen))
                return false;
        }

        return true;
    }

    private static bool PushExpressionChildren(Expression expression, Stack<object> pending)
    {
        switch (expression)
        {
            case LiteralExpression:
            case ParameterExpression:
            case CurrentTimeExpression:
            case ColumnExpression:
            case StarExpression:
            case QualifiedStarExpression:
                return true;
            case RaiseExpression raise:
                if (raise.Message is not null)
                    pending.Push(raise.Message);
                return true;
            case CollationExpression collation:
                pending.Push(collation.Expression);
                return true;
            case CastExpression cast:
                pending.Push(cast.Expression);
                return true;
            case UnaryExpression unary:
                pending.Push(unary.Operand);
                return true;
            case BinaryExpression binary:
                pending.Push(binary.Left);
                pending.Push(binary.Right);
                return true;
            case BetweenExpression between:
                pending.Push(between.Value);
                pending.Push(between.Lower);
                pending.Push(between.Upper);
                return true;
            case LikeExpression like:
                pending.Push(like.Value);
                pending.Push(like.Pattern);
                if (like.Escape is not null)
                    pending.Push(like.Escape);
                return true;
            case GlobExpression glob:
                pending.Push(glob.Value);
                pending.Push(glob.Pattern);
                return true;
            case InExpression @in:
                pending.Push(@in.Value);
                foreach (var value in @in.Values)
                    pending.Push(value);
                return true;
            case InSubqueryExpression inSubquery:
                pending.Push(inSubquery.Value);
                pending.Push(inSubquery.Query);
                return true;
            case ScalarSubqueryExpression scalar:
                pending.Push(scalar.Query);
                return true;
            case ExistsExpression exists:
                pending.Push(exists.Query);
                return true;
            case RowValueExpression rowValue:
                foreach (var value in rowValue.Values)
                    pending.Push(value);
                return true;
            case CaseExpression @case:
                if (@case.Operand is not null)
                    pending.Push(@case.Operand);
                foreach (var clause in @case.Clauses)
                {
                    pending.Push(clause.When);
                    pending.Push(clause.Then);
                }

                if (@case.Else is not null)
                    pending.Push(@case.Else);
                return true;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    pending.Push(argument);
                if (function.Filter is not null)
                    pending.Push(function.Filter);
                if (function.AggregateOrderBy is { } aggregateOrderBy)
                {
                    foreach (var term in aggregateOrderBy)
                        pending.Push(term.Expression);
                }

                if (function.Window is { } window && !PushWindowChildren(window, pending))
                    return false;
                return true;
            default:
                return false;
        }
    }

    private static bool PushWindowChildren(WindowSpecification window, Stack<object> pending)
    {
        foreach (var partition in window.PartitionBy)
            pending.Push(partition);
        foreach (var term in window.OrderBy)
            pending.Push(term.Expression);
        if (window.Frame is { } frame)
        {
            if (frame.Start.Offset is not null)
                pending.Push(frame.Start.Offset);
            if (frame.End.Offset is not null)
                pending.Push(frame.End.Offset);
        }

        return true;
    }

    private static bool PushQueryChildren(
        object node,
        Stack<object> pending,
        HashSet<object> seen)
    {
        if (!seen.Add(node))
            return true;

        switch (node)
        {
            case SelectStatement select:
                foreach (var projection in select.Projections)
                    pending.Push(projection.Expression);
                if (select.Source is not null)
                    pending.Push(select.Source);
                if (select.Where is not null)
                    pending.Push(select.Where);
                foreach (var group in select.GroupBy)
                    pending.Push(group);
                if (select.Having is not null)
                    pending.Push(select.Having);
                foreach (var term in select.OrderBy)
                    pending.Push(term.Expression);
                if (select.Limit is not null)
                    pending.Push(select.Limit);
                if (select.Offset is not null)
                    pending.Push(select.Offset);
                foreach (var named in select.NamedWindows)
                {
                    if (!PushWindowChildren(named.Specification, pending))
                        return false;
                }

                return true;
            case CompoundSelectStatement compound:
                foreach (var term in compound.Terms)
                    pending.Push(term);
                foreach (var term in compound.OrderBy)
                    pending.Push(term.Expression);
                if (compound.Limit is not null)
                    pending.Push(compound.Limit);
                if (compound.Offset is not null)
                    pending.Push(compound.Offset);
                return true;
            case ValuesClause values:
                foreach (var row in values.Rows)
                {
                    foreach (var value in row)
                        pending.Push(value);
                }

                return true;
            case WithSelectStatement with:
                foreach (var cte in with.CommonTableExpressions)
                    pending.Push(cte.Body);
                pending.Push(with.Query);
                return true;
            case NamedTableSource:
                return true;
            case DerivedTableSource derived:
                pending.Push(derived.Query);
                return true;
            case TableValuedFunctionSource function:
                foreach (var argument in function.Arguments)
                    pending.Push(argument);
                return true;
            case JoinTableSource join:
                pending.Push(join.Left);
                pending.Push(join.Right);
                if (join.Condition is not null)
                    pending.Push(join.Condition);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Collects every subquery body reachable from <paramref name="expression"/>. Returns false
    /// when an unmodelled node is reached, since it could hide a subquery.
    /// </summary>
    private static bool TryCollectSubqueries(Expression? expression, List<QueryStatement> found)
    {
        if (expression is null)
            return true;

        return ForEachExpression(expression, candidate =>
        {
            switch (candidate)
            {
                case ScalarSubqueryExpression scalar:
                    found.Add(scalar.Query);
                    break;
                case ExistsExpression exists:
                    found.Add(exists.Query);
                    break;
                case InSubqueryExpression inSubquery:
                    found.Add(inSubquery.Query);
                    break;
            }

            return true;
        });
    }
}

/// <summary>Counts of applied AST rewrites, used as test evidence.</summary>
/// <param name="FlattenedFromSubqueries">FROM-clause derived tables hoisted into their parent.</param>
/// <param name="SemiJoins">Correlated <c>EXISTS</c>/<c>IN</c> terms turned into a semi join.</param>
/// <param name="AntiJoins">Correlated <c>NOT EXISTS</c> terms turned into an anti join.</param>
/// <param name="AggregateGroupFirstRewrites">
/// Correlated single-value aggregate subqueries decorrelated through the group-first route: a
/// grouped derived table LEFT JOINed on the correlation keys.
/// </param>
/// <param name="AggregateJoinFirstRewrites">
/// Correlated single-value aggregate subqueries decorrelated through the join-first route: the
/// inner table LEFT JOINed, grouped by the outer rowid, with the comparison moved to HAVING.
/// </param>
/// <param name="AggregateDecorrelationDeclines">
/// Correlated scalar subqueries that reached the aggregate decorrelation stage carrying an
/// aggregate — i.e. plausible candidates — and were declined by one of its semantic gates. This
/// is the positive evidence that an excluded shape was actually considered and rejected, rather
/// than never reaching the rewriter at all.
/// </param>
internal readonly record struct SelectRewriteDiagnostics(
    long FlattenedFromSubqueries,
    long SemiJoins,
    long AntiJoins,
    long AggregateGroupFirstRewrites,
    long AggregateJoinFirstRewrites,
    long AggregateDecorrelationDeclines);

/// <summary>
/// The node replacements one structural rewrite pass applies. A null delegate leaves that node
/// kind untouched, which is how the flattening rewrite keeps subquery operands opaque while the
/// aggregate decorrelation rewrite replaces exactly the scalar subquery it decorrelated.
/// </summary>
/// <param name="Column">Applied to every column reference.</param>
/// <param name="ScalarSubquery">
/// Applied to every scalar subquery operand, without descending into its body.
/// </param>
/// <param name="Function">
/// Applied to every function call <em>after</em> its arguments, FILTER and ORDER BY have been
/// rewritten, so a replacement observes the already-rewritten call.
/// </param>
internal readonly record struct AstSubstitution(
    Func<ColumnExpression, Expression?>? Column,
    Func<ScalarSubqueryExpression, Expression?>? ScalarSubquery = null,
    Func<FunctionExpression, Expression?>? Function = null)
{
    public static AstSubstitution ForColumns(Func<ColumnExpression, Expression?> column) => new(column);
}

/// <summary>The value a single-value aggregate subquery produces when no inner row matches.</summary>
internal enum AggregateEmptyInputValue
{
    /// <summary>SQL NULL, as returned by <c>avg</c>, <c>min</c>, <c>max</c> and <c>sum</c>.</summary>
    Null,

    /// <summary>Integer zero, as returned by <c>count</c>.</summary>
    IntegerZero,

    /// <summary>Real zero, as returned by <c>total</c>.</summary>
    RealZero,
}

/// <summary>
/// One <c>inner = outer</c> equality that links a correlated aggregate subquery to its enclosing
/// query. <paramref name="InnerWasLeft"/> records which operand the subquery wrote the inner
/// column on, so the rewritten comparison resolves its collation from the same side.
/// </summary>
internal sealed record AggregateCorrelationPair(
    ColumnExpression Inner,
    ColumnExpression Outer,
    bool InnerWasLeft);

/// <summary>The declared comparison behavior of one correlation key column.</summary>
internal readonly record struct CorrelationKeyDescription(
    ColumnAffinity Affinity,
    string Collation);

/// <summary>
/// A correlated single-value aggregate subquery that passed every shared eligibility gate, with
/// the pieces both decorrelation routes need.
/// </summary>
/// <param name="Subquery">The scalar subquery node to be replaced.</param>
/// <param name="Inner">Its SELECT body.</param>
/// <param name="InnerTable">The single base table it reads.</param>
/// <param name="InnerQualifier">The name that table answers to inside the subquery.</param>
/// <param name="Value">Its single result expression, which contains the aggregate.</param>
/// <param name="Pairs">The correlation equalities that link it to the enclosing query.</param>
/// <param name="InnerFilter">The WHERE terms that stay local to the inner table, if any.</param>
/// <param name="EmptyInputValue">What the subquery answers when nothing matches.</param>
/// <param name="CanRunForUnusedKeys">
/// True when evaluating the aggregate for a correlation key no outer row asks for cannot raise
/// an error the original statement never raises — the condition that selects group-first over
/// join-first.
/// </param>
internal sealed record AggregateSubqueryShape(
    ScalarSubqueryExpression Subquery,
    SelectStatement Inner,
    NamedTableSource InnerTable,
    string InnerQualifier,
    Expression Value,
    IReadOnlyList<AggregateCorrelationPair> Pairs,
    Expression? InnerFilter,
    AggregateEmptyInputValue EmptyInputValue,
    bool CanRunForUnusedKeys);

/// <summary>
/// Identity comparer for AST nodes. The AST is built from records, so structural equality
/// would compare whole subtrees; visitor bookkeeping only needs to know whether the exact
/// same node instance was already expanded.
/// </summary>
internal sealed class AstReferenceComparer : IEqualityComparer<object>
{
    public static readonly AstReferenceComparer Instance = new();

    private AstReferenceComparer()
    {
    }

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
