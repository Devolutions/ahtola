using Ahtola.Core.Storage;

namespace Ahtola.Core;

/// <summary>
/// Builds an <see cref="AsyncSchemaCatalog"/> by walking <c>sqlite_schema</c>'s own b-tree
/// (always rooted at page 1) through <see cref="IAsyncSqliteBtreePageIo"/>.
/// </summary>
/// <remarks>
/// This mirrors exactly what <c>EmbeddedFileStore.Load()</c>'s own schema reconstruction does —
/// same page-format parsing (<see cref="SqliteBtreePageHeader"/>,
/// <see cref="SqliteTableLeafPageView"/>, <see cref="SqliteTableInteriorPageView"/>), same
/// <see cref="ManagedSchemaRowParser.ParseTable"/> call to turn stored SQL text into an
/// <see cref="EmbeddedTable"/> — so the two can never disagree about what a schema row means.
/// Only the page-fetch primitive is asynchronous here.
/// </remarks>
internal static class AsyncSchemaCatalogLoader
{
    private const uint SchemaRootPage = 1;

    /// <summary>Walks the schema b-tree and reconstructs every recognizable base table.</summary>
    public static async ValueTask<AsyncSchemaCatalog> LoadAsync(
        IAsyncSqliteBtreePageIo pageIo,
        SqliteTextEncoding textEncoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageIo);

        var rows = new List<ManagedSchemaRow>();
        await WalkAsync(pageIo, SchemaRootPage, isFirstPage: true, rows, textEncoding, cancellationToken)
            .ConfigureAwait(false);

        var indexedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.IsIndex)
                indexedTableNames.Add(row.TableName);
        }

        var entries = new Dictionary<string, AsyncSchemaCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // Views, triggers, and virtual tables (rootpage 0) are out of scope for a bounded
            // rowid-table scan; they are simply absent from the catalog, so any query naming one
            // fails classification with an ordinary "unknown table" rather than a special case.
            if (!row.IsTable || row.RootPage == 0)
                continue;

            EmbeddedTable table;
            try
            {
                table = ManagedSchemaRowParser.ParseTable(row);
            }
            catch (EmbeddedSqlException)
            {
                // A table shape this schema reader cannot understand is excluded from the
                // catalog rather than failing the whole load -- the same safe, conservative
                // "not found" fallback as an out-of-scope schema object above.
                continue;
            }

            entries[row.Name] = new AsyncSchemaCatalogEntry(
                table,
                row.RootPage,
                indexedTableNames.Contains(row.Name));
        }

        return new AsyncSchemaCatalog(entries);
    }

    private static async ValueTask WalkAsync(
        IAsyncSqliteBtreePageIo pageIo,
        uint pageNumber,
        bool isFirstPage,
        List<ManagedSchemaRow> rows,
        SqliteTextEncoding textEncoding,
        CancellationToken cancellationToken)
    {
        var image = await pageIo.ReadPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        var header = SqliteBtreePageHeader.Parse(image, isFirstPage, pageIo.UsableSpace);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    var leaf = SqliteTableLeafPageView.Parse(image, pageIo.UsableSpace, isFirstPage);
                    var overflowReader = new AsyncSqliteOverflowChainReader(pageIo);
                    foreach (var pageCell in leaf.Cells)
                    {
                        var record = await overflowReader
                            .ReadPayloadAsync(pageCell.Cell, cancellationToken)
                            .ConfigureAwait(false);
                        var values = SqliteRecordCodec.Decode(record, textEncoding);
                        rows.Add(ToSchemaRow(pageCell.Cell.RowId, values));
                    }

                    break;
                }

            case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(image, pageIo.UsableSpace, isFirstPage);
                    foreach (var cell in interior.Cells)
                    {
                        await WalkAsync(
                            pageIo,
                            cell.Cell.LeftChildPage,
                            isFirstPage: false,
                            rows,
                            textEncoding,
                            cancellationToken).ConfigureAwait(false);
                    }

                    await WalkAsync(
                        pageIo,
                        interior.Header.RightMostChildPage,
                        isFirstPage: false,
                        rows,
                        textEncoding,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

            default:
                throw new InvalidDataException(
                    $"SQLite page {pageNumber} is not part of the sqlite_schema b-tree.");
        }
    }

    private static ManagedSchemaRow ToSchemaRow(long rowId, SqlValue[] values)
    {
        if (values.Length != 5)
            throw new InvalidDataException("A sqlite_schema row must have exactly 5 columns.");

        var type = values[0].AsText();
        var name = values[1].AsText();
        var tableName = values[2].AsText();
        var rootPage = values[3].Kind == SqlValueKind.Null ? 0u : checked((uint)values[3].AsInteger());
        var sql = values[4].Kind == SqlValueKind.Null ? null : values[4].AsText();
        return new ManagedSchemaRow(rowId, type, name, tableName, rootPage, sql);
    }
}
