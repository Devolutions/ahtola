using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>A SQLite table-interior cell: a left child page followed by a rowid key.</summary>
public sealed class SqliteTableInteriorCell
{
    public const int ChildPointerLength = sizeof(uint);

    private SqliteTableInteriorCell(uint leftChildPage, long rowId, int encodedLength)
    {
        LeftChildPage = leftChildPage;
        RowId = rowId;
        EncodedLength = encodedLength;
    }

    /// <summary>The non-zero child page containing keys less than or equal to <see cref="RowId"/>.</summary>
    public uint LeftChildPage { get; }

    /// <summary>
    /// The inclusive routing upper bound for <see cref="LeftChildPage"/>.
    /// SQLite may retain a value above that child's live maximum after deletion.
    /// </summary>
    public long RowId { get; }

    /// <summary>The exact number of bytes occupied by this cell.</summary>
    public int EncodedLength { get; }

    /// <summary>Creates a table-interior cell with a non-zero left child page.</summary>
    public static SqliteTableInteriorCell Create(uint leftChildPage, long rowId)
    {
        if (leftChildPage == 0)
            throw new ArgumentOutOfRangeException(nameof(leftChildPage), "SQLite interior child pages are 1-based.");

        return new SqliteTableInteriorCell(
            leftChildPage,
            rowId,
            ChildPointerLength + SqliteVarint.GetLength(unchecked((ulong)rowId)));
    }

    /// <summary>Decodes a table-interior cell from the beginning of <paramref name="source"/>.</summary>
    public static SqliteTableInteriorCell Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < ChildPointerLength + 1)
            throw new InvalidDataException("SQLite table-interior cell is truncated.");

        var leftChildPage = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (leftChildPage == 0)
            throw new InvalidDataException("SQLite table-interior cell has a zero left child page.");
        if (!SqliteVarint.TryRead(source[ChildPointerLength..], out var rowId, out var rowIdLength))
            throw new InvalidDataException("SQLite table-interior cell has an invalid rowid varint.");

        return new SqliteTableInteriorCell(
            leftChildPage,
            unchecked((long)rowId),
            ChildPointerLength + rowIdLength);
    }

    /// <summary>Encodes this cell into a new SQLite-format byte array.</summary>
    public byte[] ToArray()
    {
        var destination = new byte[EncodedLength];
        WriteTo(destination);
        return destination;
    }

    /// <summary>Encodes this cell at the beginning of <paramref name="destination"/>.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
            throw new ArgumentException($"Destination must contain at least {EncodedLength} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination, LeftChildPage);
        SqliteVarint.Write(unchecked((ulong)RowId), destination[ChildPointerLength..]);
    }
}

/// <summary>Packs a compact SQLite table-interior page from sorted upper-bound separator cells.</summary>
/// <remarks>
/// This is a single-page codec. It has no page allocation, parent propagation,
/// sibling handling, or durable multi-page mutation.
/// </remarks>
public sealed class SqliteTableInteriorPageBuilder
{
    private readonly List<SqliteTableInteriorCell> _cells = [];
    private readonly HashSet<uint> _childPages;
    private readonly int _headerOffset;
    private long? _lastRowId;

    /// <summary>Creates a builder with the page's mandatory non-zero right-most child.</summary>
    public SqliteTableInteriorPageBuilder(
        int pageSize,
        int usableSpace,
        uint rightMostChildPage,
        bool isFirstPage = false)
    {
        if (rightMostChildPage == 0)
            throw new ArgumentOutOfRangeException(nameof(rightMostChildPage), "SQLite interior child pages are 1-based.");

        SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.TableInterior,
            pageSize,
            isFirstPage,
            usableSpace);

