using Ahtola.Core.Indexing;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// One statement's live view of a managed index method: the refreshed attachment plus the results
/// already computed for it.
/// </summary>
/// <remarks>
/// <para>
/// The maintenance cursor is opened, used to refresh the derived state, and disposed immediately, so
/// nothing outlives the statement. Only immutable results are memoized.
/// </para>
/// <para>
/// Everything here is expressed in the method-agnostic
/// <see cref="ManagedIndexMethodResultRow"/> contract. The binding never learns which method
/// produced a row, which is what lets the vector method reuse this path unchanged.
/// </para>
/// </remarks>
internal sealed class ManagedIndexMethodScanBinding
{
    private readonly Dictionary<QueryKey, IReadOnlyList<ManagedIndexMethodResultRow>> _results = [];
    private readonly Dictionary<QueryKey, Dictionary<long, SqlValue>> _rankByRowId = [];

    public ManagedIndexMethodScanBinding(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        ManagedIndexMethodAttachment attachment)
    {
        TableName = tableName;
        Table = table;
        Index = index;
        Attachment = attachment;
    }

    public string TableName { get; }

    public EmbeddedTable Table { get; }

    public EmbeddedIndex Index { get; }

    public ManagedIndexMethodAttachment Attachment { get; }

    /// <summary>Runs one pattern, memoized by pattern and arguments so repeated evaluation is O(1).</summary>
    public IReadOnlyList<ManagedIndexMethodResultRow> Execute(
        int patternIndex,
        SqlValue argument,
        int? limit)
    {
        var key = new QueryKey(patternIndex, DescribeArgument(argument), limit);
        if (_results.TryGetValue(key, out var cached))
            return cached;

        using var cursor = Attachment.Open(new EmbeddedTableIndexSource(Table));
        cursor.OpenRead();
        var arguments = limit is { } value
            ? new[] { argument, SqlValue.Integer(value) }
            : [argument];
        var rows = cursor.Drain(patternIndex, arguments);
        _results.Add(key, rows);
        return rows;
    }

    /// <summary>
    /// The method's rank column for one base row, memoized per query. Rows the method did not
    /// return have no rank, which the caller renders as the method's documented "no match" value.
    /// </summary>
    public bool TryGetRank(int patternIndex, SqlValue argument, long rowId, out SqlValue rank)
    {
        var key = new QueryKey(patternIndex, DescribeArgument(argument), null);
        if (!_rankByRowId.TryGetValue(key, out var map))
        {
            map = [];
            foreach (var row in Execute(patternIndex, argument, limit: null))
                map[row.RowId] = row.Column(0);

            _rankByRowId.Add(key, map);
        }

        return map.TryGetValue(rowId, out rank);
    }

    /// <summary>
    /// A stable memo key for any argument value. Method arguments are not necessarily text — a
    /// vector method receives a blob — so the key has to describe the value's type as well as its
    /// contents, or two differently typed arguments with the same rendering would collide.
    /// </summary>
    private static string DescribeArgument(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "n",
            SqlValueKind.Integer => "i" + value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => "r" + value.AsReal().ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Text => "t" + value.AsText(),
            SqlValueKind.Blob => "b" + Convert.ToHexString(value.AsBlob().Span),
            _ => "?" + value.Kind,
        };

    private readonly record struct QueryKey(int PatternIndex, string Argument, int? Limit);
}

/// <summary>Statement-scoped cache of <see cref="ManagedIndexMethodScanBinding"/> instances.</summary>
internal sealed class ManagedIndexMethodScanCache
{
    private readonly Dictionary<string, ManagedIndexMethodScanBinding> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The attachment for one index without reconciling anything.
    /// </summary>
    /// <remarks>
    /// Planning uses this. It returns the already-reconciled attachment when this statement has
    /// opened the index, and otherwise the catalog's attachment exactly as it stands — cold, stale
    /// or current. Nothing is trained, rebuilt or published, so pricing a candidate the planner goes
    /// on to reject costs nothing beyond the method's own arithmetic.
    /// </remarks>
    public ManagedIndexMethodAttachment GetAttachmentForPlanning(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        if (_bindings.TryGetValue(BuildKey(tableName, index), out var binding)
            && ReferenceEquals(binding.Index, index))
        {
            return binding.Attachment;
        }

        return ManagedIndexMethodSemantics.GetAttachment(tableName, table, index);
    }

