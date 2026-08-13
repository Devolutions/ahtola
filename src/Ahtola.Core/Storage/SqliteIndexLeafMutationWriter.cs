using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>Immutable input for one SQLite index-leaf record.</summary>
public sealed class SqliteIndexLeafCellInput
{
    private readonly byte[] _record;

    /// <summary>Copies one complete SQLite record payload.</summary>
    public SqliteIndexLeafCellInput(ReadOnlySpan<byte> record)
    {
        _record = record.ToArray();
    }

    /// <summary>The immutable complete SQLite record payload.</summary>
    public ReadOnlyMemory<byte> Record => _record;
}

/// <summary>
/// A complete index-leaf replacement mutation, including newly allocated
/// overflow-page images.
/// </summary>
/// <remarks>
/// The mutation is deliberately not a B-tree insertion. It replaces exactly
/// one complete index-leaf page and never creates parents, splits leaves, or
/// balances a tree.
/// </remarks>
public sealed class SqliteIndexLeafMutation
{
    private readonly byte[] _indexLeafPage;
    private readonly SqlitePageImage[] _overflowPages;

    internal SqliteIndexLeafMutation(
        uint sourceDatabaseSizeInPages,
        uint targetDatabaseSizeInPages,
        uint indexLeafPageNumber,
        int pageSize,
        ReadOnlySpan<byte> indexLeafPage,
        IEnumerable<SqlitePageImage> overflowPages)
    {
        if (sourceDatabaseSizeInPages == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDatabaseSizeInPages));
        if (targetDatabaseSizeInPages < sourceDatabaseSizeInPages)
            throw new ArgumentOutOfRangeException(nameof(targetDatabaseSizeInPages));
        if (indexLeafPageNumber < 2)
            throw new ArgumentOutOfRangeException(nameof(indexLeafPageNumber), "SQLite page 1 is the sqlite_schema table root.");
        if (indexLeafPage.Length != pageSize)
            throw new ArgumentException("The index-leaf image must be exactly one page.", nameof(indexLeafPage));
        ArgumentNullException.ThrowIfNull(overflowPages);

        SourceDatabaseSizeInPages = sourceDatabaseSizeInPages;
        TargetDatabaseSizeInPages = targetDatabaseSizeInPages;
        IndexLeafPageNumber = indexLeafPageNumber;
        PageSize = pageSize;
        _indexLeafPage = indexLeafPage.ToArray();
        _overflowPages = overflowPages.ToArray();

