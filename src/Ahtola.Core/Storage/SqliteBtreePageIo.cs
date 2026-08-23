using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// Raised when an incremental b-tree mutation would require page merging,
/// rebalancing, or defragmentation beyond freelist allocate/free.
/// </summary>
/// <remarks>
/// Incremental writers implement ordinary SQLite split, sibling redistribution,
/// merge, and root-collapse maintenance. This exception is reserved for a page
/// shape that cannot be represented safely by those bounded operations, so the
/// caller can fall back to a full rewrite.
/// </remarks>
public sealed class SqliteBtreeMaintenanceRequiredException : Exception
{
    public SqliteBtreeMaintenanceRequiredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The single logical page boundary crossed by incremental b-tree mutation.
/// </summary>
/// <remarks>
/// Every page a cursor reads, every page a mutation dirties, and every page a
/// split allocates passes through this interface. It is therefore the seam at
/// which synthetic I/O failures (short writes, fsync failures, ENOSPC) can be
/// injected without the b-tree code being aware of them: a decorator that
/// throws from <see cref="ReadPage"/>, <see cref="WritePage"/>, or
/// <see cref="AllocatePage"/> exercises the exact failure paths a real device
/// produces.
/// </remarks>
public interface ISqliteBtreePageIo
{
    /// <summary>The physical page size shared by every page.</summary>
    int PageSize { get; }

    /// <summary>The portion of each page usable by SQLite.</summary>
    int UsableSpace { get; }

    /// <summary>The number of pages currently addressable, including allocations.</summary>
    uint PageCount { get; }

    /// <summary>Returns a private copy of one page image.</summary>
    byte[] ReadPage(uint pageNumber);

    /// <summary>Replaces one page image.</summary>
    void WritePage(uint pageNumber, ReadOnlySpan<byte> image);

    /// <summary>
    /// Reserves one page, preferring the SQLite freelist before growing the file.
    /// </summary>
    uint AllocatePage();

    /// <summary>
    /// Returns one page to the SQLite freelist (Turso <c>Pager::free_page</c>).
    /// </summary>
    void FreePage(uint pageNumber);
}

/// <summary>
/// An <see cref="ISqliteBtreePageIo"/> that reads through to a committed page
/// source and buffers every dirtied or allocated page in memory.
/// </summary>
/// <remarks>
/// Buffering is what lets a mutation be applied cursor-by-cursor while still
/// being published as one pager transaction whose final size is known before
/// the first frame is written. Only the buffered pages are written, so the cost
/// of a commit is proportional to the pages the mutation actually touched
/// rather than to the size of the database. When constructed with freelist
/// header state, <see cref="AllocatePage"/> mirrors Turso/SQLite
/// <c>Pager::allocate_page</c> freelist reuse and <see cref="FreePage"/> mirrors
/// <c>Pager::free_page</c>. Overflow chains released by incremental DELETE/UPDATE
/// are returned through <see cref="SqliteOverflowChainWriter.Free"/>.
/// </remarks>
public sealed class SqliteStagedBtreePageIo : ISqliteBtreePageIo
{
    private const int TrunkHeaderLength = 2 * sizeof(uint);
    private const uint PendingByte = SqlitePageLimits.PendingByte;

    private readonly Func<uint, byte[]> _readCommittedPage;
    private readonly Dictionary<uint, byte[]> _staged = [];
    private readonly HashSet<uint> _freePages = [];
    private readonly uint _committedPageCount;
    private uint _pageCount;
    private uint _firstFreelistTrunkPage;
    private uint _freelistPageCount;

    /// <summary>Creates a staging layer over a committed page source.</summary>
    public SqliteStagedBtreePageIo(
        Func<uint, byte[]> readCommittedPage,
        uint committedPageCount,
        int pageSize,
        int usableSpace,
        uint firstFreelistTrunkPage = 0,
        uint freelistPageCount = 0)
    {
        ArgumentNullException.ThrowIfNull(readCommittedPage);
        ArgumentOutOfRangeException.ThrowIfZero(committedPageCount);
        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace || usableSpace > pageSize)
            throw new ArgumentOutOfRangeException(nameof(usableSpace));
        if ((freelistPageCount == 0) != (firstFreelistTrunkPage == 0))
        {
            throw new ArgumentException(
                "SQLite freelist trunk page and freelist page count must both be zero or both non-zero.");
        }

