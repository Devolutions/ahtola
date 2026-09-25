using System.Runtime.CompilerServices;
using Ahtola.Core;

namespace Ahtola.Core.Storage;

/// <summary>
/// Page-bounded traversal of a WITHOUT ROWID table's index b-tree in either key direction.
/// Both index leaves and interior separator cells contain complete table records.
/// </summary>
internal static class AsyncBoundedWithoutRowidTableScanCursor
{
    private const int MaximumDepth = 64;

    public static IAsyncEnumerable<SqlValue[]> ScanDescendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        long offset,
        CancellationToken cancellationToken = default)
        => ScanAscendingAsync(
            pageCache, rootPage, table, textEncoding, limit, offset, cancellationToken,
            descending: true);

    public static async IAsyncEnumerable<SqlValue[]> ScanAscendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        long offset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(pageCache);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var primaryKey = table.PrimaryKeySchema
            ?? throw new InvalidDataException("WITHOUT ROWID table has no primary-key schema.");
        var comparer = new SqliteIndexRecordComparer(
            textEncoding,
            primaryKey.Terms.Select(static term =>
                new SqliteIndexComparisonTerm(term.SortOrder, term.Collation)).ToArray());
        var overflowReader = new AsyncSqliteOverflowChainReader(pageCache);
        var stack = new List<(uint PageNumber, SqliteIndexInteriorPageView View, int ChildIndex)>();
        var currentPage = rootPage;
        var skipped = 0L;
        var yielded = 0L;
        SqlValue[]? previousKey = null;

        try
        {
            while (true)
            {
                if (limit is { } max && yielded >= max)
                    yield break;

                cancellationToken.ThrowIfCancellationRequested();
                var image = await pageCache.ReadPageAsync(currentPage, cancellationToken).ConfigureAwait(false);
                var pageType = SqliteBtreePageHeader.Parse(
                    image, currentPage == 1, pageCache.UsableSpace).PageType;
                if (pageType == SqliteBtreePageType.IndexInterior)
                {
                    if (stack.Count >= MaximumDepth)
                        throw new InvalidDataException($"SQLite index b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels or contains a cycle.");

                    var interior = SqliteIndexInteriorPageView.Parse(
                        image, pageCache.UsableSpace, textEncoding, currentPage == 1,
                        recordComparer: comparer);
                    var childIndex = descending ? interior.Cells.Count : 0;
                    pageCache.Pin(currentPage);
                    stack.Add((currentPage, interior, childIndex));
                    currentPage = childIndex == interior.Cells.Count
                        ? interior.Header.RightMostChildPage
                        : interior.Cells[childIndex].Cell.LeftChildPage;
                    continue;
                }

                if (pageType != SqliteBtreePageType.IndexLeaf)
                    throw new InvalidDataException($"SQLite page {currentPage} is not part of a WITHOUT ROWID index b-tree.");

                var leaf = SqliteIndexLeafPageView.Parse(
                    image, pageCache.UsableSpace, textEncoding, currentPage == 1,
                    recordComparer: comparer);
                pageCache.Pin(currentPage);
                try
                {
                    for (var index = descending ? leaf.Cells.Count - 1 : 0;
                         descending ? index >= 0 : index < leaf.Cells.Count;
                         index += descending ? -1 : 1)
                    {
                        var pageCell = leaf.Cells[index];
                        cancellationToken.ThrowIfCancellationRequested();
                        if (limit is { } leafLimit && yielded >= leafLimit)
                            yield break;

                        var record = await overflowReader.ReadPayloadAsync(pageCell.Cell, cancellationToken)
                            .ConfigureAwait(false);
                        var storedValues = SqliteRecordCodec.Decode(record, textEncoding);
                        var key = ValidateAndExtractKey(
                            storedValues, table, primaryKey, comparer, previousKey, descending);
                        previousKey = key;
                        var row = EmbeddedFileStore.RestoreWithoutRowidRecord(
                            table.Name, table, primaryKey, storedValues);
                        if (skipped < offset)
                        {
                            skipped++;
                            continue;
                        }

                        yield return row;
                        yielded++;
                    }
                }
                finally
                {
                    pageCache.Unpin(currentPage);
                }

                if (limit is { } scannedLimit && yielded >= scannedLimit)
                    yield break;

                // After child i, visit separator i in ascending order or separator i-1
                // in descending order, then enter the next child in that direction.
                uint? nextPage = null;
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frameIndex = stack.Count - 1;
                    var frame = stack[frameIndex];
                    if (descending ? frame.ChildIndex > 0 : frame.ChildIndex < frame.View.Cells.Count)
                    {
                        var separatorIndex = descending ? frame.ChildIndex - 1 : frame.ChildIndex;
                        var cell = frame.View.Cells[separatorIndex].Cell;
                        var record = await overflowReader.ReadPayloadAsync(cell.Key, cancellationToken)
                            .ConfigureAwait(false);
                        var storedValues = SqliteRecordCodec.Decode(record, textEncoding);
                        var key = ValidateAndExtractKey(
                            storedValues, table, primaryKey, comparer, previousKey, descending);
                        previousKey = key;
                        var row = EmbeddedFileStore.RestoreWithoutRowidRecord(
                            table.Name, table, primaryKey, storedValues);

                        var nextChildIndex = frame.ChildIndex + (descending ? -1 : 1);
                        stack[frameIndex] = frame with { ChildIndex = nextChildIndex };
                        nextPage = nextChildIndex == frame.View.Cells.Count
                            ? frame.View.Header.RightMostChildPage
                            : frame.View.Cells[nextChildIndex].Cell.LeftChildPage;

                        if (skipped < offset)
                            skipped++;
                        else
                        {
                            yield return row;
                            yielded++;
                        }

                        if (limit is { } separatorLimit && yielded >= separatorLimit)
                            yield break;

                        break;
                    }

                    pageCache.Unpin(frame.PageNumber);
                    stack.RemoveAt(frameIndex);
                }

                if (nextPage is not { } resolvedPage)
                    yield break;

                currentPage = resolvedPage;
            }
        }
        finally
        {
            foreach (var frame in stack)
                pageCache.Unpin(frame.PageNumber);
        }
    }

    private static SqlValue[] ValidateAndExtractKey(
        SqlValue[] storedValues,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKey,
        SqliteIndexRecordComparer comparer,
        SqlValue[]? previousKey,
        bool descending)
    {
        if (storedValues.Length < primaryKey.Terms.Count)
            throw new InvalidDataException($"WITHOUT ROWID table '{table.Name}' record is missing primary-key values.");

        var key = storedValues[..primaryKey.Terms.Count];
        if (key.Any(static value => value.Kind == SqlValueKind.Null))
            throw new InvalidDataException($"WITHOUT ROWID table '{table.Name}' has a NULL primary-key value.");
        if (previousKey is not null
            && (descending
                ? comparer.Compare(previousKey, key) <= 0
                : comparer.Compare(previousKey, key) >= 0))
        {
            throw new InvalidDataException(
                $"WITHOUT ROWID table '{table.Name}' records are not in strictly "
                + (descending ? "decreasing" : "increasing") + " primary-key order.");
        }

        return key;
    }
}
