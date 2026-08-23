namespace Ahtola.Core.Storage;

/// <summary>
/// A cursor-based incremental writer for SQLite rowid-table b-trees.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation descends from the root to exactly one leaf, so the pages read
/// and the pages dirtied are bounded by the tree's height plus the pages a
/// split creates. Nothing else in the tree, and nothing in any other tree, is
/// read or rewritten.
/// </para>
/// <para>
/// The growth half of SQLite's balancing rules is implemented: a full page
/// splits and promotes separators into its parent, and a full root deepens the
/// tree while keeping its catalog page number. Overflow chains released by
/// DELETE/UPDATE are returned to the freelist via
/// <see cref="ISqliteBtreePageIo.FreePage"/>. Empty non-root leaves are freed and
/// unlinked (with single-child interior collapse into the parent/root). Under-full
/// non-empty leaves merge into a left or right sibling when both cell sets fit
/// on one page. When a pair cannot be balanced safely, up to three adjacent
/// siblings are repacked together and surplus pages are returned to the
/// freelist. Leaf/interior images are
/// always packed compactly on write, so freeblock-style in-page defragmentation
/// is not required.
/// </para>
/// <para>
/// The managed loader requires an interior separator to equal the exact maximum
/// rowid of its left child. That invariant is preserved without extra work on
/// insertion, because a descent only routes a rowid into a non-right-most child
/// when the rowid is at most that child's separator, and it is restored
/// explicitly after a deletion removes a child's maximum rowid.
/// </para>
/// </remarks>
public sealed class SqliteIncrementalTableBtree
{
    private const int MaximumDepth = 64;

    private readonly ISqliteBtreePageIo _io;

