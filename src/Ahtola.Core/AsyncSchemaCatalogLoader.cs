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

    /// <summary>
    /// The same depth ceiling <see cref="Storage.AsyncBoundedRowidTableScanCursor"/> and the
    /// synchronous <see cref="Storage.SqliteTableBtreeCursor"/> already enforce. A self- or
    /// long-cyclic <c>sqlite_schema</c> b-tree is caught here because revisiting any page always
    /// requires one more descend step (this walk pushes a frame and moves to a child, never to
    /// an already-popped ancestor), so a cycle strictly increases stack depth every time it
    /// repeats rather than looping at constant depth — the same reasoning that makes depth
    /// capping alone sufficient for those two traversals, with no separate visited-page set
    /// needed.
    /// </summary>
    private const int MaximumDepth = 64;

    /// <summary>Walks the schema b-tree and reconstructs every recognizable base table.</summary>
    public static async ValueTask<AsyncSchemaCatalog> LoadAsync(
        IAsyncSqliteBtreePageIo pageIo,
        SqliteTextEncoding textEncoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageIo);

        var rows = new List<ManagedSchemaRow>();
        await WalkAsync(pageIo, SchemaRootPage, rows, textEncoding, cancellationToken)
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
        uint rootPage,
        List<ManagedSchemaRow> rows,
        SqliteTextEncoding textEncoding,
        CancellationToken cancellationToken)
    {
        var overflowReader = new AsyncSqliteOverflowChainReader(pageIo);
        // Each frame is one still-open ancestor interior page: its parsed view and the index of
        // the next child to descend into once the currently active subtree is exhausted. This
        // mirrors AsyncBoundedRowidTableScanCursor.ScanAscendingAsync's iterative traversal
        // exactly, replacing the previous unbounded recursive WalkAsync (self-call per child,
        // per rightmost child) that a corrupt/adversarial sqlite_schema b-tree containing a
        // self-referencing or long interior-page cycle could drive into unbounded recursion —
        // a StackOverflowException, which .NET cannot catch, ahead of ever reaching the
        // classifier or any caller's try/catch.
        var stack = new List<(SqliteTableInteriorPageView View, int NextChildIndex)>();
        var currentPage = rootPage;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Computed fresh from the actual page number, exactly like the synchronous
            // SqliteTableBtreeCursor.TrySeekLeaf does on every iteration -- never hardcoded false
            // for every page after the root. Only physical page 1 carries the 100-byte database
            // header prefix; a corrupt tree that routes back to page 1 as if it were an ordinary
            // child must still be parsed at the right offset, not misread as a bogus page type
            // from byte 0 of the SQLite header ('S' of "SQLite format 3").
            var isFirstPage = currentPage == 1;
            var image = await pageIo.ReadPageAsync(currentPage, cancellationToken).ConfigureAwait(false);
            var header = SqliteBtreePageHeader.Parse(image, isFirstPage, pageIo.UsableSpace);

            if (header.PageType == SqliteBtreePageType.TableInterior)
            {
                if (stack.Count >= MaximumDepth)
                {
                    throw new InvalidDataException(
                        $"SQLite sqlite_schema b-tree rooted at page {rootPage} is deeper than "
                        + $"{MaximumDepth} levels, or contains a cycle.");
                }

                var interior = SqliteTableInteriorPageView.Parse(image, pageIo.UsableSpace, isFirstPage);
                stack.Add((interior, 0));
                currentPage = ChildAt(interior, 0);
                continue;
            }

            if (header.PageType != SqliteBtreePageType.TableLeaf)
            {
                throw new InvalidDataException(
                    $"SQLite page {currentPage} is not part of the sqlite_schema b-tree.");
            }

            var leaf = SqliteTableLeafPageView.Parse(image, pageIo.UsableSpace, isFirstPage);
            foreach (var pageCell in leaf.Cells)
            {
                var record = await overflowReader
                    .ReadPayloadAsync(pageCell.Cell, cancellationToken)
                    .ConfigureAwait(false);
                var values = SqliteRecordCodec.Decode(record, textEncoding);
                rows.Add(ToSchemaRow(pageCell.Cell.RowId, values));
            }

            // Ascend until a frame has an unvisited next child, descending into it; an empty
            // stack after popping everything means the whole tree is exhausted.
            uint? nextPage = null;
            while (stack.Count > 0)
            {
                var frameIndex = stack.Count - 1;
                var frame = stack[frameIndex];
                var nextChildIndex = frame.NextChildIndex + 1;
                if (nextChildIndex <= frame.View.Cells.Count)
                {
                    stack[frameIndex] = frame with { NextChildIndex = nextChildIndex };
                    nextPage = ChildAt(frame.View, nextChildIndex);
                    break;
                }

                stack.RemoveAt(frameIndex);
            }

            if (nextPage is not { } resolvedNextPage)
                return;

            currentPage = resolvedNextPage;
        }
    }

    private static uint ChildAt(SqliteTableInteriorPageView view, int childIndex)
        => childIndex < view.Cells.Count
            ? view.Cells[childIndex].Cell.LeftChildPage
            : view.Header.RightMostChildPage;

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
