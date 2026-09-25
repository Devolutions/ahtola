using System.Runtime.CompilerServices;
using Ahtola.Core;

namespace Ahtola.Core.Storage;

internal readonly record struct SqlitePrimaryKeyConstraint(
    SqlValue Value,
    bool Lower,
    bool Inclusive);

/// <summary>
/// Page-bounded traversal of a WITHOUT ROWID table's index b-tree in either key direction.
/// Both index leaves and interior separator cells contain complete table records.
/// </summary>
internal static class AsyncBoundedWithoutRowidTableScanCursor
{
    private const int MaximumDepth = 64;

    public static async IAsyncEnumerable<SqlValue[]> SeekPrimaryKeyAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        IReadOnlyList<SqlValue> keys,
        long? limit,
        long offset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageCache);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit == 0 || offset != 0)
            yield break;

        var found = await TrySeekPrimaryKeyAsync(
            pageCache, rootPage, table, textEncoding, keys, cancellationToken).ConfigureAwait(false);
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
        SqlValue? firstPrimaryKeyEquals = null,
        IReadOnlyList<SqlitePrimaryKeyConstraint>? firstPrimaryKeyBounds = null)
        => ScanAscendingAsync(
            pageCache, rootPage, table, textEncoding, limit, offset, cancellationToken,
            firstPrimaryKeyEquals,
            descending: true,
            firstPrimaryKeyBounds: firstPrimaryKeyBounds);

    public static async IAsyncEnumerable<SqlValue[]> ScanAscendingAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        long? limit,
        long offset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        SqlValue? firstPrimaryKeyEquals = null,
        bool descending = false,
        IReadOnlyList<SqlitePrimaryKeyConstraint>? firstPrimaryKeyBounds = null)
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
            ? new[] { target }
            : null;
        var candidate = new SqlValue[1];
        var boundTarget = new SqlValue[1];
        var overflowReader = new AsyncSqliteOverflowChainReader(pageCache);
        var stack = new List<(uint PageNumber, SqliteIndexInteriorPageView View, int ChildIndex)>();
        var currentPage = rootPage;
        var skipped = 0L;
        var yielded = 0L;
        SqlValue[]? previousKey = null;
        var startingBound = FindStartingBound(
            firstPrimaryKeyEquals, firstPrimaryKeyBounds, comparer, descending);
        var seekingStart = startingBound is not null;

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
                    pageCache.Pin(currentPage);
                    stack.Add((currentPage, interior, 0));
                    var childIndex = seekingStart && startingBound is { } bound
                        ? await FindStartingChildAsync(
                            interior, bound, descending, table, primaryKey,
                            comparer, textEncoding, overflowReader, cancellationToken).ConfigureAwait(false)
                        : descending ? interior.Cells.Count : 0;
                    stack[^1] = (currentPage, interior, childIndex);
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
                seekingStart = false;
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
                        if (firstPrimaryKeyBounds is not null)
                        {
                            candidate[0] = key[0];
                            var (matches, beyond) = CheckFirstKeyBounds(
                                candidate, boundTarget, firstPrimaryKeyBounds, comparer, descending);
                            if (beyond)
                                yield break;
                            if (!matches)
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
                        if (firstPrimaryKeyBounds is not null)
                        {
                            candidate[0] = key[0];
                            var (inRange, beyond) = CheckFirstKeyBounds(
                                candidate, boundTarget, firstPrimaryKeyBounds, comparer, descending);
                            if (beyond)
                                yield break;
                            matches &= inRange;
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

    private static SqlitePrimaryKeyConstraint? FindStartingBound(
        SqlValue? firstPrimaryKeyEquals,
        IReadOnlyList<SqlitePrimaryKeyConstraint>? bounds,
        SqliteIndexRecordComparer comparer,
        bool descending)
    {
        if (firstPrimaryKeyEquals is { } exact)
            return new SqlitePrimaryKeyConstraint(exact, Lower: !descending, Inclusive: true);
        SqlitePrimaryKeyConstraint? strongest = null;
        if (bounds is null)
            return null;
        foreach (var bound in bounds)
        {
            if (bound.Lower == descending)
                continue;
            if (strongest is { } current)
            {
                var comparison = comparer.Compare([bound.Value], [current.Value]);
                if (descending ? comparison > 0 : comparison < 0)
                    continue;
                if (comparison == 0 && (bound.Inclusive || !current.Inclusive))
                    continue;
            }
            strongest = bound;
        }
        return strongest;
    }

    private static async ValueTask<int> FindStartingChildAsync(
        SqliteIndexInteriorPageView interior,
        SqlitePrimaryKeyConstraint bound,
        bool descending,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKey,
        SqliteIndexRecordComparer comparer,
        SqliteTextEncoding textEncoding,
        AsyncSqliteOverflowChainReader overflowReader,
        CancellationToken cancellationToken)
    {
        var candidate = new SqlValue[1];
        var target = new[] { bound.Value };
        var includeEqual = descending ? !bound.Inclusive : bound.Inclusive;
        for (var index = 0; index < interior.Cells.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await overflowReader.ReadPayloadAsync(
                interior.Cells[index].Cell.Key, cancellationToken).ConfigureAwait(false);
            var storedValues = SqliteRecordCodec.Decode(record, textEncoding);
            var key = ValidateAndExtractKey(
                storedValues, table, primaryKey, comparer, previousKey: null, descending: false);
            candidate[0] = key[0];
            var comparison = comparer.Compare(candidate, target);
            if (comparison > 0 || comparison == 0 && includeEqual)
                return index;
        }
        return interior.Cells.Count;
    }

    private static (bool Matches, bool Beyond) CheckFirstKeyBounds(
        SqlValue[] candidate,
        SqlValue[] target,
        IReadOnlyList<SqlitePrimaryKeyConstraint> bounds,
        SqliteIndexRecordComparer comparer,
        bool descending)
    {
        var matches = true;
        foreach (var bound in bounds)
        {
            target[0] = bound.Value;
            var comparison = comparer.Compare(candidate, target);
            var excluded = bound.Lower
                ? comparison < 0 || comparison == 0 && !bound.Inclusive
                : comparison > 0 || comparison == 0 && !bound.Inclusive;
            if (!excluded)
                continue;
            if (bound.Lower == descending)
                return (false, true);
            matches = false;
        }
        return (matches, false);
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

    private static async ValueTask<SqlValue[]?> TrySeekPrimaryKeyAsync(
        BoundedAsyncPageCache pageCache,
        uint rootPage,
        EmbeddedTable table,
        SqliteTextEncoding textEncoding,
        IReadOnlyList<SqlValue> keys,
        CancellationToken cancellationToken)
    {
        var schema = table.PrimaryKeySchema
            ?? throw new InvalidDataException("WITHOUT ROWID table has no primary-key schema.");
        if (keys.Count != schema.Terms.Count || keys.Count == 0)
            throw new InvalidOperationException("A direct primary-key lookup requires every key column.");
        for (var index = 0; index < schema.Terms.Count; index++)
        {
            var term = schema.Terms[index];
            if (term.SortOrder != SqliteKeySortOrder.Ascending
                || !term.Collation.IsBinary
                || term.Collation.Comparison is not null
                || !IsSupportedKey(
                    table.ColumnDefinitions[term.ColumnIndex].DeclaredType,
                    keys[index]))
            {
                throw new NotSupportedException(
                    "A direct primary-key lookup requires ascending BINARY INTEGER/TEXT keys.");
            }
        }

        var comparer = new SqliteIndexRecordComparer(
            textEncoding,
            schema.Terms.Select(static term =>
                new SqliteIndexComparisonTerm(term.SortOrder, term.Collation)).ToArray());
        var target = keys.ToArray();
        var overflowReader = new AsyncSqliteOverflowChainReader(pageCache);
        var pinnedPages = new List<uint>();
        var currentPage = rootPage;

        (SqlValue[] Values, int Comparison) DecodeAndCompare(byte[] record)
        {
            var values = SqliteRecordCodec.Decode(record, textEncoding);
            var recordKey = ValidateAndExtractKey(
                values, table, schema, comparer, previousKey: null, descending: false);
            return (values, comparer.Compare(recordKey, target));
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

    private static bool IsSupportedKey(string? declaredType, SqlValue value)
        => (value.Kind == SqlValueKind.Integer
                && string.Equals(declaredType, "INTEGER", StringComparison.OrdinalIgnoreCase))
            || (value.Kind == SqlValueKind.Text
                && string.Equals(declaredType, "TEXT", StringComparison.OrdinalIgnoreCase));
}
