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

    private bool TrySeekCell(uint rootPage, long rowId, out SqliteTableLeafCell cell)
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
                            cell = null!;
                            return false;
                        }

                        cell = leaf.Cells[search.Index].Cell;
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