        PageSize = pageSize;
        UsableSpace = usableSpace;
        IsFirstPage = isFirstPage;
        RightMostChildPage = rightMostChildPage;
        _headerOffset = isFirstPage ? SqliteBtreePageHeader.FirstPageOffset : 0;
        _childPages = [rightMostChildPage];
    }

    /// <summary>The physical page size.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>Whether the b-tree header starts after the database header.</summary>
    public bool IsFirstPage { get; }

    /// <summary>The child selected when a target rowid exceeds every separator.</summary>
    public uint RightMostChildPage { get; }

    /// <summary>Separator cells in exact pointer-array order.</summary>
    public IReadOnlyList<SqliteTableInteriorCell> Cells
        => new ReadOnlyCollection<SqliteTableInteriorCell>(_cells);

    /// <summary>
    /// Adds one left-child/inclusive-upper-bound separator. Keys and child pages must be
    /// unique so this one page cannot introduce an ambiguous path.
    /// </summary>
    public void Append(SqliteTableInteriorCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (_lastRowId is { } lastRowId && cell.RowId <= lastRowId)
            throw new ArgumentException("SQLite table-interior rowids must be strictly increasing.", nameof(cell));
        if (_cells.Count == ushort.MaxValue)
            throw new InvalidOperationException("A SQLite table-interior page cannot contain more than 65535 cells.");
        if (_childPages.Contains(cell.LeftChildPage))
            throw new ArgumentException("SQLite table-interior child pages must be distinct.", nameof(cell));

        EnsureFits(cell.EncodedLength);
        _cells.Add(cell);
        _childPages.Add(cell.LeftChildPage);
        _lastRowId = cell.RowId;
    }

    /// <summary>Returns a zero-initialized packed page image.</summary>
    public byte[] Build()
    {
        var page = new byte[PageSize];
        WriteTo(page);
        return page;
    }

    /// <summary>
    /// Replaces the b-tree portion of <paramref name="destination"/> while
    /// preserving page-one database-header and reserved-space bytes.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != PageSize)
        {
            throw new ArgumentException(
                $"SQLite table-interior destination must be exactly {PageSize} bytes.",
                nameof(destination));
        }

        var cellContentAreaOffset = CalculateCellContentAreaOffset();
        destination.Slice(_headerOffset, UsableSpace - _headerOffset).Clear();

        var offsets = new ushort[_cells.Count];
        var cellOffset = UsableSpace;
        for (var index = _cells.Count - 1; index >= 0; index--)
        {
            var cell = _cells[index];
            cellOffset -= cell.EncodedLength;
            cell.WriteTo(destination[cellOffset..UsableSpace]);
            offsets[index] = checked((ushort)cellOffset);
        }

        var header = SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.TableInterior,
            PageSize,
            IsFirstPage,
            UsableSpace) with
        {
            CellCount = checked((ushort)_cells.Count),
            CellContentAreaOffset = cellContentAreaOffset,
            RightMostChildPage = RightMostChildPage,
        };
        header.WriteTo(destination);
        SqliteCellPointerArray.WriteTo(destination, header, offsets, UsableSpace);
    }

    private void EnsureFits(int additionalCellLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additionalCellLength);

        var cellBytes = additionalCellLength;
        foreach (var existingCell in _cells)
            cellBytes = checked(cellBytes + existingCell.EncodedLength);

        var pointerArrayEnd = checked(
            _headerOffset
            + SqliteBtreePageHeader.InteriorHeaderSize
            + ((_cells.Count + 1) * sizeof(ushort)));
        if (UsableSpace - cellBytes < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite table-interior cells and their pointer array do not fit in the page's usable space.");
        }
    }

    private int CalculateCellContentAreaOffset()
    {
        var cellBytes = 0;
        foreach (var cell in _cells)
            cellBytes = checked(cellBytes + cell.EncodedLength);

        var cellContentAreaOffset = UsableSpace - cellBytes;
        var pointerArrayEnd = checked(
            _headerOffset
            + SqliteBtreePageHeader.InteriorHeaderSize
            + (_cells.Count * sizeof(ushort)));
        if (cellContentAreaOffset < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite table-interior cells and their pointer array do not fit in the page's usable space.");
        }

        return cellContentAreaOffset;
    }
}

/// <summary>An immutable, validated snapshot of a SQLite table-interior page.</summary>
public sealed class SqliteTableInteriorPageView
{
    private SqliteTableInteriorPageView(
        int pageSize,
        int usableSpace,
        SqliteBtreePageHeader header,
        SqliteCellPointerArray cellPointers,
        SqliteTableInteriorPageCell[] cells)
    {
        PageSize = pageSize;
        UsableSpace = usableSpace;
        Header = header;
        CellPointers = cellPointers;
        Cells = new ReadOnlyCollection<SqliteTableInteriorPageCell>(cells);
    }