        ValidateImages();
        OverflowPages = new ReadOnlyCollection<SqlitePageImage>(_overflowPages);
    }

    /// <summary>The page count used when this mutation was prepared.</summary>
    public uint SourceDatabaseSizeInPages { get; }

    /// <summary>The page count declared by the committed mutation.</summary>
    public uint TargetDatabaseSizeInPages { get; }

    /// <summary>The page whose complete index-leaf image is replaced.</summary>
    public uint IndexLeafPageNumber { get; }

    /// <summary>The physical size of every page image.</summary>
    public int PageSize { get; }

    /// <summary>The immutable packed index-leaf image.</summary>
    public ReadOnlyMemory<byte> IndexLeafPage => _indexLeafPage;

    /// <summary>The immutable overflow-page images needed by the index cells.</summary>
    public IReadOnlyList<SqlitePageImage> OverflowPages { get; }

    /// <summary>
    /// Appends this mutation as one WAL transaction. The index-leaf frame is
    /// written last and carries the commit marker after every overflow frame.
    /// </summary>
    public long AppendToWal(SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (wal.PageSize != PageSize)
            throw new InvalidOperationException("SQLite WAL and index-leaf mutation page sizes do not match.");

        var recovery = wal.ScanRecovery();
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || recovery.LastValidFrameNumber != 0)
        {
            throw new InvalidOperationException(
                "This mutation writer requires an empty, recovered WAL; WAL overlay and checkpoint coordination are outside this layer.");
        }

        foreach (var overflowPage in _overflowPages)
            wal.AppendFrame(overflowPage.PageNumber, overflowPage.Page.Span);

        return wal.AppendFrame(IndexLeafPageNumber, _indexLeafPage, TargetDatabaseSizeInPages);
    }

    /// <summary>
    /// Installs the page images after the caller has made the mutation durable
    /// through its WAL/checkpoint lifecycle.
    /// </summary>
    /// <remarks>
    /// This is a checkpoint-style page installation primitive, not a
    /// crash-atomic multi-page transaction. Commit through <see cref="AppendToWal"/>
    /// before calling this method when crash atomicity is required.
    /// </remarks>
    public void ApplyTo(SqlitePageStore pageStore)
    {
        ArgumentNullException.ThrowIfNull(pageStore);
        if (pageStore.PageSize != PageSize)
            throw new InvalidOperationException("SQLite page store and index-leaf mutation page sizes do not match.");
        if (pageStore.PageCount != SourceDatabaseSizeInPages)
        {
            throw new InvalidOperationException(
                "SQLite page-store size changed after this mutation was prepared.");
        }

        var appendedImages = GetAllImages()
            .Where(image => image.PageNumber > SourceDatabaseSizeInPages)
            .OrderBy(image => image.PageNumber);
        foreach (var image in appendedImages)
            pageStore.WritePage(image.PageNumber, image.Page.Span);

        foreach (var overflowPage in _overflowPages.Where(page => page.PageNumber <= SourceDatabaseSizeInPages))
            pageStore.WritePage(overflowPage.PageNumber, overflowPage.Page.Span);

        if (IndexLeafPageNumber <= SourceDatabaseSizeInPages)
            pageStore.WritePage(IndexLeafPageNumber, _indexLeafPage);
    }

    private IEnumerable<SqlitePageImage> GetAllImages()
    {
        foreach (var overflowPage in _overflowPages)
            yield return overflowPage;
        yield return new SqlitePageImage(IndexLeafPageNumber, _indexLeafPage);
    }

    private void ValidateImages()
    {
        var seen = new HashSet<uint>();
        foreach (var overflowPage in _overflowPages)
        {
            if (overflowPage.Page.Length != PageSize)
                throw new ArgumentException("Every SQLite overflow image must be exactly one page.", nameof(OverflowPages));
            if (!seen.Add(overflowPage.PageNumber))
                throw new ArgumentException("SQLite mutation images cannot target the same page twice.", nameof(OverflowPages));
        }

        if (!seen.Add(IndexLeafPageNumber))
            throw new ArgumentException("The index-leaf image overlaps an overflow image.", nameof(IndexLeafPageNumber));
        if (IndexLeafPageNumber > TargetDatabaseSizeInPages)
            throw new ArgumentException("The index-leaf image is beyond the target database size.", nameof(IndexLeafPageNumber));

        if (TargetDatabaseSizeInPages > SourceDatabaseSizeInPages)
        {
            for (var pageNumber = SourceDatabaseSizeInPages + 1;
                 pageNumber <= TargetDatabaseSizeInPages;
                 pageNumber++)
            {
                if (!seen.Contains(pageNumber))
                {
                    throw new ArgumentException(
                        $"SQLite mutation is missing appended page {pageNumber}.",
                        nameof(TargetDatabaseSizeInPages));
                }

                if (pageNumber == uint.MaxValue)
                    break;
            }
        }

        foreach (var pageNumber in seen)
        {
            if (pageNumber > TargetDatabaseSizeInPages)
            {
                throw new ArgumentException(
                    $"SQLite mutation image page {pageNumber} is beyond the target database size.",
                    nameof(TargetDatabaseSizeInPages));
            }
        }
    }
}

/// <summary>
/// Builds complete replacement images for one SQLite index-leaf page and its
/// required overflow chain.
/// </summary>
/// <remarks>
/// This writer supports only BINARY ascending records, whole-page creation and
/// replacement, append-only allocation, and an empty recovered WAL. It has no
/// search, insert, delete, split, parent update, balancing, or freelist work.
/// Replacing a page that already owns overflow cells is rejected because this
/// layer cannot safely return those pages to SQLite's freelist.
/// </remarks>
public sealed class SqliteIndexLeafMutationWriter
{
    private readonly SqlitePageStore _pageStore;
    private readonly ISqlitePageAllocator _allocator;
    private readonly SqliteIndexRecordComparer _recordComparer;