    /// <summary>
    /// The reconciled binding for one index, refreshing its derived state on first use.
    /// </summary>
    /// <remarks>
    /// Only the access path that was actually selected, and a scalar call that actually needs a
    /// corpus rank, reach this. Planning deliberately does not.
    /// </remarks>
    public ManagedIndexMethodScanBinding GetOrOpen(string tableName, EmbeddedTable table, EmbeddedIndex index)
    {
        // Key on table + index: two tables may legitimately carry indexes with the same name in
        // different schemas, and a binding must never be reused across them.
        var key = BuildKey(tableName, index);
        if (_bindings.TryGetValue(key, out var binding) && ReferenceEquals(binding.Index, index))
            return binding;

        var attachment = ManagedIndexMethodSemantics.GetAttachment(tableName, table, index);
        using (var cursor = attachment.Open(new EmbeddedTableIndexSource(table)))
        {
            // Reconciles the derived state with the base rows exactly once per statement.
            cursor.OpenRead();
        }

        binding = new ManagedIndexMethodScanBinding(tableName, table, index, attachment);
        _bindings[key] = binding;
        return binding;
    }

    private static string BuildKey(string tableName, EmbeddedIndex index)
        => tableName + "\u0000" + index.Name;
}

/// <summary>The method-index access path the planner selected for one table source.</summary>
internal sealed record ManagedIndexMethodScanPlan(
    NamedTableSource Source,
    string TableName,
    EmbeddedTable Table,
    EmbeddedIndex Index,
    int PatternIndex,
    ManagedIndexPatternShape Shape,
    Expression QueryExpression,
    bool FiltersRows,
    Action<SqlValue>? ValidateArgument,
    long? Limit,
    double EstimatedCost,
    long EstimatedRows,
    bool RetainsUnrankedRows,
    string? Detail,
    ManagedIndexUnrankedMergePolicy UnrankedMergePolicy = ManagedIndexUnrankedMergePolicy.Append,
    double UnrankedRank = 0.0);

public sealed partial class EmbeddedDatabase
{
    private static long _methodIndexScansExecuted;

    /// <summary>
    /// Process-wide diagnostic counter of executed method-index access paths. Tests use it to prove
    /// that a plan the optimizer reported was actually the one that produced the rows, rather than
    /// inferring it from a result set an ordinary scan could also have produced.
    /// </summary>
    internal static long MethodIndexScansExecuted => Interlocked.Read(ref _methodIndexScansExecuted);