    /// <summary>Creates a writer over one page-access boundary.</summary>
    public SqliteIncrementalTableBtree(ISqliteBtreePageIo pageIo)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        _io = pageIo;
    }

    /// <summary>
    /// Inserts <paramref name="record"/> at a <paramref name="rowId"/> the tree
    /// must not already contain.
    /// </summary>
    /// <remarks>
    /// The absence check is not defensive bookkeeping: it verifies the caller's
    /// idea of the committed contents against the pages actually on disk, at the
    /// cost of the search the insert already performs.
    /// </remarks>
    public void Insert(uint rootPage, long rowId, ReadOnlySpan<byte> record)
    {
        var path = Descend(rootPage, rowId);
        var view = ParseLeaf(path[^1].PageNumber);
        var search = view.Search(rowId);
        if (search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Rowid {rowId} already exists, so the caller's view of the committed table is stale.");
        }

        var cells = view.Cells.Select(cell => cell.Cell).ToList();
        cells.Insert(search.Index, CreateLeafCell(rowId, record));
        WriteLeafAndPropagate(path, cells, appendedAtEnd: search.Index == cells.Count - 1);
    }

    /// <summary>
    /// Replaces the record stored at a <paramref name="rowId"/> the tree must
    /// already contain.
    /// </summary>
    public void Update(uint rootPage, long rowId, ReadOnlySpan<byte> record)
    {
        var path = Descend(rootPage, rowId);
        var view = ParseLeaf(path[^1].PageNumber);
        var search = view.Search(rowId);
        if (!search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Rowid {rowId} is absent, so the caller's view of the committed table is stale.");
        }

        var cells = view.Cells.Select(cell => cell.Cell).ToList();
        FreeOverflowIfPresent(cells[search.Index]);
        cells.RemoveAt(search.Index);
        cells.Insert(search.Index, CreateLeafCell(rowId, record));
        WriteLeafAndPropagate(path, cells, appendedAtEnd: false);
    }

    /// <summary>
    /// Removes the row stored at a <paramref name="rowId"/> the tree must
    /// already contain.
    /// </summary>
    public void Delete(uint rootPage, long rowId)
    {
        var path = Descend(rootPage, rowId);
        var leafPage = path[^1].PageNumber;
        var view = ParseLeaf(leafPage);
        var search = view.Search(rowId);
        if (!search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Rowid {rowId} is absent, so the caller's view of the committed table is stale.");
        }

        var cells = view.Cells.Select(cell => cell.Cell).ToList();
        FreeOverflowIfPresent(cells[search.Index]);
        var removedMaximum = search.Index == cells.Count - 1;
        cells.RemoveAt(search.Index);
        if (cells.Count == 0 && path.Count > 1)
        {
            // Empty non-root leaf: drop its parent pointer first, then free it.
            RemoveChildLink(path, path.Count - 2);
            _io.FreePage(leafPage);
            return;
        }

        WriteSinglePage(leafPage, BuildLeafImage(leafPage, cells));
        if (removedMaximum && cells.Count > 0)
            UpdateSeparatorChain(path, cells[^1].RowId);

        // If the leaf is now well under full, try to merge or redistribute with a sibling
        // so deleted space is reclaimed without waiting for VACUUM.
        TryMergeUnderfullLeaf(path, cells);
    }

                /// <summary>
                                /// Merges an under-full non-root leaf into a neighboring sibling when both
                                /// sets of cells fit on one page. When merge does not fit and this leaf is
                                /// below half full, redistributes cells with up to three siblings.
                                /// Empty-leaf reclaim still collapses single-child interiors via
                                /// <see cref="CollapseSingleChildInterior"/>. Underfull (non-empty) merge is
                                /// deferred when the parent has only two children under a deep tree so a
                                /// separator-only delete keeps parent topology byte-stable (empty-leaf paths
                                /// handle true shrink).
                                /// </summary>
                                private void TryMergeUnderfullLeaf(List<PathEntry> path, List<SqliteTableLeafCell> cells)
                                {
                                    if (path.Count < 2 || cells.Count == 0)
                                        return;

                                    var leafPage = path[^1].PageNumber;
                                    var capacity = LeafCapacity(leafPage);
                                    var used = LeafUsedBytes(cells);
                                    // Only attempt a sibling merge when this leaf has meaningful free space
                                    // (below ~3/4 full). Fits-check below still decides whether merge happens.
                                    if (used * 4 > capacity * 3)
                                        return;

                                    // Redistribute only when well under half full so ordinary sparse deletes
                                    // still prefer merge-when-fit and do not thrash packing on every remove.
                                    var allowRedistribute = used * 2 <= capacity;

                                    var parentEntry = path[^2];
                                    var parentLinks = ReadChildLinks(ParseInterior(parentEntry.PageNumber));
                                    var childIndex = parentEntry.ChildIndex;
                                    if (childIndex < 0 || childIndex >= parentLinks.Count)
                                        return;

                                    // Merging away a child from a 2-child non-root parent leaves a single-child
                                    // interior. Empty-leaf reclaim may collapse that via sibling absorb, but
                                    // underfull non-empty merge must not: separator-only deletes on deep trees
                                    // are required to keep parent pages byte-identical (topology tests / WAL
                                    // frame interruption fixtures). Defer to the next empty-leaf or full rewrite.
                                    if (parentLinks.Count <= 2 && path.Count > 2)
                                        return;

                                    // Prefer merging into the left sibling (keeps lower page numbers live).
                                    if (childIndex > 0
                                        && ParseHeaderType(parentLinks[childIndex - 1].PageNumber) == SqliteBtreePageType.TableLeaf)
                                    {
                        var leftPage = parentLinks[childIndex - 1].PageNumber;
                        var leftCells = ParseLeaf(leftPage).Cells.Select(cell => cell.Cell).ToList();
                        if (LeafCellsFit(leftPage, leftCells, cells))
                        {
                            leftCells.AddRange(cells);
                            WriteSinglePage(leftPage, BuildLeafImage(leftPage, leftCells));
                            // Left sibling now owns this key range; refresh its separator then
                            // drop this child from the parent. If we were the right-most child,
                            // the left sibling becomes right-most and keeps the open upper bound.
                            var leftUpperBound = childIndex + 1 == parentLinks.Count
                                ? long.MaxValue
                                : leftCells[^1].RowId;
                            parentLinks[childIndex - 1] = parentLinks[childIndex - 1] with
                            {
                                MaximumRowId = leftUpperBound,
                            };
                            parentLinks.RemoveAt(childIndex);
                            WriteOrCollapseParent(path, path.Count - 2, parentLinks);
                            _io.FreePage(leafPage);
                            return;
                        }

                        if (allowRedistribute
                            && TryRedistributeLeafPair(
                                path,
                                parentLinks,
                                leftIndex: childIndex - 1,
                                leftPage,
                                leftCells,
                                leafPage,
                                cells))
                        {
                            return;
                        }
                    }

                    if (childIndex + 1 < parentLinks.Count
                        && ParseHeaderType(parentLinks[childIndex + 1].PageNumber) == SqliteBtreePageType.TableLeaf)
                    {
                        var rightPage = parentLinks[childIndex + 1].PageNumber;
                        var rightCells = ParseLeaf(rightPage).Cells.Select(cell => cell.Cell).ToList();
                        if (LeafCellsFit(leafPage, cells, rightCells))
                        {
                            cells.AddRange(rightCells);
                            WriteSinglePage(leafPage, BuildLeafImage(leafPage, cells));
                            // Keep this page; drop the right sibling and inherit its upper bound.
                            parentLinks[childIndex] = parentLinks[childIndex] with
                            {
                                MaximumRowId = parentLinks[childIndex + 1].MaximumRowId,
                            };
                            parentLinks.RemoveAt(childIndex + 1);
                            WriteOrCollapseParent(path, path.Count - 2, parentLinks);
                            _io.FreePage(rightPage);
                            return;
                        }

                        if (allowRedistribute
                            && TryRedistributeLeafPair(
                                path,
                                parentLinks,
                                leftIndex: childIndex,
                                leafPage,
                                cells,
                                rightPage,
                                rightCells))
                        {
                            return;
                        }
                    }

                    if (allowRedistribute)
                        TryBalanceLeafSiblingRun(path, parentLinks, childIndex);
                }

                /// <summary>
                /// Evenly redistributes cells across two adjacent table leaves when a full
                /// merge does not fit. Updates the left child's parent separator to the new
                /// left maximum rowid. No-op when either side would end empty or either page
                /// cannot hold its half.
                /// </summary>
                private bool TryRedistributeLeafPair(
                    List<PathEntry> path,
                    List<ChildLink> parentLinks,
                    int leftIndex,
                    uint leftPage,
                    List<SqliteTableLeafCell> leftCells,
                    uint rightPage,
                    List<SqliteTableLeafCell> rightCells)
                {
                    if (leftCells.Count + rightCells.Count < 2)
                        return false;

                    var combined = new List<SqliteTableLeafCell>(leftCells.Count + rightCells.Count);
                    combined.AddRange(leftCells);
                    combined.AddRange(rightCells);

                    var total = LeafUsedBytes(combined);
                    var target = total / 2;
                    var split = 1;
                    var running = 0;
                    // Leave at least one cell on each side.
                    for (var index = 0; index < combined.Count - 1; index++)
                    {
                        running += combined[index].EncodedLength + sizeof(ushort);
                        split = index + 1;
                        if (running >= target)
                            break;
                    }

                    var newLeft = combined.GetRange(0, split);
                    var newRight = combined.GetRange(split, combined.Count - split);
                    if (newLeft.Count == 0 || newRight.Count == 0)
                        return false;
                    if (!LeafCellsFit(leftPage, newLeft, [])
                        || !LeafCellsFit(rightPage, newRight, []))
                    {
                        return false;
                    }

                    // Skip no-op redistribute that does not move any cells.
                    if (newLeft.Count == leftCells.Count
                        && newRight.Count == rightCells.Count
                        && newLeft[^1].RowId == leftCells[^1].RowId)
                    {
                        return false;
                    }

                    WriteSinglePage(leftPage, BuildLeafImage(leftPage, newLeft));
                    WriteSinglePage(rightPage, BuildLeafImage(rightPage, newRight));

                    // Left child's separator is always the max rowid on the left leaf; the
                    // right child keeps its existing upper bound (next separator or open max).
                    parentLinks[leftIndex] = parentLinks[leftIndex] with
                    {
                        MaximumRowId = newLeft[^1].RowId,
                    };
                    WriteOrCollapseParent(path, path.Count - 2, parentLinks);
                    return true;
                }

                private bool TryBalanceLeafSiblingRun(
                    List<PathEntry> path,
                    List<ChildLink> parentLinks,
                    int childIndex)
                {
                    if (parentLinks.Count < 3)
                        return false;

                    var first = childIndex == 0
                        ? 0
                        : childIndex == parentLinks.Count - 1
                            ? parentLinks.Count - 3
                            : childIndex - 1;
                    const int siblingCount = 3;
                    var pages = new uint[siblingCount];
                    var combined = new List<SqliteTableLeafCell>();
                    for (var index = 0; index < siblingCount; index++)
                    {
                        var pageNumber = parentLinks[first + index].PageNumber;
                        if (ParseHeaderType(pageNumber) != SqliteBtreePageType.TableLeaf)
                            return false;

                        pages[index] = pageNumber;
                        combined.AddRange(ParseLeaf(pageNumber).Cells.Select(cell => cell.Cell));
                    }

                    var assignedPages = pages.Order().ToList();
                    var groups = PartitionLeafCells(assignedPages[0], combined, appendedAtEnd: false);
                    if (groups.Count > siblingCount || groups.Any(group => group.Count == 0))
                        return false;

                    var inheritedUpperBound = parentLinks[first + siblingCount - 1].MaximumRowId;
                    var replacement = new List<ChildLink>(groups.Count);
                    for (var index = 0; index < groups.Count; index++)
                    {
                        var group = groups[index];
                        var pageNumber = assignedPages[index];
                        WriteSinglePage(pageNumber, BuildLeafImage(pageNumber, group));
                        replacement.Add(new ChildLink(
                            pageNumber,
                            index + 1 == groups.Count ? inheritedUpperBound : group[^1].RowId));
                    }

                    parentLinks.RemoveRange(first, siblingCount);
                    parentLinks.InsertRange(first, replacement);
                    WriteOrCollapseParent(path, path.Count - 2, parentLinks);
                    for (var index = groups.Count; index < assignedPages.Count; index++)
                        _io.FreePage(assignedPages[index]);
                    return true;
                }

    /// <summary>
    /// Writes <paramref name="links"/> into the interior at <paramref name="level"/>,
        /// or collapses a single-child interior: root absorb, or merge the sole child into a
        /// sibling interior under the grandparent (preserves uniform tree height).
        /// </summary>
        private void WriteOrCollapseParent(List<PathEntry> path, int level, List<ChildLink> links)
        {
            var entry = path[level];
            if (links.Count == 0)
            {
                throw new InvalidDataException(
                    $"SQLite table-interior page {entry.PageNumber} lost every child during leaf merge.");
            }

            if (links.Count == 1)
            {
                CollapseSingleChildInterior(path, level, links[0].PageNumber);
                return;
            }

            WriteSinglePage(entry.PageNumber, BuildInteriorImage(entry.PageNumber, links));
        }

        /// <summary>
        /// Collapses an interior that has exactly one remaining child. At the root, the child
        /// is absorbed into page 1 / root (height shrink). Under a non-root parent, the sole
        /// child is merged into a neighboring sibling interior so grandparent children stay
        /// the same height (never promote a leaf beside an interior).
        /// </summary>
        private void CollapseSingleChildInterior(List<PathEntry> path, int level, uint soleChildPage)
        {
            var entry = path[level];
            if (level == 0)
            {
                AbsorbChildIntoPage(entry.PageNumber, soleChildPage);
                return;
            }

            if (level < 1 || entry.ChildIndex < 0)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    $"SQLite table-interior page {entry.PageNumber} cannot collapse: missing grandparent path.");
            }

            var grandEntry = path[level - 1];
            var grandLinks = ReadChildLinks(ParseInterior(grandEntry.PageNumber));
            var parentIndex = entry.ChildIndex;
            if (parentIndex >= grandLinks.Count || grandLinks[parentIndex].PageNumber != entry.PageNumber)
            {
                // Path may be stale after prior rewrites; locate by page number.
                parentIndex = grandLinks.FindIndex(link => link.PageNumber == entry.PageNumber);
                if (parentIndex < 0)
                {
                    throw new SqliteBtreeMaintenanceRequiredException(
                        $"SQLite table-interior page {entry.PageNumber} is not a child of {grandEntry.PageNumber} during single-child collapse.");
                }
            }

            var parentUpperBound = grandLinks[parentIndex].MaximumRowId;

            // Prefer left sibling interior (keeps lower page numbers live).
            if (parentIndex > 0
                && ParseHeaderType(grandLinks[parentIndex - 1].PageNumber) == SqliteBtreePageType.TableInterior)
            {
                var leftPage = grandLinks[parentIndex - 1].PageNumber;
                var leftLinks = ReadChildLinks(ParseInterior(leftPage));
                if (leftLinks.Count > 0)
                {
                    leftLinks[^1] = leftLinks[^1] with
                    {
                        MaximumRowId = ReadSubtreeMaximumRowId(leftLinks[^1].PageNumber),
                    };
                }

                leftLinks.Add(new ChildLink(soleChildPage, long.MaxValue));
                if (InteriorLinksFit(leftPage, leftLinks))
                {
                    WriteSinglePage(leftPage, BuildInteriorImage(leftPage, leftLinks));
                    grandLinks[parentIndex - 1] = grandLinks[parentIndex - 1] with
                    {
                        MaximumRowId = parentUpperBound,
                    };
                    grandLinks.RemoveAt(parentIndex);
                    _io.FreePage(entry.PageNumber);
                    WriteOrCollapseParent(path, level - 1, grandLinks);
                    return;
                }
            }

            // Right sibling interior.
            if (parentIndex + 1 < grandLinks.Count
                && ParseHeaderType(grandLinks[parentIndex + 1].PageNumber) == SqliteBtreePageType.TableInterior)
            {
                var rightPage = grandLinks[parentIndex + 1].PageNumber;
                var rightLinks = ReadChildLinks(ParseInterior(rightPage));
                var soleMax = ReadSubtreeMaximumRowId(soleChildPage);
                rightLinks.Insert(0, new ChildLink(soleChildPage, soleMax));
                if (InteriorLinksFit(rightPage, rightLinks))
                {
                    WriteSinglePage(rightPage, BuildInteriorImage(rightPage, rightLinks));
                    grandLinks.RemoveAt(parentIndex);
                    _io.FreePage(entry.PageNumber);
                    WriteOrCollapseParent(path, level - 1, grandLinks);
                    return;
                }
            }

            if (TryRedistributeSingleChildInterior(
                    path,
                    level,
                    grandLinks,
                    parentIndex,
                    soleChildPage))
            {
                return;
            }

            throw new SqliteBtreeMaintenanceRequiredException(
                $"SQLite table-interior page {entry.PageNumber} would collapse to a single child under non-root parent {grandEntry.PageNumber}; no sibling interior can absorb the survivor.");
        }

        private bool TryRedistributeSingleChildInterior(
            List<PathEntry> path,
            int level,
            List<ChildLink> grandLinks,
            int parentIndex,
            uint soleChildPage)
        {
            if (grandLinks.Count < 2)
                return false;

            var siblingCount = Math.Min(3, grandLinks.Count);
            var first = parentIndex == 0
                ? 0
                : parentIndex == grandLinks.Count - 1
                    ? grandLinks.Count - siblingCount
                    : Math.Min(parentIndex - 1, grandLinks.Count - siblingCount);
            var pages = new uint[siblingCount];
            var combined = new List<ChildLink>();
            for (var offset = 0; offset < siblingCount; offset++)
            {
                var index = first + offset;
                var pageNumber = grandLinks[index].PageNumber;
                pages[offset] = pageNumber;
                List<ChildLink> links;
                if (index == parentIndex)
                {
                    links = [new ChildLink(soleChildPage, long.MaxValue)];
                }
                else
                {
                    if (ParseHeaderType(pageNumber) != SqliteBtreePageType.TableInterior)
                        return false;
                    links = ReadChildLinks(ParseInterior(pageNumber));
                }

                links[^1] = links[^1] with { MaximumRowId = grandLinks[index].MaximumRowId };
                combined.AddRange(links);
            }

            var assignedPages = pages.Order().ToList();
            var groups = PartitionInteriorChildren(assignedPages[0], combined);
            if (groups.Count > siblingCount || groups.Any(group => group.Count < 2))
                return false;

            var inheritedUpperBound = grandLinks[first + siblingCount - 1].MaximumRowId;
            var replacement = new List<ChildLink>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var pageNumber = assignedPages[index];
                WriteSinglePage(pageNumber, BuildInteriorImage(pageNumber, group));
                replacement.Add(new ChildLink(
                    pageNumber,
                    index + 1 == groups.Count ? inheritedUpperBound : group[^1].MaximumRowId));
            }

            grandLinks.RemoveRange(first, siblingCount);
            grandLinks.InsertRange(first, replacement);
            WriteOrCollapseParent(path, level - 1, grandLinks);
            for (var index = groups.Count; index < assignedPages.Count; index++)
                _io.FreePage(assignedPages[index]);
            return true;
        }

        private bool InteriorLinksFit(uint pageNumber, List<ChildLink> links)
        {
            if (links.Count == 0)
                return false;

            // Match PartitionInteriorChildren costing: each child charges a pointer + separator
            // varint + cell-pointer slot (conservative by one separator on the rightmost).
            var capacity = InteriorCapacity(pageNumber);
            var used = 0;
            foreach (var link in links)
            {
                used += SqliteTableInteriorCell.ChildPointerLength
                    + SqliteVarint.GetLength(unchecked((ulong)link.MaximumRowId))
                    + sizeof(ushort);
                if (used > capacity)
                    return false;
            }

            return true;
        }

        private long ReadSubtreeMaximumRowId(uint pageNumber)
        {
            switch (ParseHeaderType(pageNumber))
            {
                case SqliteBtreePageType.TableLeaf:
                    {
                        var cells = ParseLeaf(pageNumber).Cells;
                        if (cells.Count == 0)
                        {
                            throw new InvalidDataException(
                                $"SQLite table-leaf page {pageNumber} is empty while computing subtree maximum rowid.");
                        }

                        return cells[^1].Cell.RowId;
                    }
                case SqliteBtreePageType.TableInterior:
                    {
                        var view = ParseInterior(pageNumber);
                        return ReadSubtreeMaximumRowId(view.Header.RightMostChildPage);
                    }
                default:
                    throw new InvalidDataException(
                        $"SQLite page {pageNumber} is not a table b-tree page while computing subtree maximum rowid.");
            }
        }

    private bool LeafCellsFit(uint pageNumber, List<SqliteTableLeafCell> left, List<SqliteTableLeafCell> right)
        => LeafUsedBytes(left) + LeafUsedBytes(right) <= LeafCapacity(pageNumber);

    private static int LeafUsedBytes(List<SqliteTableLeafCell> cells)
    {
        var used = 0;
        foreach (var cell in cells)
            used += cell.EncodedLength + sizeof(ushort);
        return used;
    }

    private SqliteBtreePageType ParseHeaderType(uint pageNumber)
        => SqliteBtreePageHeader.Parse(_io.ReadPage(pageNumber), IsFirstPage(pageNumber)).PageType;

    /// <summary>
    /// Drops the child pointer recorded at <paramref name="level"/> and collapses
    /// a single-child interior into its parent (or root) when required.
    /// </summary>
    private void RemoveChildLink(List<PathEntry> path, int level)
    {
        var entry = path[level];
        var links = ReadChildLinks(ParseInterior(entry.PageNumber));
        if (entry.ChildIndex < 0 || entry.ChildIndex >= links.Count)
        {
            throw new InvalidOperationException(
                $"SQLite table-interior page {entry.PageNumber} has no child index {entry.ChildIndex}.");
        }

        links.RemoveAt(entry.ChildIndex);
                if (links.Count == 0)
                {
                    throw new InvalidDataException(
                        $"SQLite table-interior page {entry.PageNumber} lost every child during empty-leaf reclaim.");
                }

                if (links.Count == 1)
                {
                    CollapseSingleChildInterior(path, level, links[0].PageNumber);
                    return;
                }

                WriteSinglePage(entry.PageNumber, BuildInteriorImage(entry.PageNumber, links));
            }

    /// <summary>
    /// Copies <paramref name="childPage"/>'s b-tree payload into
    /// <paramref name="destinationPage"/> (preserving page-1 DB header bytes)
    /// and frees the child page. Used when the root collapses after its last
    /// sibling pointer is removed.
    /// </summary>
    private void AbsorbChildIntoPage(uint destinationPage, uint childPage)
    {
        if (destinationPage == childPage)
            return;

        var childHeader = SqliteBtreePageHeader.Parse(_io.ReadPage(childPage), IsFirstPage(childPage));
        switch (childHeader.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    var cells = ParseLeaf(childPage).Cells.Select(cell => cell.Cell).ToList();
                    WriteSinglePage(destinationPage, BuildLeafImage(destinationPage, cells));
                    break;
                }
            case SqliteBtreePageType.TableInterior:
                {
                    var links = ReadChildLinks(ParseInterior(childPage));
                    WriteSinglePage(destinationPage, BuildInteriorImage(destinationPage, links));
                    break;
                }
            default:
                throw new InvalidDataException(
                    $"SQLite page {childPage} cannot be absorbed into table root {destinationPage}.");
        }

        _io.FreePage(childPage);
    }

    private void FreeOverflowIfPresent(SqliteTableLeafCell cell)
    {
        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            return;

        var localLength = cell.LocalPayload.Length;
        if (cell.PayloadLength < (ulong)localLength)
        {
            throw new InvalidDataException(
                "SQLite table-leaf cell local payload exceeds its logical payload length.");
        }

        var overflowLength = cell.PayloadLength - (ulong)localLength;
        if (overflowLength == 0)
        {
            throw new InvalidDataException(
                "SQLite table-leaf cell has an unnecessary overflow page.");
        }

        SqliteOverflowChainWriter.Free(_io, firstOverflowPage, overflowLength);
    }

    private void WriteLeafAndPropagate(
        List<PathEntry> path,
        List<SqliteTableLeafCell> cells,
        bool appendedAtEnd)
    {
        var leafPage = path[^1].PageNumber;
        var groups = PartitionLeafCells(leafPage, cells, appendedAtEnd);
        if (groups.Count == 1)
        {
            WriteSinglePage(leafPage, BuildLeafImage(leafPage, groups[0]));
            return;
        }

        // Splitting the leaf replaces one child pointer in the parent with one
        // pointer per new page. The right-most group keeps the original page's
        // upper bound, so no ancestor separator above the parent can change.
        var children = new List<ChildLink>(groups.Count);
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var pageNumber = index == 0 && path.Count > 1 ? leafPage : _io.AllocatePage();
            WriteSinglePage(pageNumber, BuildLeafImage(pageNumber, group));
            children.Add(new ChildLink(pageNumber, group[^1].RowId));
        }

        if (path.Count == 1)
        {
            // The root cannot move, so every split page is newly allocated and
            // the root becomes an interior page one level deeper.
            ReplaceRoot(leafPage, children);
            return;
        }

        ReplaceChildLinks(path, path.Count - 2, children);
    }

    /// <summary>
    /// Replaces the child pointer at <paramref name="level"/> with
    /// <paramref name="children"/>, splitting and deepening as required.
    /// </summary>
    private void ReplaceChildLinks(List<PathEntry> path, int level, List<ChildLink> children)
    {
        while (true)
        {
            var entry = path[level];
            var view = ParseInterior(entry.PageNumber);
            var links = ReadChildLinks(view);
            links.RemoveAt(entry.ChildIndex);
            links.InsertRange(entry.ChildIndex, children);

            var groups = PartitionInteriorChildren(entry.PageNumber, links);
            if (groups.Count == 1)
            {
                WriteSinglePage(entry.PageNumber, BuildInteriorImage(entry.PageNumber, groups[0]));
                return;
            }

            var promoted = new List<ChildLink>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var pageNumber = index == 0 && level > 0 ? entry.PageNumber : _io.AllocatePage();
                WriteSinglePage(pageNumber, BuildInteriorImage(pageNumber, group));
                promoted.Add(new ChildLink(pageNumber, group[^1].MaximumRowId));
            }

            if (level == 0)
            {
                ReplaceRoot(entry.PageNumber, promoted);
                return;
            }

            children = promoted;
            level--;
        }
    }

    private void ReplaceRoot(uint rootPage, List<ChildLink> children)
    {
        var groups = PartitionInteriorChildren(rootPage, children);
        while (groups.Count > 1)
        {
            var promoted = new List<ChildLink>(groups.Count);
            foreach (var group in groups)
            {
                var pageNumber = _io.AllocatePage();
                WriteSinglePage(pageNumber, BuildInteriorImage(pageNumber, group));
                promoted.Add(new ChildLink(pageNumber, group[^1].MaximumRowId));
            }

            groups = PartitionInteriorChildren(rootPage, promoted);
        }

        WriteSinglePage(rootPage, BuildInteriorImage(rootPage, groups[0]));
    }

    /// <summary>
    /// Restores exact-maximum separators after a deletion lowered the maximum
    /// rowid of the subtree at the end of <paramref name="path"/>.
    /// </summary>
    private void UpdateSeparatorChain(List<PathEntry> path, long maximumRowId)
    {
        for (var level = path.Count - 2; level >= 0; level--)
        {
            var entry = path[level];
            var view = ParseInterior(entry.PageNumber);
            if (entry.ChildIndex == view.Cells.Count)
                continue;

            var links = ReadChildLinks(view);
            links[entry.ChildIndex] = links[entry.ChildIndex] with { MaximumRowId = maximumRowId };
            WriteSinglePage(entry.PageNumber, BuildInteriorImage(entry.PageNumber, links));
            return;
        }
    }

    private List<PathEntry> Descend(uint rootPage, long rowId)
    {
        var path = new List<PathEntry>(8);
        var pageNumber = rootPage;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            var header = SqliteBtreePageHeader.Parse(_io.ReadPage(pageNumber), IsFirstPage(pageNumber));
            switch (header.PageType)
            {
                case SqliteBtreePageType.TableLeaf:
                    path.Add(new PathEntry(pageNumber, -1));
                    return path;
                case SqliteBtreePageType.TableInterior:
                    {
                        var view = ParseInterior(pageNumber);
                        var child = view.SearchChild(rowId);
                        path.Add(new PathEntry(pageNumber, child.ChildIndex));
                        pageNumber = child.ChildPage;
                        break;
                    }
                default:
                    throw new InvalidDataException(
                        $"SQLite page {pageNumber} is not part of a rowid-table b-tree.");
            }
        }

        throw new InvalidDataException($"SQLite table b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels.");
    }

    private SqliteTableLeafPageView ParseLeaf(uint pageNumber)
        => SqliteTableLeafPageView.Parse(_io.ReadPage(pageNumber), _io.UsableSpace, IsFirstPage(pageNumber));

    private SqliteTableInteriorPageView ParseInterior(uint pageNumber)
        => SqliteTableInteriorPageView.Parse(_io.ReadPage(pageNumber), _io.UsableSpace, IsFirstPage(pageNumber));

    private static List<ChildLink> ReadChildLinks(SqliteTableInteriorPageView view)
    {
        var links = new List<ChildLink>(view.Cells.Count + 1);
        foreach (var cell in view.Cells)
            links.Add(new ChildLink(cell.Cell.LeftChildPage, cell.Cell.RowId));

        // The right-most child has no separator; long.MaxValue is its implicit
        // upper bound and is replaced when the child is promoted below a parent.
        links.Add(new ChildLink(view.Header.RightMostChildPage, long.MaxValue));
        return links;
    }

    private SqliteTableLeafCell CreateLeafCell(long rowId, ReadOnlySpan<byte> record)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)record.Length),
            _io.UsableSpace);
        if (!layout.UsesOverflow)
            return SqliteTableLeafCell.Create(rowId, record, _io.UsableSpace);

        var firstOverflowPage = SqliteOverflowChainWriter.Write(_io, record[layout.LocalPayloadLength..]);
        return SqliteTableLeafCell.Create(
            rowId,
            checked((ulong)record.Length),
            record[..layout.LocalPayloadLength],
            firstOverflowPage,
            _io.UsableSpace);
    }

    /// <summary>
    /// Splits one leaf's cells across as few pages as they need.
    /// </summary>
    /// <remarks>
    /// SQLite distinguishes the two ways a leaf overflows. An append at the
    /// right edge takes the quick-balance path, which leaves the full page alone
    /// and starts a fresh page, so a monotonically increasing key sequence packs
    /// pages completely. Any other overflow distributes the cells evenly, which
    /// leaves each resulting page roughly half empty so the following insertions
    /// into that neighbourhood do not split again. Packing greedily in the second
    /// case would make every subsequent middle insertion split.
    /// </remarks>
    private List<List<SqliteTableLeafCell>> PartitionLeafCells(
        uint pageNumber,
        List<SqliteTableLeafCell> cells,
        bool appendedAtEnd)
    {
        var capacity = LeafCapacity(pageNumber);
        var costs = new int[cells.Count];
        var total = 0;
        for (var index = 0; index < cells.Count; index++)
        {
            costs[index] = cells[index].EncodedLength + sizeof(ushort);
            if (costs[index] > capacity)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    $"A SQLite table-leaf cell of {cells[index].EncodedLength} bytes does not fit in page {pageNumber}.");
            }

            total += costs[index];
        }

        if (total <= capacity)
            return [cells];

        var minimumPageCount = MinimumPageCount(costs, capacity);
        if (appendedAtEnd)
            return Distribute(cells, costs, capacity);

        // Aim for an even distribution, but never spend more pages than the
        // cells strictly need.
        var even = Distribute(cells, costs, (total + minimumPageCount - 1) / minimumPageCount);
        return even.Count <= minimumPageCount ? even : Distribute(cells, costs, capacity);
    }

    private static List<List<SqliteTableLeafCell>> Distribute(
        List<SqliteTableLeafCell> cells,
        int[] costs,
        int budget)
    {
        var groups = new List<List<SqliteTableLeafCell>> { new List<SqliteTableLeafCell>() };
        var used = 0;
        for (var index = 0; index < cells.Count; index++)
        {
            if (used + costs[index] > budget && groups[^1].Count > 0)
            {
                groups.Add([]);
                used = 0;
            }

            groups[^1].Add(cells[index]);
            used += costs[index];
        }

        return groups;
    }

    /// <summary>
    /// The fewest pages that can hold <paramref name="costs"/> in order.
    /// </summary>
    private static int MinimumPageCount(int[] costs, int capacity)
    {
        var pageCount = 1;
        var used = 0;
        foreach (var cost in costs)
        {
            if (used + cost > capacity)
            {
                pageCount++;
                used = 0;
            }

            used += cost;
        }

        return pageCount;
    }

    private List<List<ChildLink>> PartitionInteriorChildren(uint pageNumber, List<ChildLink> links)
    {
        var capacity = InteriorCapacity(pageNumber);
        var groups = new List<List<ChildLink>> { new List<ChildLink>() };
        var used = 0;
        for (var index = 0; index < links.Count; index++)
        {
            // Every child except the group's last one needs a separator cell, so
            // charging one to each child is conservative by exactly one cell.
            var cost = SqliteTableInteriorCell.ChildPointerLength
                + SqliteVarint.GetLength(unchecked((ulong)links[index].MaximumRowId))
                + sizeof(ushort);
            if (used + cost > capacity && groups[^1].Count > 0)
            {
                groups.Add([]);
                used = 0;
            }

            groups[^1].Add(links[index]);
            used += cost;
        }

        // A one-child group cannot carry a separator for a following group, so
        // rebalance the tail rather than emitting a degenerate page.
        if (groups.Count > 1 && groups[^1].Count == 1 && groups[^2].Count > 1)
        {
            var moved = groups[^2][^1];
            groups[^2].RemoveAt(groups[^2].Count - 1);
            groups[^1].Insert(0, moved);
        }

        return groups;
    }

    private byte[] BuildLeafImage(uint pageNumber, List<SqliteTableLeafCell> cells)
    {
        var isFirstPage = IsFirstPage(pageNumber);
        var builder = new SqliteTableLeafPageBuilder(_io.PageSize, _io.UsableSpace, isFirstPage);
        foreach (var cell in cells)
            builder.Append(cell);

        var image = isFirstPage ? _io.ReadPage(pageNumber) : new byte[_io.PageSize];
        builder.WriteTo(image);
        return image;
    }

    private byte[] BuildInteriorImage(uint pageNumber, List<ChildLink> links)
    {
        if (links.Count == 0)
            throw new InvalidOperationException("A SQLite table-interior page requires at least one child.");

        var isFirstPage = IsFirstPage(pageNumber);
        var builder = new SqliteTableInteriorPageBuilder(
            _io.PageSize,
            _io.UsableSpace,
            links[^1].PageNumber,
            isFirstPage);
        for (var index = 0; index < links.Count - 1; index++)
            builder.Append(SqliteTableInteriorCell.Create(links[index].PageNumber, links[index].MaximumRowId));

        var image = isFirstPage ? _io.ReadPage(pageNumber) : new byte[_io.PageSize];
        builder.WriteTo(image);
        return image;
    }

    private void WriteSinglePage(uint pageNumber, ReadOnlySpan<byte> image)
        => _io.WritePage(pageNumber, image);

    private int LeafCapacity(uint pageNumber)
        => _io.UsableSpace
           - (IsFirstPage(pageNumber) ? SqliteBtreePageHeader.FirstPageOffset : 0)
           - SqliteBtreePageHeader.LeafHeaderSize;

    private int InteriorCapacity(uint pageNumber)
        => _io.UsableSpace
           - (IsFirstPage(pageNumber) ? SqliteBtreePageHeader.FirstPageOffset : 0)
           - SqliteBtreePageHeader.InteriorHeaderSize;

    private static bool IsFirstPage(uint pageNumber) => pageNumber == 1;

    private readonly record struct PathEntry(uint PageNumber, int ChildIndex);

    private readonly record struct ChildLink(uint PageNumber, long MaximumRowId);
}
