namespace Ahtola.Core.Storage;

/// <summary>
/// The genuinely asynchronous counterpart of <see cref="SqliteOverflowChainReader"/>: reads
/// SQLite overflow chains through <see cref="IAsyncSqliteBtreePageIo"/> instead of a
/// synchronous page-read delegate.
/// </summary>
/// <remarks>
/// This ports the same traversal semantics as <see cref="SqliteOverflowChainReader"/> — cycle
/// detection, non-root page-range validation, and exact logical-length accounting — through a
/// separate implementation because the synchronous type's page source is a plain
/// <c>Func&lt;uint, byte[]&gt;</c> delegate, which cannot <c>await</c>. Per <c>async-io-port</c>,
/// this changes the call style (one <c>await</c> per page instead of one synchronous call), not
/// the semantics: the same cycle/range checks, the same page-capacity accounting, and the same
/// exception messages/shapes.
/// </remarks>
internal sealed class AsyncSqliteOverflowChainReader(IAsyncSqliteBtreePageIo pageIo)
{
    private readonly IAsyncSqliteBtreePageIo _pageIo = pageIo
        ?? throw new ArgumentNullException(nameof(pageIo));

    /// <summary>The number of payload bytes available on each overflow page.</summary>
    private int PayloadCapacity => _pageIo.UsableSpace - SqliteOverflowPageView.HeaderLength;

    /// <summary>
    /// Reconstructs the complete logical payload of a decoded table-leaf cell, following its
    /// overflow chain (if any) one page at a time.
    /// </summary>
    public async ValueTask<byte[]> ReadPayloadAsync(
        SqliteTableLeafCell cell,
        CancellationToken cancellationToken = default)
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

        await ReadAsync(
            firstOverflowPage,
            payload.AsMemory(localPayload.Length),
            cancellationToken).ConfigureAwait(false);
        return payload;
    }

    /// <summary>Reads an exact overflow payload into <paramref name="destination"/>.</summary>
    private async ValueTask ReadAsync(
        uint firstOverflowPage,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var overflowPayloadLength = checked((ulong)destination.Length);
        if (overflowPayloadLength == 0)
        {
            if (firstOverflowPage != 0)
                throw new InvalidDataException("An empty SQLite overflow payload must not reference an overflow page.");

            return;
        }

        if (firstOverflowPage == 0)
            throw new InvalidDataException("A non-empty SQLite overflow payload has a zero first overflow page.");

        var pageCount = _pageIo.PageCount;
        var usableSpace = _pageIo.UsableSpace;
        var payloadCapacity = usableSpace - SqliteOverflowPageView.HeaderLength;
        var seen = new HashSet<uint>();
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

            var image = await _pageIo.ReadPageAsync(currentPage, cancellationToken).ConfigureAwait(false);
            var page = SqliteOverflowPageView.Parse(image, usableSpace);

            var bytesFromPage = checked((int)Math.Min(remaining, (ulong)payloadCapacity));
            page.Payload.Span[..bytesFromPage].CopyTo(destination.Span.Slice(destinationOffset, bytesFromPage));
            destinationOffset += bytesFromPage;

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
    }
}
