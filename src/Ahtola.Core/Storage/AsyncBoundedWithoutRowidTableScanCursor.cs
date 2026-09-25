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

    public static async IAsyncEnumerable<SqlValue[]> SeekIntegerPrimaryKeyAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long key,
        long? limit,
        long offset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageCache);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit == 0 || offset != 0)
            yield break;

        var found = await TrySeekIntegerPrimaryKeyAsync(
            pageCache, rootPage, table, textEncoding, key, cancellationToken).ConfigureAwait(false);
        if (found is not null)
            yield return found;
    }

    public static IAsyncEnumerable<SqlValue[]> ScanDescendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        long offset,
        CancellationToken cancellationToken = default,
        long? firstPrimaryKeyEquals = null)
        => ScanAscendingAsync(
            pageCache, rootPage, table, textEncoding, limit, offset, cancellationToken,
            firstPrimaryKeyEquals,
            descending: true);

    public static async IAsyncEnumerable<SqlValue[]> ScanAscendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        long offset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        long? firstPrimaryKeyEquals = null,
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
        var filterKey = firstPrimaryKeyEquals is { } target
            ? new[] { SqlValue.Integer(target) }
            : null;
        var candidate = new SqlValue[1];
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
                        if (filterKey is not null)
                        {
                            candidate[0] = key[0];
                            var comparison = comparer.Compare(candidate, filterKey);
                            if (descending ? comparison < 0 : comparison > 0)
                                yield break;
                            if (comparison != 0)
                                continue;
                        }

                        if (skipped < offset)
                        {
                            skipped++;
                            continue;
                        }

                        var row = EmbeddedFileStore.RestoreWithoutRowidRecord(
                            table.Name, table, primaryKey, storedValues);
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
                        var matches = true;
                        if (filterKey is not null)
                        {
                            candidate[0] = key[0];
                            var comparison = comparer.Compare(candidate, filterKey);
                            if (descending ? comparison < 0 : comparison > 0)
                                yield break;
                            matches = comparison == 0;
                        }

                        var nextChildIndex = frame.ChildIndex + (descending ? -1 : 1);
                        stack[frameIndex] = frame with { ChildIndex = nextChildIndex };
                        nextPage = nextChildIndex == frame.View.Cells.Count
                            ? frame.View.Header.RightMostChildPage
                            : frame.View.Cells[nextChildIndex].Cell.LeftChildPage;

                        if (matches)
                        {
                            if (skipped < offset)
                                skipped++;
                            else
                            {
                                var row = EmbeddedFileStore.RestoreWithoutRowidRecord(
                                    table.Name, table, primaryKey, storedValues);
                                yield return row;
                                yielded++;
                            }
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

    private static async ValueTask<SqlValue[]?> TrySeekIntegerPrimaryKeyAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long key,
        CancellationToken cancellationToken)
    {
        var schema = table.PrimaryKeySchema
            ?? throw new InvalidDataException("WITHOUT ROWID table has no primary-key schema.");
        if (schema.Terms.Count != 1)
            throw new InvalidOperationException("A direct primary-key lookup requires one key column.");
        var term = schema.Terms[0];
        if (term.SortOrder != SqliteKeySortOrder.Ascending
            || !term.Collation.IsBinary
            || term.Collation.Comparison is not null
            || !string.Equals(
                table.ColumnDefinitions[term.ColumnIndex].DeclaredType,
                "INTEGER",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "A direct primary-key lookup requires an ascending BINARY INTEGER key.");
        }

        var comparer = new SqliteIndexRecordComparer(
            textEncoding,
            [new SqliteIndexComparisonTerm(term.SortOrder, term.Collation)]);
        var target = new[] { SqlValue.Integer(key) };
        var overflowReader = new AsyncSqliteOverflowChainReader(pageCache);
        var pinnedPages = new List<uint>();
        var currentPage = rootPage;

        (SqlValue[] Values, int Comparison) DecodeAndCompare(byte[] record)
        {
            var values = SqliteRecordCodec.Decode(record, textEncoding);
            if (values.Length == 0 || values[0].Kind == SqlValueKind.Null)
                throw new InvalidDataException("WITHOUT ROWID table record is missing its primary key.");
            return (values, comparer.Compare(new[] { values[0] }, target));
        }

        try
        {
            for (var depth = 0; depth < MaximumDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = await pageCache.ReadPageAsync(currentPage, cancellationToken).ConfigureAwait(false);
                var pageType = SqliteBtreePageHeader.Parse(
                    image, currentPage == 1, pageCache.UsableSpace).PageType;
                if (pageType == SqliteBtreePageType.IndexInterior)
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        image, pageCache.UsableSpace, textEncoding, currentPage == 1,
                        recordComparer: comparer);
                    pageCache.Pin(currentPage);
                    pinnedPages.Add(currentPage);
                    var childPage = interior.Header.RightMostChildPage;
                    foreach (var separator in interior.Cells)
                    {
                        var record = await overflowReader.ReadPayloadAsync(
                            separator.Cell.Key, cancellationToken).ConfigureAwait(false);
                        var (values, comparison) = DecodeAndCompare(record);
                        if (comparison == 0)
                            return EmbeddedFileStore.RestoreWithoutRowidRecord(
                                table.Name, table, schema, values);
                        if (comparison > 0)
                        {
                            childPage = separator.Cell.LeftChildPage;
                            break;
                        }
                    }
                    currentPage = childPage;
                    continue;
                }

                if (pageType != SqliteBtreePageType.IndexLeaf)
                {
                    throw new InvalidDataException(
                        $"SQLite page {currentPage} is not part of a WITHOUT ROWID index b-tree.");
                }

                var leaf = SqliteIndexLeafPageView.Parse(
                    image, pageCache.UsableSpace, textEncoding, currentPage == 1,
                    recordComparer: comparer);
                pageCache.Pin(currentPage);
                pinnedPages.Add(currentPage);
                foreach (var cell in leaf.Cells)
                {
                    var record = await overflowReader.ReadPayloadAsync(
                        cell.Cell, cancellationToken).ConfigureAwait(false);
                    var (values, comparison) = DecodeAndCompare(record);
                    if (comparison == 0)
                        return EmbeddedFileStore.RestoreWithoutRowidRecord(
                            table.Name, table, schema, values);
                    if (comparison > 0)
                        return null;
                }
                return null;
            }

            throw new InvalidDataException(
                $"SQLite index b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels or contains a cycle.");
        }
        finally
        {
            foreach (var pinnedPage in pinnedPages)
                pageCache.Unpin(pinnedPage);
        }
    }
}
