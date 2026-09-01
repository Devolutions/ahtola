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
    /// Reads one exact range from a logical overflow payload without materializing
    /// bytes before or after that range.
    /// </summary>
    /// <remarks>
    /// Pages before <paramref name="offset"/> are visited only to follow their
    /// next-page pointers. Payload bytes are copied only from pages intersecting
    /// the requested range.
    /// </remarks>
    public void ReadRange(
        uint firstOverflowPage,
        ulong overflowPayloadLength,
        ulong offset,
        Span<byte> destination)
    {
        ValidateRange(overflowPayloadLength, offset, destination.Length);
        if (overflowPayloadLength == 0)
        {
            if (firstOverflowPage != 0)
                throw new InvalidDataException("An empty SQLite overflow payload must not reference an overflow page.");

            return;
        }

        if (firstOverflowPage == 0)
            throw new InvalidDataException("A non-empty SQLite overflow payload has a zero first overflow page.");
        if (destination.IsEmpty)
            return;

        var pageCount = _getPageCount();
        var payloadCapacity = checked((ulong)PayloadCapacity);
        var targetPageIndex = offset / payloadCapacity;
        var offsetWithinTargetPage = offset % payloadCapacity;
        var seen = new HashSet<uint>();
        var currentPage = firstOverflowPage;
        ulong pageIndex = 0;
        var copied = 0;

        while (true)
        {
            if (currentPage < 2 || currentPage > pageCount)
            {
                throw new InvalidDataException(
                    $"SQLite overflow page {currentPage} is outside the valid non-root page range 2..{pageCount}.");
            }

            if (!seen.Add(currentPage))
                throw new InvalidDataException($"SQLite overflow chain contains a cycle at page {currentPage}.");

            var page = SqliteOverflowPageView.Parse(_readPage(currentPage), _usableSpace);
            var logicalPageStart = checked(pageIndex * payloadCapacity);
            var logicalBytesOnPage = Math.Min(payloadCapacity, overflowPayloadLength - logicalPageStart);
            var requiresNextPage = logicalPageStart + logicalBytesOnPage < overflowPayloadLength;
            if (requiresNextPage)
            {
                if (page.NextPageNumber == 0)
                    throw new InvalidDataException("SQLite overflow chain ends before its logical payload length.");
                if (page.NextPageNumber < 2 || page.NextPageNumber > pageCount)
                {
                    throw new InvalidDataException(
                        $"SQLite overflow page {page.NextPageNumber} is outside the valid non-root page range 2..{pageCount}.");
                }
                if (seen.Contains(page.NextPageNumber))
                {
                    throw new InvalidDataException(
                        $"SQLite overflow chain contains a cycle at page {page.NextPageNumber}.");
                }
            }
            else if (page.NextPageNumber != 0)
            {
                throw new InvalidDataException("SQLite overflow chain continues past its logical payload length.");
            }

            if (pageIndex >= targetPageIndex)
            {
                var within = pageIndex == targetPageIndex ? offsetWithinTargetPage : 0;
                var available = checked((int)(logicalBytesOnPage - within));
                var count = Math.Min(available, destination.Length - copied);
                page.Payload.Span.Slice(checked((int)within), count)
                    .CopyTo(destination[copied..]);
                copied += count;
                if (copied == destination.Length)
                    return;
            }

            currentPage = page.NextPageNumber;
            pageIndex = checked(pageIndex + 1);
        }
    }

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
    /// Reads one exact range from a table-leaf cell's logical payload, spanning
    /// its local bytes and overflow pages as needed.
    /// </summary>
    public void ReadPayloadRange(SqliteTableLeafCell cell, ulong offset, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ValidateRange(cell.PayloadLength, offset, destination.Length);

        var localPayload = cell.LocalPayload;
        var localLength = checked((ulong)localPayload.Length);
        if (localLength > cell.PayloadLength)
            throw new InvalidDataException("SQLite table-leaf cell local payload exceeds its logical payload length.");

        var overflowPayloadLength = cell.PayloadLength - localLength;
        if (overflowPayloadLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException("SQLite table-leaf cell has an unnecessary overflow page.");
        }
        else
        {
            if (cell.FirstOverflowPage is not { } firstOverflowPage || firstOverflowPage == 0)
                throw new InvalidDataException("SQLite table-leaf cell is missing its first overflow page.");

            var pageCount = _getPageCount();
            if (firstOverflowPage < 2 || firstOverflowPage > pageCount)
            {
                throw new InvalidDataException(
                    $"SQLite overflow page {firstOverflowPage} is outside the valid non-root page range 2..{pageCount}.");
            }
        }

        if (destination.IsEmpty)
            return;

        var copied = 0;
        if (offset < localLength)
        {
            var count = checked((int)Math.Min((ulong)destination.Length, localLength - offset));
            localPayload.Span.Slice(checked((int)offset), count).CopyTo(destination);
            copied = count;
        }

        if (copied == destination.Length)
            return;

        var overflowOffset = checked(offset + (ulong)copied - localLength);
        ReadRange(
            cell.FirstOverflowPage!.Value,
            overflowPayloadLength,
            overflowOffset,
            destination[copied..]);
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

    private static void ValidateRange(ulong payloadLength, ulong offset, int count)
    {
        if (offset > payloadLength)
            throw new ArgumentOutOfRangeException(nameof(offset), "The payload offset is past the end of the value.");
        if ((ulong)count > payloadLength - offset)
        {
            throw new ArgumentException(
                "The requested range extends past the end of the payload.",
                "destination");
        }
    }
}
