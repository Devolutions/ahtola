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
    /// Merges an ordered base image with typed MVCC overlays. The supplied
    /// comparison must be the owning table's SQLite key comparison, including
    /// collations and directions for a WITHOUT ROWID primary key.
    /// </summary>
    internal static IReadOnlyList<Row> MergeVisibleRows(
        MvStore store,
        MvccTxId txId,
        long tableId,
        IReadOnlyList<Row> baseRows,
        Comparison<MvccKey> compare)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(baseRows);
        ArgumentNullException.ThrowIfNull(compare);

        var results = new List<Row>(baseRows.Count);
        var covered = new HashSet<MvccKey>();

        foreach (var baseRow in baseRows)
        {
            var identity = new MvccRowId(tableId, baseRow.Key);
            if (store.TryRead(txId, identity, out var overlay) && overlay is not null)
            {
                results.Add(new Row(baseRow.Key, overlay));
                covered.Add(baseRow.Key);
                continue;
            }

            if (store.IsBaseRowInvalidated(txId, identity))
            {
                covered.Add(baseRow.Key);
                continue;
            }

            results.Add(new Row(baseRow.Key, (SqlValue[])baseRow.Cells.Clone()));
            covered.Add(baseRow.Key);
        }

        foreach (var (identity, cells) in store.ScanVisible(txId))
        {
            if (identity.TableId != tableId || covered.Contains(identity.Key))
                continue;
            results.Add(new Row(identity.Key, cells));
        }

        results.Sort((left, right) => compare(left.Key, right.Key));
        return results;
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

        var typedBaseRows = new Row[baseRowIds.Count];
        for (var i = 0; i < baseRowIds.Count; i++)
        {
            typedBaseRows[i] = new Row(MvccKey.FromInteger(baseRowIds[i]), baseRows[i]);
        }

        return MergeVisibleRows(
                store,
                txId,
                tableId,
                typedBaseRows,
                static (left, right) => left.Integer.CompareTo(right.Integer))
            .Select(static row => (row.Key.Integer, row.Cells))
            .ToArray();
    }
}
