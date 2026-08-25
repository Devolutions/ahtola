namespace Ahtola.Core.Mvcc;

/// <summary>
/// Merges a classic base-table snapshot with MVCC version-store overlays for one
/// transaction (Turso dual-cursor isolation spirit). Base rows that the store
/// has invalidated (deleted/updated for this reader) are suppressed; store-only
/// inserts appear as additional rows.
/// </summary>
internal static class MvccDualCursor
{
    internal readonly record struct Row(MvccKey Key, SqlValue[] Cells);

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
}
