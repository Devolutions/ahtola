using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable B-tree mutation staged against one committed pager view.
/// </summary>
/// <remarks>
/// The mutation writes every new page before every modified routing image. When
/// page one changes it is required to be the final image and WAL commit frame,
/// so an interrupted write
/// therefore cannot route readers to an absent child. It can replace existing
/// pages or append new pages, but never shrinks, rebalances, or reclaims pages.
/// </remarks>
public sealed class SqliteBtreeSplitMutation
{
    private readonly SqlitePageImage[] _sourcePages;
    private readonly SqlitePageImage[] _writeImages;

    internal SqliteBtreeSplitMutation(
        uint sourceDatabaseSizeInPages,
        uint targetDatabaseSizeInPages,
        int pageSize,
        IEnumerable<SqlitePageImage> sourcePages,
        IEnumerable<SqlitePageImage> writeImages)
    {
        if (sourceDatabaseSizeInPages == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDatabaseSizeInPages));
        if (targetDatabaseSizeInPages < sourceDatabaseSizeInPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetDatabaseSizeInPages),
                "A B-tree mutation cannot reduce the committed database size.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentNullException.ThrowIfNull(sourcePages);
        ArgumentNullException.ThrowIfNull(writeImages);

        SourceDatabaseSizeInPages = sourceDatabaseSizeInPages;
        TargetDatabaseSizeInPages = targetDatabaseSizeInPages;
        PageSize = pageSize;
        _sourcePages = sourcePages
            .Select(image => new SqlitePageImage(image.PageNumber, image.Page.Span))
            .ToArray();
        _writeImages = writeImages
            .Select(image => new SqlitePageImage(image.PageNumber, image.Page.Span))
            .ToArray();

        ValidateImages();
        WriteImages = new ReadOnlyCollection<SqlitePageImage>(_writeImages);
    }

    /// <summary>The committed page count used while preparing this mutation.</summary>
    public uint SourceDatabaseSizeInPages { get; }

    /// <summary>The page count written into the final WAL commit frame.</summary>
    public uint TargetDatabaseSizeInPages { get; }

    /// <summary>The exact physical size of every page image.</summary>
    public int PageSize { get; }

    /// <summary>
    /// Ordered page writes. The final routing or catalog image receives the WAL
    /// commit marker.
    /// </summary>
    public IReadOnlyList<SqlitePageImage> WriteImages { get; }

    /// <summary>
    /// Commits this split through <paramref name="pager"/>'s WAL transaction.
    /// </summary>
    /// <remarks>
    /// Before staging any image, the method checks that every source page still
    /// exactly matches the snapshot used to prepare the split. A stale mutation
    /// is rejected without appending a WAL frame.
    /// </remarks>
    public void CommitTo(SqlitePager pager)
    {
        ArgumentNullException.ThrowIfNull(pager);
        if (pager.PageSize != PageSize)
            throw new InvalidOperationException("SQLite pager and split mutation page sizes do not match.");

        using var transaction = pager.BeginTransaction(TargetDatabaseSizeInPages);
        if (pager.CommittedPageCount != SourceDatabaseSizeInPages)
        {
            throw new InvalidOperationException(
                "SQLite committed page count changed after the split mutation was prepared.");
        }

        foreach (var sourcePage in _sourcePages)
        {
            if (!transaction.ReadPage(sourcePage.PageNumber).AsSpan().SequenceEqual(sourcePage.Page.Span))
            {
                throw new InvalidOperationException(
                    $"SQLite page {sourcePage.PageNumber} changed after the split mutation was prepared.");
            }
        }

        foreach (var writeImage in _writeImages)
            transaction.WritePage(writeImage.PageNumber, writeImage.Page.Span);
        transaction.Commit();
    }

    private void ValidateImages()
    {
        if (_sourcePages.Length == 0)
            throw new ArgumentException("A split mutation must retain its source page images.", nameof(_sourcePages));
        if (_writeImages.Length == 0)
            throw new ArgumentException("A split mutation must write page images.", nameof(_writeImages));

        var sourcePageNumbers = new HashSet<uint>();
        foreach (var sourcePage in _sourcePages)
        {
            if (sourcePage.PageNumber == 0 || sourcePage.PageNumber > SourceDatabaseSizeInPages)
                throw new ArgumentException("A split source page is outside the prepared database view.", nameof(_sourcePages));
            if (sourcePage.Page.Length != PageSize)
                throw new ArgumentException("Every split source image must be exactly one page.", nameof(_sourcePages));
            if (!sourcePageNumbers.Add(sourcePage.PageNumber))
                throw new ArgumentException("Split source pages must be distinct.", nameof(_sourcePages));
        }

        var writePageNumbers = new HashSet<uint>();
        foreach (var writeImage in _writeImages)
        {
            if (writeImage.PageNumber == 0 || writeImage.PageNumber > TargetDatabaseSizeInPages)
                throw new ArgumentException("A split write image is outside the target database view.", nameof(_writeImages));
            if (writeImage.Page.Length != PageSize)
                throw new ArgumentException("Every split write image must be exactly one page.", nameof(_writeImages));
            if (!writePageNumbers.Add(writeImage.PageNumber))
                throw new ArgumentException("Split write images must target distinct pages.", nameof(_writeImages));
        }

        var pageOneIndex = Array.FindIndex(_writeImages, image => image.PageNumber == 1);
        if (pageOneIndex >= 0 && pageOneIndex != _writeImages.Length - 1)
        {
            throw new ArgumentException(
                "A split mutation that writes page 1 must make it the final WAL image.",
                nameof(_writeImages));
        }

        for (var pageNumber = SourceDatabaseSizeInPages + 1;
             pageNumber <= TargetDatabaseSizeInPages;
             pageNumber++)
        {
            if (!writePageNumbers.Contains(pageNumber))
            {
                throw new ArgumentException(
                    $"Split mutation is missing appended page {pageNumber}.",
                    nameof(_writeImages));
            }

            if (pageNumber == uint.MaxValue)
                break;
        }
    }
}

