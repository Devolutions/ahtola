namespace Ahtola.Core.Storage;

/// <summary>
/// A cursor-based incremental writer for SQLite index b-trees, including the
/// primary-key trees of WITHOUT ROWID tables.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="SqliteIncrementalTableBtree"/>, a mutation descends from the
/// root to one leaf and dirties only that leaf plus the pages a split creates.
/// </para>
/// <para>
/// SQLite stores a real index entry in every interior separator cell, so a
/// split promotes one cell out of a page rather than duplicating a key.
/// Deletion rotates the predecessor into an interior separator, then balances
/// up to three neighboring pages. Parent separators are pulled down into the
/// redistributed children, surplus pages are returned to the freelist, and a
/// root with one child absorbs that child without changing its catalog page.
/// </para>
/// </remarks>
public sealed class SqliteIncrementalIndexBtree
{
    private const int MaximumDepth = 64;

    private readonly ISqliteBtreePageIo _io;
    private readonly SqliteIndexRecordComparer _comparer;
    private readonly SqliteOverflowChainReader _overflowReader;
    private readonly SqliteTextEncoding _textEncoding;

    /// <summary>Creates a writer for one index's key ordering.</summary>
    public SqliteIncrementalIndexBtree(
        ISqliteBtreePageIo pageIo,
        SqliteIndexRecordComparer comparer,
        SqliteTextEncoding textEncoding)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        ArgumentNullException.ThrowIfNull(comparer);
        _io = pageIo;
        _comparer = comparer;
        _textEncoding = textEncoding;
        _overflowReader = new SqliteOverflowChainReader(pageIo);
    }

    /// <summary>Inserts one complete index record.</summary>
    public void Insert(uint rootPage, byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = Descend(rootPage, record, out var separatorMatch);
        if (separatorMatch)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                "An index key that already exists in an interior separator cannot be inserted incrementally.");
        }

        var view = ParseLeaf(path[^1].PageNumber);
        var search = view.Search(record);
        if (search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                "An index key that already exists cannot be inserted incrementally.");
        }

        var entries = ReadLeafEntries(view);
        entries.Insert(search.Index, CreateLeafEntry(record));
        WriteLeafAndPropagate(path, entries);
    }

    /// <summary>Removes one complete index record.</summary>
    public void Delete(uint rootPage, byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = Descend(rootPage, record, out var separatorMatch);
        if (separatorMatch)
        {
            DeleteInteriorSeparator(path, path.Count - 1);
            return;
        }

        var leafPage = path[^1].PageNumber;
        var view = ParseLeaf(leafPage);
        var search = view.Search(record);
        if (!search.IsExact)
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                "The index key is absent, so the caller's view of the committed index is stale.");
        }

        var entries = ReadLeafEntries(view);
        FreeOverflowIfPresent(entries[search.Index].Cell);
        entries.RemoveAt(search.Index);
        _io.WritePage(leafPage, BuildLeafImage(entries));
        BalanceAfterDelete(path, path.Count - 1);
    }

    private void DeleteInteriorSeparator(List<PathEntry> path, int separatorLevel)
    {
        var separatorEntry = path[separatorLevel];
        var links = ReadChildLinks(ParseInterior(separatorEntry.PageNumber));
        if (separatorEntry.ChildIndex < 0
            || separatorEntry.ChildIndex >= links.Count - 1
            || links[separatorEntry.ChildIndex].Separator is not { } deletedSeparator)
        {
            throw new InvalidDataException(
                $"SQLite index-interior page {separatorEntry.PageNumber} has no separator at {separatorEntry.ChildIndex}.");
        }

        var predecessorPath = new List<PathEntry>(path);
        var pageNumber = links[separatorEntry.ChildIndex].PageNumber;
        while (true)
        {
            var header = SqliteBtreePageHeader.Parse(_io.ReadPage(pageNumber));
            if (header.PageType == SqliteBtreePageType.IndexLeaf)
            {
                predecessorPath.Add(new PathEntry(pageNumber, -1));
                break;
            }

            if (header.PageType != SqliteBtreePageType.IndexInterior)
            {
                throw new InvalidDataException(
                    $"SQLite page {pageNumber} is not part of the predecessor subtree for index separator deletion.");
            }

            var interior = ParseInterior(pageNumber);
            predecessorPath.Add(new PathEntry(pageNumber, interior.Cells.Count));
            pageNumber = interior.Header.RightMostChildPage;
        }

        var predecessorLeaf = ParseLeaf(pageNumber);
        var entries = ReadLeafEntries(predecessorLeaf);
        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                $"SQLite index-leaf page {pageNumber} is empty while replacing an interior separator.");
        }

        var predecessor = entries[^1];
        entries.RemoveAt(entries.Count - 1);
        FreeOverflowIfPresent(deletedSeparator.Cell);
        links[separatorEntry.ChildIndex] = links[separatorEntry.ChildIndex] with
        {
            Separator = predecessor,
        };

        if (!InteriorLinksFit(links))
        {
            throw new SqliteBtreeMaintenanceRequiredException(
                $"Replacing separator {separatorEntry.ChildIndex} on SQLite index-interior page {separatorEntry.PageNumber} would overflow the page.");
        }

        _io.WritePage(separatorEntry.PageNumber, BuildInteriorImage(links));
        _io.WritePage(pageNumber, BuildLeafImage(entries));
        var predecessorLevel = predecessorPath.Count - 1;
        var predecessorUnderflows = IsUnderfull(pageNumber);
        BalanceAfterDelete(predecessorPath, predecessorLevel);
        if (!predecessorUnderflows)
            BalanceAfterDelete(predecessorPath, separatorLevel);
    }

    private void BalanceAfterDelete(List<PathEntry> path, int level)
    {
        if (level == 0)
        {
            CollapseIndexRoot(path[0].PageNumber);
            return;
        }

        var pageNumber = path[level].PageNumber;
        if (!IsUnderfull(pageNumber))
            return;

        BalanceSiblingRun(path, level);
    }

    private void BalanceSiblingRun(List<PathEntry> path, int level)
    {
        var parentLevel = level - 1;
        var parentPage = path[parentLevel].PageNumber;
        var parentLinks = ReadChildLinks(ParseInterior(parentPage));
        var pageNumber = path[level].PageNumber;
        var childIndex = parentLinks.FindIndex(link => link.PageNumber == pageNumber);
        if (childIndex < 0)
        {
            throw new InvalidDataException(
                $"SQLite index page {pageNumber} is not a child of page {parentPage} during balancing.");
        }

        var siblingCount = Math.Min(3, parentLinks.Count);
        var first = childIndex == 0
            ? 0
            : childIndex == parentLinks.Count - 1
                ? parentLinks.Count - siblingCount
                : Math.Min(childIndex - 1, parentLinks.Count - siblingCount);
        var oldPages = parentLinks
            .Skip(first)
            .Take(siblingCount)
            .Select(link => link.PageNumber)
            .ToArray();
        var pageType = SqliteBtreePageHeader.Parse(_io.ReadPage(oldPages[0])).PageType;
        if (oldPages.Any(page => SqliteBtreePageHeader.Parse(_io.ReadPage(page)).PageType != pageType))
        {
            throw new InvalidDataException(
                $"SQLite index siblings under page {parentPage} do not have a uniform page type.");
        }

        var inheritedSeparator = parentLinks[first + siblingCount - 1].Separator;
        List<ChildLink> replacement;
        List<uint> assignedPages;
        switch (pageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                {
                    var combined = new List<IndexEntry>();
                    for (var offset = 0; offset < siblingCount; offset++)
                    {
                        combined.AddRange(ReadLeafEntries(ParseLeaf(oldPages[offset])));
                        if (offset + 1 < siblingCount)
                        {
                            combined.Add(parentLinks[first + offset].Separator
                                ?? throw new InvalidDataException(
                                    $"SQLite index parent {parentPage} is missing divider {first + offset}."));
                        }
                    }

                    var split = PartitionLeafEntries(combined);
                    assignedPages = AssignSiblingPages(oldPages, split.Groups.Count);
                    for (var index = 0; index < split.Groups.Count; index++)
                        _io.WritePage(assignedPages[index], BuildLeafImage(split.Groups[index]));

                    replacement = new List<ChildLink>(split.Groups.Count);
                    for (var index = 0; index < split.Groups.Count; index++)
                    {
                        replacement.Add(new ChildLink(
                            assignedPages[index],
                            index < split.Separators.Count
                                ? split.Separators[index]
                                : inheritedSeparator));
                    }
                    break;
                }

            case SqliteBtreePageType.IndexInterior:
                {
                    var combined = new List<ChildLink>();
                    for (var offset = 0; offset < siblingCount; offset++)
                    {
                        var childLinks = ReadChildLinks(ParseInterior(oldPages[offset]));
                        if (offset + 1 < siblingCount)
                        {
                            childLinks[^1] = childLinks[^1] with
                            {
                                Separator = parentLinks[first + offset].Separator
                                    ?? throw new InvalidDataException(
                                        $"SQLite index parent {parentPage} is missing divider {first + offset}."),
                            };
                        }
                        combined.AddRange(childLinks);
                    }

                    var split = PartitionInteriorLinks(combined);
                    assignedPages = AssignSiblingPages(oldPages, split.Groups.Count);
                    for (var index = 0; index < split.Groups.Count; index++)
                        _io.WritePage(assignedPages[index], BuildInteriorImage(split.Groups[index]));

                    replacement = new List<ChildLink>(split.Groups.Count);
                    for (var index = 0; index < split.Groups.Count; index++)
                    {
                        replacement.Add(new ChildLink(
                            assignedPages[index],
                            index < split.Separators.Count
                                ? split.Separators[index]
                                : inheritedSeparator));
                    }
                    break;
                }

            default:
                throw new InvalidDataException(
                    $"SQLite page {oldPages[0]} is not an index b-tree page during balancing.");
        }

        parentLinks.RemoveRange(first, siblingCount);
        parentLinks.InsertRange(first, replacement);
        if (parentLevel == 0 && parentLinks.Count == 1)
        {
            AbsorbIndexChild(parentPage, parentLinks[0].PageNumber);
        }
        else
        {
            if (!InteriorLinksFit(parentLinks))
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    $"Balancing SQLite index children would overflow parent page {parentPage}.");
            }
            _io.WritePage(parentPage, BuildInteriorImage(parentLinks));
        }

        var assigned = assignedPages.ToHashSet();
        foreach (var oldPage in oldPages)
        {
            if (!assigned.Contains(oldPage))
                _io.FreePage(oldPage);
        }

        if (parentLevel > 0 && IsUnderfull(parentPage))
            BalanceSiblingRun(path, parentLevel);
        else if (parentLevel == 0)
            CollapseIndexRoot(parentPage);
    }

    private List<uint> AssignSiblingPages(uint[] oldPages, int requiredCount)
    {
        var pages = oldPages.Order().Take(requiredCount).ToList();
        while (pages.Count < requiredCount)
            pages.Add(_io.AllocatePage());
        return pages;
    }

    private bool IsUnderfull(uint pageNumber)
    {
        var header = SqliteBtreePageHeader.Parse(_io.ReadPage(pageNumber));
        var capacity = _io.UsableSpace - (header.PageType == SqliteBtreePageType.IndexLeaf
            ? SqliteBtreePageHeader.LeafHeaderSize
            : SqliteBtreePageHeader.InteriorHeaderSize);
        var used = header.PageType switch
        {
            SqliteBtreePageType.IndexLeaf => ReadLeafEntries(ParseLeaf(pageNumber))
                .Sum(entry => entry.Cell.EncodedLength + sizeof(ushort)),
            SqliteBtreePageType.IndexInterior => ReadChildLinks(ParseInterior(pageNumber))
                .Where(link => link.Separator is not null)
                .Sum(link => SqliteIndexInteriorCell.ChildPointerLength
                    + link.Separator!.Cell.EncodedLength
                    + sizeof(ushort)),
            _ => throw new InvalidDataException(
                $"SQLite page {pageNumber} is not an index b-tree page during underflow detection."),
        };
        return used * 3 < capacity;
    }

    private bool InteriorLinksFit(List<ChildLink> links)
    {
        var used = 0;
        foreach (var link in links)
        {
            if (link.Separator is null)
                continue;
            used += SqliteIndexInteriorCell.ChildPointerLength
                + link.Separator.Cell.EncodedLength
                + sizeof(ushort);
        }
        return used <= _io.UsableSpace - SqliteBtreePageHeader.InteriorHeaderSize;
    }

    private void CollapseIndexRoot(uint rootPage)
    {
        var header = SqliteBtreePageHeader.Parse(_io.ReadPage(rootPage));
        if (header.PageType != SqliteBtreePageType.IndexInterior)
            return;

        var links = ReadChildLinks(ParseInterior(rootPage));
        if (links.Count == 1)
            AbsorbIndexChild(rootPage, links[0].PageNumber);
    }

    private void AbsorbIndexChild(uint rootPage, uint childPage)
    {
        if (rootPage == childPage)
            return;

        var header = SqliteBtreePageHeader.Parse(_io.ReadPage(childPage));
        switch (header.PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                _io.WritePage(rootPage, BuildLeafImage(ReadLeafEntries(ParseLeaf(childPage))));
                break;
            case SqliteBtreePageType.IndexInterior:
                _io.WritePage(rootPage, BuildInteriorImage(ReadChildLinks(ParseInterior(childPage))));
                break;
            default:
                throw new InvalidDataException(
                    $"SQLite page {childPage} cannot be absorbed into index root {rootPage}.");
        }

        _io.FreePage(childPage);
    }

    private void FreeOverflowIfPresent(SqliteIndexLeafCell cell)
    {
        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            return;

        var localLength = cell.LocalPayload.Length;
        if (cell.PayloadLength < (ulong)localLength)
        {
            throw new InvalidDataException(
                "SQLite index-leaf cell local payload exceeds its logical payload length.");
        }

        var overflowLength = cell.PayloadLength - (ulong)localLength;
        if (overflowLength == 0)
        {
            throw new InvalidDataException(
                "SQLite index-leaf cell has an unnecessary overflow page.");
        }

        SqliteOverflowChainWriter.Free(_io, firstOverflowPage, overflowLength);
    }

    private void WriteLeafAndPropagate(List<PathEntry> path, List<IndexEntry> entries)
    {
        var leafPage = path[^1].PageNumber;
        var split = PartitionLeafEntries(entries);
        if (split.Groups.Count == 1)
        {
            _io.WritePage(leafPage, BuildLeafImage(split.Groups[0]));
            return;
        }

        var children = new List<ChildLink>(split.Groups.Count);
        for (var index = 0; index < split.Groups.Count; index++)
        {
            var pageNumber = index == 0 && path.Count > 1 ? leafPage : _io.AllocatePage();
            _io.WritePage(pageNumber, BuildLeafImage(split.Groups[index]));
            children.Add(new ChildLink(
                pageNumber,
                index < split.Separators.Count ? split.Separators[index] : null));
        }

        if (path.Count == 1)
        {
            ReplaceRoot(leafPage, children);
            return;
        }

        ReplaceChildLinks(path, path.Count - 2, children);
    }

    private void ReplaceChildLinks(List<PathEntry> path, int level, List<ChildLink> children)
    {
        while (true)
        {
            var entry = path[level];
            var view = ParseInterior(entry.PageNumber);
            var links = ReadChildLinks(view);
            var replaced = links[entry.ChildIndex];
            links.RemoveAt(entry.ChildIndex);

            // The right-most page of the replacement run inherits the separator
            // that used to follow the child it replaces.
            var replacement = new List<ChildLink>(children);
            replacement[^1] = replacement[^1] with { Separator = replaced.Separator };
            links.InsertRange(entry.ChildIndex, replacement);

            var split = PartitionInteriorLinks(links);
            if (split.Groups.Count == 1)
            {
                _io.WritePage(entry.PageNumber, BuildInteriorImage(split.Groups[0]));
                return;
            }

            var promoted = new List<ChildLink>(split.Groups.Count);
            for (var index = 0; index < split.Groups.Count; index++)
            {
                var pageNumber = index == 0 && level > 0 ? entry.PageNumber : _io.AllocatePage();
                _io.WritePage(pageNumber, BuildInteriorImage(split.Groups[index]));
                promoted.Add(new ChildLink(
                    pageNumber,
                    index < split.Separators.Count ? split.Separators[index] : null));
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
        var split = PartitionInteriorLinks(children);
        while (split.Groups.Count > 1)
        {
            var promoted = new List<ChildLink>(split.Groups.Count);
            for (var index = 0; index < split.Groups.Count; index++)
            {
                var pageNumber = _io.AllocatePage();
                _io.WritePage(pageNumber, BuildInteriorImage(split.Groups[index]));
                promoted.Add(new ChildLink(
                    pageNumber,
                    index < split.Separators.Count ? split.Separators[index] : null));
            }

            split = PartitionInteriorLinks(promoted);
        }

        _io.WritePage(rootPage, BuildInteriorImage(split.Groups[0]));
    }

    private List<PathEntry> Descend(uint rootPage, byte[] record, out bool separatorMatch)
    {
        separatorMatch = false;
        var path = new List<PathEntry>(8);
        var pageNumber = rootPage;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            var header = SqliteBtreePageHeader.Parse(_io.ReadPage(pageNumber));
            switch (header.PageType)
            {
                case SqliteBtreePageType.IndexLeaf:
                    path.Add(new PathEntry(pageNumber, -1));
                    return path;
                case SqliteBtreePageType.IndexInterior:
                    {
                        var view = ParseInterior(pageNumber);
                        var child = view.SearchChild(record);
                        path.Add(new PathEntry(pageNumber, child.ChildIndex));
                        if (child.IsSeparatorKey)
                        {
                            separatorMatch = true;
                            return path;
                        }

                        pageNumber = child.ChildPage;
                        break;
                    }
                default:
                    throw new InvalidDataException($"SQLite page {pageNumber} is not part of an index b-tree.");
            }
        }

        throw new InvalidDataException(
            $"SQLite index b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels.");
    }

    private SqliteIndexLeafPageView ParseLeaf(uint pageNumber)
        => SqliteIndexLeafPageView.Parse(
            _io.ReadPage(pageNumber),
            _io.UsableSpace,
            _textEncoding,
            isFirstPage: false,
            _overflowReader,
            _comparer);

    private SqliteIndexInteriorPageView ParseInterior(uint pageNumber)
        => SqliteIndexInteriorPageView.Parse(
            _io.ReadPage(pageNumber),
            _io.UsableSpace,
            _textEncoding,
            isFirstPage: false,
            _overflowReader,
            _comparer);

    private static List<IndexEntry> ReadLeafEntries(SqliteIndexLeafPageView view)
    {
        var entries = new List<IndexEntry>(view.Cells.Count);
        for (var index = 0; index < view.Cells.Count; index++)
            entries.Add(new IndexEntry(view.Cells[index].Cell, view.GetRecord(index)));

        return entries;
    }

    private static List<ChildLink> ReadChildLinks(SqliteIndexInteriorPageView view)
    {
        var links = new List<ChildLink>(view.Cells.Count + 1);
        for (var index = 0; index < view.Cells.Count; index++)
        {
            links.Add(new ChildLink(
                view.Cells[index].Cell.LeftChildPage,
                new IndexEntry(view.Cells[index].Cell.Key, view.GetRecord(index))));
        }

        links.Add(new ChildLink(view.Header.RightMostChildPage, null));
        return links;
    }

    private IndexEntry CreateLeafEntry(byte[] record)
    {
        _comparer.Validate(record);
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            checked((ulong)record.Length),
            _io.UsableSpace);
        if (!layout.UsesOverflow)
            return new IndexEntry(SqliteIndexLeafCell.Create(record, _io.UsableSpace), record);

        var firstOverflowPage = SqliteOverflowChainWriter.Write(_io, record.AsSpan(layout.LocalPayloadLength));
        return new IndexEntry(
            SqliteIndexLeafCell.Create(
                checked((ulong)record.Length),
                record.AsSpan(0, layout.LocalPayloadLength),
                firstOverflowPage,
                _io.UsableSpace),
            record);
    }

    private LeafSplit PartitionLeafEntries(List<IndexEntry> entries)
    {
        var capacity = _io.UsableSpace - SqliteBtreePageHeader.LeafHeaderSize;
        var groups = new List<List<IndexEntry>> { new List<IndexEntry>() };
        var separators = new List<IndexEntry>();
        var used = 0;
        foreach (var entry in entries)
        {
            var cost = entry.Cell.EncodedLength + sizeof(ushort);
            if (used + cost <= capacity)
            {
                groups[^1].Add(entry);
                used += cost;
                continue;
            }

            if (groups[^1].Count == 0)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    $"A SQLite index-leaf cell of {entry.Cell.EncodedLength} bytes does not fit in an empty page.");
            }

            // The entry that does not fit becomes the separator promoted into the
            // parent, exactly as SQLite's index b-tree split does.
            separators.Add(entry);
            groups.Add([]);
            used = 0;
        }

        if (groups[^1].Count == 0)
        {
            if (separators.Count == 0)
                throw new InvalidOperationException("A SQLite index-leaf split produced an empty page.");

            // Nothing followed the last promoted separator, so it descends into
            // the new page and the previous group's last entry is promoted in
            // its place. Every group but the last must still be followed by
            // exactly one separator, so the counts have to stay in step.
            if (groups[^2].Count < 2)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    "A SQLite index-leaf split cannot promote a separator without emptying a page.");
            }

            groups[^1].Add(separators[^1]);
            separators[^1] = groups[^2][^1];
            groups[^2].RemoveAt(groups[^2].Count - 1);
        }

        return new LeafSplit(groups, separators);
    }

    private InteriorSplit PartitionInteriorLinks(List<ChildLink> links)
    {
        var capacity = _io.UsableSpace - SqliteBtreePageHeader.InteriorHeaderSize;
        var groups = new List<List<ChildLink>> { new List<ChildLink>() };
        var separators = new List<IndexEntry>();
        var used = 0;
        foreach (var link in links)
        {
            if (link.Separator is null)
            {
                // A keyless right-most child costs no cell, so it always fits.
                groups[^1].Add(link);
                continue;
            }

            var cost = SqliteIndexInteriorCell.ChildPointerLength
                + link.Separator.Cell.EncodedLength
                + sizeof(ushort);
            if (used + cost <= capacity)
            {
                groups[^1].Add(link);
                used += cost;
                continue;
            }

            if (groups[^1].Count == 0)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    "A SQLite index-interior separator does not fit in an empty page.");
            }

            groups[^1].Add(link with { Separator = null });
            separators.Add(link.Separator);
            groups.Add([]);
            used = 0;
        }

        if (groups[^1].Count == 0)
            throw new InvalidOperationException("A SQLite index-interior split produced an empty page.");

        // A group holding only the keyless right-most child would be an interior
        // page with no cells, which the loader rejects.
        foreach (var group in groups)
        {
            if (group.Count == 1 && group[0].Separator is null)
            {
                throw new SqliteBtreeMaintenanceRequiredException(
                    "A SQLite index-interior split would produce a cell-less interior page.");
            }
        }

        return new InteriorSplit(groups, separators);
    }

    private byte[] BuildLeafImage(List<IndexEntry> entries)
    {
        var builder = new SqliteIndexLeafPageBuilder(_io.PageSize, _io.UsableSpace, _comparer);
        foreach (var entry in entries)
            builder.Append(entry.Cell, entry.Record);

        return builder.Build();
    }

    private byte[] BuildInteriorImage(List<ChildLink> links)
    {
        if (links.Count == 0 || links[^1].Separator is not null)
            throw new InvalidOperationException("A SQLite index-interior page requires a keyless right-most child.");

        var builder = new SqliteIndexInteriorPageBuilder(
            _io.PageSize,
            _io.UsableSpace,
            links[^1].PageNumber,
            _comparer);
        for (var index = 0; index < links.Count - 1; index++)
        {
            if (links[index].Separator is not { } separator)
            {
                throw new InvalidOperationException(
                    $"SQLite index-interior child {index} of {links.Count} has no separator key.");
            }

            builder.Append(
                SqliteIndexInteriorCell.Create(links[index].PageNumber, separator.Cell),
                separator.Record);
        }

        return builder.Build();
    }

    private readonly record struct PathEntry(uint PageNumber, int ChildIndex);

    private readonly record struct ChildLink(uint PageNumber, IndexEntry? Separator);

    private sealed record IndexEntry(SqliteIndexLeafCell Cell, byte[] Record);

    private sealed record LeafSplit(List<List<IndexEntry>> Groups, List<IndexEntry> Separators);

    private sealed record InteriorSplit(List<List<ChildLink>> Groups, List<IndexEntry> Separators);
}
