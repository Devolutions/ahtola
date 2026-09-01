namespace Ahtola.Core.Storage;

/// <summary>
/// A read cursor over a SQLite rowid-table b-tree.
/// </summary>
/// <remarks>
/// Seeking descends from the root to one leaf. A complete record read then visits
/// its overflow chain, while a bounded payload read visits only the pages needed
/// to reach and fill that range. It shares the <see cref="ISqliteBtreePageIo"/> boundary with
/// <see cref="SqliteIncrementalTableBtree"/>, so a cursor opened over a staging
/// layer observes uncommitted mutations exactly as the writer left them.
/// </remarks>
public sealed class SqliteTableBtreeCursor
{
    private const int MaximumDepth = 64;

    private readonly ISqliteBtreePageIo _io;

    /// <summary>Creates a cursor over one page-access boundary.</summary>
    public SqliteTableBtreeCursor(ISqliteBtreePageIo pageIo)
    {
        ArgumentNullException.ThrowIfNull(pageIo);
        _io = pageIo;
    }

    /// <summary>
    /// Reads the record stored at <paramref name="rowId"/>, returning
    /// <see langword="false"/> when the tree does not contain it.
    /// </summary>
    public bool TrySeek(uint rootPage, long rowId, out byte[] record)
    {
        if (!TrySeekCell(rootPage, rowId, out var cell))
        {
            record = [];
            return false;
        }

        record = new SqliteOverflowChainReader(_io).ReadPayload(cell);
        return true;
    }

    /// <summary>
    /// Reads one exact range from the record payload stored at
    /// <paramref name="rowId"/> without materializing the complete record.
    /// </summary>
    public bool TryReadPayload(
        uint rootPage,
        long rowId,
        ulong offset,
        Span<byte> destination)
    {
        if (!TrySeekCell(rootPage, rowId, out var cell))
            return false;

        new SqliteOverflowChainReader(_io).ReadPayloadRange(cell, offset, destination);
        return true;
    }

    /// <summary>
    /// Gets the logical record-payload length for <paramref name="rowId"/>
    /// without reading its overflow payload.
    /// </summary>
    public bool TryGetPayloadLength(uint rootPage, long rowId, out ulong payloadLength)
    {
        if (!TrySeekCell(rootPage, rowId, out var cell))
        {
            payloadLength = 0;
            return false;
        }

        payloadLength = cell.PayloadLength;
        return true;
    }

    /// <summary>
    /// Locates one physical record column without decoding or materializing any column body.
    /// </summary>
    public bool TryGetColumnLocation(
        uint rootPage,
        long rowId,
        int columnIndex,
        out SqliteRecordColumnLocation location)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        if (!TrySeekCell(rootPage, rowId, out var cell))
        {
            location = default;
            return false;
        }

