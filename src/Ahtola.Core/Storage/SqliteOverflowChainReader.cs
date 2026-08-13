using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// Reads SQLite overflow chains from a page store without interpreting or
/// modifying B-tree pages.
/// </summary>
public sealed class SqliteOverflowChainReader
{
    private readonly Func<uint, byte[]> _readPage;
    private readonly Func<uint> _getPageCount;
    private readonly int _usableSpace;

    /// <summary>Creates a reader over <paramref name="pageStore"/>.</summary>
    public SqliteOverflowChainReader(SqlitePageStore pageStore)
    {
        ArgumentNullException.ThrowIfNull(pageStore);
        _readPage = pageStore.ReadPage;
        _getPageCount = () => pageStore.PageCount;
        _usableSpace = pageStore.Header.UsableSpace;
    }

    /// <summary>
    /// Creates a reader over the committed view of a SQLite WAL pager.
    /// </summary>
    public SqliteOverflowChainReader(SqlitePager pager, SqliteDatabaseHeader header)
    {
        ArgumentNullException.ThrowIfNull(pager);
        ArgumentNullException.ThrowIfNull(header);
        if (pager.PageSize != header.PageSize)
            throw new ArgumentException("SQLite pager and database header page sizes do not match.", nameof(header));

        _readPage = pager.ReadCommittedPage;
        _getPageCount = () => pager.CommittedPageCount;
        _usableSpace = header.UsableSpace;
    }

    /// <summary>
    /// Creates a reader over an incremental b-tree page-access boundary, so a
    /// mutation in progress reads its own staged overflow pages.
    /// </summary>
    public SqliteOverflowChainReader(ISqliteBtreePageIo pageIo)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        _readPage = pageIo.ReadPage;
        _getPageCount = () => pageIo.PageCount;
        _usableSpace = pageIo.UsableSpace;
    }

    /// <summary>The number of payload bytes available on each overflow page.</summary>
    public int PayloadCapacity => _usableSpace - SqliteOverflowPageView.HeaderLength;

    /// <summary>
    /// Traverses exactly the pages required for <paramref name="overflowPayloadLength"/>
    /// bytes, rejecting truncated, overlong, cyclic, and out-of-range chains.
    /// </summary>
    public IReadOnlyList<uint> Traverse(uint firstOverflowPage, ulong overflowPayloadLength)
        => Visit(firstOverflowPage, overflowPayloadLength, Span<byte>.Empty, copyPayload: false);

    /// <summary>
    /// Reads an exact overflow payload into <paramref name="destination"/>.
    /// </summary>
    public void Read(uint firstOverflowPage, Span<byte> destination)
        => Visit(firstOverflowPage, checked((ulong)destination.Length), destination, copyPayload: true);

    /// <summary>
    /// Allocates and reads an overflow payload of <paramref name="overflowPayloadLength"/> bytes.
    /// </summary>
    public byte[] Read(uint firstOverflowPage, int overflowPayloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(overflowPayloadLength);
        var payload = new byte[overflowPayloadLength];
        Read(firstOverflowPage, payload);
        return payload;
    }

    /// <summary>
    /// Reconstructs the complete logical payload of a decoded table-leaf cell.
    /// </summary>
    public byte[] ReadPayload(SqliteTableLeafCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.PayloadLength > int.MaxValue)
        {
            throw new NotSupportedException(
                "A SQLite payload larger than Int32.MaxValue bytes cannot be materialized as one managed array.");
        }

        var localPayload = cell.LocalPayload;
        if ((ulong)localPayload.Length > cell.PayloadLength)
            throw new InvalidDataException("SQLite table-leaf cell local payload exceeds its logical payload length.");

        var payload = new byte[checked((int)cell.PayloadLength)];
        localPayload.Span.CopyTo(payload);
        var overflowPayloadLength = payload.Length - localPayload.Length;
        if (overflowPayloadLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException("SQLite table-leaf cell has an unnecessary overflow page.");

            return payload;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException("SQLite table-leaf cell is missing its first overflow page.");

        Read(firstOverflowPage, payload.AsSpan(localPayload.Length));
        return payload;
    }

    /// <summary>
    /// Reconstructs the complete logical payload of a decoded index-leaf cell.
    /// </summary>
    public byte[] ReadPayload(SqliteIndexLeafCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.PayloadLength > int.MaxValue)
        {
            throw new NotSupportedException(
                "A SQLite payload larger than Int32.MaxValue bytes cannot be materialized as one managed array.");
        }

        var localPayload = cell.LocalPayload;
        if ((ulong)localPayload.Length > cell.PayloadLength)
            throw new InvalidDataException("SQLite index-leaf cell local payload exceeds its logical payload length.");

        var payload = new byte[checked((int)cell.PayloadLength)];
        localPayload.Span.CopyTo(payload);
        var overflowPayloadLength = payload.Length - localPayload.Length;
        if (overflowPayloadLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException("SQLite index-leaf cell has an unnecessary overflow page.");

            return payload;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException("SQLite index-leaf cell is missing its first overflow page.");

        Read(firstOverflowPage, payload.AsSpan(localPayload.Length));
        return payload;
    }

    private IReadOnlyList<uint> Visit(
        uint firstOverflowPage,
        ulong overflowPayloadLength,
        Span<byte> destination,
        bool copyPayload)
    {
        if (copyPayload && (ulong)destination.Length != overflowPayloadLength)
        {
            throw new ArgumentException(
                "Destination length must exactly match the requested overflow payload length.",
                nameof(destination));
        }

        if (overflowPayloadLength == 0)
        {
            if (firstOverflowPage != 0)
                throw new InvalidDataException("An empty SQLite overflow payload must not reference an overflow page.");

            return Array.Empty<uint>();
        }

        if (firstOverflowPage == 0)
            throw new InvalidDataException("A non-empty SQLite overflow payload has a zero first overflow page.");

        var pageCount = _getPageCount();
        var usableSpace = _usableSpace;
        var payloadCapacity = usableSpace - SqliteOverflowPageView.HeaderLength;
        var seen = new HashSet<uint>();
        var pages = new List<uint>();
        var remaining = overflowPayloadLength;
        var destinationOffset = 0;
        var currentPage = firstOverflowPage;

        while (remaining != 0)
        {
            if (currentPage < 2 || currentPage > pageCount)
            {
                throw new InvalidDataException(
                    $"SQLite overflow page {currentPage} is outside the valid non-root page range 2..{pageCount}.");
            }

            if (!seen.Add(currentPage))
                throw new InvalidDataException($"SQLite overflow chain contains a cycle at page {currentPage}.");

            var page = SqliteOverflowPageView.Parse(_readPage(currentPage), usableSpace);
            pages.Add(currentPage);

            var bytesFromPage = checked((int)Math.Min(remaining, (ulong)payloadCapacity));
            if (copyPayload)
            {
                page.Payload.Span[..bytesFromPage].CopyTo(
                    destination.Slice(destinationOffset, bytesFromPage));
                destinationOffset += bytesFromPage;
            }

            remaining -= (ulong)bytesFromPage;
            if (remaining == 0)
            {
                if (page.NextPageNumber != 0)
                    throw new InvalidDataException("SQLite overflow chain continues past its logical payload length.");

                break;
            }

            if (page.NextPageNumber == 0)
                throw new InvalidDataException("SQLite overflow chain ends before its logical payload length.");

            currentPage = page.NextPageNumber;
        }

        return new ReadOnlyCollection<uint>(pages);
    }
}