    /// <summary>The physical page size.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>The validated interior-page header.</summary>
    public SqliteBtreePageHeader Header { get; }

    /// <summary>Physical cell offsets in logical separator order.</summary>
    public SqliteCellPointerArray CellPointers { get; }

    /// <summary>Decoded separator cells in logical order.</summary>
    public IReadOnlyList<SqliteTableInteriorPageCell> Cells { get; }

    /// <summary>Parses and validates a table-interior page snapshot.</summary>
    public static SqliteTableInteriorPageView Parse(
        ReadOnlySpan<byte> page,
        int usableSpace,
        bool isFirstPage = false)
    {
        var snapshot = page.ToArray();
        var header = SqliteBtreePageHeader.Parse(snapshot, isFirstPage, usableSpace);
        if (header.PageType != SqliteBtreePageType.TableInterior)
            throw new InvalidDataException("SQLite page is not a table-interior b-tree page.");
        if (header.RightMostChildPage == 0)
            throw new InvalidDataException("SQLite table-interior page has a zero right-most child page.");

        var pointers = SqliteCellPointerArray.Parse(snapshot, header, usableSpace);
        SqliteBtreePageValidation.ValidateFreeblocks(
            snapshot,
            header,
            usableSpace,
            SqliteTableInteriorCell.ChildPointerLength);

        var cells = new SqliteTableInteriorPageCell[pointers.Count];
        var ranges = new List<(int Start, int End)>(pointers.Count);
        var childPages = new HashSet<uint>();
        long? previousRowId = null;
        for (var index = 0; index < pointers.Count; index++)
        {
            var offset = pointers[index];
            var cell = SqliteTableInteriorCell.Decode(snapshot[offset..usableSpace]);
            var end = checked(offset + cell.EncodedLength);
            if (end > usableSpace)
                throw new InvalidDataException("SQLite table-interior cell extends into reserved page space.");
            if (previousRowId is { } previous && cell.RowId <= previous)
                throw new InvalidDataException("SQLite table-interior rowids are not in strictly increasing order.");

            SqliteBtreePageValidation.RequireNonZeroAndDistinctChild(
                cell.LeftChildPage,
                childPages,
                "table-interior");
            cells[index] = new SqliteTableInteriorPageCell(offset, cell);
            ranges.Add((offset, end));
            previousRowId = cell.RowId;
        }

        SqliteBtreePageValidation.RequireNonZeroAndDistinctChild(
            header.RightMostChildPage,
            childPages,
            "table-interior");
        SqliteBtreePageValidation.ValidateCellRanges(ranges, "table-interior");
        SqliteBtreePageValidation.ValidateCellsDoNotOverlapFreeblocks(
            snapshot,
            header,
            usableSpace,
            ranges,
            "table-interior");
        return new SqliteTableInteriorPageView(page.Length, usableSpace, header, pointers, cells);
    }

    /// <summary>
    /// Returns whether a non-empty child's live rowid range fits between its
    /// exclusive lower separator and inclusive upper separator.
    /// </summary>
    internal bool IsChildRangeValid(int childIndex, long minimumRowId, long maximumRowId)
    {
        if (childIndex < 0 || childIndex > Cells.Count || minimumRowId > maximumRowId)
            return false;

        return (childIndex == 0 || minimumRowId > Cells[childIndex - 1].Cell.RowId)
            && (childIndex == Cells.Count || maximumRowId <= Cells[childIndex].Cell.RowId);
    }

    /// <summary>
    /// Selects the child whose key range can contain <paramref name="rowId"/>.
    /// A separator rowid remains in its left child, as required by SQLite table
    /// interior-page semantics.
    /// </summary>
    public SqliteBtreeChildSearchResult SearchChild(long rowId)
    {
        var low = 0;
        var high = Cells.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (Cells[middle].Cell.RowId < rowId)
                low = middle + 1;
            else
                high = middle;
        }

        var childPage = low == Cells.Count
            ? Header.RightMostChildPage
            : Cells[low].Cell.LeftChildPage;
        return new SqliteBtreeChildSearchResult(
            low,
            childPage,
            low < Cells.Count && Cells[low].Cell.RowId == rowId);
    }
}

/// <summary>A table-interior cell together with its physical page offset.</summary>
public sealed record SqliteTableInteriorPageCell(ushort Offset, SqliteTableInteriorCell Cell);