        if (firstFreelistTrunkPage != 0
            && (firstFreelistTrunkPage < 2 || firstFreelistTrunkPage > committedPageCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstFreelistTrunkPage),
                "SQLite freelist trunk page is outside the committed page range.");
        }

        _readCommittedPage = readCommittedPage;
        _committedPageCount = committedPageCount;
        _pageCount = committedPageCount;
        _firstFreelistTrunkPage = firstFreelistTrunkPage;
        _freelistPageCount = freelistPageCount;
        PageSize = pageSize;
        UsableSpace = usableSpace;
        MaximumPageCount = SqlitePageLimits.DefaultMaximumPageCount;
        ValidateCommittedFreelist();
    }

    /// <inheritdoc />
    public int PageSize { get; }

    /// <inheritdoc />
    public int UsableSpace { get; }

    /// <inheritdoc />
    public uint PageCount => _pageCount;

    /// <summary>
    /// The growth ceiling enforced by <see cref="AllocatePage"/>, mirroring
    /// SQLite's <c>max_page_count</c> pragma and Turso
    /// <c>Pager::set_max_page_count</c>: it never drops below the pages the
    /// database already occupies and never exceeds the format ceiling.
    /// </summary>
    public uint MaximumPageCount { get; private set; }

    /// <summary>Applies a clamped growth ceiling to subsequent allocations.</summary>
    public uint SetMaximumPageCount(uint requested)
        => MaximumPageCount = SqlitePageLimits.ClampMaximumPageCount(requested, _pageCount);

    /// <summary>The page count this staging layer started from.</summary>
    public uint CommittedPageCount => _committedPageCount;

    /// <summary>Current first freelist trunk after staged allocate/free operations.</summary>
    public uint FirstFreelistTrunkPage => _firstFreelistTrunkPage;

    /// <summary>Current freelist page count after staged allocate/free operations.</summary>
    public uint FreelistPageCount => _freelistPageCount;

    /// <summary>The dirtied and newly allocated page images, keyed by page number.</summary>
    public IReadOnlyDictionary<uint, byte[]> StagedPages => _staged;

    /// <summary>The number of pages read from the committed source.</summary>
    public int CommittedPageReadCount { get; private set; }

    /// <inheritdoc />
    public byte[] ReadPage(uint pageNumber)
    {
        ValidatePageNumber(pageNumber);
        if (_staged.TryGetValue(pageNumber, out var staged))
            return (byte[])staged.Clone();

        CommittedPageReadCount++;
        var page = _readCommittedPage(pageNumber);
        if (page.Length != PageSize)
        {
            throw new InvalidDataException(
                $"SQLite page {pageNumber} has {page.Length} bytes, expected {PageSize}.");
        }

        return page;
    }

    /// <inheritdoc />
    public void WritePage(uint pageNumber, ReadOnlySpan<byte> image)
    {
        ValidatePageNumber(pageNumber);
        if (image.Length != PageSize)
            throw new ArgumentException($"A SQLite page image must be exactly {PageSize} bytes.", nameof(image));

        _staged[pageNumber] = image.ToArray();
    }

    /// <inheritdoc />
    public uint AllocatePage()
    {
        if (_freelistPageCount > 0)
            return AllocateFromFreelist();

        var pageNumber = checked(_pageCount + 1);
        if (SqlitePageLimits.IsPendingBytePage(pageNumber, PageSize))
        {
            EnsureWithinGrowthCeiling(pageNumber);
            _pageCount = pageNumber;
            _staged[pageNumber] = new byte[PageSize];
            pageNumber = checked(pageNumber + 1);
        }

        EnsureWithinGrowthCeiling(pageNumber);
        _pageCount = pageNumber;
        _staged[pageNumber] = new byte[PageSize];
        return pageNumber;
    }

    private void EnsureWithinGrowthCeiling(uint pageNumber)
    {
        if (pageNumber > MaximumPageCount)
        {
            throw new InvalidOperationException(
                $"SQLite database is full: page {pageNumber} exceeds the maximum page count {MaximumPageCount}.");
        }
    }

    /// <inheritdoc />
    public void FreePage(uint pageNumber)
    {
        if (pageNumber < 2 || pageNumber > _pageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"SQLite cannot free page {pageNumber} outside 2..{_pageCount}.");
        }
        if (_freePages.Contains(pageNumber))
            throw new InvalidOperationException($"SQLite page {pageNumber} is already on the freelist.");

        // Ensure the freed page image is staged so callers publish a zeroed leaf
        // or rewritten trunk as part of the same mutation.
        _ = ReadPage(pageNumber);
        _freePages.Add(pageNumber);
        _freelistPageCount = checked(_freelistPageCount + 1);

        if (_firstFreelistTrunkPage != 0)
        {
            var trunk = GetMutablePage(_firstFreelistTrunkPage);
            var leafCount = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(sizeof(uint)));
            var maxLeaves = (UsableSpace - TrunkHeaderLength) / sizeof(uint);
            if (leafCount < maxLeaves)
            {
                BinaryPrimitives.WriteUInt32BigEndian(trunk.AsSpan(sizeof(uint)), leafCount + 1);
                BinaryPrimitives.WriteUInt32BigEndian(
                    trunk.AsSpan(TrunkHeaderLength + ((int)leafCount * sizeof(uint))),
                    pageNumber);
                ZeroPage(pageNumber);
                return;
            }
        }

        var newTrunk = GetMutablePage(pageNumber);
        newTrunk.AsSpan().Clear();
        BinaryPrimitives.WriteUInt32BigEndian(newTrunk, _firstFreelistTrunkPage);
        BinaryPrimitives.WriteUInt32BigEndian(newTrunk.AsSpan(sizeof(uint)), 0);
        _firstFreelistTrunkPage = pageNumber;
    }

    private uint AllocateFromFreelist()
    {
        if (_firstFreelistTrunkPage == 0 || _freelistPageCount == 0)
            throw new InvalidOperationException("SQLite freelist allocation requires a non-empty freelist.");

        var trunkPageNumber = _firstFreelistTrunkPage;
        var trunk = GetMutablePage(trunkPageNumber);
        var nextTrunk = BinaryPrimitives.ReadUInt32BigEndian(trunk);
        var leafCount = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(sizeof(uint)));
        if (leafCount > 0)
        {
            var leafPageNumber = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(TrunkHeaderLength));
            if (leafPageNumber < 2 || leafPageNumber > _pageCount)
            {
                throw new InvalidDataException(
                    $"SQLite freelist leaf page {leafPageNumber} is outside 2..{_pageCount}.");
            }

            var remaining = checked((int)(leafCount - 1));
            if (remaining > 0)
            {
                Buffer.BlockCopy(
                    trunk,
                    TrunkHeaderLength + sizeof(uint),
                    trunk,
                    TrunkHeaderLength,
                    remaining * sizeof(uint));
            }

            BinaryPrimitives.WriteUInt32BigEndian(trunk.AsSpan(sizeof(uint)), (uint)remaining);
            // Clear the vacated trailing leaf slot so freelist pages stay tidy.
            BinaryPrimitives.WriteUInt32BigEndian(
                trunk.AsSpan(TrunkHeaderLength + (remaining * sizeof(uint))),
                0);
            _freelistPageCount--;
            _freePages.Remove(leafPageNumber);
            ZeroPage(leafPageNumber);
            return leafPageNumber;
        }

        // Empty trunk: reuse the trunk page itself.
        _firstFreelistTrunkPage = nextTrunk;
        _freelistPageCount--;
        _freePages.Remove(trunkPageNumber);
        if ((_freelistPageCount == 0) != (_firstFreelistTrunkPage == 0))
        {
            throw new InvalidDataException(
                "SQLite freelist became inconsistent while reusing an empty trunk page.");
        }

        trunk.AsSpan().Clear();
        return trunkPageNumber;
    }

    private void ValidateCommittedFreelist()
    {
        if (_freelistPageCount == 0)
            return;

        var leafCapacity = (UsableSpace - TrunkHeaderLength) / sizeof(uint);
        var currentTrunk = _firstFreelistTrunkPage;
        while (currentTrunk != 0)
        {
            ValidateFreePage(currentTrunk, "trunk");
            if (!_freePages.Add(currentTrunk))
                throw new InvalidDataException($"SQLite freelist contains a cycle at trunk page {currentTrunk}.");

            var trunk = _readCommittedPage(currentTrunk);
            if (trunk.Length != PageSize)
            {
                throw new InvalidDataException(
                    $"SQLite freelist trunk page {currentTrunk} has {trunk.Length} bytes, expected {PageSize}.");
            }

            var nextTrunk = BinaryPrimitives.ReadUInt32BigEndian(trunk);
            var leafCount = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(sizeof(uint)));
            if (leafCount > leafCapacity)
            {
                throw new InvalidDataException(
                    $"SQLite freelist trunk page {currentTrunk} declares {leafCount} leaves, exceeding capacity {leafCapacity}.");
            }

            for (var index = 0U; index < leafCount; index++)
            {
                var offset = checked(TrunkHeaderLength + ((int)index * sizeof(uint)));
                var leafPage = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(offset));
                ValidateFreePage(leafPage, "leaf");
                if (!_freePages.Add(leafPage))
                    throw new InvalidDataException($"SQLite freelist page {leafPage} appears more than once.");
            }

            currentTrunk = nextTrunk;
        }

        if (_freePages.Count != _freelistPageCount)
        {
            throw new InvalidDataException(
                $"SQLite freelist header declares {_freelistPageCount} pages but its trunks contain {_freePages.Count}.");
        }
    }

    private void ValidateFreePage(uint pageNumber, string kind)
    {
        if (pageNumber < 2 || pageNumber > _committedPageCount)
        {
            throw new InvalidDataException(
                $"SQLite freelist {kind} page {pageNumber} is outside the valid non-root page range 2..{_committedPageCount}.");
        }
    }

    private byte[] GetMutablePage(uint pageNumber)
    {
        ValidatePageNumber(pageNumber);
        if (_staged.TryGetValue(pageNumber, out var staged))
            return staged;

        var page = ReadPage(pageNumber);
        _staged[pageNumber] = page;
        return page;
    }

    private void ZeroPage(uint pageNumber)
    {
        if (_staged.TryGetValue(pageNumber, out var staged))
        {
            staged.AsSpan().Clear();
            return;
        }

        _staged[pageNumber] = new byte[PageSize];
    }

    private void ValidatePageNumber(uint pageNumber)
    {
        if (pageNumber == 0 || pageNumber > _pageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"SQLite page numbers must be within 1..{_pageCount}.");
        }
    }
}