    /// <summary>Creates a writer that prepares mutations against <paramref name="pageStore"/>.</summary>
    public SqliteIndexLeafMutationWriter(SqlitePageStore pageStore, ISqlitePageAllocator allocator)
    {
        _pageStore = pageStore ?? throw new ArgumentNullException(nameof(pageStore));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _recordComparer = new SqliteIndexRecordComparer(_pageStore.Header.TextEncoding);
    }

    /// <summary>
    /// Creates a new index-leaf page using an allocator-selected page number.
    /// The page is only reserved and represented in the returned mutation.
    /// </summary>
    public SqliteIndexLeafMutation CreatePage(IEnumerable<SqliteIndexLeafCellInput> cells)
    {
        var inputs = SnapshotInputs(cells);
        var sourcePageCount = _pageStore.PageCount;
        ValidateFits(inputs);

        var indexPage = _allocator.Allocate();
        ValidateIndexPageAllocation(indexPage);
        var materialized = MaterializeCells(inputs);
        var targetPageCount = CalculateTargetPageCount(sourcePageCount, indexPage, materialized);
        var indexPageImage = BuildIndexPage(materialized, new byte[_pageStore.PageSize]);

        return new SqliteIndexLeafMutation(
            sourcePageCount,
            targetPageCount,
            indexPage.PageNumber,
            _pageStore.PageSize,
            indexPageImage,
            materialized.OverflowPages);
    }

