using Ahtola.Core;

namespace Ahtola.Core.Parsing;

/// <summary>
/// A classified, fully resolved plan for a bounded ascending rowid-table scan: the exact shape
/// <see cref="BoundedRowidScanShapeClassifier"/> currently accepts.
/// </summary>
internal sealed record BoundedRowidScanPlan(
    string TableName,
    uint RootPage,
    EmbeddedTable Table,
    IReadOnlyList<int> ProjectedColumnIndexes,
    IReadOnlyList<string> ColumnNames,
    long? Limit);

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
/// v1 supported shape: <c>SELECT column-list|* FROM one-ordinary-rowid-base-table [LIMIT n]</c>.
/// No <c>WHERE</c>, no joins/subqueries, no <c>ORDER BY</c>/<c>GROUP BY</c>/<c>HAVING</c>, no
/// aggregates, no <c>DISTINCT</c>, no expressions beyond plain column references, no
/// <c>WITHOUT ROWID</c> or indexed tables, no <c>OFFSET</c>. Everything else is named follow-on
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

        if (select.Where is not null)
        {
            rejectionReason = "WHERE clauses are not yet supported by a bounded scan connection.";
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

        if (select.OrderBy.Count != 0)
        {
            rejectionReason = "ORDER BY is not supported by a bounded scan connection (scans are always in natural rowid-ascending order).";
            return null;
        }

        if (select.Offset is not null)
        {
            rejectionReason = "OFFSET is not supported by a bounded scan connection.";
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

        if (!entry.Table.HasRowid)
        {
            rejectionReason =
                $"WITHOUT ROWID table '{tableSource.Name}' is not yet supported by a bounded scan connection.";
            return null;
        }

        if (entry.HasAnyIndex)
        {
            rejectionReason =
                $"Table '{tableSource.Name}' has a registered index; indexed tables are not yet supported by a bounded scan connection.";
            return null;
        }

        if (Array.Exists(entry.Table.ColumnDefinitions, static column => column.IsGenerated))
        {
            rejectionReason =
                $"Table '{tableSource.Name}' has a generated column, which is not yet supported by a bounded scan connection.";
            return null;
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

        rejectionReason = "";
        return new BoundedRowidScanPlan(
            tableSource.Name,
            entry.RootPage,
            entry.Table,
            columnIndexes,
            columnNames,
            limit);
    }
}
