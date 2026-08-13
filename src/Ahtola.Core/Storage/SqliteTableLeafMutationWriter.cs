using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// Immutable input for one table-leaf cell whose payload is materialized by a
/// <see cref="SqliteTableLeafMutationWriter"/>.
/// </summary>
public sealed class SqliteTableLeafCellInput
{
    private readonly byte[] _payload;

    /// <summary>Copies <paramref name="payload"/> for a cell with <paramref name="rowId"/>.</summary>
    public SqliteTableLeafCellInput(long rowId, ReadOnlySpan<byte> payload)
    {
        RowId = rowId;
        _payload = payload.ToArray();
    }

    /// <summary>The SQLite rowid.</summary>
    public long RowId { get; }

    /// <summary>The immutable logical record payload.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;
}

/// <summary>An immutable SQLite page image assigned to one page number.</summary>
public sealed class SqlitePageImage
{
    private readonly byte[] _page;

    /// <summary>Copies a physical page image assigned to <paramref name="pageNumber"/>.</summary>
    public SqlitePageImage(uint pageNumber, ReadOnlySpan<byte> page)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");

        PageNumber = pageNumber;
        _page = page.ToArray();
    }

    /// <summary>The 1-based target page number.</summary>
    public uint PageNumber { get; }

    /// <summary>The immutable physical page bytes.</summary>
    public ReadOnlyMemory<byte> Page => _page;

    /// <summary>Returns a copy of the physical page bytes.</summary>
    public byte[] ToArray() => _page.ToArray();
}

/// <summary>
/// A complete table-leaf page mutation, including any overflow-page images.
/// </summary>
/// <remarks>
/// The mutation is deliberately not a B-tree insertion. It replaces exactly
/// one complete table-leaf page and never creates parents, splits leaves, or
/// balances a tree.
/// </remarks>
public sealed class SqliteTableLeafMutation
{
    private readonly byte[] _tableLeafPage;
    private readonly SqlitePageImage[] _overflowPages;

    internal SqliteTableLeafMutation(
        uint sourceDatabaseSizeInPages,
        uint targetDatabaseSizeInPages,
        uint tableLeafPageNumber,
        int pageSize,
        ReadOnlySpan<byte> tableLeafPage,
        IEnumerable<SqlitePageImage> overflowPages)
    {
        if (sourceDatabaseSizeInPages == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDatabaseSizeInPages));
        if (targetDatabaseSizeInPages < sourceDatabaseSizeInPages)
            throw new ArgumentOutOfRangeException(nameof(targetDatabaseSizeInPages));
        if (tableLeafPageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(tableLeafPageNumber));
        if (tableLeafPage.Length != pageSize)
            throw new ArgumentException("The table-leaf image must be exactly one page.", nameof(tableLeafPage));
        ArgumentNullException.ThrowIfNull(overflowPages);

        SourceDatabaseSizeInPages = sourceDatabaseSizeInPages;
        TargetDatabaseSizeInPages = targetDatabaseSizeInPages;
        TableLeafPageNumber = tableLeafPageNumber;
        PageSize = pageSize;
        _tableLeafPage = tableLeafPage.ToArray();
        _overflowPages = overflowPages.ToArray();

