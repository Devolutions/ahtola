using Ahtola.Core.Compilation;
using Ahtola.Core.Compilation.JoinOrdering;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;
using System.Globalization;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private readonly PlannerAccessPathMetrics _plannerAccessPathMetrics = new();

    internal PlannerAccessPathMetrics PlannerAccessPathMetrics => _plannerAccessPathMetrics;

    private sealed record ManagedAndIndexBranch(EmbeddedIndex Index, Expression Predicate);

    private sealed record ManagedAndIndexIntersectionPlan(
        NamedTableSource Source,
        EmbeddedTable Table,
        IReadOnlyList<ManagedAndIndexBranch> Branches,
        double EstimatedRows);

    private EmbeddedTable GetOrCreateSqliteStat4Table(SchemaCatalog catalog)
    {
        if (catalog.Tables.TryGetValue(SqliteStat4TableName, out var existing))
        {
            ValidateSqliteStat4Table(existing);
            return existing;
        }

        if (catalog.Views.ContainsKey(SqliteStat4TableName)
            || catalog.Triggers.ContainsKey(SqliteStat4TableName)
            || TryFindIndex(catalog.Tables, SqliteStat4TableName, out _, out _))
        {
            throw new EmbeddedSqlException($"object name reserved for internal use: {SqliteStat4TableName}");
        }

        EnforceMaxPageCountForCatalogChange(1);
        var statistics = new EmbeddedTable(
            SqliteStat4TableName,
            [
                new EmbeddedColumn("tbl", null, false, false, false, null),
                new EmbeddedColumn("idx", null, false, false, false, null),
                new EmbeddedColumn("neq", null, false, false, false, null),
                new EmbeddedColumn("nlt", null, false, false, false, null),
                new EmbeddedColumn("ndlt", null, false, false, false, null),
                new EmbeddedColumn("sample", null, false, false, false, null),
            ])
        {
            Sql = "CREATE TABLE sqlite_stat4(tbl,idx,neq,nlt,ndlt,sample)"
        };
        catalog.Tables.Add(statistics.Name, statistics);
        return statistics;
    }

    private static void ValidateSqliteStat4Table(EmbeddedTable table)
    {
        string[] expected = ["tbl", "idx", "neq", "nlt", "ndlt", "sample"];
        if (!table.HasRowid
            || table.ColumnDefinitions.Length != expected.Length
            || table.PrimaryKeyColumns.Count != 0
            || table.Indexes.Count != 0)
        {
            throw new EmbeddedSqlException("database disk image is malformed");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(
                    table.ColumnDefinitions[index].Name,
                    expected[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException("database disk image is malformed");
            }
        }
    }

    private static void DeletePlannerStatistics(
        EmbeddedTable statistics,
        string tableName,
        string? indexName)
    {
        for (var rowIndex = statistics.Rows.Count - 1; rowIndex >= 0; rowIndex--)
        {
            var row = statistics.Rows[rowIndex];
            if (row.Length < 2
                || row[0].Kind != SqlValueKind.Text
                || !string.Equals(row[0].AsText(), tableName, StringComparison.OrdinalIgnoreCase)
                || indexName is not null
                    && (row[1].Kind != SqlValueKind.Text
                        || !string.Equals(row[1].AsText(), indexName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            statistics.Rows.RemoveAt(rowIndex);
            statistics.RowIds.RemoveAt(rowIndex);
        }
    }

    private void AddIndexStat4Samples(
        EmbeddedTable statistics,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        if (table.Rows.Count == 0
            || !table.HasRowid
            || index.Columns.Count == 0
            || index.IsPartial
            || index.IsMethodIndex
            || index.Columns.Any(static column => column.IsExpression)
            || IndexUsesRegisteredFunctions(index)
            || index.Columns.Any(column =>
                !IsBuiltInCollation(IndexExpressionSemantics.GetCollationName(table, column))
                || IsOverriddenBuiltInCollation(
                    IndexExpressionSemantics.GetCollationName(table, column))))
        {
            return;
        }

        var entries = new List<(SqlValue[] Key, long RowId)>(table.Rows.Count);
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var rowId = table.RowIds[rowIndex];
            if (!IndexExpressionSemantics.Qualifies(
                    index,
                    table,
                    row,
                    rowId,
                    EvaluateIndexExpression))
            {
                continue;
            }

            entries.Add((
                IndexExpressionSemantics.ProjectKey(
                    index,
                    table,
                    row,
                    rowId,
                    EvaluateIndexExpression),
                rowId));
        }

        if (entries.Count == 0)
            return;

        entries.Sort((left, right) =>
        {
            var comparison = CompareIndexStatisticKeys(table, index, left.Key, right.Key);
            return comparison != 0 ? comparison : left.RowId.CompareTo(right.RowId);
        });

        var samplePositions = SelectStat4SamplePositions(table, index, entries);
        var textEncoding = GetTextEncoding();
        foreach (var samplePosition in samplePositions)
        {
            var sample = entries[samplePosition];
            var equalCounts = new long[index.Columns.Count];
            var lessCounts = new long[index.Columns.Count];
            var distinctLessCounts = new long[index.Columns.Count];
            for (var prefixLength = 1; prefixLength <= index.Columns.Count; prefixLength++)
            {
                SqlValue[]? previous = null;
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    var current = entries[entryIndex].Key;
                    var comparison = CompareIndexStatisticKeyPrefix(
                        table,
                        index,
                        current,
                        sample.Key,
                        prefixLength);
                    if (comparison < 0)
                    {
                        lessCounts[prefixLength - 1]++;
                        if (previous is null
                            || !IndexPrefixesEqual(
                                table,
                                index,
                                previous,
                                current,
                                prefixLength))
                        {
                            distinctLessCounts[prefixLength - 1]++;
                        }
                    }
                    else if (comparison == 0)
                    {
                        equalCounts[prefixLength - 1]++;
                    }

                    previous = current;
                }
            }

            var recordValues = new SqlValue[index.Columns.Count + 1];
            Array.Copy(sample.Key, recordValues, sample.Key.Length);
            recordValues[^1] = SqlValue.Integer(sample.RowId);
            AddStat4Row(
                statistics,
                table.Name,
                index.Name,
                JoinStatVector(equalCounts),
                JoinStatVector(lessCounts),
                JoinStatVector(distinctLessCounts),
                SqliteRecordCodec.Encode(recordValues, textEncoding));
        }
    }

    private IReadOnlyList<int> SelectStat4SamplePositions(
        EmbeddedTable table,
        EmbeddedIndex index,
        IReadOnlyList<(SqlValue[] Key, long RowId)> entries)
    {
        const int maximumSamples = 24;
        if (entries.Count <= maximumSamples)
            return Enumerable.Range(0, entries.Count).ToArray();

        var runs = new List<(int Start, int Count)>();
        var start = 0;
        for (var indexPosition = 1; indexPosition <= entries.Count; indexPosition++)
        {
            if (indexPosition < entries.Count
                && IndexPrefixesEqual(
                    table,
                    index,
                    entries[indexPosition - 1].Key,
                    entries[indexPosition].Key,
                    prefixLength: 1))
            {
                continue;
            }

            runs.Add((start, indexPosition - start));
            start = indexPosition;
        }

        var selected = new HashSet<int>();
        foreach (var run in runs
                     .OrderByDescending(static run => run.Count)
                     .ThenBy(static run => run.Start)
                     .Take(maximumSamples / 2))
        {
            selected.Add(run.Start + (run.Count / 2));
        }

        for (var sample = 0; selected.Count < maximumSamples && sample < maximumSamples; sample++)
        {
            var position = (int)(((long)(sample * 2 + 1) * entries.Count) / (maximumSamples * 2L));
            selected.Add(Math.Min(entries.Count - 1, position));
        }
        for (var position = 0; selected.Count < maximumSamples && position < entries.Count; position++)
            selected.Add(position);

        return selected.Order().Take(maximumSamples).ToArray();
    }

    private int CompareIndexStatisticKeyPrefix(
        EmbeddedTable table,
        EmbeddedIndex index,
        SqlValue[] left,
        SqlValue[] right,
        int prefixLength)
    {
        for (var position = 0; position < prefixLength; position++)
        {
            var column = index.Columns[position];
            var comparison = Compare(
                left[position],
                right[position],
                IndexExpressionSemantics.GetCollationName(table, column));
            if (comparison != 0)
                return column.Descending ? -comparison : comparison;
        }

        return 0;
    }

    private static string JoinStatVector(IEnumerable<long> values)
        => string.Join(" ", values.Select(value => value.ToString(CultureInfo.InvariantCulture)));

    private static void AddStat4Row(
        EmbeddedTable statistics,
        string tableName,
        string indexName,
        string equal,
        string less,
        string distinctLess,
        byte[] sample)
    {
        var rowId = statistics.RowIds.Count == 0
            ? 1
            : NextAutoRowId(statistics.RowIds.Max(), new HashSet<long>(statistics.RowIds));
        statistics.Rows.Add(
        [
            SqlValue.Text(tableName),
            SqlValue.Text(indexName),
            SqlValue.Text(equal),
            SqlValue.Text(less),
            SqlValue.Text(distinctLess),
            SqlValue.Blob(sample),
        ]);
        statistics.RowIds.Add(rowId);
    }

    private ManagedAndIndexIntersectionPlan? TryPlanManagedAndIndexIntersection(
        SelectStatement statement,
        QueryContext context)
    {
        if (statement.Source is not NamedTableSource { IndexDirective: null } source
            || statement.Where is null
            || statement.OrderBy.Count != 0
            || statement.GroupBy.Count != 0
            || statement.Having is not null
            || statement.Distinct
            || statement.Limit is not null
            || statement.Offset is not null
            || IsAggregateSelect(statement)
            || context.ConcurrentMvStore is not null
            || context.ConcurrentMvccTxId is not null
            || IsSchemaTable(source.Name)
            || context.CommonTableExpressions.ContainsKey(source.Name)
            || context.Views?.ContainsKey(source.Name) == true
            || !context.Tables.TryGetValue(source.Name, out var table))
        {
            return null;
        }

        var conjuncts = new List<Expression>();
        CollectTopLevelAndLeaves(statement.Where, conjuncts);
        if (conjuncts.Count < 2)
            return null;

        // A contiguous composite equality prefix is always preferable to building rowid sets.
        if (table.Indexes.Any(index =>
                !index.IsMethodIndex
                && CountLeadingEqualityIndexTerms(statement.Where, table, index) >= 2))
        {
            return null;
        }

        var branches = new List<ManagedAndIndexBranch>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct is not BinaryExpression { Operator: BinaryOperator.Equal }
                || !TryFindEqualityIndexForBranch(table, source, conjunct, out var index)
                || !TryCreateTransientEqualityLookup(
                    source,
                    table,
                    conjunct,
                    context,
                    outerRow: null,
                    out var lookup)
                || index.Columns[0].ColumnIndex != lookup.ColumnOrdinal
                || branches.Any(branch =>
                    string.Equals(branch.Index.Name, index.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            branches.Add(new ManagedAndIndexBranch(index, conjunct));
        }

        if (branches.Count < 2)
            return null;

        var baseRows = Math.Max(1.0, table.Rows.Count);
        var statsAreCurrent = TryGetSqliteStat1TableRowCount(context, table.Name, out var statRows)
            && statRows == table.Rows.Count;
        var estimates = new double[branches.Count];
        var intersectionRows = baseRows;
        var multiIndexCost = 0.0;
        for (var index = 0; index < branches.Count; index++)
        {
            var branch = branches[index];
            double estimate;
            if (statsAreCurrent
                && TryEstimateStat4EqualityRows(
                    context,
                    table,
                    branch.Index,
                    branch.Predicate,
                    out var stat4Rows))
            {
                estimate = stat4Rows;
            }
            else if (statsAreCurrent
                     && TryGetSqliteStat1LeadingAverage(
                         context,
                         table.Name,
                         branch.Index.Name,
                         out var leadingAverage))
            {
                estimate = leadingAverage;
            }
            else
            {
                estimate = baseRows * JoinCostParams.SelectivityEqualityUnindexed;
            }

            estimate = Math.Clamp(estimate, 1.0, baseRows);
            estimates[index] = estimate;
            intersectionRows *= estimate / baseRows;
            multiIndexCost += Math.Log2(Math.Max(baseRows, 2.0))
                + estimate
                + estimate * 0.05;
        }

        intersectionRows = Math.Clamp(intersectionRows, 1.0, estimates.Min());
        multiIndexCost += intersectionRows * 4.0;
        var fullScanCost = baseRows;
        var bestSingleIndexCost = estimates.Min(estimate =>
            Math.Log2(Math.Max(baseRows, 2.0)) + estimate + estimate * 4.0);
        if (multiIndexCost >= Math.Min(fullScanCost, bestSingleIndexCost))
            return null;

        return new ManagedAndIndexIntersectionPlan(source, table, branches, intersectionRows);
    }

    private static void CollectTopLevelAndLeaves(Expression expression, List<Expression> leaves)
    {
        if (expression is BinaryExpression { Operator: BinaryOperator.And } and)
        {
            CollectTopLevelAndLeaves(and.Left, leaves);
            CollectTopLevelAndLeaves(and.Right, leaves);
            return;
        }

        leaves.Add(expression);
    }

    private bool TryCompileManagedAndIndexIntersectionSelect(
        SelectStatement select,
        ManagedAndIndexIntersectionPlan plan,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        bool materializeRows,
        out CompiledSelect compiled)
    {
        compiled = null!;
        if (select.Projections.Count == 0
            || select.Distinct
            || select.OrderBy.Count != 0
            || select.GroupBy.Count != 0
            || select.Having is not null
            || select.Limit is not null
            || select.Offset is not null
            || IsAggregateSelect(select)
            || CollectSelectWindowFunctions(select).Count != 0)
        {
            return false;
        }

        IReadOnlyList<SqlValue[]> rows;
        IReadOnlyList<long>? rowIds;
        if (materializeRows)
        {
            var intersected = GetManagedAndIndexIntersectionRows(
                plan,
                parameters,
                context,
                outerRow);
            rows = intersected.Rows.Select(row => row.Values).ToArray();
            rowIds = plan.Table.HasRowid
                ? intersected.Rows.Select(row =>
                    row.RowId ?? throw new InvalidOperationException(
                        "An intersected row is missing its rowid.")).ToArray()
                : null;
        }
        else
        {
            rows = plan.Table.Rows;
            rowIds = plan.Table.HasRowid ? plan.Table.RowIds : null;
        }

        var qualifier = plan.Source.Alias ?? plan.Source.Name;
        var qualifiedColumns = BuildQualifiedColumns(qualifier, plan.Table.Columns);
        var target = new ScanTarget(
            plan.Source.Name,
            qualifier,
            plan.Table.Columns,
            rows,
            name => ResolveScanColumnIndex(name, plan.Table.Columns, qualifiedColumns),
            rowIds,
            string.Join("&", plan.Branches.Select(branch => branch.Index.Name)),
            plan.Table.ColumnDefinitions,
            BuildQualifiedColumnDefinitions(qualifier, plan.Table.ColumnDefinitions),
            AccessKind: ScanAccessKind.MultiIndexAnd);
        var compiler = new SelectStatementCompiler(
            IsConstantScalarExpression,
            expression => Evaluate(expression, parameters, null, context),
            _ => target,
            (where, scan) => CompileRowPredicate(where, scan, parameters, context, outerRow),
            (where, scan) => CanEmitNativeScanPredicate(where, scan, context),
            (where, scan) => CompileSimpleRowIdPredicate(where, scan, parameters, context, outerRow),
            (statement, scan) => CompileDistinctScanEquality(statement, scan, context),
            function => TryGetRoutableBuiltinScalarCall(function, out var routable)
                ? BuildBuiltinScalarFunction(routable, parameters, context)
                : null,
            ArithmeticNumericAffinity,
            ModuloNumericAffinity,
            BitwiseIntegerAffinity);
        return compiler.TryCompile(select, out compiled);
    }

    private SourceData GetManagedAndIndexIntersectionRows(
        ManagedAndIndexIntersectionPlan plan,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow)
    {
        List<SourceRow>? firstRows = null;
        HashSet<MvccKey>? intersection = null;
        EmbeddedFileReadSnapshot? readSnapshot = null;
        if (!context.InTransaction
            && context.ConcurrentMvStore is null
            && context.ConcurrentMvccTxId is null)
        {
            _ = _fileStore?.TryOpenReadSnapshot(plan.Table, out readSnapshot);
        }

        using (readSnapshot)
        {
            foreach (var branch in plan.Branches)
            {
                var candidates = GetManagedAndIndexBranchRows(
                    plan,
                    branch,
                    parameters,
                    context,
                    outerRow,
                    readSnapshot);
                _plannerAccessPathMetrics.IntersectionProbe(candidates.Count);
                var identities = candidates
                    .Select(row => GetManagedIntersectionIdentity(plan.Table, row, context))
                    .ToHashSet();
                if (intersection is null)
                {
                    intersection = identities;
                    firstRows = candidates.ToList();
                }
                else
                {
                    intersection.IntersectWith(identities);
                }

                if (intersection.Count == 0)
                    break;
            }
        }

        _plannerAccessPathMetrics.IntersectionExecuted();
        if (intersection is null || firstRows is null || intersection.Count == 0)
            return new SourceData(plan.Table.Columns, []);

        return new SourceData(
            plan.Table.Columns,
            firstRows
                .Where(row => intersection.Contains(GetManagedIntersectionIdentity(plan.Table, row, context)))
                .ToArray());
    }

    private IReadOnlyList<SourceRow> GetManagedAndIndexBranchRows(
        ManagedAndIndexIntersectionPlan plan,
        ManagedAndIndexBranch branch,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        EmbeddedFileReadSnapshot? readSnapshot)
    {
        if (TryCreateTransientEqualityLookup(
                plan.Source,
                plan.Table,
                branch.Predicate,
                context,
                outerRow,
                out var lookup))
        {
            var value = Evaluate(lookup.ValueExpression, parameters, outerRow, context);
            if (value.Kind == SqlValueKind.Null)
                return [];
            if (lookup.ValueConvertsTextToNumeric)
                value = ApplyComparisonNumericAffinity(value);
            else if (lookup.ValueConvertsNumericToText
                     && value.Kind is SqlValueKind.Integer or SqlValueKind.Real)
                value = SqlValue.Text(ToSqlText(value));

            if (readSnapshot is not null
                && branch.Index.Columns.Count > 0
                && branch.Index.Columns[0].ColumnIndex == lookup.ColumnOrdinal
                && _fileStore?.TryOpenIndexAccessor(
                    plan.Table,
                    branch.Index,
                    prefixLength: 1,
                    covering: false,
                    readSnapshot,
                    out var accessor) == true)
            {
                var qualifier = plan.Source.Alias ?? plan.Source.Name;
                var qualifiedColumns = BuildQualifiedColumns(qualifier, plan.Table.Columns);
                var qualifiedDefinitions = BuildQualifiedColumnDefinitions(
                    qualifier,
                    plan.Table.ColumnDefinitions);
                using (accessor)
                {
                    return accessor.Seek(
                            [value],
                            _plannerAccessPathMetrics.IntersectionIndexPageRead,
                            _plannerAccessPathMetrics.IntersectionKeyCompared)
                        .Select(row => new SourceRow(
                            plan.Table.Columns,
                            row.Values,
                            qualifiedColumns,
                            outerRow,
                            RowId: row.RowId,
                            RowIdQualifier: qualifier,
                            ColumnDefinitions: plan.Table.ColumnDefinitions,
                            QualifiedColumnDefinitions: qualifiedDefinitions))
                        .ToArray();
                }
            }
        }

        return TryGetTransientLookupRows(
                plan.Source,
                branch.Predicate,
                parameters,
                context,
                outerRow)?.Rows
            ?? GetNamedTableRows(
                plan.Source,
                context,
                maximumRows: null,
                outerRow).Rows;
    }

    private static MvccKey GetManagedIntersectionIdentity(
        EmbeddedTable table,
        SourceRow row,
        QueryContext context)
        => table.HasRowid
            ? MvccKey.FromInteger(
                row.RowId ?? throw new InvalidOperationException(
                    "A rowid-table intersection candidate is missing its rowid."))
            : MvccKey.FromPrimaryKey(
                table.PrimaryKeySchema
                    ?? throw new InvalidOperationException(
                        "A WITHOUT ROWID table is missing primary-key metadata."),
                row.Values,
                context.MvccTextEncoding);

    private static string FormatManagedAndIndexIntersectionExplainDetail(
        ManagedAndIndexIntersectionPlan plan)
        => $"MULTI-INDEX AND {plan.Source.Alias ?? plan.Source.Name} "
            + $"({string.Join(", ", plan.Branches.Select(branch => branch.Index.Name))})";

    private bool TryEstimateStat4EqualityRows(
        QueryContext context,
        EmbeddedTable table,
        EmbeddedIndex index,
        Expression predicate,
        out double rows)
    {
        rows = 0;
        if (index.Columns.Count == 0
            || index.IsPartial
            || index.IsMethodIndex
            || index.Columns.Any(static column => column.IsExpression)
            || !IsBuiltInCollation(IndexExpressionSemantics.GetCollationName(table, index.Columns[0]))
            || IsOverriddenBuiltInCollation(
                IndexExpressionSemantics.GetCollationName(table, index.Columns[0]))
            || !TryFindIndexEqualityBound(
                predicate,
                table,
                index.Columns[0],
                out var bound)
            || bound is not LiteralExpression literal
            || literal.Value.Kind == SqlValueKind.Null
            || !context.Tables.TryGetValue(SqliteStat4TableName, out var statistics)
            || !TryGetSqliteStat1TableRowCount(context, table.Name, out var statRows)
            || statRows != table.Rows.Count)
        {
            return false;
        }

        try
        {
            ValidateSqliteStat4Table(statistics);
        }
        catch (EmbeddedSqlException)
        {
            return false;
        }

        var value = table.CoerceColumnAffinity(
            table.ColumnDefinitions[index.Columns[0].ColumnIndex],
            literal.Value);
        var collation = IndexExpressionSemantics.GetCollationName(table, index.Columns[0]);
        var exactEstimates = new List<long>();
        var neighboringEstimates = new List<(int Comparison, long Rows)>();
        var matchingRows = 0;
        foreach (var row in statistics.Rows)
        {
            if (row.Length != 6
                || row[0].Kind != SqlValueKind.Text
                || row[1].Kind != SqlValueKind.Text
                || !string.Equals(row[0].AsText(), table.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(row[1].AsText(), index.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchingRows++;
            if (row[2].Kind != SqlValueKind.Text
                || row[3].Kind != SqlValueKind.Text
                || row[4].Kind != SqlValueKind.Text
                || row[5].Kind != SqlValueKind.Blob
                || !TryParseStat4Vector(row[2].AsText(), index.Columns.Count, out var equal)
                || !TryParseStat4Vector(row[3].AsText(), index.Columns.Count, out var less)
                || !TryParseStat4Vector(row[4].AsText(), index.Columns.Count, out var distinctLess))
            {
                return false;
            }

            SqlValue[] sample;
            try
            {
                sample = SqliteRecordCodec.Decode(row[5].AsBlob().Span, GetTextEncoding());
            }
            catch (InvalidDataException)
            {
                return false;
            }

            if (sample.Length < index.Columns.Count + 1
                || sample[^1].Kind != SqlValueKind.Integer
                || equal[0] <= 0
                || equal[0] > table.Rows.Count
                || less[0] < 0
                || less[0] > table.Rows.Count
                || distinctLess[0] < 0
                || distinctLess[0] > less[0])
            {
                return false;
            }

            var sampleRowId = sample[^1].AsInteger();
            var sampleRowPosition = table.RowIds.IndexOf(sampleRowId);
            if (sampleRowPosition < 0)
                return false;
            var currentKey = IndexExpressionSemantics.ProjectKey(
                index,
                table,
                table.Rows[sampleRowPosition],
                sampleRowId,
                EvaluateIndexExpression);
            for (var position = 0; position < index.Columns.Count; position++)
            {
                if (Compare(
                        sample[position],
                        currentKey[position],
                        IndexExpressionSemantics.GetCollationName(table, index.Columns[position])) != 0)
                {
                    return false;
                }
            }

            var comparison = Compare(sample[0], value, collation);
            if (comparison == 0)
                exactEstimates.Add(equal[0]);
            else
                neighboringEstimates.Add((comparison, equal[0]));
        }

        if (matchingRows == 0)
            return false;
        if (exactEstimates.Count > 0)
        {
            exactEstimates.Sort();
            rows = exactEstimates[exactEstimates.Count / 2];
        }
        else
        {
            var lower = neighboringEstimates.LastOrDefault(entry => entry.Comparison < 0);
            var upper = neighboringEstimates.FirstOrDefault(entry => entry.Comparison > 0);
            if (lower.Rows == 0 && upper.Rows == 0)
                return false;
            rows = lower.Rows == 0
                ? upper.Rows
                : upper.Rows == 0
                    ? lower.Rows
                    : Math.Max(1.0, (lower.Rows + upper.Rows) / 2.0);
        }

        _plannerAccessPathMetrics.Stat4EstimateUsed(rows);
        return true;
    }

    private static bool TryParseStat4Vector(
        string text,
        int minimumCount,
        out long[] values)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        values = new long[parts.Length];
        if (parts.Length < minimumCount)
            return false;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!long.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out values[index]))
            {
                values = [];
                return false;
            }
        }

        return true;
    }
}

internal sealed class PlannerAccessPathMetrics
{
    private long _intersectionsExecuted;
    private long _intersectionIndexProbes;
    private long _intersectionCandidateRows;
    private long _intersectionIndexPagesRead;
    private long _intersectionKeyComparisons;
    private long _stat4EstimatesUsed;
    private double _lastStat4EstimatedRows;

    public long IntersectionsExecuted => Interlocked.Read(ref _intersectionsExecuted);

    public long IntersectionIndexProbes => Interlocked.Read(ref _intersectionIndexProbes);

    public long IntersectionCandidateRows => Interlocked.Read(ref _intersectionCandidateRows);

    public long IntersectionIndexPagesRead => Interlocked.Read(ref _intersectionIndexPagesRead);

    public long IntersectionKeyComparisons => Interlocked.Read(ref _intersectionKeyComparisons);

    public long Stat4EstimatesUsed => Interlocked.Read(ref _stat4EstimatesUsed);

    public double LastStat4EstimatedRows => Volatile.Read(ref _lastStat4EstimatedRows);

    internal void IntersectionExecuted() => Interlocked.Increment(ref _intersectionsExecuted);

    internal void IntersectionProbe(int candidateRows)
    {
        Interlocked.Increment(ref _intersectionIndexProbes);
        Interlocked.Add(ref _intersectionCandidateRows, candidateRows);
    }

    internal void IntersectionIndexPageRead() => Interlocked.Increment(ref _intersectionIndexPagesRead);

    internal void IntersectionKeyCompared() => Interlocked.Increment(ref _intersectionKeyComparisons);

    internal void Stat4EstimateUsed(double estimatedRows)
    {
        Volatile.Write(ref _lastStat4EstimatedRows, estimatedRows);
        Interlocked.Increment(ref _stat4EstimatesUsed);
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref _intersectionsExecuted, 0);
        Interlocked.Exchange(ref _intersectionIndexProbes, 0);
        Interlocked.Exchange(ref _intersectionCandidateRows, 0);
        Interlocked.Exchange(ref _intersectionIndexPagesRead, 0);
        Interlocked.Exchange(ref _intersectionKeyComparisons, 0);
        Interlocked.Exchange(ref _stat4EstimatesUsed, 0);
        Volatile.Write(ref _lastStat4EstimatedRows, 0);
    }
}