    /// <summary>
    /// Replaces an existing non-root index-leaf page that has no existing
    /// overflow cells. The page is fully parsed before any new numbers are
    /// reserved.
    /// </summary>
    public SqliteIndexLeafMutation RewritePage(
        uint pageNumber,
        IEnumerable<SqliteIndexLeafCellInput> cells)
    {
        var sourcePageCount = _pageStore.PageCount;
        if (pageNumber < 2 || pageNumber > sourcePageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Index leaf page number is out of range for a database of {sourcePageCount} page(s).");
        }

        var template = _pageStore.ReadPage(pageNumber);
        var existing = SqliteIndexLeafPageView.Parse(
            template,
            _pageStore.Header.UsableSpace,
            _pageStore.Header.TextEncoding,
            overflowReader: new SqliteOverflowChainReader(_pageStore));
        if (existing.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
        {
            throw new NotSupportedException(
                "Rewriting an index leaf with existing overflow cells requires SQLite freelist reclamation, which this storage layer does not implement.");
        }

        var inputs = SnapshotInputs(cells);
        ValidateFits(inputs);

        var materialized = MaterializeCells(inputs);
        var targetPageCount = CalculateTargetPageCount(sourcePageCount, allocation: null, materialized);
        var indexPageImage = BuildIndexPage(materialized, template);

        return new SqliteIndexLeafMutation(
            sourcePageCount,
            targetPageCount,
            pageNumber,
            _pageStore.PageSize,
            indexPageImage,
            materialized.OverflowPages);
    }

    private void ValidateFits(IReadOnlyList<SqliteIndexLeafCellInput> inputs)
    {
        var builder = new SqliteIndexLeafPageBuilder(
            _pageStore.PageSize,
            _pageStore.Header.UsableSpace,
            _recordComparer);
        foreach (var input in inputs)
        {
            var record = input.Record.Span;
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)record.Length),
                _pageStore.Header.UsableSpace);
            var localPayload = record[..layout.LocalPayloadLength];
            builder.Append(SqliteIndexLeafCell.Create(
                checked((ulong)record.Length),
                localPayload,
                layout.UsesOverflow ? 1U : null,
                _pageStore.Header.UsableSpace),
                record);
        }

        _ = builder.Build();
    }

    private MaterializedCells MaterializeCells(IReadOnlyList<SqliteIndexLeafCellInput> inputs)
    {
        var cells = new List<MaterializedCell>(inputs.Count);
        var overflowPages = new List<SqlitePageImage>();
        var allocationDatabaseSizeInPages = 0U;
        var usableSpace = _pageStore.Header.UsableSpace;
        var overflowCapacity = usableSpace - SqliteOverflowPageView.HeaderLength;

        foreach (var input in inputs)
        {
            var record = input.Record.Span;
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)record.Length),
                usableSpace);
            if (!layout.UsesOverflow)
            {
                cells.Add(new MaterializedCell(
                    SqliteIndexLeafCell.Create(record, usableSpace),
                    input.Record.ToArray()));
                continue;
            }

            var remainingOverflowBytes = record.Length - layout.LocalPayloadLength;
            var allocations = new List<SqlitePageAllocation>();
            while (remainingOverflowBytes > 0)
            {
                var allocation = _allocator.Allocate();
                ValidateIndexPageAllocation(allocation);
                if (allocation.DatabaseSizeInPages > allocationDatabaseSizeInPages)
                    allocationDatabaseSizeInPages = allocation.DatabaseSizeInPages;
                allocations.Add(allocation);
                remainingOverflowBytes -= Math.Min(overflowCapacity, remainingOverflowBytes);
            }

            var overflowOffset = layout.LocalPayloadLength;
            for (var index = 0; index < allocations.Count; index++)
            {
                var bytesOnPage = Math.Min(overflowCapacity, record.Length - overflowOffset);
                var nextPageNumber = index + 1 < allocations.Count
                    ? allocations[index + 1].PageNumber
                    : 0U;
                overflowPages.Add(new SqlitePageImage(
                    allocations[index].PageNumber,
                    SqliteOverflowPageView.Create(
                        _pageStore.PageSize,
                        usableSpace,
                        nextPageNumber,
                        record.Slice(overflowOffset, bytesOnPage)).ToArray()));
                overflowOffset += bytesOnPage;
            }

            cells.Add(new MaterializedCell(
                SqliteIndexLeafCell.Create(
                    checked((ulong)record.Length),
                    record[..layout.LocalPayloadLength],
                    allocations[0].PageNumber,
                    usableSpace),
                input.Record.ToArray()));
        }

        return new MaterializedCells(cells, overflowPages, allocationDatabaseSizeInPages);
    }

    private uint CalculateTargetPageCount(
        uint sourcePageCount,
        SqlitePageAllocation? allocation,
        MaterializedCells materialized)
    {
        var targetPageCount = allocation?.DatabaseSizeInPages ?? sourcePageCount;
        if (materialized.AllocationDatabaseSizeInPages > targetPageCount)
            targetPageCount = materialized.AllocationDatabaseSizeInPages;

        foreach (var overflowPage in materialized.OverflowPages)
        {
            if (overflowPage.PageNumber > targetPageCount)
                targetPageCount = overflowPage.PageNumber;
        }

        return Math.Max(sourcePageCount, targetPageCount);
    }

    private byte[] BuildIndexPage(MaterializedCells materialized, byte[] destination)
    {
        if (destination.Length != _pageStore.PageSize)
            throw new ArgumentException("SQLite index page template has an invalid page size.", nameof(destination));

        var builder = new SqliteIndexLeafPageBuilder(
            _pageStore.PageSize,
            _pageStore.Header.UsableSpace,
            _recordComparer);
        foreach (var cell in materialized.Cells)
            builder.Append(cell.Cell, cell.Record);

        builder.WriteTo(destination);
        return destination;
    }

    private static SqliteIndexLeafCellInput[] SnapshotInputs(IEnumerable<SqliteIndexLeafCellInput> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var snapshot = new List<SqliteIndexLeafCellInput>();
        foreach (var cell in cells)
        {
            ArgumentNullException.ThrowIfNull(cell);
            snapshot.Add(cell);
        }

        return [.. snapshot];
    }

    private static void ValidateIndexPageAllocation(SqlitePageAllocation allocation)
    {
        if (allocation.PageNumber == 1)
        {
            throw new InvalidOperationException(
                "Page 1 contains the sqlite_schema table root and cannot be allocated for an index leaf or overflow page.");
        }
    }

    private sealed record MaterializedCell(SqliteIndexLeafCell Cell, byte[] Record);

    private sealed record MaterializedCells(
        IReadOnlyList<MaterializedCell> Cells,
        IReadOnlyList<SqlitePageImage> OverflowPages,
        uint AllocationDatabaseSizeInPages);
}
