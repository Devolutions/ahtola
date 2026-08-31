using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable, fully validated snapshot of the physical cells on a SQLite
/// index-leaf page.
/// </summary>
public sealed class SqliteIndexLeafPageView
{
    private SqliteIndexLeafPageView(
        int pageSize,
        int usableSpace,
        SqliteBtreePageHeader header,
        SqliteCellPointerArray cellPointers,
        SqliteIndexLeafPageCell[] cells,
        SqliteIndexRecordComparer recordComparer,
        byte[][]? records)
    {
        PageSize = pageSize;
        UsableSpace = usableSpace;
        Header = header;
        CellPointers = cellPointers;
        Cells = new ReadOnlyCollection<SqliteIndexLeafPageCell>(cells);
        _recordComparer = recordComparer;
        _records = records;
    }

    private readonly SqliteIndexRecordComparer _recordComparer;
    private readonly byte[][]? _records;

    /// <summary>The validated b-tree header.</summary>
    public SqliteBtreePageHeader Header { get; }

    /// <summary>The physical size of the parsed page.</summary>
    public int PageSize { get; }

    /// <summary>The portion of the parsed page usable by SQLite.</summary>
    public int UsableSpace { get; }

    /// <summary>The validated cell offsets in on-page key order.</summary>
    public SqliteCellPointerArray CellPointers { get; }

    /// <summary>The decoded physical cells in on-page key order.</summary>
    public IReadOnlyList<SqliteIndexLeafPageCell> Cells { get; }

    /// <summary>
    /// Whether all index record payloads were available and verified in the
    /// configured strict SQLite index order.
    /// </summary>
    /// <remarks>
    /// When a page has overflowing cells, pass an overflow reader to
    /// <see cref="Parse"/>
    /// to validate logical key order as well as physical page layout.
    /// </remarks>
    public bool HasVerifiedRecordOrdering => _records is not null;

    /// <summary>The record comparator used while validating this page.</summary>
    public SqliteIndexRecordComparer RecordComparer => _recordComparer;

    /// <summary>
    /// Parses a snapshot of an index-leaf page. The source page is copied before
    /// exposing any cell data.
    /// </summary>
    public static SqliteIndexLeafPageView Parse(
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
                "SQLite page 1 is the sqlite_schema table root and cannot be an index-leaf page.");
        }

        var snapshot = page.ToArray();
        var header = SqliteBtreePageHeader.Parse(snapshot, isFirstPage, usableSpace);
        if (header.PageType != SqliteBtreePageType.IndexLeaf)
            throw new InvalidDataException("SQLite page is not an index-leaf b-tree page.");

        var pointers = SqliteCellPointerArray.Parse(snapshot, header, usableSpace);
        SqliteBtreePageValidation.ValidateFreeblocks(
            snapshot,
            header,
            usableSpace,
            SqliteIndexLeafCell.MinimumStorageLength);

        var cells = new SqliteIndexLeafPageCell[pointers.Count];
        var ranges = new List<(int Start, int End)>(pointers.Count);
        for (var index = 0; index < pointers.Count; index++)
        {
            var offset = pointers[index];
            var cell = SqliteIndexLeafCell.Decode(snapshot.AsSpan(offset, usableSpace - offset), usableSpace);
            var end = checked(offset + cell.EncodedLength);
            if (end > usableSpace)
                throw new InvalidDataException("SQLite index-leaf cell extends into reserved page space.");

            cells[index] = new SqliteIndexLeafPageCell(offset, cell);
            ranges.Add((offset, end));
        }

        SqliteBtreePageValidation.ValidateCellRanges(ranges, "index-leaf");
        SqliteBtreePageValidation.ValidateCellsDoNotOverlapFreeblocks(
            snapshot,
            header,
            usableSpace,
            ranges,
            "index-leaf");
        recordComparer ??= new SqliteIndexRecordComparer(textEncoding);
        var records = ReadAndValidateRecords(cells, recordComparer, overflowReader);
        return new SqliteIndexLeafPageView(
            page.Length,
            usableSpace,
            header,
            pointers,
            cells,
            recordComparer,
            records);
    }

    /// <summary>
    /// Finds the first complete index record not less than <paramref name="record"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an overflow reader was not supplied for a page containing
    /// overflowing records.
    /// </exception>
    public SqliteBtreeSearchResult Search(ReadOnlySpan<byte> record)
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

        return new SqliteBtreeSearchResult(
            low,
            low < records.Length && _recordComparer.Compare(records[low], record) == 0);
    }

    /// <summary>Returns a copy of a complete, ordering-validated index record.</summary>
    public byte[] GetRecord(int index)
    {
        var records = RequireRecords();
        return records[index].ToArray();
    }

    private byte[][] RequireRecords()
    {
        return _records ?? throw new InvalidOperationException(
            "SQLite index-leaf search requires complete records; supply an overflow reader when parsing.");
    }

    private static byte[][]? ReadAndValidateRecords(
        IReadOnlyList<SqliteIndexLeafPageCell> cells,
        SqliteIndexRecordComparer comparer,
        SqliteOverflowChainReader? overflowReader)
    {
        if (cells.Any(cell => cell.Cell.FirstOverflowPage is not null) && overflowReader is null)
            return null;

        var records = new byte[cells.Count][];
        SqlValue[]? previousDecoded = null;
        // A deferred comparer (no collation callback registered yet) always reports "equal" for
        // the term it cannot resolve, which is an inconclusive answer, not a genuine duplicate:
        // per SqliteIndexRecordComparer's contract this mode exists solely to validate
        // page/record shape while the required collation callback is unavailable, never to
        // decide ordering. Skip the strict-order check entirely in that case; the real order is
        // proven later, once RegisterCollation supplies the callback (EmbeddedFileStore
        // revalidates order/content/uniqueness with the real comparer at that point).
        var validateOrder = !comparer.HasDeferredTerms;
        for (var index = 0; index < cells.Count; index++)
        {
            var pageCell = cells[index];
            var record = pageCell.Cell.FirstOverflowPage is null
                ? pageCell.Cell.LocalPayload.ToArray()
                : overflowReader!.ReadPayload(pageCell.Cell);
            // Decode once and reuse the decoded values for both this record's own
            // validation and the ordering comparison against the previous record.
            // Calling comparer.Validate(record) followed by comparer.Compare(previousRecord, record)
            // would otherwise decode every record up to three times per cell (once to
            // validate it, once as "previous", once as "current"), which dominates
            // allocation on a fully-populated page.
            var decoded = SqliteRecordCodec.Decode(record, comparer.TextEncoding);
            if (validateOrder && previousDecoded is not null && comparer.Compare(previousDecoded, decoded) >= 0)
                throw new InvalidDataException(
                    $"SQLite index-leaf records are not in strictly increasing declared order at cell {index}.");

            previousDecoded = decoded;
            records[index] = record;
        }

        return records;
    }
}

/// <summary>An index-leaf cell together with its physical page offset.</summary>
public sealed record SqliteIndexLeafPageCell(ushort Offset, SqliteIndexLeafCell Cell);
