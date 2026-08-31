namespace Ahtola.Core.Storage;

/// <summary>
/// A read cursor over a SQLite rowid-table b-tree.
/// </summary>
/// <remarks>
/// Seeking descends from the root to one leaf, so the pages it reads are bounded
/// by the height of the tree plus the overflow pages of the row it returns. It
/// shares the <see cref="ISqliteBtreePageIo"/> boundary with
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
                            record = [];
                            return false;
                        }

                        record = new SqliteOverflowChainReader(_io)
                            .ReadPayload(leaf.Cells[search.Index].Cell);
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
