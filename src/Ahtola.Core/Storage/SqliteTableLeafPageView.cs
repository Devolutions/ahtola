using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable, fully validated snapshot of the cells on a SQLite table-leaf page.
/// </summary>
public sealed class SqliteTableLeafPageView
{
    private SqliteTableLeafPageView(
        int pageSize,
        int usableSpace,
        SqliteBtreePageHeader header,
        SqliteCellPointerArray cellPointers,
        SqliteTableLeafPageCell[] cells)
    {
        PageSize = pageSize;
        UsableSpace = usableSpace;
        Header = header;
        CellPointers = cellPointers;
        Cells = new ReadOnlyCollection<SqliteTableLeafPageCell>(cells);
    }

    /// <summary>The validated b-tree header.</summary>
    public SqliteBtreePageHeader Header { get; }

    /// <summary>The physical size of the parsed page.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the parsed page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>The validated cell offsets in logical rowid order.</summary>
    public SqliteCellPointerArray CellPointers { get; }

    /// <summary>Immutable decoded cells in logical rowid order.</summary>
    public IReadOnlyList<SqliteTableLeafPageCell> Cells { get; }

    /// <summary>
    /// Parses a snapshot of a table-leaf page. The source page is copied before
    /// exposing any cell data, so later caller mutations cannot invalidate the view.
    /// </summary>
    public static SqliteTableLeafPageView Parse(
        ReadOnlySpan<byte> page,
        int usableSpace,
        bool isFirstPage = false)
    {
        var snapshot = page.ToArray();
        var header = SqliteBtreePageHeader.Parse(snapshot, isFirstPage, usableSpace);
        if (header.PageType != SqliteBtreePageType.TableLeaf)
            throw new InvalidDataException("SQLite page is not a table-leaf b-tree page.");

        var pointers = SqliteCellPointerArray.Parse(snapshot, header, usableSpace);
        SqliteBtreePageValidation.ValidateFreeblocks(
            snapshot,
            header,
            usableSpace,
            MinimumCellStorageLength);

        var cells = new SqliteTableLeafPageCell[pointers.Count];
        var ranges = new List<(int Start, int End)>(pointers.Count);
        long? previousRowId = null;
        for (var index = 0; index < pointers.Count; index++)
        {
            var offset = pointers[index];
            var cell = SqliteTableLeafCell.Decode(snapshot.AsSpan(offset, usableSpace - offset), usableSpace);
            var end = checked(offset + cell.EncodedLength);
            if (end > usableSpace)
                throw new InvalidDataException("SQLite table-leaf cell extends into reserved page space.");
            if (previousRowId is { } previous && cell.RowId <= previous)
                throw new InvalidDataException("SQLite table-leaf cell rowids are not in strictly increasing order.");

            cells[index] = new SqliteTableLeafPageCell(offset, cell);
            ranges.Add((offset, end));
            previousRowId = cell.RowId;
        }

        SqliteBtreePageValidation.ValidateCellRanges(ranges, "table-leaf");
        SqliteBtreePageValidation.ValidateCellsDoNotOverlapFreeblocks(
            snapshot,
            header,
            usableSpace,
            ranges,
            "table-leaf");
        return new SqliteTableLeafPageView(page.Length, usableSpace, header, pointers, cells);
    }

    /// <summary>
    /// Finds the first rowid not less than <paramref name="rowId"/>.
    /// </summary>
    public SqliteBtreeSearchResult Search(long rowId)
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

        return new SqliteBtreeSearchResult(
            low,
            low < Cells.Count && Cells[low].Cell.RowId == rowId);
    }

    private const int MinimumCellStorageLength = SqliteTableLeafCell.MinimumStorageLength;
}

/// <summary>A table-leaf cell together with its physical page offset.</summary>
public sealed record SqliteTableLeafPageCell(ushort Offset, SqliteTableLeafCell Cell);
