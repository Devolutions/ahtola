using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// Packs a complete SQLite index-leaf page in its configured key order.
/// </summary>
/// <remarks>
/// This builder creates one compact leaf image. It does not descend a B-tree,
/// split a page, update parent pages, or balance a tree.
/// </remarks>
public sealed class SqliteIndexLeafPageBuilder
{
    private readonly List<CellEntry> _cells = [];
    private readonly int _headerOffset;
    private byte[]? _lastRecord;

    /// <summary>Creates a builder for one index-leaf page.</summary>
    public SqliteIndexLeafPageBuilder(
        int pageSize,
        int usableSpace,
        SqliteIndexRecordComparer? recordComparer = null,
        bool isFirstPage = false)
    {
        if (isFirstPage)
        {
            throw new ArgumentException(
                "SQLite page 1 is the sqlite_schema table root and cannot be an index-leaf page.",
                nameof(isFirstPage));
        }

        SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.IndexLeaf,
            pageSize,
            isFirstPage,
            usableSpace);

        PageSize = pageSize;
        UsableSpace = usableSpace;
        IsFirstPage = isFirstPage;
        RecordComparer = recordComparer ?? new SqliteIndexRecordComparer();
        _headerOffset = isFirstPage ? SqliteBtreePageHeader.FirstPageOffset : 0;
    }

    /// <summary>The physical page size in bytes.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>Whether the b-tree header begins after SQLite's database header.</summary>
    public bool IsFirstPage { get; }

    /// <summary>The comparator used to validate index record order.</summary>
    public SqliteIndexRecordComparer RecordComparer { get; }

    /// <summary>The appended cells in strict configured record order.</summary>
    public IReadOnlyList<SqliteIndexLeafCell> Cells
        => new ReadOnlyCollection<SqliteIndexLeafCell>(_cells.Select(entry => entry.Cell).ToArray());

    /// <summary>
    /// Appends a fully local cell after all existing cells.
    /// </summary>
    public void Append(SqliteIndexLeafCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.FirstOverflowPage is not null)
        {
            throw new ArgumentException(
                "An overflowing SQLite index cell requires its complete record for ordering validation.",
                nameof(cell));
        }

        Append(cell, cell.LocalPayload.Span);
    }

    /// <summary>
    /// Appends <paramref name="cell"/> after all existing cells, validating the
    /// complete logical <paramref name="record"/> before retaining the cell.
    /// </summary>
    public void Append(SqliteIndexLeafCell cell, ReadOnlySpan<byte> record)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if ((ulong)record.Length != cell.PayloadLength)
        {
            throw new ArgumentException(
                "SQLite index record length does not match its cell payload length.",
                nameof(record));
        }
        if (!record[..cell.LocalPayload.Length].SequenceEqual(cell.LocalPayload.Span))
        {
            throw new ArgumentException(
                "SQLite index record does not begin with the cell's local payload.",
                nameof(record));
        }

        RecordComparer.Validate(record);
        if (_lastRecord is not null && RecordComparer.Compare(_lastRecord, record) >= 0)
            throw new ArgumentException("SQLite index records must be strictly increasing in configured order.", nameof(record));
        if (_cells.Count == ushort.MaxValue)
            throw new InvalidOperationException("A SQLite index-leaf page cannot contain more than 65535 cells.");

        EnsureFits(cell.EncodedLength);
        var recordCopy = record.ToArray();
        _cells.Add(new CellEntry(cell, recordCopy));
        _lastRecord = recordCopy;
    }

    /// <summary>Returns a zero-initialized page image packed with the appended cells.</summary>
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
    /// compact index-leaf image while preserving page-one and reserved bytes.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != PageSize)
        {
            throw new ArgumentException(
                $"SQLite index-leaf destination must be exactly {PageSize} bytes.",
                nameof(destination));
        }

        var cellContentAreaOffset = CalculateCellContentAreaOffset();
        destination.Slice(_headerOffset, UsableSpace - _headerOffset).Clear();

        var offsets = new ushort[_cells.Count];
        var cellOffset = UsableSpace;
        for (var index = _cells.Count - 1; index >= 0; index--)
        {
            var cell = _cells[index].Cell;
            cellOffset -= cell.EncodedLength;
            cell.WriteTo(destination[cellOffset..UsableSpace]);
            offsets[index] = checked((ushort)cellOffset);
        }

        var header = SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.IndexLeaf,
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
            cellBytes = checked(cellBytes + existingCell.Cell.EncodedLength);

        var pointerArrayEnd = checked(
            _headerOffset
            + SqliteBtreePageHeader.LeafHeaderSize
            + ((_cells.Count + 1) * sizeof(ushort)));
        if (UsableSpace - cellBytes < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite index-leaf cells and their pointer array do not fit in the page's usable space.");
        }
    }

    private int CalculateCellContentAreaOffset()
    {
        var cellBytes = 0;
        foreach (var cell in _cells)
            cellBytes = checked(cellBytes + cell.Cell.EncodedLength);

        var cellContentAreaOffset = UsableSpace - cellBytes;
        var pointerArrayEnd = checked(
            _headerOffset
            + SqliteBtreePageHeader.LeafHeaderSize
            + (_cells.Count * sizeof(ushort)));
        if (cellContentAreaOffset < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite index-leaf cells and their pointer array do not fit in the page's usable space.");
        }

        return cellContentAreaOffset;
    }

    private sealed record CellEntry(SqliteIndexLeafCell Cell, byte[] Record);
}
