using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// The complete SQLite freelist represented by a database header and its trunk
/// pages.
/// </summary>
/// <remarks>
/// Managed file rewrites use this type to make the free-page partition explicit:
/// every page is either reachable from a b-tree or appears exactly once here.
/// Freelist leaf pages are zeroed when created so retired payload bytes are not
/// retained by the main database file.
/// </remarks>
public sealed class SqliteFreelist
{
    private const int TrunkHeaderLength = 2 * sizeof(uint);

    private readonly uint[] _pageNumbers;
    private readonly uint[] _trunkPageNumbers;
    private readonly uint[] _leafPageNumbers;
    private readonly SqlitePageImage[] _pageImages;

    private SqliteFreelist(
        uint firstTrunkPage,
        IEnumerable<uint> pageNumbers,
        IEnumerable<uint> trunkPageNumbers,
        IEnumerable<SqlitePageImage> pageImages)
    {
        FirstTrunkPage = firstTrunkPage;
        _pageNumbers = pageNumbers.Order().ToArray();
        _trunkPageNumbers = trunkPageNumbers.Order().ToArray();
        _leafPageNumbers = _pageNumbers.Except(_trunkPageNumbers).ToArray();
        _pageImages = pageImages.OrderBy(image => image.PageNumber).ToArray();
        PageNumbers = new ReadOnlyCollection<uint>(_pageNumbers);
        TrunkPageNumbers = new ReadOnlyCollection<uint>(_trunkPageNumbers);
        LeafPageNumbers = new ReadOnlyCollection<uint>(_leafPageNumbers);
        PageImages = new ReadOnlyCollection<SqlitePageImage>(_pageImages);
    }

    /// <summary>The first trunk page number, or zero when the freelist is empty.</summary>
    public uint FirstTrunkPage { get; }

    /// <summary>The number of pages owned by the freelist, including trunks.</summary>
    public uint PageCount => checked((uint)_pageNumbers.Length);

    /// <summary>Every trunk and leaf page in the freelist.</summary>
    public IReadOnlyList<uint> PageNumbers { get; }

    /// <summary>The pages that contain freelist trunk headers.</summary>
    public IReadOnlyList<uint> TrunkPageNumbers { get; }

    /// <summary>
    /// The freelist leaf pages that may be safely reinitialized by a complete
    /// replacement transaction.
    /// </summary>
    public IReadOnlyList<uint> LeafPageNumbers { get; }

    /// <summary>
    /// Complete zeroed leaf and initialized trunk page images for a newly built
    /// freelist. Parsed freelists expose an empty collection.
    /// </summary>
    public IReadOnlyList<SqlitePageImage> PageImages { get; }

    /// <summary>
    /// Builds a compact freelist covering every page after
    /// <paramref name="usedPageCount"/> through <paramref name="targetPageCount"/>.
    /// </summary>
    public static SqliteFreelist Create(
        uint usedPageCount,
        uint targetPageCount,
        int pageSize,
        int usableSpace)
    {
        if (usedPageCount == 0)
            throw new ArgumentOutOfRangeException(nameof(usedPageCount));
        if (targetPageCount < usedPageCount)
            throw new ArgumentOutOfRangeException(nameof(targetPageCount));
        ValidatePageLayout(pageSize, usableSpace);

        if (usedPageCount == targetPageCount)
            return new SqliteFreelist(0, [], [], []);

        var freePages = new List<uint>();
        var pendingBytePage = SqlitePageLimits.PendingBytePage(pageSize);
        for (var pageNumber = checked(usedPageCount + 1);
             pageNumber <= targetPageCount;
             pageNumber++)
        {
            if (pageNumber != pendingBytePage)
                freePages.Add(pageNumber);
            if (pageNumber == uint.MaxValue)
                break;
        }

        return CreateFromFreePages(targetPageCount, freePages, pageSize, usableSpace);
    }

    /// <summary>
    /// Builds a freelist over an explicit free-page partition. Leaf images are
    /// zeroed and trunks are initialized from scratch so a replacement transaction
    /// cannot expose retired payload bytes through its new freelist.
    /// </summary>
    /// <remarks>
    /// Callers must prove that the supplied pages are exactly the non-active pages
    /// of the target database before committing these images. This permits a
    /// bounded catalog rewrite to reuse validated former leaf pages instead of
    /// requiring all free pages to remain a suffix.
    /// </remarks>
    public static SqliteFreelist CreateFromFreePages(
        uint databasePageCount,
        IEnumerable<uint> freePageNumbers,
        int pageSize,
        int usableSpace)
    {
        ArgumentOutOfRangeException.ThrowIfZero(databasePageCount);
        ArgumentNullException.ThrowIfNull(freePageNumbers);
        ValidatePageLayout(pageSize, usableSpace);

        var freePages = new HashSet<uint>();
        var pendingBytePage = SqlitePageLimits.PendingBytePage(pageSize);
        foreach (var pageNumber in freePageNumbers)
        {
            ValidateFreePageNumber(pageNumber, databasePageCount, "supplied");
            if (pageNumber == pendingBytePage)
            {
                throw new ArgumentException(
                    $"SQLite freelist page {pageNumber} is the unusable pending-byte page and can never be freed.",
                    nameof(freePageNumbers));
            }

            if (!freePages.Add(pageNumber))
                throw new ArgumentException($"SQLite freelist page {pageNumber} was supplied more than once.", nameof(freePageNumbers));
        }

        if (freePages.Count == 0)
            return new SqliteFreelist(0, [], [], []);

        var orderedFreePages = freePages.Order().ToArray();
        var trunkLeafCapacity = (usableSpace - TrunkHeaderLength) / sizeof(uint);
        var trunkCount = checked((int)(
            ((ulong)orderedFreePages.Length + (uint)trunkLeafCapacity)
            / ((uint)trunkLeafCapacity + 1)));
        var trunks = orderedFreePages[..trunkCount];
        var leaves = orderedFreePages[trunkCount..];
        var pageImages = new List<SqlitePageImage>(orderedFreePages.Length);
        var leafOffset = 0;

        for (var trunkIndex = 0; trunkIndex < trunks.Length; trunkIndex++)
        {
            var trunk = new byte[pageSize];
            var leafCount = Math.Min(trunkLeafCapacity, leaves.Length - leafOffset);
            var nextTrunk = trunkIndex + 1 < trunks.Length
                ? trunks[trunkIndex + 1]
                : 0U;
            BinaryPrimitives.WriteUInt32BigEndian(trunk, nextTrunk);
            BinaryPrimitives.WriteUInt32BigEndian(trunk.AsSpan(sizeof(uint)), checked((uint)leafCount));

            for (var leafIndex = 0; leafIndex < leafCount; leafIndex++)
            {
                var leafPage = leaves[leafOffset++];
                BinaryPrimitives.WriteUInt32BigEndian(
                    trunk.AsSpan(TrunkHeaderLength + (leafIndex * sizeof(uint))),
                    leafPage);
                pageImages.Add(new SqlitePageImage(leafPage, new byte[pageSize]));
            }

            pageImages.Add(new SqlitePageImage(trunks[trunkIndex], trunk));
        }

        if (leafOffset != leaves.Length)
            throw new InvalidOperationException("SQLite freelist construction did not assign every free leaf.");

        return new SqliteFreelist(trunks[0], orderedFreePages, trunks, pageImages);
    }

