namespace Ahtola.Core.Storage;

/// <summary>
/// A pager-owned bounded LRU cache for clean main-database page images scoped to one committed view.
/// </summary>
/// <remarks>
/// WAL-overlay and transaction pages are deliberately excluded: the overlay is
/// recovery state and transaction images are not durable. Cached arrays never
/// leave the pager, so callers cannot retain or mutate an evicted image.
/// </remarks>
internal sealed class SqlitePagerReadCache
{
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly LinkedList<uint> _leastToMostRecent = [];

    public SqlitePagerReadCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _entries.Count;

    public bool TryGetValue(uint pageNumber, long viewGeneration, out byte[] page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(viewGeneration);
        if (_entries.TryGetValue(pageNumber, out var entry))
        {
            if (entry.ViewGeneration != viewGeneration)
            {
                Remove(pageNumber);
                page = null!;
                return false;
            }

            _leastToMostRecent.Remove(entry.RecencyNode);
            _leastToMostRecent.AddLast(entry.RecencyNode);
            page = entry.Page;
            return true;
        }

        page = null!;
        return false;
    }

    public void Add(uint pageNumber, long viewGeneration, byte[] page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(viewGeneration);
        ArgumentNullException.ThrowIfNull(page);

        Remove(pageNumber);
        if (_entries.Count == Capacity)
        {
            var leastRecent = _leastToMostRecent.First
                ?? throw new InvalidOperationException("SQLite pager read-cache recency state is inconsistent.");
            if (!_entries.Remove(leastRecent.Value))
                throw new InvalidOperationException("SQLite pager read-cache entry state is inconsistent.");
            _leastToMostRecent.RemoveFirst();
        }

        var recencyNode = _leastToMostRecent.AddLast(pageNumber);
        _entries.Add(pageNumber, new Entry(page, viewGeneration, recencyNode));
    }

    public void Remove(uint pageNumber)
    {
        if (!_entries.Remove(pageNumber, out var entry))
            return;

        _leastToMostRecent.Remove(entry.RecencyNode);
    }

    public void Clear()
    {
        _entries.Clear();
        _leastToMostRecent.Clear();
    }

    private sealed record Entry(byte[] Page, long ViewGeneration, LinkedListNode<uint> RecencyNode);
}