/// <summary>
/// Allocates and writes a SQLite overflow chain through an
/// <see cref="ISqliteBtreePageIo"/>.
/// </summary>
public static class SqliteOverflowChainWriter
{
    /// <summary>
    /// Writes <paramref name="overflowPayload"/> across freshly allocated
    /// overflow pages and returns the first page of the chain.
    /// </summary>
    public static uint Write(ISqliteBtreePageIo pageIo, ReadOnlySpan<byte> overflowPayload)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        if (overflowPayload.Length == 0)
            throw new ArgumentException("A SQLite overflow chain requires at least one payload byte.", nameof(overflowPayload));

        var capacity = pageIo.UsableSpace - SqliteOverflowPageView.HeaderLength;
        var pageCount = (overflowPayload.Length + capacity - 1) / capacity;
        var pageNumbers = new uint[pageCount];
        for (var index = 0; index < pageCount; index++)
            pageNumbers[index] = pageIo.AllocatePage();

        for (var index = 0; index < pageCount; index++)
        {
            var offset = index * capacity;
            var length = Math.Min(capacity, overflowPayload.Length - offset);
            var next = index + 1 == pageCount ? 0U : pageNumbers[index + 1];
            pageIo.WritePage(
                pageNumbers[index],
                SqliteOverflowPageView.Create(
                    pageIo.PageSize,
                    pageIo.UsableSpace,
                    next,
                    overflowPayload.Slice(offset, length)).ToArray());
        }

        return pageNumbers[0];
    }

    /// <summary>
    /// Returns every page of an overflow chain to the freelist, matching Turso
    /// free-page handling when a cell with overflow is deleted or replaced.
    /// </summary>
    public static void Free(
        ISqliteBtreePageIo pageIo,
        uint firstOverflowPage,
        ulong overflowPayloadLength)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        if (firstOverflowPage == 0)
            throw new ArgumentOutOfRangeException(nameof(firstOverflowPage));

        var pages = new SqliteOverflowChainReader(pageIo)
            .Traverse(firstOverflowPage, overflowPayloadLength);
        // Free last-to-first so a crash mid-free leaves a still-valid shorter
        // chain rather than an orphaned prefix pointing at recycled pages.
        for (var index = pages.Count - 1; index >= 0; index--)
            pageIo.FreePage(pages[index]);
    }
}