    /// <summary>
    /// Reads and validates every SQLite freelist trunk and leaf page represented
    /// by <paramref name="header"/>.
    /// </summary>
    public static SqliteFreelist Read(
        SqliteDatabaseHeader header,
        uint databasePageCount,
        Func<uint, byte[]> readPage)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(readPage);
        ValidatePageLayout(header.PageSize, header.UsableSpace);
        if (databasePageCount == 0)
            throw new InvalidDataException("SQLite database has no page 1.");
        if (header.DatabaseSizeInPages != 0
            && header.VersionValidFor == header.ChangeCounter
            && header.DatabaseSizeInPages != databasePageCount)
        {
            throw new InvalidDataException(
                "SQLite header page count does not match the page count used to read its freelist.");
        }

        if (header.FreelistPageCount == 0)
        {
            if (header.FirstFreelistTrunkPage != 0)
                throw new InvalidDataException("SQLite empty freelist has a non-zero first trunk page.");

            return new SqliteFreelist(0, [], [], []);
        }

        if (header.FirstFreelistTrunkPage < 2 || header.FirstFreelistTrunkPage > databasePageCount)
            throw new InvalidDataException("SQLite freelist first trunk page is outside the valid page range.");

        var trunkLeafCapacity = (header.UsableSpace - TrunkHeaderLength) / sizeof(uint);
        var pages = new HashSet<uint>();
        var trunks = new List<uint>();
        var currentTrunk = header.FirstFreelistTrunkPage;

        while (currentTrunk != 0)
        {
            ValidateFreePageNumber(currentTrunk, databasePageCount, "trunk");
            if (!pages.Add(currentTrunk))
                throw new InvalidDataException($"SQLite freelist contains a cycle at trunk page {currentTrunk}.");
            trunks.Add(currentTrunk);

            var trunk = readPage(currentTrunk);
            if (trunk.Length != header.PageSize)
            {
                throw new InvalidDataException(
                    $"SQLite freelist trunk page {currentTrunk} has {trunk.Length} bytes, expected {header.PageSize}.");
            }

            var nextTrunk = BinaryPrimitives.ReadUInt32BigEndian(trunk);
            var leafCount = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(sizeof(uint)));
            if (leafCount > trunkLeafCapacity)
            {
                throw new InvalidDataException(
                    $"SQLite freelist trunk page {currentTrunk} declares {leafCount} leaves, exceeding capacity {trunkLeafCapacity}.");
            }

            for (var leaf = 0U; leaf < leafCount; leaf++)
            {
                var leafOffset = checked(TrunkHeaderLength + ((int)leaf * sizeof(uint)));
                var leafPage = BinaryPrimitives.ReadUInt32BigEndian(trunk.AsSpan(leafOffset));
                ValidateFreePageNumber(leafPage, databasePageCount, "leaf");
                if (!pages.Add(leafPage))
                {
                    throw new InvalidDataException(
                        $"SQLite freelist page {leafPage} appears more than once.");
                }
            }

            currentTrunk = nextTrunk;
        }

        if (pages.Count != header.FreelistPageCount)
        {
            throw new InvalidDataException(
                $"SQLite freelist header declares {header.FreelistPageCount} pages but its trunks contain {pages.Count}.");
        }

        return new SqliteFreelist(header.FirstFreelistTrunkPage, pages, trunks, []);
    }

    private static void ValidateFreePageNumber(uint pageNumber, uint databasePageCount, string kind)
    {
        if (pageNumber < 2 || pageNumber > databasePageCount)
        {
            throw new InvalidDataException(
                $"SQLite freelist {kind} page {pageNumber} is outside the valid non-root page range 2..{databasePageCount}.");
        }
    }

    private static void ValidatePageLayout(int pageSize, int usableSpace)
    {
        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace || usableSpace > pageSize)
            throw new ArgumentOutOfRangeException(nameof(usableSpace));
        if (pageSize < SqlitePageSize.Minimum || pageSize > SqlitePageSize.Maximum)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if ((usableSpace - TrunkHeaderLength) / sizeof(uint) == 0)
            throw new ArgumentOutOfRangeException(nameof(usableSpace));
    }
}
