using System.Runtime.CompilerServices;
using Ahtola.Core;

namespace Ahtola.Core.Storage;

internal readonly record struct SqliteRowIdRange(
    long? Lower,
    bool IncludeLower,
    long? Upper,
    bool IncludeUpper)
{
    internal bool IsEmpty => Lower is { } lower && Upper is { } upper
        && (lower > upper || lower == upper && (!IncludeLower || !IncludeUpper));

    internal bool IsBelowLower(long rowId) => Lower is { } lower
        && (rowId < lower || rowId == lower && !IncludeLower);

    internal bool IsAboveUpper(long rowId) => Upper is { } upper
        && (rowId > upper || rowId == upper && !IncludeUpper);
}

/// <summary>
/// A genuinely asynchronous, page-bounded rowid scan or exact-rowid seek for an ordinary
/// (indexless, has-rowid) SQLite table b-tree.
/// </summary>
/// <remarks>
/// <para>
/// This is an <c>async</c> C# iterator method (<c>await</c> ... <c>yield return</c> inside an
/// <c>async</c> method) — the compiler's own suspend/resume state machine is the "real
/// await/resumption boundary" this design relies on, not a hand-rolled fault/retry mechanism.
/// </para>
/// <para>
/// Because SQLite table b-trees have no leaf sibling pointers, ascending iteration keeps the
/// full ancestor interior-page stack live for the whole scan (a standard iterative in-order
/// b-tree traversal: interior pages route to children left to right; only leaves hold rows).
/// The stack depth is therefore bounded by the tree's height, never by its row count. Every page
/// this cursor touches (root, every interior page, every leaf, every overflow page) flows
/// through the same <see cref="BoundedAsyncPageCache"/>, whose pin/capacity accounting is what
/// mechanically proves — not merely asserts — that resident pages never exceed the connection's
/// configured page budget: an ancestor frame is pinned for as long as it is on the stack and
/// unpinned the moment traversal backtracks past it, so the cache can always evict an
/// already-visited leaf/overflow page to make room, and only genuinely runs out of room (and
/// throws <see cref="SqliteBoundedPageBudgetExceededException"/>) when the *ancestor stack
/// itself* would need to exceed the configured budget.
/// </para>
/// <para>
/// Unlike <c>EmbeddedFileStore.Load()</c>'s eager, whole-table structural validation, this
/// cursor validates each page's structure (rowid ordering, page-type expectations, cell-range
/// bounds — all already enforced by <see cref="SqliteTableInteriorPageView.Parse"/>/
/// <see cref="SqliteTableLeafPageView.Parse"/> themselves) lazily, the first time that page is
/// visited. A corrupt region not yet scanned is not detected until (if ever) a later scan
/// reaches it — an intentional, documented difference of this narrow, additive profile; a
/// corrupted page that <em>is</em> visited still fails closed.
/// </para>
/// </remarks>
internal static class AsyncBoundedRowidTableScanCursor
{
    private const int MaximumDepth = 64;

