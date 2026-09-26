using System.Collections.Immutable;

namespace Ahtola.Core;

/// <summary>
/// The existing rowids of a table as an immutable set plus their maximum, valid for exactly one
/// row-store state (see <see cref="EmbeddedTable.GetRowIdSet"/>).
/// </summary>
internal sealed record RowIdSet(
    long LineageId,
    long Revision,
    int Count,
    ImmutableHashSet<long> Ids,
    long Max);

/// <summary>
/// The rowids an INSERT statement must treat as taken: the table's rowids when the statement
/// started, plus the ones it has allocated since, minus any allocation it gave back.
/// </summary>
/// <remarks>
/// It used to be a <see cref="HashSet{T}"/> copied from every rowid in the table at the start of
/// every INSERT statement. The base is now the table's cached immutable <see cref="RowIdSet"/>,
/// so a statement pays only for the rowids it touches.
/// </remarks>
internal sealed class RowIdAllocationSet(ImmutableHashSet<long> taken)
{
    private readonly HashSet<long> _added = [];
    private readonly HashSet<long> _removed = [];
    private ImmutableHashSet<long> _base = taken;

    public bool Contains(long rowId)
        => _added.Contains(rowId) || (_base.Contains(rowId) && !_removed.Contains(rowId));

    public bool Add(long rowId)
    {
        if (Contains(rowId))
            return false;
        if (!_removed.Remove(rowId))
            _added.Add(rowId);
        return true;
    }

    public bool Remove(long rowId)
        => _added.Remove(rowId) || (_base.Contains(rowId) && _removed.Add(rowId));

    /// <summary>Replaces the whole set with <paramref name="taken"/>.</summary>
    public void Reset(ImmutableHashSet<long> taken)
    {
        _base = taken;
        _added.Clear();
        _removed.Clear();
    }

    /// <summary>Adds every rowid in <paramref name="taken"/>, keeping everything already present.</summary>
    public void UnionWith(ImmutableHashSet<long> taken)
    {
        var merged = taken.ToBuilder();
        foreach (var rowId in _base)
        {
            if (!_removed.Contains(rowId))
                merged.Add(rowId);
        }

        merged.UnionWith(_added);
        Reset(merged.ToImmutable());
    }
}