    /// <summary>
    /// Attempts to bind a single-table source to one of its method indexes.
    /// </summary>
    /// <remarks>
    /// Mirrors Turso's optimizer stage that matches a source against a method's declared patterns
    /// most-specific first (turso-src/core/translate/optimizer/mod.rs:236-413) and restricts the
    /// candidate collection to single-table access paths (mod.rs:2368). A candidate is accepted only
    /// when its estimated cost beats the full scan, and the original predicate is never omitted, so
    /// choosing the index can filter and rank but can never change the answer.
    /// </remarks>
    private bool TryPlanMethodIndexScan(
        NamedTableSource source,
        QueryContext context,
        Expression? predicate,
        IReadOnlyList<OrderByTerm>? orderBy,
        long? maximumRows,
        out ManagedIndexMethodScanPlan plan,
        bool allowsRowTruncation = false,
        IReadOnlyList<Expression>? resultExpressions = null)
    {
        plan = null!;
        if (source.IndexDirective is not null)
            return false;
        if (!TryResolveMethodIndexTable(source, context, out var tableName, out var table))
            return false;

        var qualifier = source.Alias ?? source.Name;
        ManagedIndexMethodScanPlan? best = null;
        var bestSteadyState = 0.0;
        ManagedIndexMethodAttachment? matchedSemantics = null;
        foreach (var index in table.Indexes)
        {
            if (!index.IsMethodIndex)
                continue;

            // Planning reads the attachment as it stands. Nothing here reconciles, trains, rebuilds
            // or publishes: a candidate that loses must cost nothing, and EXPLAIN QUERY PLAN must
            // not be a way to make the engine rebuild every cold index on a table.
            var attachment = context.MethodIndexCache.GetAttachmentForPlanning(tableName, table, index);
            ManagedIndexMethodMvcc.Ensure(attachment.Definition, context.ConcurrentMvStore is not null, forWrite: false);

            var plannerContext = new ManagedIndexMethodPlannerContext(
                tableName,
                qualifier,
                attachment.Configuration.Columns,
                predicate,
                orderBy,
                maximumRows,
                IsShadowedMethodFunction,
                IsHoistableArgument,
                allowsRowTruncation,
                resultExpressions);
            if (!attachment.Planner.TryMatch(plannerContext, out var match))
                continue;

            if (matchedSemantics is not null
                && !matchedSemantics.HasEquivalentQuerySemantics(attachment))
            {
                // Two indexes can cover the same call while assigning different tokenizers,
                // boosts or posting detail to it. Choosing either for prefiltering and another (or
                // neither) for scalar evaluation changes the answer, so the only safe plan is the
                // ordinary row-local path.
                return false;
            }
            matchedSemantics ??= attachment;

            // Last line of defence, independent of what the method claimed: a pattern that filters
            // rows and truncates to a pushed-down LIMIT returns *only* its best rows, so honoring
            // that limit is safe solely when the statement shape proved nothing downstream can
            // re-rank or re-filter them. Otherwise the limited shape degrades to its unlimited form
            // and the ordinary pipeline applies the LIMIT, which can add work but never lose rows.
            if (match.FiltersRows
                && !allowsRowTruncation
                && ManagedIndexPatternShapes.HasLimit(match.Shape))
            {
                match = match with { Shape = ManagedIndexPatternShapes.WithoutLimit(match.Shape) };
            }

            if (!attachment.Definition.TryFindPattern(match.Shape, out var patternIndex))
            {
                // Fall back to the unlimited form when the method does not declare the limited one.
                var relaxed = ManagedIndexPatternShapes.WithoutLimit(match.Shape);
                if (relaxed == match.Shape || !attachment.Definition.TryFindPattern(relaxed, out patternIndex))
                    continue;

                match = match with { Shape = relaxed };
            }

            var pushedLimit = ManagedIndexPatternShapes.HasLimit(match.Shape) ? maximumRows : null;

            // Truncation is honored only when the method asked for it, the shape really carries a
            // pushed-down limit, and the statement shape allows it. Any doubt keeps every unranked
            // base row, which can only ever produce extra rows, never missing ones. The decision is
            // made before pricing so the method prices the plan it will actually be asked to run.
            var retainsUnrankedRows = match.RetainsUnrankedRows
                || pushedLimit is null
                || !ManagedIndexPatternShapes.HasLimit(match.Shape)
                || !allowsRowTruncation;
            var snapshot = attachment.EstimateCost(
                new EmbeddedTableIndexSource(table),
                new ManagedIndexMethodCostContext(
                    patternIndex,
                    table.Rows.Count,
                    pushedLimit,
                    [],
                    retainsUnrankedRows));
            if (snapshot is not { Estimate: var cost })
                continue;

            // A full scan reads every base row once; only take the method when it is cheaper. The
            // comparison uses the steady-state cost, because the reconciliation the method reports
            // separately is owed by the index itself — a scalar call on the plain scan path pays the
            // same maintenance — rather than by choosing this access path. Charging it here would
            // make a cold index permanently unusable: it would lose every comparison, never be
            // selected, and therefore never be reconciled.
            var scanCost = Math.Max(table.Rows.Count, 1);
            var steadyState = cost.SteadyStateCost;
            if (steadyState >= scanCost)
                continue;

            var candidate = new ManagedIndexMethodScanPlan(
                source,
                tableName,
                table,
                index,
                patternIndex,
                match.Shape,
                match.QueryExpression,
                match.FiltersRows,
                match.ValidateArgument,
                pushedLimit,
                cost.EstimatedCost,
                cost.EstimatedRows,
                retainsUnrankedRows,
                cost.Detail,
                match.UnrankedMergePolicy,
                match.UnrankedRank);

            // Steady state decides; the reconciliation each candidate still owes breaks ties, so two
            // otherwise equal indexes resolve to the one that is already current instead of to
            // whichever the catalog happens to list first.
            if (best is null
                || steadyState < bestSteadyState
                || (steadyState == bestSteadyState && candidate.EstimatedCost < best.EstimatedCost)
                || (steadyState == bestSteadyState
                    && candidate.EstimatedCost == best.EstimatedCost
                    && string.Compare(
                        candidate.Index.Name,
                        best.Index.Name,
                        StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = candidate;
                bestSteadyState = steadyState;
            }
        }

        if (best is null)
            return false;

        plan = best;
        return true;
    }

    /// <summary>
    /// True when a connection-registered scalar callback shadows a method-owned function name.
    /// </summary>
    /// <remarks>
    /// A user callback named <c>fts_match</c> replaces the built-in for every call in the statement.
    /// Planning a method index for such a call would answer with index semantics while the scalar
    /// evaluator answers with the user's function, so the two paths would disagree. Declining is the
    /// only behavior that keeps "the plan cannot change the answer" true.
    /// </remarks>
    private bool IsShadowedMethodFunction(string name)
    {
        lock (_gate)
        {
            foreach (var (registeredName, _) in _scalarFunctions)
            {
                if (string.Equals(registeredName.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool TryResolveMethodIndexTable(
        NamedTableSource source,
        QueryContext context,
        out string tableName,
        out EmbeddedTable table)
    {
        tableName = source.Name;
        table = null!;
        if (context.Views?.ContainsKey(tableName) == true || context.VirtualTables?.ContainsKey(tableName) == true)
            return false;
        if (ManagedSchemaName.TrySplit(tableName, out var schema, out var localName))
        {
            if (!string.Equals(schema, "main", StringComparison.OrdinalIgnoreCase))
                return false;

            tableName = localName;
        }

        if (!context.Tables.TryGetValue(tableName, out var resolved) || !resolved.HasMethodIndexes)
            return false;

        // A method index derives from base rows; the concurrent MVCC overlay path materializes a
        // different row set, so fall back to the ordinary scan there rather than answer from stale
        // derived state.
        if (context.ConcurrentMvStore is not null)
            return false;

        table = resolved;
        return true;
    }

    /// <summary>
    /// True when an expression may be evaluated once for the whole scan instead of once per row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row independence is necessary but not sufficient. A method-index query argument is
    /// evaluated a single time before the scan starts, so anything whose value can differ between
    /// evaluations — or whose evaluation is itself an observable per-row event — must stay on the
    /// ordinary scalar path: a non-deterministic built-in (<c>random()</c>, <c>changes()</c>,
    /// <c>datetime('now')</c>), a function the connection registered itself (arbitrary managed
    /// code that may answer differently, or throw, on each call), <c>CURRENT_TIMESTAMP</c>, and
    /// every node shape this walk does not model.
    /// </para>
    /// <para>
    /// The test is recursive, so a deterministic wrapper around an unhoistable call
    /// (<c>abs(random())</c>, <c>'x' || my_udf()</c>) is just as unhoistable as the call itself.
    /// </para>
    /// </remarks>
    private bool IsHoistableArgument(Expression expression)
    {
        var pending = new Stack<Expression>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case ColumnExpression { BooleanKeyword: null }:
                    return false;
                case LiteralExpression or ParameterExpression:
                    continue;
                case UnaryExpression unary:
                    pending.Push(unary.Operand);
                    continue;
                case BinaryExpression binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    continue;
                case FunctionExpression function:
                    if (!IsHoistableFunction(function.Name))
                        return false;
                    foreach (var argument in function.Arguments)
                        pending.Push(argument);
                    continue;
                case CollationExpression collation:
                    pending.Push(collation.Expression);
                    continue;
                default:
                    // Unknown shapes are treated conservatively as row dependent.
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when a call to <paramref name="name"/> is a deterministic built-in that no registered
    /// callback shadows.
    /// </summary>
    /// <remarks>
    /// A registered scalar callback never hoists, even when its name matches a deterministic
    /// built-in: the callback replaces the built-in for every call in the statement, and the
    /// engine cannot know whether it is pure. An unknown name does not hoist either — a function
    /// this build does not model is not a function it can reason about.
    /// </remarks>
    private bool IsHoistableFunction(string name)
    {
        if (_hasScalarFunctions)
        {
            lock (_gate)
            {
                foreach (var (registeredName, _) in _scalarFunctions)
                {
                    if (string.Equals(registeredName.Name, name, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
        }

        return SqliteBuiltinFunctions.Contains(name) && SqliteBuiltinFunctions.IsDeterministic(name);
    }

    /// <summary>
    /// Executes a method-index access path: the method produces ranked rowids and the engine seeks
    /// each one back to its base row, exactly like Turso's <c>query_rowid</c> contract
    /// (turso-src/core/index_method/mod.rs:196-209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here is FTS specific. The plan carries a pattern index, a row-independent argument
    /// and a "does this pattern filter rows" flag; the rows come back through
    /// <see cref="ManagedIndexMethodResultRow"/>. A KNN pattern from a vector method flows through
    /// the identical code.
    /// </para>
    /// <para>
    /// A ranking-only pattern (score or distance ordering with no matching predicate) never removes
    /// rows from a result set. Those plans therefore append every base row the method did not rank,
    /// in rowid order, after the ranked ones. Dropping them is what made
    /// <c>ORDER BY fts_score(...) DESC</c> silently lose rows whose score was zero.
    /// </para>
    /// </remarks>
    private SourceData GetMethodIndexRows(
        ManagedIndexMethodScanPlan plan,
        SqlValue[] parameters,
        QueryContext context,
        long? maximumRows,
        SourceRow? outerRow)
    {
        Interlocked.Increment(ref _methodIndexScansExecuted);
        var table = plan.Table;
        var qualifier = plan.Source.Alias ?? plan.Source.Name;
        context.RegisterMethodIndexSource(qualifier, plan.TableName, table);
        var methodIndexSource = new MethodIndexSourceBinding(plan.TableName, table);
        var qualifiedColumns = BuildQualifiedColumns(qualifier, table.Columns);
        var qualifiedColumnDefinitions = BuildQualifiedColumnDefinitions(qualifier, table.ColumnDefinitions);
        var queryValue = Evaluate(plan.QueryExpression, parameters, outerRow, context);

        // Raise exactly the errors the scalar form would have raised for this argument, before any
        // plan-specific work: choosing an index must never convert an error into a row set.
        plan.ValidateArgument?.Invoke(queryValue);

        var rowPositions = new Dictionary<long, int>(table.Rows.Count);
        var orderedRowIds = new List<long>(table.Rows.Count);
        for (var position = 0; position < table.Rows.Count; position++)
        {
            var rowId = position < table.RowIds.Count ? table.RowIds[position] : position + 1L;
            rowPositions[rowId] = position;
            orderedRowIds.Add(rowId);
        }

        // An ordinary table scan rewinds a b-tree cursor to its smallest integer key, so it produces
        // rows in ascending rowid order regardless of the order they were inserted in or the order
        // the evaluator's heap-backed row list happens to hold them in. This path has to emit the
        // rows it did not rank in exactly that order: a stable ORDER BY keeps its input order among
        // equal keys, so appending them in storage order would make a tie at the LIMIT boundary
        // resolve differently here than on the scan — and differently again after a reopen, which
        // reloads the rows in b-tree order.
        orderedRowIds.Sort();

        IReadOnlyList<ManagedIndexMethodResultRow> ranked;
        var retainUnranked = plan.RetainsUnrankedRows;
        if (queryValue.Kind == SqlValueKind.Null)
        {
            // A NULL argument selects nothing. For a filtering pattern that is an empty result; for
            // a ranking-only pattern every base row still has to be produced, unranked. A method
            // that asked to truncate never gets to truncate an empty ranking down to no rows at all,
            // because "the method ranked nothing" is not the same claim as "the statement keeps
            // nothing".
            ranked = [];
            retainUnranked = true;
        }
        else
        {
            var binding = context.MethodIndexCache.GetOrOpen(plan.TableName, table, plan.Index);
            var limit = plan.Limit is { } value && value >= 0 ? (int)Math.Min(value, int.MaxValue) : (int?)null;
            ranked = binding.Execute(plan.PatternIndex, queryValue, limit);
        }

        var rows = new List<SourceRow>(Math.Min(ranked.Count + 1, 1024));
        var emitted = new HashSet<long>();

        // A ranking-only pattern that pushed the statement's own ORDER BY down into the method (see
        // ManagedIndexUnrankedMergePolicy.MergeByDescendingRank) leaves nothing downstream to re-sort
        // by score: the rows this method returns, in the order it returns them, are what the LIMIT
        // truncates. Emitting every ranked hit before any unranked row is only sound when an unranked
        // row is genuinely worse than every ranked one, and that is exactly the claim a rank tie
        // breaks: an all-zero-weight FTS index scores every row 0.0, so a row it ranked and a row it
        // never looked at compare equal, not "ranked beats unranked". The two groups have to be
        // merged by the same rank before the LIMIT sees them, or a tie at the boundary keeps rows in
        // storage order instead of the lowest rowid an unindexed scan of the same statement keeps.
        if (!plan.FiltersRows && retainUnranked && plan.UnrankedMergePolicy == ManagedIndexUnrankedMergePolicy.MergeByDescendingRank)
        {
            var merged = new List<(double Rank, long RowId)>(ranked.Count + orderedRowIds.Count);
            foreach (var hit in ranked)
            {
                context.CheckInterrupt();
                if (emitted.Add(hit.RowId))
                    merged.Add((hit.Rank, hit.RowId));
            }

            // Every rowid the method never ranked ties at the same fallback the scalar path's own
            // scoring function returns for it (see ManagedFtsPlannerAdapter's
            // ScoreOrdered/ScoreOrderedLimit branch and EvaluateFtsScore's unranked fallback), so it
            // takes part in the same sort rather than being appended after every ranked row
            // regardless of how it would actually compare.
            foreach (var rowId in orderedRowIds)
            {
                context.CheckInterrupt();
                if (emitted.Add(rowId))
                    merged.Add((plan.UnrankedRank, rowId));
            }

            // Requesting only plan.Limit ranked hits above stays safe under this merge:
            // ManagedFtsSearchIndex.Search already returns its hits sorted (score DESC, rowid ASC)
            // and truncated at that same limit, so any hit the index dropped sorts strictly after
            // every hit it kept under the identical comparator — merging an already-truncated ranked
            // list can only omit rows that belong after everything kept, never one that belonged
            // ahead of them.
            merged.Sort(static (left, right) =>
            {
                var byRank = right.Rank.CompareTo(left.Rank);
                return byRank != 0 ? byRank : left.RowId.CompareTo(right.RowId);
            });

            foreach (var (_, rowId) in merged)
            {
                context.CheckInterrupt();
                if (maximumRows is { } cap && rows.Count >= cap)
                    break;
                if (!rowPositions.TryGetValue(rowId, out var position))
                    continue;

                rows.Add(CreateMethodIndexRow(table, position, rowId, qualifier, qualifiedColumns, qualifiedColumnDefinitions, outerRow, methodIndexSource));
            }

            return new SourceData(table.Columns, rows);
        }

        foreach (var hit in ranked)
        {
            context.CheckInterrupt();
            if (maximumRows is { } cap && rows.Count >= cap)
                return new SourceData(table.Columns, rows);
            if (!rowPositions.TryGetValue(hit.RowId, out var position) || !emitted.Add(hit.RowId))
                continue;

            rows.Add(CreateMethodIndexRow(table, position, hit.RowId, qualifier, qualifiedColumns, qualifiedColumnDefinitions, outerRow, methodIndexSource));
        }

        if (plan.FiltersRows)
            return new SourceData(table.Columns, rows);

        // A ranking pattern that did not ask to truncate must still produce every base row the
        // method left unranked, so ordering can never remove a row from a result set.
        if (!retainUnranked)
            return new SourceData(table.Columns, rows);

        foreach (var rowId in orderedRowIds)
        {
            context.CheckInterrupt();
            if (maximumRows is { } cap && rows.Count >= cap)
                break;
            if (!emitted.Add(rowId) || !rowPositions.TryGetValue(rowId, out var position))
                continue;

            rows.Add(CreateMethodIndexRow(table, position, rowId, qualifier, qualifiedColumns, qualifiedColumnDefinitions, outerRow, methodIndexSource));
        }

        return new SourceData(table.Columns, rows);
    }

    private static SourceRow CreateMethodIndexRow(
        EmbeddedTable table,
        int position,
        long rowId,
        string qualifier,
        IReadOnlyDictionary<string, int> qualifiedColumns,
        IReadOnlyDictionary<string, EmbeddedColumn> qualifiedColumnDefinitions,
        SourceRow? outerRow,
        MethodIndexSourceBinding methodIndexSource)
        => new(
            table.Columns,
            table.Rows[position],
            qualifiedColumns,
            outerRow,
            RowId: table.HasRowid ? rowId : null,
            RowIdQualifier: qualifier,
            ColumnDefinitions: table.ColumnDefinitions,
            QualifiedColumnDefinitions: qualifiedColumnDefinitions,
            MethodIndexSource: methodIndexSource);
    /// <summary>Resolves a source qualifier (alias or table name) to its base table.</summary>
    /// <remarks>
    /// The row that is being evaluated is authoritative: it carries the identity of every source
    /// that contributed to it, so an alias a nested query reused for a different table cannot
    /// redirect the binding. The statement's scanned-source registry is consulted next, for callers
    /// that have no row at all; it declines for any qualifier it saw bound to two different tables.
    /// Falling back to the catalog by name covers sources resolved before any row was produced
    /// (EXPLAIN, for example).
    /// </remarks>
    private static bool TryResolveSourceTable(
        string qualifier,
        SourceRow? row,
        QueryContext context,
        out string tableName,
        out EmbeddedTable table)
    {
        if (row?.GetMethodIndexSourceForQualifier(qualifier) is { } carried)
        {
            tableName = carried.TableName;
            table = carried.Table;
            return true;
        }

        if (!context.AmbiguousMethodIndexSources.Contains(qualifier)
            && context.MethodIndexSourceBindings.TryGetValue(qualifier, out var bound))
        {
            tableName = bound.TableName;
            table = bound.Table;
            return true;
        }

        tableName = ResolveMethodIndexSourceName(qualifier);
        table = null!;
        if (ManagedSchemaName.TrySplit(qualifier, out var schema, out _)
            && !string.Equals(schema, "main", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!context.Tables.TryGetValue(tableName, out var resolved))
            return false;

        table = resolved;
        return true;
    }

    /// <summary>Strips a <c>main.</c> schema qualifier from a source name.</summary>
    private static string ResolveMethodIndexSourceName(string name)
        => ManagedSchemaName.TrySplit(name, out var schema, out var localName)
            && string.Equals(schema, "main", StringComparison.OrdinalIgnoreCase)
                ? localName
                : name;

    private static IReadOnlyList<string?> CollectArgumentColumnNames(FunctionExpression function)
    {
        var names = new string?[Math.Max(function.Arguments.Count - 1, 0)];
        for (var position = 0; position < names.Length; position++)
        {
            names[position] = function.Arguments[position] is ColumnExpression { BooleanKeyword: null } column
                ? column.UnqualifiedName ?? column.Name
                : null;
        }

        return names;
    }

    /// <summary>Plans a whole SELECT against its table's method indexes, for EXPLAIN QUERY PLAN.</summary>
    /// <remarks>
    /// ORDER BY terms are resolved against the projections first, so a result alias
    /// (<c>SELECT distance(...) AS d … ORDER BY d</c>) is planned as the call it names. The execution
    /// path resolves the same way, so the plan this reports is the plan that runs.
    /// </remarks>
    private bool TryPlanMethodIndexScanForSelect(
        SelectStatement select,
        QueryContext context,
        out ManagedIndexMethodScanPlan plan)
    {
        plan = null!;
        return select.Source is NamedTableSource named
            && TryPlanMethodIndexScan(
                named,
                context,
                select.Where,
                ResolveOrderBy(select.OrderBy, select.Projections),
                ReadMethodIndexLimit(select),
                out plan,
                AllowsMethodIndexRowTruncation(select),
                select.Projections.Select(static projection => projection.Expression).ToArray());
    }

    /// <summary>
    /// True when a method may return only the rows its pushed-down LIMIT keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Truncation is sound only when nothing between the source and the final result can reintroduce
    /// or re-rank a row the method dropped. <c>DISTINCT</c> can collapse kept rows and pull more in
    /// from behind the cut; <c>GROUP BY</c>, <c>HAVING</c>, an aggregate or a window computes over
    /// the whole input; a second ORDER BY term reorders across the cut; a non-literal LIMIT or
    /// OFFSET means the cut is not known at plan time; and a source that is not a plain table has no
    /// single scan to truncate.
    /// </para>
    /// <para>
    /// Everything that reaches a method through any other route — a join arm, a subquery, a DML
    /// statement — never sets this, so the conservative retain-everything behavior is the default.
    /// </para>
    /// </remarks>
    private bool AllowsMethodIndexRowTruncation(SelectStatement select)
        => select.Source is NamedTableSource
            && !select.Distinct
            && select.GroupBy.Count == 0
            && select.Having is null
            && select.NamedWindows.Count == 0
            && select.OrderBy.Count <= 1
            && ReadMethodIndexLimit(select) is not null
            && !select.Projections.Any(projection => ContainsAggregate(projection.Expression))
            && !select.OrderBy.Any(term => ContainsAggregate(term.Expression))
            && !select.Projections.Any(projection => ContainsWindowFunction(projection.Expression))
            && !select.OrderBy.Any(term => ContainsWindowFunction(term.Expression));

    /// <summary>
    /// The row count a method may truncate to for this statement, or null when no literal bound can
    /// be proven.
    /// </summary>
    /// <remarks>
    /// OFFSET is folded in: <c>LIMIT 3 OFFSET 5</c> still needs the first eight ranked rows, and a
    /// non-literal OFFSET blocks pushdown entirely rather than silently discarding rows the outer
    /// query would have skipped past.
    /// </remarks>
    private static long? ReadMethodIndexLimit(SelectStatement select)
    {
        if (ReadLiteralLimit(select.Limit) is not { } limit || limit < 0)
            return null;
        if (select.Offset is null)
            return limit;
        if (ReadLiteralLimit(select.Offset) is not { } offset || offset < 0)
            return null;

        return limit + offset;
    }

    /// <summary>
    /// True when this SELECT has a viable method-index access path and must therefore be executed by
    /// the evaluator rather than lowered to bytecode.
    /// </summary>
    /// <remarks>
    /// The cheap gate runs first: a source that is not a plain base table, or whose table carries no
    /// method index, never pays for the full planner probe.
    /// </remarks>
    private bool ShouldDeferSelectToMethodIndexPlan(SelectStatement select, QueryContext context)
        => select.Source is NamedTableSource named
            && named.IndexDirective is null
            && TryResolveMethodIndexTable(named, context, out _, out _)
            && TryPlanMethodIndexScanForSelect(select, context, out _);

    private static long? ReadLiteralLimit(Expression? limit)
        => limit is LiteralExpression { Value.Kind: SqlValueKind.Integer } literal
            ? literal.Value.AsInteger()
            : null;

    /// <summary>Renders the EXPLAIN QUERY PLAN detail row for a method-index access path.</summary>
    /// <remarks>
    /// The method's own plan description is appended verbatim; the core neither produces nor parses
    /// it, so a method can make its plan auditable without leaking its vocabulary into the planner.
    /// </remarks>
    private static string FormatMethodIndexExplainDetail(ManagedIndexMethodScanPlan plan)
    {
        var alias = plan.Source.Alias is { } aliasName && !string.Equals(aliasName, plan.TableName, StringComparison.OrdinalIgnoreCase)
            ? $"{plan.TableName} AS {aliasName}"
            : plan.TableName;
        var detail = string.IsNullOrEmpty(plan.Detail) ? string.Empty : " " + plan.Detail;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"SEARCH {alias} USING INDEX METHOD {plan.Index.Method} INDEX {plan.Index.Name} (pattern={plan.Shape}{detail} rows~{plan.EstimatedRows} cost~{plan.EstimatedCost:0.###})");
    }
}
