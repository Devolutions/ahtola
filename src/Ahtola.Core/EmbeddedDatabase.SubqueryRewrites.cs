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
/// </list>
/// <para>
/// Everything here is fail-closed: any AST node, clause or reference the rewriter does not
/// explicitly model declines the rewrite and leaves the original statement untouched. The
/// aggregate decorrelation rewrites of <c>unnest.rs</c>
/// (<c>try_rewrite_single_value_aggregate</c>, group-first and join-first) are deliberately
/// <b>not</b> ported; see <c>docs/turso-gap-analysis.md</c>.
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

    /// <summary>
    /// Counts of the rewrites this database instance has applied. Test-only evidence that an
    /// eligible shape actually took the rewritten route (and, just as importantly, that an
    /// excluded shape did not).
    /// </summary>
    internal SelectRewriteDiagnostics RewriteDiagnostics => new(
        Interlocked.Read(ref _flattenedFromSubqueries),
        Interlocked.Read(ref _semiJoinRewrites),
        Interlocked.Read(ref _antiJoinRewrites));

    internal void ResetRewriteDiagnostics()
    {
        Interlocked.Exchange(ref _flattenedFromSubqueries, 0);
        Interlocked.Exchange(ref _semiJoinRewrites, 0);
        Interlocked.Exchange(ref _antiJoinRewrites, 0);
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

        return RewriteCorrelatedSubqueriesAsJoins(rewritten, context);
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

        var projections = RewriteFlattenedProjections(
            select,
            derivedColumns,
            outerColumnNames,
            alias,
            Substitute,
            Count,
            ref declined);
        if (declined || projections is null)
            return select;

        if (!TryRewriteClause(select.Where, Substitute, ref declined, out var outerWhere)
            || !TryRewriteClause(select.Having, Substitute, ref declined, out var having)
            || !TryRewriteClause(select.Limit, Substitute, ref declined, out var limit)
            || !TryRewriteClause(select.Offset, Substitute, ref declined, out var offset)
            || !TryRewriteExpressionList(select.GroupBy, Substitute, ref declined, out var groupBy)
            || !TryRewriteOrderBy(select.OrderBy, Substitute, ref declined, out var orderBy)
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
        Func<ColumnExpression, Expression?> substitute,
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
    {
        innerTable = null!;
        innerQualifier = string.Empty;

        if (inner.Source is not NamedTableSource named
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

        innerTable = named;
        innerQualifier = named.Alias ?? named.Name;
        return true;
    }

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
        Func<ColumnExpression, Expression?> substitute,
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
        Func<ColumnExpression, Expression?> substitute,
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
        Func<ColumnExpression, Expression?> substitute,
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
    /// Structural expression rewrite that replaces column references through
    /// <paramref name="substitute"/>. Subquery operands are opaque leaves (callers vet them
    /// separately), and any node type not listed declines the rewrite rather than being copied
    /// through unexamined.
    /// </summary>
    private static bool TryRewriteExpression(
        Expression expression,
        Func<ColumnExpression, Expression?> substitute,
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
                    var replacement = substitute(column);
                    if (declined)
                        return false;

                    result = replacement ?? column;
                    return true;
                }
            case LiteralExpression:
            case ParameterExpression:
            case CurrentTimeExpression:
            case ScalarSubqueryExpression:
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
                    pending.Push(cte.Query);
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
internal readonly record struct SelectRewriteDiagnostics(
    long FlattenedFromSubqueries,
    long SemiJoins,
    long AntiJoins);

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
