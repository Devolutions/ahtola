namespace Ahtola.Core.Mvcc;

/// <summary>
/// Merges a classic base-table snapshot with MVCC version-store overlays for one
/// transaction (Turso <c>core/mvcc/cursor.rs::MvccLazyCursor</c>). Base rows
/// invalidated for this reader are suppressed; store-only inserts appear as
/// additional rows.
/// </summary>
internal static class MvccDualCursor
{
    internal readonly record struct Row(MvccKey Key, SqlValue[] Cells);

    internal readonly record struct IndexRow(
        MvccKey TableKey,
        SqlValue[] IndexKey,
        SqlValue[] Cells);

    /// <summary>
    /// Lazily overlays an ordered base image with the ordered MVCC range. This
    /// mirrors Turso's two-peek cursor: equal keys consume both inputs, and only
    /// the winning input advances for unequal keys.
    /// </summary>
    internal static IEnumerable<Row> EnumerateVisibleRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        IEnumerable<Row> baseRows,
        IComparer<MvccKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(baseRows);
        ArgumentNullException.ThrowIfNull(comparer);

        using var baseCursor = baseRows.GetEnumerator();
        using var overlayCursor = store.EnumerateVisible(txId, tableId, comparer).GetEnumerator();
        var hasBase = baseCursor.MoveNext();
        var hasOverlay = overlayCursor.MoveNext();

        while (hasBase || hasOverlay)
        {
            if (!hasOverlay)
            {
                var baseRow = baseCursor.Current;
                yield return new Row(baseRow.Key, (SqlValue[])baseRow.Cells.Clone());
                hasBase = baseCursor.MoveNext();
                continue;
            }

            if (!hasBase)
            {
                var overlay = overlayCursor.Current;
                if (!overlay.IsDelete)
                    yield return new Row(overlay.Key, overlay.Cells!);
                hasOverlay = overlayCursor.MoveNext();
                continue;
            }

            var comparison = comparer.Compare(baseCursor.Current.Key, overlayCursor.Current.Key);
            if (comparison < 0)
            {
                var baseRow = baseCursor.Current;
                yield return new Row(baseRow.Key, (SqlValue[])baseRow.Cells.Clone());
                hasBase = baseCursor.MoveNext();
                continue;
            }

            var winner = overlayCursor.Current;
            if (!winner.IsDelete)
                yield return new Row(winner.Key, winner.Cells!);
            hasOverlay = overlayCursor.MoveNext();
            if (comparison == 0)
                hasBase = baseCursor.MoveNext();
        }
    }

    /// <summary>Compatibility materializer for callers that require a list.</summary>
    internal static IReadOnlyList<Row> MergeVisibleRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        IReadOnlyList<Row> baseRows,
        IComparer<MvccKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        return EnumerateVisibleRows(
                store,
                txId,
                tableId,
                baseRows,
                comparer)
            .ToArray();
    }

    /// <summary>
    /// Returns the row set visible to <paramref name="txId"/>: base rows not
    /// invalidated by the store, plus live store versions for this table id.
    /// </summary>
    internal static IReadOnlyList<(long RowId, SqlValue[] Cells)> MergeVisibleRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        IReadOnlyList<long> baseRowIds,
        IReadOnlyList<SqlValue[]> baseRows)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(baseRowIds);
        ArgumentNullException.ThrowIfNull(baseRows);
        if (baseRowIds.Count != baseRows.Count)
            throw new ArgumentException("Base row id and cell lists must have equal length.");

        var typedBaseRows = baseRowIds
            .Select((rowId, index) => new Row(MvccKey.FromInteger(rowId), baseRows[index]))
            .OrderBy(static row => row.Key.Integer);

        return EnumerateVisibleRows(
                store,
                txId,
                tableId,
                typedBaseRows,
                MvccKeyComparer.Integer)
            .Select(static row => (row.Key.Integer, row.Cells))
            .ToArray();
    }

    /// <summary>
    /// Lazily merges a base index cursor with visible version-chain entries. Base
    /// entries shadowed by a visible table-key effect are skipped before index-key
    /// comparison, so updates that move between index keys are emitted only at
    /// their new position.
    /// </summary>
    internal static IEnumerable<IndexRow> EnumerateVisibleIndexRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        IEnumerable<IndexRow> baseRows,
        Func<MvccVisibleRow, IndexRow?> projectOverlay,
        IComparer<IndexRow> indexComparer,
        IComparer<MvccKey> tableKeyComparer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(baseRows);
        ArgumentNullException.ThrowIfNull(projectOverlay);
        ArgumentNullException.ThrowIfNull(indexComparer);
        ArgumentNullException.ThrowIfNull(tableKeyComparer);

        using var baseCursor = EnumerateUnshadowedBaseIndexRows(
            store,
            txId,
            tableId,
            baseRows).GetEnumerator();
        using var overlayCursor = EnumerateOverlayIndexRows(
            store,
            txId,
            tableId,
            projectOverlay,
            indexComparer,
            tableKeyComparer).GetEnumerator();
        var hasBase = baseCursor.MoveNext();
        var hasOverlay = overlayCursor.MoveNext();
        while (hasBase || hasOverlay)
        {
            if (!hasOverlay)
            {
                yield return baseCursor.Current;
                hasBase = baseCursor.MoveNext();
                continue;
            }

            if (!hasBase)
            {
                yield return overlayCursor.Current;
                hasOverlay = overlayCursor.MoveNext();
                continue;
            }

            var comparison = indexComparer.Compare(baseCursor.Current, overlayCursor.Current);
            if (comparison < 0)
            {
                yield return baseCursor.Current;
                hasBase = baseCursor.MoveNext();
                continue;
            }

            yield return overlayCursor.Current;
            hasOverlay = overlayCursor.MoveNext();
            if (comparison == 0)
                hasBase = baseCursor.MoveNext();
        }
    }

    private static IEnumerable<IndexRow> EnumerateUnshadowedBaseIndexRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        IEnumerable<IndexRow> baseRows)
    {
        foreach (var row in baseRows)
        {
            if (store.TryReadVisibleEffect(
                    txId,
                    new MvccRowId(tableId, row.TableKey),
                    out _,
                    out _))
            {
                continue;
            }

            yield return new IndexRow(
                row.TableKey,
                (SqlValue[])row.IndexKey.Clone(),
                (SqlValue[])row.Cells.Clone());
        }
    }

    private static IEnumerable<IndexRow> EnumerateOverlayIndexRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        Func<MvccVisibleRow, IndexRow?> projectOverlay,
        IComparer<IndexRow> indexComparer,
        IComparer<MvccKey> tableKeyComparer)
    {
        IndexRow? previous = null;
        while (true)
        {
            IndexRow? next = null;
            foreach (var visible in store.EnumerateVisible(
                         txId,
                         tableId,
                         tableKeyComparer))
            {
                if (visible.IsDelete || projectOverlay(visible) is not { } candidate)
                    continue;
                if (previous is { } prior
                    && indexComparer.Compare(candidate, prior) <= 0)
                {
                    continue;
                }
                if (next is null || indexComparer.Compare(candidate, next.Value) < 0)
                    next = candidate;
            }

            if (next is not { } selected)
                yield break;
            yield return selected;
            previous = selected;
        }
    }
}
