using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// Packs a complete SQLite table-leaf page from already-encoded logical cells.
/// </summary>
/// <remarks>
/// This builder only creates a single leaf image. It does not descend a
/// B-tree, split a page, update parent pages, or balance a tree.
/// </remarks>
public sealed class SqliteTableLeafPageBuilder
{
    private readonly List<SqliteTableLeafCell> _cells = [];
    private readonly int _headerOffset;
    private long? _lastRowId;

    /// <summary>
    /// Creates a builder for one table-leaf page.
    /// </summary>
    public SqliteTableLeafPageBuilder(int pageSize, int usableSpace, bool isFirstPage = false)
    {
        SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.TableLeaf,
            pageSize,
            isFirstPage,
            usableSpace);

        PageSize = pageSize;
        UsableSpace = usableSpace;
        IsFirstPage = isFirstPage;
        _headerOffset = isFirstPage ? SqliteBtreePageHeader.FirstPageOffset : 0;
    }

    /// <summary>The physical page size in bytes.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>Whether the b-tree header begins after SQLite's database header.</summary>
    public bool IsFirstPage { get; }

    /// <summary>The appended cells, in strictly increasing rowid order.</summary>
    public IReadOnlyList<SqliteTableLeafCell> Cells => new ReadOnlyCollection<SqliteTableLeafCell>(_cells);

    /// <summary>
    /// Adds one cell after all existing cells. Rowids must be strictly
    /// increasing because the cell-pointer array is SQLite's logical key order.
    /// </summary>
    public void Append(SqliteTableLeafCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (_lastRowId is { } lastRowId && cell.RowId <= lastRowId)
            throw new ArgumentException("SQLite table-leaf rowids must be strictly increasing.", nameof(cell));
        if (_cells.Count == ushort.MaxValue)
            throw new InvalidOperationException("A SQLite table-leaf page cannot contain more than 65535 cells.");

        EnsureFits(cell.EncodedLength);
        _cells.Add(cell);
        _lastRowId = cell.RowId;
    }

    /// <summary>
    /// Returns a zero-initialized page image packed with the appended cells.
    /// </summary>
    /// <remarks>
    /// For page 1, callers that need a valid database header should use
    /// <see cref="WriteTo"/> with an existing page-one image.
    /// </remarks>
    public byte[] Build()
    {
        var page = new byte[PageSize];
        WriteTo(page);
        return page;
    }

    /// <summary>
    /// Replaces the b-tree portion of <paramref name="destination"/> with a
    /// compact table-leaf image while preserving page-one database-header and
    /// reserved-space bytes.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != PageSize)
        {
            throw new ArgumentException(
                $"SQLite table-leaf destination must be exactly {PageSize} bytes.",
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
            SqliteBtreePageType.TableLeaf,
            PageSize,
            IsFirstPage,
            UsableSpace) with
        {
            CellCount = checked((ushort)_cells.Count),
            CellContentAreaOffset = cellContentAreaOffset,
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
            + SqliteBtreePageHeader.LeafHeaderSize
            + ((_cells.Count + 1) * sizeof(ushort)));
        if (UsableSpace - cellBytes < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite table-leaf cells and their pointer array do not fit in the page's usable space.");
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
            + SqliteBtreePageHeader.LeafHeaderSize
            + (_cells.Count * sizeof(ushort)));
        if (cellContentAreaOffset < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite table-leaf cells and their pointer array do not fit in the page's usable space.");
        }

        return cellContentAreaOffset;
    }
}
