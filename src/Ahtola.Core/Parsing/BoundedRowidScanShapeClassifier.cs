using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Parsing;

/// <summary>
/// A classified, fully resolved plan for a bounded base-table scan or exact rowid lookup: the shape
/// <see cref="BoundedRowidScanShapeClassifier"/> currently accepts.
/// </summary>
internal sealed record BoundedRowidScanPlan(
    string TableName,
    uint RootPage,
    EmbeddedTable Table,
    IReadOnlyList<int> ProjectedColumnIndexes,
    IReadOnlyList<string> ColumnNames,
    long? Limit,
    long Offset,
    long? EqualRowId,
    SqliteRowIdRange? RowIdRange,
    bool Descending,
    bool WithoutRowid,
    SqlValue? FirstPrimaryKeyEquals,
    IReadOnlyList<SqlValue>? FullPrimaryKeyEquals);

/// <summary>
/// Classifies a parsed SQL statement against an <see cref="AsyncSchemaCatalog"/>, either
/// returning a <see cref="BoundedRowidScanPlan"/> for the narrow v1 supported shape, or
/// <see langword="null"/> with a human-readable rejection reason naming the exact unsupported
/// construct.
/// </summary>
/// <remarks>
/// This never reads a page: classification is pure parsing (the existing SQL parser) plus
/// lookups against an already-resolved, in-memory schema catalog. A bounded scan connection
/// throws before any I/O happens for anything this classifier rejects — see
/// <c>AhtolaBrowserBoundedConnection.ExecuteBoundedScanAsync</c>.
/// <para>
/// Supported shape: <c>SELECT column-list|* FROM one-ordinary-rowid-base-table
/// [WHERE integer-primary-key integer-comparison integer-literal
/// [AND integer-primary-key integer-comparison integer-literal ...]]
/// (or an inclusive integer-literal BETWEEN range)
/// [ORDER BY integer-primary-key [ASC|DESC]] [LIMIT n [OFFSET m]]</c>.
/// No other <c>WHERE</c>, joins/subqueries, <c>ORDER BY</c>/<c>GROUP BY</c>/<c>HAVING</c>, no
/// aggregates, no <c>DISTINCT</c>, no expressions beyond plain column references.
/// <c>WITHOUT ROWID</c> tables additionally support ascending BINARY primary keys and
/// first-key or complete INTEGER/TEXT-key equality. A registered secondary index does not change a base-table
/// rowid scan or seek; other indexed access paths remain out of scope. Everything else is named follow-on
/// work, not silently downgraded.
/// </para>
/// </remarks>
internal static class BoundedRowidScanShapeClassifier
{
    public static BoundedRowidScanPlan? TryClassify(
        string sql,
        AsyncSchemaCatalog catalog,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(catalog);

        ParsedStatement statement;
        try
        {
            statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        }
        catch (Exception exception) when (exception is EmbeddedSqlException or InvalidOperationException or FormatException)
        {
            rejectionReason = $"The statement could not be parsed: {exception.Message}";
            return null;
        }

        if (statement is not SelectStatement select)
        {
            rejectionReason = "Only a single, plain SELECT statement is supported by a bounded scan connection.";
            return null;
        }

        if (select.Distinct)
        {
            rejectionReason = "DISTINCT is not supported by a bounded scan connection.";
            return null;
        }

        if (select.GroupBy.Count != 0 || select.Having is not null)
        {
            rejectionReason = "GROUP BY/HAVING are not supported by a bounded scan connection.";
            return null;
        }

        if (select.NamedWindows.Count != 0)
        {
            rejectionReason = "Window definitions are not supported by a bounded scan connection.";
            return null;
        }

        if (select.Source is null)
        {
            rejectionReason = "A bounded scan connection requires exactly one FROM table.";
            return null;
        }

        if (select.Source is not NamedTableSource tableSource)
        {
            rejectionReason = "FROM must name exactly one base table (no joins, subqueries, or table-valued functions).";
            return null;
        }

        if (tableSource.IndexDirective is not null)
        {
            rejectionReason = "INDEXED BY/NOT INDEXED is not supported by a bounded scan connection.";
            return null;
        }

        if (!catalog.TryGetTable(tableSource.Name, out var entry))
        {
            rejectionReason =
                $"Table '{tableSource.Name}' is unknown, or its shape is not supported by a bounded scan connection.";
            return null;
        }

        var withoutRowid = !entry.Table.HasRowid;
        if (withoutRowid && entry.Table.PrimaryKeySchema is not { Terms.Count: > 0 })
        {
            rejectionReason =
                $"WITHOUT ROWID table '{tableSource.Name}' has no usable primary-key schema.";
            return null;
        }

        if (withoutRowid && entry.Table.PrimaryKeySchema!.Terms.Any(static term =>
                term.SortOrder != SqliteKeySortOrder.Ascending || !term.Collation.IsBinary
                || term.Collation.Comparison is not null))
        {
            rejectionReason = "WITHOUT ROWID scans require an ascending BINARY primary key.";
            return null;
        }

        if (Array.Exists(entry.Table.ColumnDefinitions, static column => column.IsGenerated))
        {
            rejectionReason =
                $"Table '{tableSource.Name}' has a generated column, which is not yet supported by a bounded scan connection.";
            return null;
        }

        if (withoutRowid && select.OrderBy.Count != 0
            && !IsWithoutRowidPrimaryKeyOrder(select.OrderBy, tableSource, entry.Table))
        {
            rejectionReason = "ORDER BY on a bounded WITHOUT ROWID scan requires an ascending primary-key prefix.";
            return null;
        }

        if (!withoutRowid && select.OrderBy.Count != 0
            && (entry.Table.RowidAliasColumnIndex < 0
                || select.OrderBy is not [var order]
                || !IsRowIdColumn(order.Expression, tableSource, entry.Table)))
        {
            rejectionReason = "ORDER BY supports only the INTEGER PRIMARY KEY column.";
            return null;
        }

        long? equalRowId = null;
        SqliteRowIdRange? rowIdRange = null;
        SqlValue? firstPrimaryKeyEquals = null;
        IReadOnlyList<SqlValue>? fullPrimaryKeyEquals = null;
        if (select.Where is { } predicate)
        {
            if (withoutRowid)
            {
                if (!TryParseWithoutRowidKeyEquality(
                        predicate, tableSource, entry.Table, out var keys))
                {
                    rejectionReason =
                        "WHERE on a bounded WITHOUT ROWID scan requires equality on the first INTEGER/TEXT primary-key column or every INTEGER/TEXT primary-key column.";
                    return null;
                }

                firstPrimaryKeyEquals = keys[0];
                if (keys.Length == entry.Table.PrimaryKeySchema!.Terms.Count)
                    fullPrimaryKeyEquals = keys;
            }
            else if (entry.Table.RowidAliasColumnIndex < 0
                || !TryParseRowIdRange(predicate, tableSource, entry.Table, out var bounds))
            {
                rejectionReason =
                    "WHERE supports only AND-combined comparisons between an INTEGER PRIMARY KEY column and integer literals.";
                return null;
            }
            else if (bounds.Lower is { } exact
                     && bounds.Upper == exact
                     && bounds.IncludeLower
                     && bounds.IncludeUpper)
            {
                equalRowId = exact;
            }
            else if (!withoutRowid)
            {
                rowIdRange = bounds;
            }
        }

        var columnIndexes = new List<int>();
        var columnNames = new List<string>();
        foreach (var projection in select.Projections)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    for (var index = 0; index < entry.Table.Columns.Length; index++)
                    {
                        columnIndexes.Add(index);
                        columnNames.Add(entry.Table.Columns[index]);
                    }

                    break;

                case ColumnExpression { Qualifier: null } column:
                    var columnIndex = Array.FindIndex(
                        entry.Table.Columns,
                        candidate => string.Equals(candidate, column.Name, StringComparison.OrdinalIgnoreCase));
                    if (columnIndex < 0)
                    {
                        rejectionReason =
                            $"Column '{column.Name}' does not exist on table '{tableSource.Name}'.";
                        return null;
                    }

                    columnIndexes.Add(columnIndex);
                    columnNames.Add(projection.Alias ?? column.Name);
                    break;

                default:
                    rejectionReason =
                        "Only plain column references or * are supported in the projection list of a bounded scan connection.";
                    return null;
            }
        }

        long? limit = null;
        if (select.Limit is not null)
        {
            if (select.Limit is not LiteralExpression { Value.Kind: SqlValueKind.Integer } literal)
            {
                rejectionReason = "LIMIT must be a plain non-negative integer literal.";
                return null;
            }

            var rawLimit = literal.Value.AsInteger();
            if (rawLimit < 0)
            {
                rejectionReason = "LIMIT must be non-negative.";
                return null;
            }

            limit = rawLimit;
        }

        var offset = 0L;
        if (select.Offset is not null)
        {
            if (limit is null || select.Offset is not LiteralExpression { Value.Kind: SqlValueKind.Integer } literal)
            {
                rejectionReason = "OFFSET requires LIMIT and a plain non-negative integer literal.";
                return null;
            }

            offset = literal.Value.AsInteger();
            if (offset < 0)
            {
                rejectionReason = "OFFSET must be non-negative.";
                return null;
            }
        }

        rejectionReason = "";
        return new BoundedRowidScanPlan(
            tableSource.Name,
            entry.RootPage,
            entry.Table,
            columnIndexes,
            columnNames,
            limit,
            offset,
            equalRowId,
            rowIdRange,
            select.OrderBy.Count != 0 && select.OrderBy[0].Descending,
            withoutRowid,
            firstPrimaryKeyEquals,
            fullPrimaryKeyEquals);
    }

    private static bool TryParseWithoutRowidKeyEquality(
        Expression predicate,
        NamedTableSource source,
        EmbeddedTable table,
        out SqlValue[] keys)
    {
        var terms = table.PrimaryKeySchema!.Terms;
        var values = new SqlValue?[terms.Count];
        var matched = 0;

        bool Visit(Expression expression)
        {
            if (expression is BinaryExpression { Operator: BinaryOperator.And } conjunction)
                return Visit(conjunction.Left) && Visit(conjunction.Right);
            if (expression is not BinaryExpression { Operator: BinaryOperator.Equal } equality)
                return false;

            for (var index = 0; index < terms.Count; index++)
            {
                var term = terms[index];
                if (values[index].HasValue)
                    continue;
                var declaredType = table.ColumnDefinitions[term.ColumnIndex].DeclaredType;
                if (!(TryMatchPrimaryKey(equality.Left, equality.Right, source, term, declaredType, out var value)
                    || TryMatchPrimaryKey(equality.Right, equality.Left, source, term, declaredType, out value)))
                    continue;
                values[index] = value;
                matched++;
                return true;
            }
            return false;
        }

        if (!Visit(predicate) || !values[0].HasValue || matched != 1 && matched != terms.Count)
        {
            keys = [];
            return false;
        }

        keys = matched == 1
            ? [values[0]!.Value]
            : values.Select(static value => value!.Value).ToArray();
        return true;
    }

    private static bool TryParseRowIdRange(
        Expression expression,
        NamedTableSource source,
        EmbeddedTable table,
        out SqliteRowIdRange range)
    {
        if (expression is BinaryExpression { Operator: BinaryOperator.And } conjunction)
        {
            if (TryParseRowIdRange(conjunction.Left, source, table, out var left)
                && TryParseRowIdRange(conjunction.Right, source, table, out var right))
            {
                range = Intersect(left, right);
                return true;
            }
        }
        else if (expression is BetweenExpression { Negated: false } between
            && TryMatchRowId(between.Value, between.Lower, source, table, out var lower)
            && TryMatchRowId(between.Value, between.Upper, source, table, out var upper))
        {
            range = new SqliteRowIdRange(lower, true, upper, true);
            return true;
        }
        else if (expression is BinaryExpression comparison
            && (TryMatchRowId(comparison.Left, comparison.Right, source, table, out var rowId)
                && TryCreateBound(comparison.Operator, rowId, out range)
                || TryMatchRowId(comparison.Right, comparison.Left, source, table, out rowId)
                && TryCreateBound(ReverseComparison(comparison.Operator), rowId, out range)))
        {
            return true;
        }

        range = default;
        return false;
    }

    private static bool TryCreateBound(BinaryOperator comparison, long rowId, out SqliteRowIdRange range)
    {
        range = comparison switch
        {
            BinaryOperator.Equal => new SqliteRowIdRange(rowId, true, rowId, true),
            BinaryOperator.GreaterThan => new SqliteRowIdRange(rowId, false, null, true),
            BinaryOperator.GreaterThanOrEqual => new SqliteRowIdRange(rowId, true, null, true),
            BinaryOperator.LessThan => new SqliteRowIdRange(null, true, rowId, false),
            BinaryOperator.LessThanOrEqual => new SqliteRowIdRange(null, true, rowId, true),
            _ => default,
        };
        return comparison is BinaryOperator.Equal
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual;
    }

    private static BinaryOperator ReverseComparison(BinaryOperator comparison)
        => comparison switch
        {
            BinaryOperator.GreaterThan => BinaryOperator.LessThan,
            BinaryOperator.GreaterThanOrEqual => BinaryOperator.LessThanOrEqual,
            BinaryOperator.LessThan => BinaryOperator.GreaterThan,
            BinaryOperator.LessThanOrEqual => BinaryOperator.GreaterThanOrEqual,
            _ => comparison,
        };

    private static SqliteRowIdRange Intersect(SqliteRowIdRange left, SqliteRowIdRange right)
    {
        var lower = left.Lower;
        var includeLower = left.IncludeLower;
        if (right.Lower is { } rightLower
            && (lower is null || rightLower > lower
                || rightLower == lower && !right.IncludeLower))
        {
            lower = rightLower;
            includeLower = right.IncludeLower;
        }

        var upper = left.Upper;
        var includeUpper = left.IncludeUpper;
        if (right.Upper is { } rightUpper
            && (upper is null || rightUpper < upper
                || rightUpper == upper && !right.IncludeUpper))
        {
            upper = rightUpper;
            includeUpper = right.IncludeUpper;
        }

        return new SqliteRowIdRange(lower, includeLower, upper, includeUpper);
    }

    private static bool TryMatchRowId(
        Expression columnExpression,
        Expression valueExpression,
        NamedTableSource source,
        EmbeddedTable table,
        out long rowId)
    {
        rowId = 0;
        if (!IsRowIdColumn(columnExpression, source, table))
            return false;
        return TryGetIntegerLiteral(valueExpression, out rowId);
    }

    private static bool TryMatchPrimaryKey(
        Expression columnExpression,
        Expression valueExpression,
        NamedTableSource source,
        SqlitePrimaryKeyTerm term,
        string? declaredType,
        out SqlValue key)
    {
        key = SqlValue.Null;
        if (!IsNamedColumn(columnExpression, source, term.ColumnName))
            return false;
        if (string.Equals(declaredType, "INTEGER", StringComparison.OrdinalIgnoreCase)
            && TryGetIntegerLiteral(valueExpression, out var integer))
        {
            key = SqlValue.Integer(integer);
            return true;
        }
        if (string.Equals(declaredType, "TEXT", StringComparison.OrdinalIgnoreCase)
            && valueExpression is LiteralExpression { Value.Kind: SqlValueKind.Text } literal)
        {
            key = literal.Value;
            return true;
        }
        return false;
    }

    private static bool TryGetIntegerLiteral(Expression expression, out long value)
    {
        switch (expression)
        {
            case LiteralExpression { Value.Kind: SqlValueKind.Integer } literal:
                value = literal.Value.AsInteger();
                return true;
            case UnaryExpression
            {
                Operator: UnaryOperator.Negate,
                Operand: LiteralExpression { Value.Kind: SqlValueKind.Integer } literal
            } when literal.Value.AsInteger() != long.MinValue:
                value = -literal.Value.AsInteger();
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool IsWithoutRowidPrimaryKeyOrder(
        IReadOnlyList<OrderByTerm> order,
        NamedTableSource source,
        EmbeddedTable table)
    {
        var terms = table.PrimaryKeySchema!.Terms;
        if (order.Count > terms.Count)
            return false;
        var descending = order[0].Descending;
        for (var index = 0; index < order.Count; index++)
        {
            if (order[index].Descending != descending
                || !IsNamedColumn(order[index].Expression, source, terms[index].ColumnName))
                return false;
        }
        return true;
    }

    private static bool IsRowIdColumn(Expression expression, NamedTableSource source, EmbeddedTable table)
        => IsNamedColumn(expression, source, table.Columns[table.RowidAliasColumnIndex]);

    private static bool IsNamedColumn(Expression expression, NamedTableSource source, string name)
        => expression is ColumnExpression { Schema: null } column
            && string.Equals(
                column.UnqualifiedName ?? column.Name,
                name,
                StringComparison.OrdinalIgnoreCase)
            && (column.Qualifier is null
                || string.Equals(
                    column.Qualifier,
                    source.Alias ?? source.Name,
                    StringComparison.OrdinalIgnoreCase));
}