        location = LocateColumn(cell, columnIndex);
        return true;
    }

    /// <summary>
    /// Gets the byte length of one TEXT or BLOB column without reading its body.
    /// </summary>
    public bool TryGetColumnLength(
        uint rootPage,
        long rowId,
        int columnIndex,
        out ulong length)
    {
        if (!TryGetColumnLocation(rootPage, rowId, columnIndex, out var location))
        {
            length = 0;
            return false;
        }

        EnsureByteAddressable(location);
        length = location.Length;
        return true;
    }

    /// <summary>
    /// Reads a bounded range from one TEXT or BLOB column without materializing the complete value.
    /// </summary>
    public bool TryReadColumn(
        uint rootPage,
        long rowId,
        int columnIndex,
        ulong offset,
        Span<byte> destination,
        out int bytesRead)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        if (!TrySeekCell(rootPage, rowId, out var cell))
        {
            bytesRead = 0;
            return false;
        }

        var location = LocateColumn(cell, columnIndex);
        EnsureByteAddressable(location);
        if (offset >= location.Length || destination.IsEmpty)
        {
            bytesRead = 0;
            return true;
        }

        bytesRead = checked((int)Math.Min((ulong)destination.Length, location.Length - offset));
        var payloadOffset = checked(location.PayloadOffset + offset);
        new SqliteOverflowChainReader(_io)
            .ReadPayloadRange(cell, payloadOffset, destination[..bytesRead]);
        return true;
    }

    /// <summary>
    /// Overwrites a bounded range of one TEXT or BLOB column in place. The write
    /// cannot change the value's size; bytes past the stored length are rejected.
    /// </summary>
    public bool TryWriteColumn(
        uint rootPage,
        long rowId,
        int columnIndex,
        ulong offset,
        ReadOnlySpan<byte> source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        if (!TrySeekLeaf(rootPage, rowId, out var leafPage, out var pageCell))
            return false;

        var cell = pageCell.Cell;
        var location = LocateColumn(cell, columnIndex);
        EnsureByteAddressable(location);
        if (source.IsEmpty)
            return true;
        if (offset > location.Length || (ulong)source.Length > location.Length - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "SQLite incremental column writes cannot change the stored value size.");
        }

        var payloadOffset = checked(location.PayloadOffset + offset);
        var localLength = checked((ulong)cell.LocalPayload.Length);
        var remaining = source;
        if (payloadOffset < localLength)
        {
            var localCount = checked((int)Math.Min((ulong)remaining.Length, localLength - payloadOffset));
            var image = _io.ReadPage(leafPage);
            remaining[..localCount].CopyTo(
                image.AsSpan(
                    pageCell.Offset + cell.LocalPayloadOffset + checked((int)payloadOffset),
                    localCount));
            _io.WritePage(leafPage, image);
            remaining = remaining[localCount..];
            payloadOffset = localLength;
        }

        if (remaining.IsEmpty)
            return true;

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException("SQLite table-leaf cell is missing its first overflow page.");

        SqliteOverflowChainWriter.WriteRange(
            _io,
            firstOverflowPage,
            cell.PayloadLength - localLength,
            payloadOffset - localLength,
            remaining);
        return true;
    }

    private SqliteRecordColumnLocation LocateColumn(SqliteTableLeafCell cell, int columnIndex)
    {
        var localPayload = cell.LocalPayload.Span;
        if (!SqliteVarint.TryRead(localPayload, out var headerSizeValue, out var headerSizeLength))
            throw new InvalidDataException("SQLite record header size is invalid.");
        if (headerSizeValue < (ulong)headerSizeLength
            || headerSizeValue > cell.PayloadLength
            || headerSizeValue > SqliteRecordCodec.MaximumHeaderSize)
        {
            throw new InvalidDataException(
                $"SQLite record header size {headerSizeValue} extends outside its payload.");
        }

        var headerSize = checked((int)headerSizeValue);
        if (headerSize <= localPayload.Length)
        {
            return SqliteRecordCodec.LocateColumn(
                localPayload[..headerSize],
                cell.PayloadLength,
                columnIndex);
        }

        var header = GC.AllocateUninitializedArray<byte>(headerSize);
        new SqliteOverflowChainReader(_io).ReadPayloadRange(cell, 0, header);
        return SqliteRecordCodec.LocateColumn(header, cell.PayloadLength, columnIndex);
    }

    private static void EnsureByteAddressable(SqliteRecordColumnLocation location)
    {
        if (!location.IsByteAddressable)
        {
            throw new InvalidOperationException(
                $"SQLite incremental column reads require TEXT or BLOB storage, not {location.StorageClass}.");
        }
    }

    private bool TrySeekCell(uint rootPage, long rowId, out SqliteTableLeafCell cell)
    {
        if (!TrySeekLeaf(rootPage, rowId, out _, out var pageCell))
        {
            cell = null!;
            return false;
        }

        cell = pageCell.Cell;
        return true;
    }

    private bool TrySeekLeaf(
        uint rootPage,
        long rowId,
        out uint leafPage,
        out SqliteTableLeafPageCell pageCell)
    {
        var pageNumber = rootPage;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            var isFirstPage = pageNumber == 1;
            var image = _io.ReadPage(pageNumber);
            switch (SqliteBtreePageHeader.Parse(image, isFirstPage).PageType)
            {
                case SqliteBtreePageType.TableLeaf:
                    {
                        var leaf = SqliteTableLeafPageView.Parse(image, _io.UsableSpace, isFirstPage);
                        var search = leaf.Search(rowId);
                        if (!search.IsExact)
                        {
                            leafPage = 0;
                            pageCell = null!;
                            return false;
                        }

                        leafPage = pageNumber;
                        pageCell = leaf.Cells[search.Index];
                        return true;
                    }

                case SqliteBtreePageType.TableInterior:
                    {
                        var interior = SqliteTableInteriorPageView.Parse(image, _io.UsableSpace, isFirstPage);
                        pageNumber = interior.SearchChild(rowId).ChildPage;
                        break;
                    }

                default:
                    throw new InvalidDataException(
                        $"SQLite page {pageNumber} is not part of a rowid-table b-tree.");
            }
        }

        throw new InvalidDataException(
            $"SQLite table b-tree rooted at page {rootPage} is deeper than {MaximumDepth} levels.");
    }
}
