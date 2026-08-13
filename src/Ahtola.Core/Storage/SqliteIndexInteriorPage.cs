using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// A SQLite index-interior cell: a left child page followed by an index-key
/// payload encoded with the index-leaf payload codec.
/// </summary>
public sealed class SqliteIndexInteriorCell
{
    public const int ChildPointerLength = sizeof(uint);

    private SqliteIndexInteriorCell(uint leftChildPage, SqliteIndexLeafCell key)
    {
        LeftChildPage = leftChildPage;
        Key = key;
        EncodedLength = checked(ChildPointerLength + key.EncodedLength);
    }

    /// <summary>The non-zero child page containing records less than <see cref="Key"/>.</summary>
    public uint LeftChildPage { get; }

    /// <summary>The encoded SQLite record key and optional overflow pointer.</summary>
    public SqliteIndexLeafCell Key { get; }

    /// <summary>The complete byte count of the child pointer and key cell.</summary>
    public int EncodedLength { get; }

    /// <summary>Creates a fully local index-interior cell.</summary>
    public static SqliteIndexInteriorCell Create(
        uint leftChildPage,
        ReadOnlySpan<byte> record,
        int usableSpace)
        => Create(leftChildPage, SqliteIndexLeafCell.Create(record, usableSpace));

    /// <summary>Creates an index-interior cell whose key has already-materialized local bytes.</summary>
    public static SqliteIndexInteriorCell Create(
        uint leftChildPage,
        ulong payloadLength,
        ReadOnlySpan<byte> localPayload,
        uint? firstOverflowPage,
        int usableSpace)
        => Create(
            leftChildPage,
            SqliteIndexLeafCell.Create(payloadLength, localPayload, firstOverflowPage, usableSpace));

    /// <summary>Combines a non-zero left child with a validated index payload codec.</summary>
    public static SqliteIndexInteriorCell Create(uint leftChildPage, SqliteIndexLeafCell key)
    {
        if (leftChildPage == 0)
            throw new ArgumentOutOfRangeException(nameof(leftChildPage), "SQLite interior child pages are 1-based.");
        ArgumentNullException.ThrowIfNull(key);
        return new SqliteIndexInteriorCell(leftChildPage, key);
    }

    /// <summary>Decodes one index-interior cell from the beginning of <paramref name="source"/>.</summary>
    public static SqliteIndexInteriorCell Decode(ReadOnlySpan<byte> source, int usableSpace)
    {
        if (source.Length < ChildPointerLength + 1)
            throw new InvalidDataException("SQLite index-interior cell is truncated.");

        var leftChildPage = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (leftChildPage == 0)
            throw new InvalidDataException("SQLite index-interior cell has a zero left child page.");

        return new SqliteIndexInteriorCell(
            leftChildPage,
            SqliteIndexLeafCell.Decode(source[ChildPointerLength..], usableSpace));
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
        Key.WriteTo(destination[ChildPointerLength..]);
    }
}

/// <summary>Packs a compact SQLite index-interior page from strictly ordered records.</summary>
/// <remarks>
/// This is a single-page codec. It does not allocate pages, update parents,
/// maintain sibling links, or perform a durable multi-page mutation.
/// </remarks>
public sealed class SqliteIndexInteriorPageBuilder
{
    private readonly List<CellEntry> _cells = [];
    private readonly HashSet<uint> _childPages;
    private readonly int _headerOffset;
    private byte[]? _lastRecord;

    /// <summary>Creates an index-interior builder with its right-most child page.</summary>
    public SqliteIndexInteriorPageBuilder(
        int pageSize,
        int usableSpace,
        uint rightMostChildPage,
        SqliteIndexRecordComparer? recordComparer = null,
        bool isFirstPage = false)
    {
        if (isFirstPage)
        {
            throw new ArgumentException(
                "SQLite page 1 is the sqlite_schema table root and cannot be an index-interior page.",
                nameof(isFirstPage));
        }
        if (rightMostChildPage == 0)
            throw new ArgumentOutOfRangeException(nameof(rightMostChildPage), "SQLite interior child pages are 1-based.");

        SqliteBtreePageHeader.CreateEmpty(
            SqliteBtreePageType.IndexInterior,
            pageSize,
            isFirstPage,
            usableSpace);

        PageSize = pageSize;
        UsableSpace = usableSpace;
        IsFirstPage = isFirstPage;
        RightMostChildPage = rightMostChildPage;
        RecordComparer = recordComparer ?? new SqliteIndexRecordComparer();
        _headerOffset = isFirstPage ? SqliteBtreePageHeader.FirstPageOffset : 0;
        _childPages = [rightMostChildPage];
    }

    /// <summary>The physical page size.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>Whether the page is page 1. Index interior pages reject this value.</summary>
    public bool IsFirstPage { get; }

    /// <summary>The child selected when a target key exceeds every separator.</summary>
    public uint RightMostChildPage { get; }

