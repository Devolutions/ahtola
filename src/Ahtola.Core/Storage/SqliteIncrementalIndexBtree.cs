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
/// split promotes one cell out of a page rather than duplicating a key. A
/// deletion whose key lives in an interior page, or that would empty a leaf,
/// requires separator pull-down / page-merge rules this writer deliberately
/// omits (unlike table trees, index separators are live keys) and raises
/// <see cref="SqliteBtreeMaintenanceRequiredException"/> instead.
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
            throw new SqliteBtreeMaintenanceRequiredException(
                "Removing an index key stored in an interior separator requires page merging.");
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
                if (entries.Count == 0 && path.Count > 1)
                {
                    throw new SqliteBtreeMaintenanceRequiredException(
                        $"Removing an index key would empty child page {leafPage}, which requires page merging.");
                }

                _io.WritePage(leafPage, BuildLeafImage(entries));
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