/// <summary>
/// Prepares durable root and parent propagation for one table- or index-leaf
/// split in a committed SQLite WAL pager view.
/// </summary>
/// <remarks>
/// The writer accepts only append-only reservations. It intentionally refuses
/// freelist reuse because this storage layer does not yet own freelist recovery
/// or page reclamation. Call <see cref="SqliteBtreeSplitMutation.CommitTo"/> to
/// install a prepared split as one WAL transaction.
/// </remarks>
public sealed class SqliteBtreeSplitWriter
{
    private readonly SqlitePager _pager;
    private readonly ISqlitePageAllocator _allocator;

    /// <summary>
    /// Creates a split writer over a committed pager view and an append-only page
    /// allocator created from that view's page count.
    /// </summary>
    public SqliteBtreeSplitWriter(SqlitePager pager, ISqlitePageAllocator allocator)
    {
        _pager = pager ?? throw new ArgumentNullException(nameof(pager));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>
    /// Prepares a table-leaf split. Supply <paramref name="parentPageNumber"/>
    /// when the leaf has a table-interior parent; omit it when the leaf itself is
    /// the table root to replace that page with a new interior root.
    /// </summary>
    public SqliteBtreeSplitMutation PrepareTableLeafSplit(
        uint leafPageNumber,
        int leftCellCount,
        uint? parentPageNumber = null)
    {
        var (header, sourcePageCount) = ReadCommittedDatabaseState();
        ValidateExistingPageNumber(leafPageNumber, sourcePageCount, nameof(leafPageNumber));
        var leafImage = _pager.ReadCommittedPage(leafPageNumber);
        var leaf = SqliteTableLeafPageView.Parse(
            leafImage,
            header.UsableSpace,
            isFirstPage: leafPageNumber == 1);
        var split = SqliteTableLeafSplit.Create(leaf, leftCellCount);
        var reservations = new AppendOnlyReservation(_allocator, sourcePageCount, _pager.PageSize);

        if (parentPageNumber is null)
        {
            var rootLeftPage = reservations.Reserve();
            var rootRightPage = reservations.Reserve();
            var rootImage = BuildTableRootPage(
                leafImage,
                leafPageNumber == 1,
                header.UsableSpace,
                rootLeftPage.PageNumber,
                rootRightPage.PageNumber,
                split.SeparatorRowId);
            UpdateFirstPageHeaderIfNeeded(rootImage, leafPageNumber, reservations.TargetPageCount);

            return new SqliteBtreeSplitMutation(
                sourcePageCount,
                reservations.TargetPageCount,
                _pager.PageSize,
                [new SqlitePageImage(leafPageNumber, leafImage)],
                [
                    new SqlitePageImage(rootLeftPage.PageNumber, split.LeftPage.Span),
                    new SqlitePageImage(rootRightPage.PageNumber, split.RightPage.Span),
                    new SqlitePageImage(leafPageNumber, rootImage),
                ]);
        }

        if (leafPageNumber == 1)
        {
            throw new InvalidOperationException(
                "SQLite page 1 is a table root and cannot be a child of an interior page.");
        }

        var parentImage = ReadAndValidateTableParent(
            parentPageNumber.Value,
            leafPageNumber,
            sourcePageCount,
            header.UsableSpace);
        var parent = SqliteTableInteriorPageView.Parse(
            parentImage,
            header.UsableSpace,
            isFirstPage: parentPageNumber.Value == 1);

        var rightPageNumber = reservations.NextPageNumber;
        var propagatedParent = BuildTableParentPage(
            parentImage,
            parent,
            parentPageNumber.Value == 1,
            leafPageNumber,
            rightPageNumber,
            split.SeparatorRowId);
        var rightPage = reservations.Reserve();
        UpdateFirstPageHeaderIfNeeded(
            propagatedParent,
            parentPageNumber.Value,
            reservations.TargetPageCount);

        return new SqliteBtreeSplitMutation(
            sourcePageCount,
            reservations.TargetPageCount,
            _pager.PageSize,
            [
                new SqlitePageImage(leafPageNumber, leafImage),
                new SqlitePageImage(parentPageNumber.Value, parentImage),
            ],
            [
                new SqlitePageImage(rightPage.PageNumber, split.RightPage.Span),
                new SqlitePageImage(leafPageNumber, split.LeftPage.Span),
                new SqlitePageImage(parentPageNumber.Value, propagatedParent),
            ]);
    }

    /// <summary>
    /// Prepares a height-increasing split for a full table-interior root whose
    /// right-most leaf receives one strictly larger rowid.
    /// </summary>
    /// <remarks>
    /// The root page retains its page number. Its former leaf children are
    /// partitioned between two newly appended interior children, and the
    /// right-most source leaf is replaced by the left half of the leaf split.
    /// The supplied page-one image is the final WAL frame, so it must contain
    /// the target database page count and any caller-owned catalog stamps.
    /// </remarks>
    public SqliteBtreeSplitMutation PrepareTableInteriorRootRightmostLeafSplit(
        uint rootPageNumber,
        SqliteTableLeafCell appendedCell,
        ReadOnlySpan<byte> finalPageOne)
    {
        ArgumentNullException.ThrowIfNull(appendedCell);
        var (header, sourcePageCount) = ReadCommittedDatabaseState();
        if (header.DatabaseSizeInPages != sourcePageCount)
        {
            throw new InvalidDataException(
                "SQLite page-one database size does not match the committed pager view.");
        }
        if (sourcePageCount > uint.MaxValue - 3)
        {
            throw new InvalidOperationException(
                "SQLite cannot append the three pages required to promote a table interior root.");
        }
        if (rootPageNumber == 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootPageNumber),
                "SQLite page 1 is the sqlite_schema root and cannot be promoted as a user table root.");
        }
        if (finalPageOne.Length != _pager.PageSize)
        {
            throw new ArgumentException(
                $"The final SQLite page-one image must be exactly {_pager.PageSize} bytes.",
                nameof(finalPageOne));
        }