    /// <summary>The index comparator used for key validation.</summary>
    public SqliteIndexRecordComparer RecordComparer { get; }

    /// <summary>Separator cells in exact pointer-array order.</summary>
    public IReadOnlyList<SqliteIndexInteriorCell> Cells
        => new ReadOnlyCollection<SqliteIndexInteriorCell>(_cells.Select(entry => entry.Cell).ToArray());

    /// <summary>Adds a fully local record key after every existing separator.</summary>
    public void Append(SqliteIndexInteriorCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.Key.FirstOverflowPage is not null)
        {
            throw new ArgumentException(
                "An overflowing SQLite index key requires its complete record for ordering validation.",
                nameof(cell));
        }

        Append(cell, cell.Key.LocalPayload.Span);
    }

    /// <summary>
    /// Adds a left-child/key separator after every existing separator, validating
    /// complete record order and child-page uniqueness first.
    /// </summary>
    public void Append(SqliteIndexInteriorCell cell, ReadOnlySpan<byte> record)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if ((ulong)record.Length != cell.Key.PayloadLength)
        {
            throw new ArgumentException(
                "SQLite index record length does not match its interior-cell payload length.",
                nameof(record));
        }
        if (!record[..cell.Key.LocalPayload.Length].SequenceEqual(cell.Key.LocalPayload.Span))
        {
            throw new ArgumentException(
                "SQLite index record does not begin with the interior cell's local payload.",
                nameof(record));
        }
        if (_childPages.Contains(cell.LeftChildPage))
            throw new ArgumentException("SQLite index-interior child pages must be distinct.", nameof(cell));

        RecordComparer.Validate(record);
        if (_lastRecord is not null && RecordComparer.Compare(_lastRecord, record) >= 0)
            throw new ArgumentException("SQLite index-interior records must be strictly increasing in configured order.", nameof(record));
        if (_cells.Count == ushort.MaxValue)
            throw new InvalidOperationException("A SQLite index-interior page cannot contain more than 65535 cells.");

        EnsureFits(cell.EncodedLength);
        var recordCopy = record.ToArray();
        _cells.Add(new CellEntry(cell, recordCopy));
        _childPages.Add(cell.LeftChildPage);
        _lastRecord = recordCopy;
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
    /// preserving reserved-space bytes.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != PageSize)
        {
            throw new ArgumentException(
                $"SQLite index-interior destination must be exactly {PageSize} bytes.",
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
            SqliteBtreePageType.IndexInterior,
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
            cellBytes = checked(cellBytes + existingCell.Cell.EncodedLength);

        var pointerArrayEnd = checked(
            _headerOffset
            + SqliteBtreePageHeader.InteriorHeaderSize
            + ((_cells.Count + 1) * sizeof(ushort)));
        if (UsableSpace - cellBytes < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite index-interior cells and their pointer array do not fit in the page's usable space.");
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
            + SqliteBtreePageHeader.InteriorHeaderSize
            + (_cells.Count * sizeof(ushort)));
        if (cellContentAreaOffset < pointerArrayEnd)
        {
            throw new InvalidOperationException(
                "SQLite index-interior cells and their pointer array do not fit in the page's usable space.");
        }

        return cellContentAreaOffset;
    }

    private sealed record CellEntry(SqliteIndexInteriorCell Cell, byte[] Record);
}

/// <summary>An immutable, validated snapshot of a SQLite index-interior page.</summary>
public sealed class SqliteIndexInteriorPageView
{
    private readonly SqliteIndexRecordComparer _recordComparer;
    private readonly byte[][]? _records;

    private SqliteIndexInteriorPageView(
        int pageSize,
        int usableSpace,
        SqliteBtreePageHeader header,
        SqliteCellPointerArray cellPointers,
        SqliteIndexInteriorPageCell[] cells,
        SqliteIndexRecordComparer recordComparer,
        byte[][]? records)
    {
        PageSize = pageSize;
        UsableSpace = usableSpace;
        Header = header;
        CellPointers = cellPointers;
        Cells = new ReadOnlyCollection<SqliteIndexInteriorPageCell>(cells);
        _recordComparer = recordComparer;
        _records = records;
    }

    /// <summary>The physical page size.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>The validated interior-page header.</summary>
    public SqliteBtreePageHeader Header { get; }

    /// <summary>Physical cell offsets in logical separator order.</summary>
    public SqliteCellPointerArray CellPointers { get; }

    /// <summary>Decoded separator cells in logical record order.</summary>
    public IReadOnlyList<SqliteIndexInteriorPageCell> Cells { get; }

    /// <summary>Whether every complete record was available for key-order validation.</summary>
    public bool HasVerifiedRecordOrdering => _records is not null;

    /// <summary>The record comparator used while validating this page.</summary>
    public SqliteIndexRecordComparer RecordComparer => _recordComparer;