    public static IAsyncEnumerable<SqlValue[]> ScanDescendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        CancellationToken cancellationToken = default,
        long? equalRowId = null,
        SqliteRowIdRange? rowIdRange = null)
        => ScanAscendingAsync(
            pageCache, rootPage, table, textEncoding, limit, cancellationToken, equalRowId, rowIdRange,
            descending: true);

    public static async IAsyncEnumerable<SqlValue[]> ScanAscendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        long? equalRowId = null,
        SqliteRowIdRange? rowIdRange = null,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(pageCache);
        ArgumentNullException.ThrowIfNull(table);

        if (equalRowId is { } soughtRowId)
        {
            if (limit != 0
                && await TrySeekRowIdAsync(
                    pageCache,
                    rootPage,
                    table,
                    textEncoding,
                    soughtRowId,
                    cancellationToken).ConfigureAwait(false) is { } found)
            {
                yield return found;
            }

            yield break;
        }

        if (rowIdRange is { IsEmpty: true })
            yield break;

        var overflowReader = new AsyncSqliteOverflowChainReader(pageCache);
        // Each frame is one still-open ancestor interior page: the page number (for
        // pin/unpin), its parsed view, and the index of the next child to descend into
        // once the currently active subtree is exhausted.
        var stack = new List<(uint PageNumber, SqliteTableInteriorPageView View, int NextChildIndex)>();
        var currentPage = rootPage;
        var yielded = 0L;

        try
        {
            while (true)
            {
                if (limit is { } max && yielded >= max)
                    yield break;

                cancellationToken.ThrowIfCancellationRequested();
                // Computed fresh from the actual page number being visited, exactly like the
                // synchronous SqliteTableBtreeCursor.TrySeekLeaf does on every iteration — not
                // hardcoded false for every page after the root. Only physical page 1 carries
                // the 100-byte database header prefix; a corrupt tree that routes back to page 1
                // as if it were an ordinary child must still be parsed at the right offset, not
                // misread as a bogus page type from byte 0 of the SQLite header.
                var isFirstPage = currentPage == 1;
                var image = await pageCache.ReadPageAsync(currentPage, cancellationToken).ConfigureAwait(false);
                var header = SqliteBtreePageHeader.Parse(image, isFirstPage, pageCache.UsableSpace);

                if (header.PageType == SqliteBtreePageType.TableInterior)
                {
                    if (stack.Count >= MaximumDepth)
                    {
                        throw new InvalidDataException(
                            $"SQLite table b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels.");
                    }

                    var interior = SqliteTableInteriorPageView.Parse(image, pageCache.UsableSpace, isFirstPage);
                    var startChild = descending
                        ? rowIdRange?.Upper is { } upper
                            ? interior.SearchChild(upper).ChildIndex
                            : interior.Cells.Count
                        : rowIdRange?.Lower is { } lower
                            ? interior.SearchChild(lower).ChildIndex
                            : 0;
                    pageCache.Pin(currentPage);
                    stack.Add((currentPage, interior, startChild));
                    currentPage = ChildAt(interior, startChild);
                    continue;
                }

                if (header.PageType != SqliteBtreePageType.TableLeaf)
                {
                    throw new InvalidDataException(
                        $"SQLite page {currentPage} is not part of a rowid-table b-tree.");
                }

                var leaf = SqliteTableLeafPageView.Parse(image, pageCache.UsableSpace, isFirstPage);
                var leafPage = currentPage;
                // The leaf must stay pinned for as long as any of its cells might still need an
                // overflow-chain fetch through the same capacity-capped cache: without this, an
                // overflow read could evict the leaf's own cache entry to make room, understating
                // real resident memory (the leaf's bytes are still alive via this parsed `leaf`
                // view and its cells) instead of the cache's accounting failing loud with
                // SqliteBoundedPageBudgetExceededException when the budget is genuinely too
                // small to hold both at once.
                pageCache.Pin(leafPage);
                try
                {
                    for (var index = descending ? leaf.Cells.Count - 1 : 0;
                         descending ? index >= 0 : index < leaf.Cells.Count;
                         index += descending ? -1 : 1)
                    {
                        var cell = leaf.Cells[index];
                        if (limit is { } cellMax && yielded >= cellMax)
                            yield break;
                        if (rowIdRange is { } bounds)
                        {
                            if (descending)
                            {
                                if (bounds.IsAboveUpper(cell.Cell.RowId))
                                    continue;
                                if (bounds.IsBelowLower(cell.Cell.RowId))
                                    yield break;
                            }
                            else
                            {
                                if (bounds.IsBelowLower(cell.Cell.RowId))
                                    continue;
                                if (bounds.IsAboveUpper(cell.Cell.RowId))
                                    yield break;
                            }
                        }

                        var record = await overflowReader
                            .ReadPayloadAsync(cell.Cell, cancellationToken)
                            .ConfigureAwait(false);
                        var rawValues = SqliteRecordCodec.Decode(record, textEncoding);
                        // A row written before a later ALTER TABLE ADD COLUMN decodes with fewer
                        // values than the table's current column count; reuse the exact same
                        // default/NULL padding EmbeddedFileStore's own eager load path uses so
                        // the two can never disagree about a short record's missing columns.
                        var values = EmbeddedFileStore.RestoreRowidTableRecord(table, rawValues);
                        if (table.RowidAliasColumnIndex >= 0)
                            values[table.RowidAliasColumnIndex] = SqlValue.Integer(cell.Cell.RowId);

                        yield return values;
                        yielded++;
                    }
                }
                finally
                {
                    pageCache.Unpin(leafPage);
                }

                // Ascend until a frame has an unvisited next child, descending into it; an
                // empty stack after popping everything means the whole tree is exhausted.
                uint? nextPage = null;
                while (stack.Count > 0)
                {
                    var frameIndex = stack.Count - 1;
                    var frame = stack[frameIndex];
                    var nextChildIndex = frame.NextChildIndex + (descending ? -1 : 1);
                    if (nextChildIndex >= 0 && nextChildIndex <= frame.View.Cells.Count)
                    {
                        stack[frameIndex] = frame with { NextChildIndex = nextChildIndex };
                        nextPage = ChildAt(frame.View, nextChildIndex);
                        break;
                    }

                    pageCache.Unpin(frame.PageNumber);
                    stack.RemoveAt(frameIndex);
                }

                if (nextPage is not { } resolvedNextPage)
                    yield break;

                currentPage = resolvedNextPage;
            }
        }
        finally
        {
            foreach (var frame in stack)
                pageCache.Unpin(frame.PageNumber);
        }
    }

    private static async ValueTask<SqlValue[]?> TrySeekRowIdAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long rowId,
        CancellationToken cancellationToken)
    {
        var pinnedPages = new List<uint>();
        var pageNumber = rootPage;
        try
        {
            for (var depth = 0; depth < MaximumDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isFirstPage = pageNumber == 1;
                var image = await pageCache.ReadPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
                switch (SqliteBtreePageHeader.Parse(image, isFirstPage, pageCache.UsableSpace).PageType)
                {
                    case SqliteBtreePageType.TableInterior:
                        var interior = SqliteTableInteriorPageView.Parse(image, pageCache.UsableSpace, isFirstPage);
                        pageCache.Pin(pageNumber);
                        pinnedPages.Add(pageNumber);
                        pageNumber = interior.SearchChild(rowId).ChildPage;
                        break;

                    case SqliteBtreePageType.TableLeaf:
                        var leaf = SqliteTableLeafPageView.Parse(image, pageCache.UsableSpace, isFirstPage);
                        var search = leaf.Search(rowId);
                        if (!search.IsExact)
                            return null;

                        pageCache.Pin(pageNumber);
                        pinnedPages.Add(pageNumber);
                        var record = await new AsyncSqliteOverflowChainReader(pageCache)
                            .ReadPayloadAsync(leaf.Cells[search.Index].Cell, cancellationToken)
                            .ConfigureAwait(false);
                        var values = EmbeddedFileStore.RestoreRowidTableRecord(
                            table,
                            SqliteRecordCodec.Decode(record, textEncoding));
                        values[table.RowidAliasColumnIndex] = SqlValue.Integer(rowId);
                        return values;

                    default:
                        throw new InvalidDataException(
                            $"SQLite page {pageNumber} is not part of a rowid-table b-tree.");
                }
            }

            throw new InvalidDataException(
                $"SQLite table b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels.");
        }
        finally
        {
            foreach (var pinnedPage in pinnedPages)
                pageCache.Unpin(pinnedPage);
        }
    }

    private static uint ChildAt(SqliteTableInteriorPageView view, int childIndex)
        => childIndex < view.Cells.Count
            ? view.Cells[childIndex].Cell.LeftChildPage
            : view.Header.RightMostChildPage;
}
