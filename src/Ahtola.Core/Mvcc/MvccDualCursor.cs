namespace Ahtola.Core.Mvcc;

/// <summary>
/// Merges a classic base-table snapshot with MVCC version-store overlays for one
/// transaction (Turso dual-cursor isolation spirit). Base rows that the store
/// has invalidated (deleted/updated for this reader) are suppressed; store-only
/// inserts appear as additional rows.
/// </summary>
internal static class MvccDualCursor
{
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

        var results = new List<(long, SqlValue[])>(baseRowIds.Count);
        var covered = new HashSet<long>();

        for (var i = 0; i < baseRowIds.Count; i++)
        {
            var rowId = baseRowIds[i];
            var key = new MvccRowId(tableId, rowId);
            if (store.TryRead(txId, key, out var overlay) && overlay is not null)
            {
                // Live store version (insert or update) overrides the base image.
                results.Add((rowId, overlay));
                covered.Add(rowId);
                continue;
            }

            if (store.IsBaseRowInvalidated(txId, key))
            {
                // Deleted or otherwise invalidated for this snapshot.
                covered.Add(rowId);
                continue;
            }

            results.Add((rowId, (SqlValue[])baseRows[i].Clone()));
            covered.Add(rowId);
        }

        foreach (var (key, cells) in store.ScanVisible(txId))
        {
            if (key.TableId != tableId || covered.Contains(key.RowId))
                continue;
            results.Add((key.RowId, cells));
        }

        return results;
    }
}