    /// <summary>Parses and validates an index-interior page snapshot.</summary>
    public static SqliteIndexInteriorPageView Parse(
        ReadOnlySpan<byte> page,
        int usableSpace,
        SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8,
        bool isFirstPage = false,
        SqliteOverflowChainReader? overflowReader = null,
        SqliteIndexRecordComparer? recordComparer = null)
    {
        if (isFirstPage)
        {
            throw new InvalidDataException(
                "SQLite page 1 is the sqlite_schema table root and cannot be an index-interior page.");
        }

        var snapshot = page.ToArray();
        var header = SqliteBtreePageHeader.Parse(snapshot, isFirstPage, usableSpace);
        if (header.PageType != SqliteBtreePageType.IndexInterior)
            throw new InvalidDataException("SQLite page is not an index-interior b-tree page.");
        if (header.RightMostChildPage == 0)
            throw new InvalidDataException("SQLite index-interior page has a zero right-most child page.");

        var pointers = SqliteCellPointerArray.Parse(snapshot, header, usableSpace);
        SqliteBtreePageValidation.ValidateFreeblocks(
            snapshot,
            header,
            usableSpace,
            SqliteIndexInteriorCell.ChildPointerLength);

        var cells = new SqliteIndexInteriorPageCell[pointers.Count];
        var ranges = new List<(int Start, int End)>(pointers.Count);
        var childPages = new HashSet<uint>();
        for (var index = 0; index < pointers.Count; index++)
        {
            var offset = pointers[index];
            var cell = SqliteIndexInteriorCell.Decode(snapshot[offset..usableSpace], usableSpace);
            var end = checked(offset + cell.EncodedLength);
            if (end > usableSpace)
                throw new InvalidDataException("SQLite index-interior cell extends into reserved page space.");

            SqliteBtreePageValidation.RequireNonZeroAndDistinctChild(
                cell.LeftChildPage,
                childPages,
                "index-interior");
            cells[index] = new SqliteIndexInteriorPageCell(offset, cell);
            ranges.Add((offset, end));
        }

        SqliteBtreePageValidation.RequireNonZeroAndDistinctChild(
            header.RightMostChildPage,
            childPages,
            "index-interior");
        SqliteBtreePageValidation.ValidateCellRanges(ranges, "index-interior");
        SqliteBtreePageValidation.ValidateCellsDoNotOverlapFreeblocks(
            snapshot,
            header,
            usableSpace,
            ranges,
            "index-interior");

        recordComparer ??= new SqliteIndexRecordComparer(textEncoding);
        var records = ReadAndValidateRecords(cells, recordComparer, overflowReader);
        return new SqliteIndexInteriorPageView(
            page.Length,
            usableSpace,
            header,
            pointers,
            cells,
            recordComparer,
            records);
    }

    /// <summary>
    /// Selects the child whose key range can contain <paramref name="record"/>.
    /// A matching record is stored in the separator cell, so callers must use
    /// <see cref="SqliteBtreeChildSearchResult.IsSeparatorKey"/> before descending.
    /// </summary>
    public SqliteBtreeChildSearchResult SearchChild(ReadOnlySpan<byte> record)
    {
        var records = RequireRecords();
        _recordComparer.Validate(record);
        var low = 0;
        var high = records.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_recordComparer.Compare(records[middle], record) < 0)
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
            low < records.Length && _recordComparer.Compare(records[low], record) == 0);
    }

    /// <summary>Returns a copy of a complete, ordering-validated separator record.</summary>
    public byte[] GetRecord(int index) => RequireRecords()[index].ToArray();

    private byte[][] RequireRecords()
    {
        return _records ?? throw new InvalidOperationException(
            "SQLite index-interior search requires complete records; supply an overflow reader when parsing.");
    }

    private static byte[][]? ReadAndValidateRecords(
        IReadOnlyList<SqliteIndexInteriorPageCell> cells,
        SqliteIndexRecordComparer comparer,
        SqliteOverflowChainReader? overflowReader)
    {
        if (cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null) && overflowReader is null)
            return null;

        var records = new byte[cells.Count][];
        byte[]? previousRecord = null;
        for (var index = 0; index < cells.Count; index++)
        {
            var pageCell = cells[index];
            var record = pageCell.Cell.Key.FirstOverflowPage is null
                ? pageCell.Cell.Key.LocalPayload.ToArray()
                : overflowReader!.ReadPayload(pageCell.Cell.Key);
            comparer.Validate(record);
            if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                throw new InvalidDataException("SQLite index-interior records are not in strictly increasing configured order.");

            previousRecord = record;
            records[index] = record;
        }

        return records;
    }
}

/// <summary>An index-interior cell together with its physical page offset.</summary>
public sealed record SqliteIndexInteriorPageCell(ushort Offset, SqliteIndexInteriorCell Cell);
