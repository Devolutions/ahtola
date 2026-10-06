using System.Diagnostics.CodeAnalysis;

namespace Ahtola.Core;

/// <summary>
/// Read-only structures derived from a table's rows (rowid scan order, rowid positions,
/// equality lookups, index scan orders), shared by a table and every clone made from it.
/// </summary>
/// <remarks>
/// Every statement executes against a clone of the catalog. Caches held on the
/// <see cref="EmbeddedTable"/> instance and validated by <see cref="RowStore.Revision"/> were
/// therefore rebuilt by every statement, which made a primary-key lookup or a rowid-ordered
/// scan cost O(table rows) or O(table rows · log) per statement. Entries here are validated by
/// the <see cref="CowChunkedList{T}.ContentStamp"/> of the rows and rowids they were derived
/// from instead: a stamp is process-unique per content state, so an entry built by any clone
/// is valid for every other table holding exactly the same rows, and a clone that diverges
/// (including one whose statement is later rolled back) can never satisfy another clone's
/// lookup. Values must never be mutated after <see cref="Set"/>.
/// </remarks>
internal sealed class TableDerivedCache
{
    private readonly object _gate = new();
    private Dictionary<object, Entry>? _entries;

    public bool TryGet<T>(object key, long rowsStamp, long rowIdsStamp, [NotNullWhen(true)] out T? value)
        where T : class
    {
        lock (_gate)
        {
            if (_entries is not null
                && _entries.TryGetValue(key, out var entry)
                && entry.RowsStamp == rowsStamp
                && entry.RowIdsStamp == rowIdsStamp
                && entry.Value is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Set(object key, long rowsStamp, long rowIdsStamp, object value)
    {
        lock (_gate)
        {
            _entries ??= [];
            _entries[key] = new Entry(rowsStamp, rowIdsStamp, value);
        }
    }

    private readonly record struct Entry(long RowsStamp, long RowIdsStamp, object Value);
}