        ValidateExistingPageNumber(rootPageNumber, sourcePageCount, nameof(rootPageNumber));
        var reservations = new AppendOnlyReservation(_allocator, sourcePageCount, _pager.PageSize);
        var expectedTargetPageCount = reservations.Peek(2);
        var targetHeader = SqliteDatabaseHeader.Parse(finalPageOne);
        if (targetHeader.PageSize != _pager.PageSize
            || targetHeader.DatabaseSizeInPages != expectedTargetPageCount)
        {
            throw new ArgumentException(
                "The final SQLite page-one image must preserve the page size and publish the promoted page count.",
                nameof(finalPageOne));
        }

        var rootImage = _pager.ReadCommittedPage(rootPageNumber);
        var root = SqliteTableInteriorPageView.Parse(rootImage, header.UsableSpace);
        var childPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length < 2)
        {
            throw new InvalidDataException(
                "SQLite table-interior root promotion requires at least two leaf children.");
        }

        var replacementChildren = new List<TableTreeChild>(childPages.Length + 1);
        long? previousMaximumRowId = null;
        byte[]? sourceRightLeafPage = null;
        SqliteTableLeafPageView? sourceRightLeaf = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPageNumber = childPages[childIndex];
            if (childPageNumber == rootPageNumber)
            {
                throw new InvalidDataException(
                    "SQLite table-interior root cannot reference itself as a child.");
            }

            ValidateExistingPageNumber(childPageNumber, sourcePageCount, nameof(rootPageNumber));
            var childImage = _pager.ReadCommittedPage(childPageNumber);
            if (SqliteBtreePageHeader.Parse(childImage).PageType != SqliteBtreePageType.TableLeaf)
            {
                throw new InvalidDataException(
                    "SQLite table-interior root promotion supports only direct table-leaf children.");
            }

            var child = SqliteTableLeafPageView.Parse(childImage, header.UsableSpace);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                || (previousMaximumRowId is { } maximumRowId
                    && child.Cells[0].Cell.RowId <= maximumRowId))
            {
                throw new InvalidDataException(
                    "SQLite table-interior root has unsupported empty, overflow, or unordered leaf children.");
            }

            var childMaximumRowId = child.Cells[^1].Cell.RowId;
            if (childIndex < root.Cells.Count
                && root.Cells[childIndex].Cell.RowId != childMaximumRowId)
            {
                throw new InvalidDataException(
                    "SQLite table-interior root separator does not match its left child's maximum rowid.");
            }

            replacementChildren.Add(new TableTreeChild(childPageNumber, childMaximumRowId));
            previousMaximumRowId = childMaximumRowId;
            if (childIndex == childPages.Length - 1)
            {
                sourceRightLeafPage = childImage;
                sourceRightLeaf = child;
            }
        }

        if (sourceRightLeafPage is null
            || sourceRightLeaf is null
            || appendedCell.RowId <= sourceRightLeaf.Cells[^1].Cell.RowId)
        {
            throw new InvalidOperationException(
                "SQLite table-interior root promotion requires a strict right-most append.");
        }

        var targetLeafCells = new List<SqliteTableLeafCell>(sourceRightLeaf.Cells.Count + 1);
        targetLeafCells.AddRange(sourceRightLeaf.Cells.Select(cell => cell.Cell));
        targetLeafCells.Add(appendedCell);
        if (TryBuildTableLeafPage(
                targetLeafCells,
                0,
                targetLeafCells.Count,
                _pager.PageSize,
                header.UsableSpace,
                out _)
            || !TrySplitTableLeaf(
                targetLeafCells,
                _pager.PageSize,
                header.UsableSpace,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRowId))
        {
            throw new InvalidOperationException(
                "SQLite table-interior root promotion requires an overflowing right-most leaf that can split.");
        }

        var appendedRightLeafPageNumber = reservations.Peek(0);
        var appendedLeftInteriorPageNumber = reservations.Peek(1);
        var appendedRightInteriorPageNumber = reservations.Peek(2);
        replacementChildren[^1] = new TableTreeChild(
            root.Header.RightMostChildPage,
            separatorRowId);
        replacementChildren.Add(new TableTreeChild(appendedRightLeafPageNumber, appendedCell.RowId));
        if (TryBuildTableInteriorPage(
                replacementChildren,
                _pager.PageSize,
                header.UsableSpace,
                out _))
        {
            throw new InvalidOperationException(
                "SQLite table-interior root still has room for the propagated leaf separator.");
        }

        if (!TrySplitTableInteriorRoot(
                replacementChildren,
                appendedLeftInteriorPageNumber,
                appendedRightInteriorPageNumber,
                rootImage,
                _pager.PageSize,
                header.UsableSpace,
                out var leftInteriorPage,
                out var rightInteriorPage,
                out var replacementRootPage))
        {
            throw new InvalidOperationException(
                "SQLite table-interior root cannot be partitioned into two non-empty child pages.");
        }

        var appendedRightLeafPage = reservations.Reserve();
        var appendedLeftInteriorPage = reservations.Reserve();
        var appendedRightInteriorPage = reservations.Reserve();
        return new SqliteBtreeSplitMutation(
            sourcePageCount,
            reservations.TargetPageCount,
            _pager.PageSize,
            [
                new SqlitePageImage(1, _pager.ReadCommittedPage(1)),
                new SqlitePageImage(rootPageNumber, rootImage),
                new SqlitePageImage(root.Header.RightMostChildPage, sourceRightLeafPage),
            ],
            [
                new SqlitePageImage(appendedRightLeafPage.PageNumber, rightLeafPage),
                new SqlitePageImage(appendedLeftInteriorPage.PageNumber, leftInteriorPage),
                new SqlitePageImage(appendedRightInteriorPage.PageNumber, rightInteriorPage),
                new SqlitePageImage(root.Header.RightMostChildPage, leftLeafPage),
                new SqlitePageImage(rootPageNumber, replacementRootPage),
                new SqlitePageImage(1, finalPageOne),
            ]);
    }

    /// <summary>
    /// Prepares an index-leaf split. Supply <paramref name="parentPageNumber"/>
    /// when the leaf has an index-interior parent; omit it when the leaf itself
    /// is the index root to replace that page with a new interior root.
    /// </summary>
    public SqliteBtreeSplitMutation PrepareIndexLeafSplit(
        uint leafPageNumber,
        int leftCellCount,
        uint? parentPageNumber = null)
    {
        var (header, sourcePageCount) = ReadCommittedDatabaseState();
        if (leafPageNumber == 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leafPageNumber),
                "SQLite page 1 is the sqlite_schema table root and cannot be an index leaf.");
        }

        ValidateExistingPageNumber(leafPageNumber, sourcePageCount, nameof(leafPageNumber));
        var overflowReader = new SqliteOverflowChainReader(_pager, header);
        var leafImage = _pager.ReadCommittedPage(leafPageNumber);
        var leaf = SqliteIndexLeafPageView.Parse(
            leafImage,
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: overflowReader);
        var split = SqliteIndexLeafSplit.Create(leaf, leftCellCount);
        var separatorRecord = split.GetSeparatorRecord();
        var reservations = new AppendOnlyReservation(_allocator, sourcePageCount, _pager.PageSize);

        if (parentPageNumber is null)
        {
            var rootLeftPageNumber = reservations.Peek(0);
            var rootRightPageNumber = reservations.Peek(1);
            _ = BuildIndexRootPage(
                leafImage,
                header.UsableSpace,
                header.TextEncoding,
                rootLeftPageNumber,
                rootRightPageNumber,
                CreateIndexSeparatorCell(
                    rootLeftPageNumber,
                    separatorRecord,
                    header.UsableSpace,
                    reservations.Peek(2)),
                separatorRecord);

            var rootLeftPage = reservations.Reserve();
            var rootRightPage = reservations.Reserve();
            var rootSeparator = MaterializeIndexSeparator(
                rootLeftPage.PageNumber,
                separatorRecord,
                header.UsableSpace,
                reservations);
            var rootImage = BuildIndexRootPage(
                leafImage,
                header.UsableSpace,
                header.TextEncoding,
                rootLeftPage.PageNumber,
                rootRightPage.PageNumber,
                rootSeparator.Cell,
                separatorRecord);

            return new SqliteBtreeSplitMutation(
                sourcePageCount,
                reservations.TargetPageCount,
                _pager.PageSize,
                [new SqlitePageImage(leafPageNumber, leafImage)],
                [
                    .. rootSeparator.OverflowPages,
                    new SqlitePageImage(rootLeftPage.PageNumber, split.LeftPage.Span),
                    new SqlitePageImage(rootRightPage.PageNumber, split.RightPage.Span),
                    new SqlitePageImage(leafPageNumber, rootImage),
                ]);
        }

        if (parentPageNumber.Value == 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentPageNumber),
                "SQLite page 1 is the sqlite_schema table root and cannot be an index-interior page.");
        }

        var parentImage = ReadAndValidateIndexParent(
            parentPageNumber.Value,
            leafPageNumber,
            sourcePageCount,
            header,
            overflowReader);
        var parent = SqliteIndexInteriorPageView.Parse(
            parentImage,
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: overflowReader);
        var rightPageNumber = reservations.NextPageNumber;
        _ = BuildIndexParentPage(
            parentImage,
            parent,
            leafPageNumber,
            rightPageNumber,
            CreatePreflightIndexSeparatorCell(
                leafPageNumber,
                separatorRecord,
                header.UsableSpace,
                reservations),
            separatorRecord);

        var rightPage = reservations.Reserve();
        var separator = MaterializeIndexSeparator(
            leafPageNumber,
            separatorRecord,
            header.UsableSpace,
            reservations);
        var propagatedParent = BuildIndexParentPage(
            parentImage,
            parent,
            leafPageNumber,
            rightPage.PageNumber,
            separator.Cell,
            separatorRecord);

        return new SqliteBtreeSplitMutation(
            sourcePageCount,
            reservations.TargetPageCount,
            _pager.PageSize,
            [
                new SqlitePageImage(leafPageNumber, leafImage),
                new SqlitePageImage(parentPageNumber.Value, parentImage),
            ],
            [
                .. separator.OverflowPages,
                new SqlitePageImage(rightPage.PageNumber, split.RightPage.Span),
                new SqlitePageImage(leafPageNumber, split.LeftPage.Span),
                new SqlitePageImage(parentPageNumber.Value, propagatedParent),
            ]);
    }

    private (SqliteDatabaseHeader Header, uint PageCount) ReadCommittedDatabaseState()
    {
        var pageCount = _pager.CommittedPageCount;
        if (pageCount == 0)
            throw new InvalidDataException("A SQLite pager has no root page.");

        var header = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(1));
        if (header.PageSize != _pager.PageSize)
            throw new InvalidDataException("SQLite pager page size differs from its page-one database header.");

        return (header, pageCount);
    }

    private byte[] ReadAndValidateTableParent(
        uint parentPageNumber,
        uint leafPageNumber,
        uint sourcePageCount,
        int usableSpace)
    {
        ValidateExistingPageNumber(parentPageNumber, sourcePageCount, nameof(parentPageNumber));
        if (parentPageNumber == leafPageNumber)
            throw new ArgumentException("A SQLite leaf cannot be its own parent.", nameof(parentPageNumber));

        var parentImage = _pager.ReadCommittedPage(parentPageNumber);
        var parent = SqliteTableInteriorPageView.Parse(
            parentImage,
            usableSpace,
            isFirstPage: parentPageNumber == 1);
        ValidateTableParentChildren(parent, sourcePageCount);
        EnsureTableParentContainsChild(parent, leafPageNumber);
        return parentImage;
    }

    private byte[] ReadAndValidateIndexParent(
        uint parentPageNumber,
        uint leafPageNumber,
        uint sourcePageCount,
        SqliteDatabaseHeader header,
        SqliteOverflowChainReader overflowReader)
    {
        ValidateExistingPageNumber(parentPageNumber, sourcePageCount, nameof(parentPageNumber));
        if (parentPageNumber == leafPageNumber)
            throw new ArgumentException("A SQLite leaf cannot be its own parent.", nameof(parentPageNumber));

        var parentImage = _pager.ReadCommittedPage(parentPageNumber);
        var parent = SqliteIndexInteriorPageView.Parse(
            parentImage,
            header.UsableSpace,
            header.TextEncoding,
            overflowReader: overflowReader);
        ValidateIndexParentChildren(parent, sourcePageCount);
        EnsureIndexParentContainsChild(parent, leafPageNumber);
        return parentImage;
    }

    private static bool TrySplitTableLeaf(
        IReadOnlyList<SqliteTableLeafCell> cells,
        int pageSize,
        int usableSpace,
        out byte[] leftPage,
        out byte[] rightPage,
        out long separatorRowId)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRowId = 0;
        if (cells.Count < 2)
            return false;

        for (var leftCellCount = 1; leftCellCount < cells.Count; leftCellCount++)
        {
            if (!TryBuildTableLeafPage(
                    cells,
                    0,
                    leftCellCount,
                    pageSize,
                    usableSpace,
                    out leftPage)
                || !TryBuildTableLeafPage(
                    cells,
                    leftCellCount,
                    cells.Count - leftCellCount,
                    pageSize,
                    usableSpace,
                    out rightPage))
            {
                continue;
            }

            separatorRowId = cells[leftCellCount - 1].RowId;
            return true;
        }

        leftPage = null!;
        rightPage = null!;
        return false;
    }

    private static bool TryBuildTableLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        int start,
        int count,
        int pageSize,
        int usableSpace,
        out byte[] page)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(pageSize, usableSpace);
            for (var index = start; index < start + count; index++)
                builder.Append(cells[index]);

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private static bool TrySplitTableInteriorRoot(
        IReadOnlyList<TableTreeChild> children,
        uint leftInteriorPage,
        uint rightInteriorPage,
        ReadOnlySpan<byte> sourceRootPage,
        int pageSize,
        int usableSpace,
        out byte[] leftPage,
        out byte[] rightPage,
        out byte[] replacementRootPage)
    {
        leftPage = null!;
        rightPage = null!;
        replacementRootPage = null!;
        if (children.Count < 4)
            return false;

        var middle = children.Count / 2;
        foreach (var splitIndex in Enumerable.Range(2, children.Count - 3)
                     .OrderBy(index => Math.Abs(index - middle)))
        {
            var leftChildren = children.Take(splitIndex).ToArray();
            var rightChildren = children.Skip(splitIndex).ToArray();
            if (!TryBuildTableInteriorPage(leftChildren, pageSize, usableSpace, out leftPage)
                || !TryBuildTableInteriorPage(rightChildren, pageSize, usableSpace, out rightPage))
            {
                continue;
            }

            try
            {
                var rootBuilder = new SqliteTableInteriorPageBuilder(
                    pageSize,
                    usableSpace,
                    rightInteriorPage);
                rootBuilder.Append(SqliteTableInteriorCell.Create(
                    leftInteriorPage,
                    leftChildren[^1].MaximumRowId));
                replacementRootPage = sourceRootPage.ToArray();
                rootBuilder.WriteTo(replacementRootPage);
                return true;
            }
            catch (InvalidOperationException)
            {
                leftPage = null!;
                rightPage = null!;
                replacementRootPage = null!;
            }
        }

        return false;
    }

    private static bool TryBuildTableInteriorPage(
        IReadOnlyList<TableTreeChild> children,
        int pageSize,
        int usableSpace,
        out byte[] page)
    {
        if (children.Count < 2)
        {
            page = null!;
            return false;
        }

        try
        {
            var builder = new SqliteTableInteriorPageBuilder(
                pageSize,
                usableSpace,
                children[^1].PageNumber);
            for (var childIndex = 0; childIndex < children.Count - 1; childIndex++)
            {
                var child = children[childIndex];
                builder.Append(SqliteTableInteriorCell.Create(
                    child.PageNumber,
                    child.MaximumRowId));
            }

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private static byte[] BuildTableRootPage(
        ReadOnlySpan<byte> sourcePage,
        bool isFirstPage,
        int usableSpace,
        uint leftChildPage,
        uint rightChildPage,
        long separatorRowId)
    {
        var builder = new SqliteTableInteriorPageBuilder(
            sourcePage.Length,
            usableSpace,
            rightChildPage,
            isFirstPage);
        builder.Append(SqliteTableInteriorCell.Create(leftChildPage, separatorRowId));

        var root = sourcePage.ToArray();
        builder.WriteTo(root);
        return root;
    }

    private static byte[] BuildTableParentPage(
        ReadOnlySpan<byte> sourcePage,
        SqliteTableInteriorPageView parent,
        bool isFirstPage,
        uint leafPageNumber,
        uint rightPageNumber,
        long separatorRowId)
    {
        var childIndex = FindTableLeftChildIndex(parent, leafPageNumber);
        var isRightMostChild = childIndex < 0;
        if (isRightMostChild && parent.Header.RightMostChildPage != leafPageNumber)
            throw new InvalidDataException("SQLite table-interior parent does not reference the split leaf.");

        var builder = new SqliteTableInteriorPageBuilder(
            sourcePage.Length,
            parent.UsableSpace,
            isRightMostChild ? rightPageNumber : parent.Header.RightMostChildPage,
            isFirstPage);
        for (var index = 0; index < parent.Cells.Count; index++)
        {
            var cell = parent.Cells[index].Cell;
            if (index != childIndex)
            {
                builder.Append(cell);
                continue;
            }

            builder.Append(SqliteTableInteriorCell.Create(leafPageNumber, separatorRowId));
            builder.Append(SqliteTableInteriorCell.Create(rightPageNumber, cell.RowId));
        }

        if (isRightMostChild)
            builder.Append(SqliteTableInteriorCell.Create(leafPageNumber, separatorRowId));

        var propagated = sourcePage.ToArray();
        builder.WriteTo(propagated);
        return propagated;
    }

    private static byte[] BuildIndexRootPage(
        ReadOnlySpan<byte> sourcePage,
        int usableSpace,
        SqliteTextEncoding textEncoding,
        uint leftChildPage,
        uint rightChildPage,
        SqliteIndexInteriorCell separator,
        ReadOnlySpan<byte> separatorRecord)
    {
        var builder = new SqliteIndexInteriorPageBuilder(
            sourcePage.Length,
            usableSpace,
            rightChildPage,
            new SqliteIndexRecordComparer(textEncoding));
        builder.Append(separator, separatorRecord);

        var root = sourcePage.ToArray();
        builder.WriteTo(root);
        return root;
    }

    private static byte[] BuildIndexParentPage(
        ReadOnlySpan<byte> sourcePage,
        SqliteIndexInteriorPageView parent,
        uint leafPageNumber,
        uint rightPageNumber,
        SqliteIndexInteriorCell separator,
        ReadOnlySpan<byte> separatorRecord)
    {
        var childIndex = FindIndexLeftChildIndex(parent, leafPageNumber);
        var isRightMostChild = childIndex < 0;
        if (isRightMostChild && parent.Header.RightMostChildPage != leafPageNumber)
            throw new InvalidDataException("SQLite index-interior parent does not reference the split leaf.");

        var builder = new SqliteIndexInteriorPageBuilder(
            sourcePage.Length,
            parent.UsableSpace,
            isRightMostChild ? rightPageNumber : parent.Header.RightMostChildPage,
            parent.RecordComparer);
        for (var index = 0; index < parent.Cells.Count; index++)
        {
            var cell = parent.Cells[index].Cell;
            var record = parent.GetRecord(index);
            if (index != childIndex)
            {
                builder.Append(cell, record);
                continue;
            }

            builder.Append(separator, separatorRecord);
            builder.Append(
                SqliteIndexInteriorCell.Create(rightPageNumber, cell.Key),
                record);
        }

        if (isRightMostChild)
            builder.Append(separator, separatorRecord);

        var propagated = sourcePage.ToArray();
        builder.WriteTo(propagated);
        return propagated;
    }

    private static SqliteIndexInteriorCell CreateIndexSeparatorCell(
        uint leftChildPage,
        ReadOnlySpan<byte> separatorRecord,
        int usableSpace,
        uint firstOverflowPage)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexInterior,
            checked((ulong)separatorRecord.Length),
            usableSpace);
        return layout.UsesOverflow
            ? SqliteIndexInteriorCell.Create(
                leftChildPage,
                checked((ulong)separatorRecord.Length),
                separatorRecord[..layout.LocalPayloadLength],
                firstOverflowPage,
                usableSpace)
            : SqliteIndexInteriorCell.Create(leftChildPage, separatorRecord, usableSpace);
    }

    private static SqliteIndexInteriorCell CreatePreflightIndexSeparatorCell(
        uint leftChildPage,
        ReadOnlySpan<byte> separatorRecord,
        int usableSpace,
        AppendOnlyReservation reservations)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexInterior,
            checked((ulong)separatorRecord.Length),
            usableSpace);
        var firstOverflowPage = layout.UsesOverflow
            ? reservations.Peek(1)
            : 1U;
        return CreateIndexSeparatorCell(
            leftChildPage,
            separatorRecord,
            usableSpace,
            firstOverflowPage);
    }

    private MaterializedIndexSeparator MaterializeIndexSeparator(
        uint leftChildPage,
        ReadOnlySpan<byte> separatorRecord,
        int usableSpace,
        AppendOnlyReservation reservations)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexInterior,
            checked((ulong)separatorRecord.Length),
            usableSpace);
        if (!layout.UsesOverflow)
        {
            return new MaterializedIndexSeparator(
                SqliteIndexInteriorCell.Create(leftChildPage, separatorRecord, usableSpace),
                []);
        }

        var overflowCapacity = usableSpace - SqliteOverflowPageView.HeaderLength;
        var remainingPayloadLength = separatorRecord.Length - layout.LocalPayloadLength;
        var allocations = new List<SqlitePageAllocation>();
        while (remainingPayloadLength > 0)
        {
            allocations.Add(reservations.Reserve());
            remainingPayloadLength -= Math.Min(overflowCapacity, remainingPayloadLength);
        }

        var overflowPages = new List<SqlitePageImage>(allocations.Count);
        var offset = layout.LocalPayloadLength;
        for (var index = 0; index < allocations.Count; index++)
        {
            var bytesOnPage = Math.Min(overflowCapacity, separatorRecord.Length - offset);
            var nextPageNumber = index + 1 < allocations.Count
                ? allocations[index + 1].PageNumber
                : 0U;
            overflowPages.Add(new SqlitePageImage(
                allocations[index].PageNumber,
                SqliteOverflowPageView.Create(
                    _pager.PageSize,
                    usableSpace,
                    nextPageNumber,
                    separatorRecord.Slice(offset, bytesOnPage)).ToArray()));
            offset += bytesOnPage;
        }

        return new MaterializedIndexSeparator(
            SqliteIndexInteriorCell.Create(
                leftChildPage,
                checked((ulong)separatorRecord.Length),
                separatorRecord[..layout.LocalPayloadLength],
                allocations[0].PageNumber,
                usableSpace),
            overflowPages);
    }

    private static void UpdateFirstPageHeaderIfNeeded(byte[] page, uint pageNumber, uint targetPageCount)
    {
        if (pageNumber != 1)
            return;

        var header = SqliteDatabaseHeader.Parse(page);
        (header with
        {
            DatabaseSizeInPages = targetPageCount,
            VersionValidFor = header.ChangeCounter,
        }).WriteTo(page);
    }

    private static void ValidateExistingPageNumber(uint pageNumber, uint pageCount, string parameterName)
    {
        if (pageNumber == 0 || pageNumber > pageCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                pageNumber,
                $"SQLite page number must be in range 1..{pageCount}.");
        }
    }

    private static void ValidateTableParentChildren(SqliteTableInteriorPageView parent, uint pageCount)
    {
        foreach (var cell in parent.Cells)
            ValidateExistingPageNumber(cell.Cell.LeftChildPage, pageCount, nameof(parent));
        ValidateExistingPageNumber(parent.Header.RightMostChildPage, pageCount, nameof(parent));
    }

    private static void ValidateIndexParentChildren(SqliteIndexInteriorPageView parent, uint pageCount)
    {
        foreach (var cell in parent.Cells)
            ValidateExistingPageNumber(cell.Cell.LeftChildPage, pageCount, nameof(parent));
        ValidateExistingPageNumber(parent.Header.RightMostChildPage, pageCount, nameof(parent));
    }

    private static void EnsureTableParentContainsChild(SqliteTableInteriorPageView parent, uint childPageNumber)
    {
        if (FindTableLeftChildIndex(parent, childPageNumber) < 0
            && parent.Header.RightMostChildPage != childPageNumber)
        {
            throw new InvalidDataException("SQLite table-interior parent does not reference the split leaf.");
        }
    }

    private static void EnsureIndexParentContainsChild(SqliteIndexInteriorPageView parent, uint childPageNumber)
    {
        if (FindIndexLeftChildIndex(parent, childPageNumber) < 0
            && parent.Header.RightMostChildPage != childPageNumber)
        {
            throw new InvalidDataException("SQLite index-interior parent does not reference the split leaf.");
        }
    }

    private static int FindTableLeftChildIndex(SqliteTableInteriorPageView parent, uint childPageNumber)
    {
        for (var index = 0; index < parent.Cells.Count; index++)
        {
            if (parent.Cells[index].Cell.LeftChildPage == childPageNumber)
                return index;
        }

        return -1;
    }

    private static int FindIndexLeftChildIndex(SqliteIndexInteriorPageView parent, uint childPageNumber)
    {
        for (var index = 0; index < parent.Cells.Count; index++)
        {
            if (parent.Cells[index].Cell.LeftChildPage == childPageNumber)
                return index;
        }

        return -1;
    }

    private sealed record TableTreeChild(uint PageNumber, long MaximumRowId);

    private sealed record MaterializedIndexSeparator(
        SqliteIndexInteriorCell Cell,
        IReadOnlyList<SqlitePageImage> OverflowPages);

    private sealed class AppendOnlyReservation
    {
        private readonly ISqlitePageAllocator _allocator;
        private readonly uint _pendingBytePage;

        public AppendOnlyReservation(ISqlitePageAllocator allocator, uint sourcePageCount, int pageSize)
        {
            _allocator = allocator;
            _pendingBytePage = SqlitePageLimits.PendingBytePage(pageSize);
            TargetPageCount = sourcePageCount;
        }

        public uint TargetPageCount { get; private set; }

        public uint NextPageNumber => Peek(0);

        /// <summary>
        /// The page number produced <paramref name="ahead"/> reservations after the
        /// next one, skipping the unusable pending-byte page exactly like
        /// <see cref="SqliteAppendOnlyPageAllocator"/>. <c>Peek(0)</c> is the next
        /// page number.
        /// </summary>
        public uint Peek(uint ahead)
        {
            var pageNumber = TargetPageCount;
            for (var index = 0u; index <= ahead; index++)
                pageNumber = Advance(pageNumber);

            return pageNumber;
        }

        public SqlitePageAllocation Reserve()
        {
            var expectedPageNumber = NextPageNumber;
            var allocation = _allocator.Allocate();
            if (allocation.PageNumber != expectedPageNumber
                || allocation.DatabaseSizeInPages != expectedPageNumber)
            {
                throw new InvalidOperationException(
                    "Durable B-tree split propagation requires contiguous append-only page reservations.");
            }

            TargetPageCount = expectedPageNumber;
            return allocation;
        }

        private uint Advance(uint pageNumber)
        {
            if (pageNumber >= uint.MaxValue - 1)
                throw new InvalidOperationException("SQLite cannot append a page beyond UInt32.MaxValue.");

            var next = pageNumber + 1;
            return next == _pendingBytePage ? next + 1 : next;
        }
    }
}
