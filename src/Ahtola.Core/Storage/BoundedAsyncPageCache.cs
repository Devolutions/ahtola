namespace Ahtola.Core.Storage;

/// <summary>
/// A capacity-capped, LRU-evicting page cache wrapping an
/// <see cref="IAsyncBoundedReadSnapshot"/>, used by bounded asynchronous b-tree
/// traversals. Never grows past its configured capacity: a page still pinned on a live
/// traversal's ancestor stack can never be evicted, so a genuinely full cache with nothing
/// evictable throws <see cref="SqliteBoundedPageBudgetExceededException"/> instead of either
/// silently exceeding the cap or corrupting the in-progress traversal.
/// </summary>
/// <remarks>
/// An ascending full scan visits pages in rowid order; an exact rowid lookup retains only its
/// search path and any overflow pages. Both paths use this cache to enforce the resident-page
/// budget rather than relying on a presumed bound from their traversal shape.
/// </remarks>
internal sealed class BoundedAsyncPageCache : IAsyncSqliteBtreePageIo
{
    private readonly IAsyncBoundedReadSnapshot _transaction;
    private readonly int _capacity;
    private readonly LinkedList<uint> _lruOrder = new();
    private readonly Dictionary<uint, LinkedListNode<uint>> _nodesByPage = [];
    private readonly Dictionary<uint, byte[]> _pagesByNumber = [];
    private readonly HashSet<uint> _pinned = [];

    public BoundedAsyncPageCache(
        IAsyncBoundedReadSnapshot transaction,
        int usableSpace,
        int capacity)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _transaction = transaction;
        UsableSpace = usableSpace;
        _capacity = capacity;
    }

    public int UsableSpace { get; }

    public uint PageCount => _transaction.PageCount;

    /// <summary>The number of distinct pages currently resident.</summary>
    internal int ResidentPageCount => _pagesByNumber.Count;

    /// <summary>The configured hard capacity, exposed for tests.</summary>
    internal int Capacity => _capacity;

    /// <summary>
    /// Marks a page as currently required by a live traversal's ancestor stack, so it cannot
    /// be evicted to make room for another page. Callers must <see cref="Unpin"/> a page once
    /// their traversal backtracks past it.
    /// </summary>
    internal void Pin(uint pageNumber) => _pinned.Add(pageNumber);

    /// <summary>Releases a page pinned by <see cref="Pin"/>.</summary>
    internal void Unpin(uint pageNumber) => _pinned.Remove(pageNumber);

    public async ValueTask<byte[]> ReadPageAsync(
        uint pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (_pagesByNumber.TryGetValue(pageNumber, out var cached))
        {
            Touch(pageNumber);
            return cached;
        }

        if (_pagesByNumber.Count >= _capacity)
            EvictOneOrThrow(pageNumber);

        var page = await _transaction.ReadPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        _pagesByNumber[pageNumber] = page;
        _nodesByPage[pageNumber] = _lruOrder.AddLast(pageNumber);
        return page;
    }

    private void Touch(uint pageNumber)
    {
        if (!_nodesByPage.TryGetValue(pageNumber, out var node))
            return;

        _lruOrder.Remove(node);
        _nodesByPage[pageNumber] = _lruOrder.AddLast(pageNumber);
    }

    private void EvictOneOrThrow(uint pageNumberBeingFetched)
    {
        for (var node = _lruOrder.First; node is not null; node = node.Next)
        {
            if (_pinned.Contains(node.Value))
                continue;

            _pagesByNumber.Remove(node.Value);
            _nodesByPage.Remove(node.Value);
            _lruOrder.Remove(node);
            return;
        }

        throw new SqliteBoundedPageBudgetExceededException(
            $"Reading page {pageNumberBeingFetched} would require more than the configured page "
            + $"budget of {_capacity} resident pages; every currently resident page is still "
            + "pinned by an in-progress bounded traversal.");
    }
}