        ValidateImages();
        OverflowPages = new ReadOnlyCollection<SqlitePageImage>(_overflowPages);
    }

    /// <summary>The page count used when this mutation was prepared.</summary>
    public uint SourceDatabaseSizeInPages { get; }

    /// <summary>The page count that the committed mutation declares.</summary>
    public uint TargetDatabaseSizeInPages { get; }

    /// <summary>The page whose complete table-leaf image is replaced.</summary>
    public uint TableLeafPageNumber { get; }

    /// <summary>The physical size of every page image.</summary>
    public int PageSize { get; }

    /// <summary>The immutable packed table-leaf image.</summary>
    public ReadOnlyMemory<byte> TableLeafPage => _tableLeafPage;

    /// <summary>The immutable overflow-page images needed by the table-leaf cells.</summary>
    public IReadOnlyList<SqlitePageImage> OverflowPages { get; }

    /// <summary>
    /// Appends this mutation as one WAL transaction. The table-leaf frame is
    /// written last and carries the commit marker after every overflow frame.
    /// </summary>
    public long AppendToWal(SqliteWalFile wal)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (wal.PageSize != PageSize)
            throw new InvalidOperationException("SQLite WAL and table-leaf mutation page sizes do not match.");

        var recovery = wal.ScanRecovery();
        if (recovery.StopReason != SqliteWalRecoveryStopReason.EndOfFile
            || recovery.LastValidFrameNumber != 0)
        {
            throw new InvalidOperationException(
                "This mutation writer requires an empty, recovered WAL; WAL overlay and checkpoint coordination are outside this layer.");
        }

        foreach (var overflowPage in _overflowPages)
            wal.AppendFrame(overflowPage.PageNumber, overflowPage.Page.Span);

        return wal.AppendFrame(TableLeafPageNumber, _tableLeafPage, TargetDatabaseSizeInPages);
    }

    /// <summary>
    /// Installs the page images into the page store after they have been made
    /// durable through the caller's WAL/checkpoint lifecycle.
    /// </summary>
    /// <remarks>
    /// This is a checkpoint-style page installation primitive, not a
    /// crash-atomic multi-page transaction. Callers that need crash atomicity
    /// must commit the mutation through <see cref="AppendToWal"/> first.
    /// </remarks>
    public void ApplyTo(SqlitePageStore pageStore)
    {
        ArgumentNullException.ThrowIfNull(pageStore);
        if (pageStore.PageSize != PageSize)
            throw new InvalidOperationException("SQLite page store and table-leaf mutation page sizes do not match.");
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

        if (TableLeafPageNumber <= SourceDatabaseSizeInPages)
            pageStore.WritePage(TableLeafPageNumber, _tableLeafPage);
    }

    private IEnumerable<SqlitePageImage> GetAllImages()
    {
        foreach (var overflowPage in _overflowPages)
            yield return overflowPage;
        yield return new SqlitePageImage(TableLeafPageNumber, _tableLeafPage);
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

        if (!seen.Add(TableLeafPageNumber))
            throw new ArgumentException("The table-leaf image overlaps an overflow image.", nameof(TableLeafPageNumber));
        if (TableLeafPageNumber > TargetDatabaseSizeInPages)
            throw new ArgumentException("The table-leaf image is beyond the target database size.", nameof(TableLeafPageNumber));

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
/// Builds complete replacement images for one SQLite table-leaf page and its
/// required overflow chain.
/// </summary>
/// <remarks>
/// This writer has no insert, split, parent-update, or balancing operation. Its
/// create and rewrite methods are explicit whole-page mutations only. Rewriting
/// a page that owns overflow cells is rejected because this layer cannot safely
/// return those pages to SQLite's freelist.
/// </remarks>
public sealed class SqliteTableLeafMutationWriter
{
    private readonly SqlitePageStore _pageStore;
    private readonly ISqlitePageAllocator _allocator;

    /// <summary>Creates a writer that prepares mutations against <paramref name="pageStore"/>.</summary>
    public SqliteTableLeafMutationWriter(SqlitePageStore pageStore, ISqlitePageAllocator allocator)
    {
        _pageStore = pageStore ?? throw new ArgumentNullException(nameof(pageStore));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>
    /// Creates a new table-leaf page using an allocator-selected page number.
    /// The page is only reserved and represented in the returned mutation.
    /// </summary>
    public SqliteTableLeafMutation CreatePage(IEnumerable<SqliteTableLeafCellInput> cells)
    {
        var inputs = SnapshotInputs(cells);
        var sourcePageCount = _pageStore.PageCount;
        ValidateFits(inputs, isFirstPage: false);

        var tablePage = _allocator.Allocate();
        ValidateDataPageAllocation(tablePage);
        var materialized = MaterializeCells(inputs);
        var targetPageCount = CalculateTargetPageCount(sourcePageCount, tablePage, materialized);
        var tablePageImage = BuildTablePage(
            inputs,
            materialized.Cells,
            new byte[_pageStore.PageSize],
            isFirstPage: false,
            targetPageCount);

        return new SqliteTableLeafMutation(
            sourcePageCount,
            targetPageCount,
            tablePage.PageNumber,
            _pageStore.PageSize,
            tablePageImage,
            materialized.OverflowPages);
    }

    /// <summary>
    /// Replaces the complete contents of an existing table-leaf page. The
    /// existing page must parse cleanly before any new page numbers are reserved.
    /// </summary>
    public SqliteTableLeafMutation RewritePage(
        uint pageNumber,
        IEnumerable<SqliteTableLeafCellInput> cells)
    {
        var sourcePageCount = _pageStore.PageCount;
        if (pageNumber == 0 || pageNumber > sourcePageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                pageNumber,
                $"Page number is out of range for a database of {sourcePageCount} page(s).");
        }

        var isFirstPage = pageNumber == 1;
        var template = _pageStore.ReadPage(pageNumber);
        var existing = SqliteTableLeafPageView.Parse(
            template,
            _pageStore.Header.UsableSpace,
            isFirstPage);
        if (existing.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
        {
            throw new NotSupportedException(
                "Rewriting a table leaf with existing overflow cells requires SQLite freelist reclamation, which this storage layer does not implement.");
        }

        var inputs = SnapshotInputs(cells);
        ValidateFits(inputs, isFirstPage);

        var materialized = MaterializeCells(inputs);
        var targetPageCount = CalculateTargetPageCount(sourcePageCount, allocation: null, materialized);
        var tablePageImage = BuildTablePage(inputs, materialized.Cells, template, isFirstPage, targetPageCount);

        return new SqliteTableLeafMutation(
            sourcePageCount,
            targetPageCount,
            pageNumber,
            _pageStore.PageSize,
            tablePageImage,
            materialized.OverflowPages);
    }

    private void ValidateFits(
        IReadOnlyList<SqliteTableLeafCellInput> inputs,
        bool isFirstPage)
    {
        var builder = new SqliteTableLeafPageBuilder(
            _pageStore.PageSize,
            _pageStore.Header.UsableSpace,
            isFirstPage);
        foreach (var input in inputs)
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)input.Payload.Length),
                _pageStore.Header.UsableSpace);
            var localPayload = input.Payload.Span[..layout.LocalPayloadLength];
            builder.Append(SqliteTableLeafCell.Create(
                input.RowId,
                checked((ulong)input.Payload.Length),
                localPayload,
                layout.UsesOverflow ? 1U : null,
                _pageStore.Header.UsableSpace));
        }

        _ = builder.Build();
    }

    private MaterializedCells MaterializeCells(IReadOnlyList<SqliteTableLeafCellInput> inputs)
    {
        var cells = new List<SqliteTableLeafCell>(inputs.Count);
        var overflowPages = new List<SqlitePageImage>();
        var allocationDatabaseSizeInPages = 0U;
        var usableSpace = _pageStore.Header.UsableSpace;
        var overflowCapacity = usableSpace - SqliteOverflowPageView.HeaderLength;

        foreach (var input in inputs)
        {
            var payload = input.Payload.Span;
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)payload.Length),
                usableSpace);
            if (!layout.UsesOverflow)
            {
                cells.Add(SqliteTableLeafCell.Create(input.RowId, payload, usableSpace));
                continue;
            }

            var remainingOverflowBytes = payload.Length - layout.LocalPayloadLength;
            var allocations = new List<SqlitePageAllocation>();
            while (remainingOverflowBytes > 0)
            {
                var allocation = _allocator.Allocate();
                ValidateDataPageAllocation(allocation);
                if (allocation.DatabaseSizeInPages > allocationDatabaseSizeInPages)
                    allocationDatabaseSizeInPages = allocation.DatabaseSizeInPages;
                allocations.Add(allocation);
                remainingOverflowBytes -= Math.Min(overflowCapacity, remainingOverflowBytes);
            }

            var overflowOffset = layout.LocalPayloadLength;
            for (var index = 0; index < allocations.Count; index++)
            {
                var bytesOnPage = Math.Min(overflowCapacity, payload.Length - overflowOffset);
                var nextPageNumber = index + 1 < allocations.Count
                    ? allocations[index + 1].PageNumber
                    : 0U;
                overflowPages.Add(new SqlitePageImage(
                    allocations[index].PageNumber,
                    SqliteOverflowPageView.Create(
                        _pageStore.PageSize,
                        usableSpace,
                        nextPageNumber,
                        payload.Slice(overflowOffset, bytesOnPage)).ToArray()));
                overflowOffset += bytesOnPage;
            }

            cells.Add(SqliteTableLeafCell.Create(
                input.RowId,
                checked((ulong)payload.Length),
                payload[..layout.LocalPayloadLength],
                allocations[0].PageNumber,
                usableSpace));
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

    private byte[] BuildTablePage(
        IReadOnlyList<SqliteTableLeafCellInput> inputs,
        IReadOnlyList<SqliteTableLeafCell> cells,
        byte[] template,
        bool isFirstPage,
        uint targetPageCount)
    {
        var builder = new SqliteTableLeafPageBuilder(
            _pageStore.PageSize,
            _pageStore.Header.UsableSpace,
            isFirstPage);
        foreach (var cell in cells)
            builder.Append(cell);

        builder.WriteTo(template);
        if (isFirstPage && targetPageCount != _pageStore.PageCount)
        {
            var updatedHeader = _pageStore.Header with
            {
                DatabaseSizeInPages = targetPageCount,
                VersionValidFor = _pageStore.Header.ChangeCounter,
            };
            updatedHeader.WriteTo(template);
        }

        if (builder.Cells.Count != inputs.Count)
            throw new InvalidOperationException("SQLite table-leaf mutation lost a cell while building its page image.");

        return template;
    }

    private static SqliteTableLeafCellInput[] SnapshotInputs(IEnumerable<SqliteTableLeafCellInput> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var snapshot = new List<SqliteTableLeafCellInput>();
        foreach (var cell in cells)
        {
            ArgumentNullException.ThrowIfNull(cell);
            snapshot.Add(cell);
        }

        return [.. snapshot];
    }

    private static void ValidateDataPageAllocation(SqlitePageAllocation allocation)
    {
        if (allocation.PageNumber == 1)
        {
            throw new InvalidOperationException(
                "Page 1 contains the SQLite database header and cannot be allocated for a table leaf or overflow page.");
        }
    }

    private sealed record MaterializedCells(
        IReadOnlyList<SqliteTableLeafCell> Cells,
        IReadOnlyList<SqlitePageImage> OverflowPages,
        uint AllocationDatabaseSizeInPages);
}
