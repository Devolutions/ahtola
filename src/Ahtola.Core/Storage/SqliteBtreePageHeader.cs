using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

public enum SqliteBtreePageType : byte
{
    IndexInterior = 2,
    TableInterior = 5,
    IndexLeaf = 10,
    TableLeaf = 13,
}

public sealed record SqliteBtreePageHeader(
    int Offset,
    SqliteBtreePageType PageType,
    ushort FirstFreeblockOffset,
    ushort CellCount,
    int CellContentAreaOffset,
    byte FragmentedFreeBytes,
    uint RightMostChildPage)
{
    public const int FirstPageOffset = SqliteDatabaseHeader.Size;
    public const int LeafHeaderSize = 8;
    public const int InteriorHeaderSize = 12;

    public bool IsLeaf => PageType is SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.TableLeaf;

    public int HeaderSize => IsLeaf ? LeafHeaderSize : InteriorHeaderSize;

    public int CellPointerArrayOffset => Offset + HeaderSize;

    public static SqliteBtreePageHeader CreateEmpty(
        SqliteBtreePageType pageType,
        int pageSize,
        bool isFirstPage = false,
        int? usableSpace = null)
    {
        ValidatePageSize(pageSize);
        var cellContentAreaOffset = usableSpace ?? pageSize;
        ValidateUsableSpace(pageSize, cellContentAreaOffset);
        return new SqliteBtreePageHeader(
            isFirstPage ? FirstPageOffset : 0,
            pageType,
            0,
            0,
            cellContentAreaOffset,
            0,
            0);
    }

    /// <summary>
    /// Parses a b-tree page header. <paramref name="usableSpace"/> bounds the
    /// cell-content area and freeblock chain exactly like SQLite's
    /// <c>btreeInitPage</c>/<c>btreeComputeFreeSpace</c>, which compare against
    /// <c>usableSize</c> rather than the physical page size. Callers that do not
    /// know the reserved-space suffix fall back to the page length.
    /// </summary>
    public static SqliteBtreePageHeader Parse(
        ReadOnlySpan<byte> page,
        bool isFirstPage = false,
        int? usableSpace = null)
    {
        ValidatePageSize(page.Length);
        var contentBound = ResolveContentBound(page.Length, usableSpace);

        var offset = isFirstPage ? FirstPageOffset : 0;
        if (page.Length < offset + LeafHeaderSize)
            throw new InvalidDataException("SQLite B-tree page is too small for its header.");

        var pageType = ParsePageType(page[offset]);
        var headerSize = IsLeafPage(pageType) ? LeafHeaderSize : InteriorHeaderSize;
        if (page.Length < offset + headerSize)
            throw new InvalidDataException("SQLite B-tree page is truncated.");

        var firstFreeblockOffset = BinaryPrimitives.ReadUInt16BigEndian(page[(offset + 1)..]);
        var cellCount = BinaryPrimitives.ReadUInt16BigEndian(page[(offset + 3)..]);
        var rawCellContentAreaOffset = BinaryPrimitives.ReadUInt16BigEndian(page[(offset + 5)..]);
        var cellContentAreaOffset = rawCellContentAreaOffset == 0 && page.Length == SqlitePageSize.Maximum
            ? page.Length
            : rawCellContentAreaOffset;
        var fragmentedFreeBytes = page[offset + 7];
        var rightMostChildPage = IsLeafPage(pageType)
            ? 0
            : BinaryPrimitives.ReadUInt32BigEndian(page[(offset + 8)..]);

        ValidateLayout(
            contentBound,
            offset,
            headerSize,
            firstFreeblockOffset,
            cellCount,
            cellContentAreaOffset,
            fragmentedFreeBytes);

        return new SqliteBtreePageHeader(
            offset,
            pageType,
            firstFreeblockOffset,
            cellCount,
            cellContentAreaOffset,
            fragmentedFreeBytes,
            rightMostChildPage);
    }

    public void WriteTo(Span<byte> page, int? usableSpace = null)
    {
        ValidatePageSize(page.Length);
        var contentBound = ResolveContentBound(page.Length, usableSpace);
        if (Offset is not (0 or FirstPageOffset))
            throw new InvalidOperationException($"SQLite B-tree header offset {Offset} is invalid.");
        if (PageType is not SqliteBtreePageType.IndexInterior
            and not SqliteBtreePageType.TableInterior
            and not SqliteBtreePageType.IndexLeaf
            and not SqliteBtreePageType.TableLeaf)
        {
            throw new InvalidOperationException($"Unsupported SQLite B-tree page type {(byte)PageType}.");
        }

        var expectedOffset = Offset == FirstPageOffset ? FirstPageOffset : 0;
        var expectedHeaderSize = IsLeaf ? LeafHeaderSize : InteriorHeaderSize;
        if (page.Length < expectedOffset + expectedHeaderSize)
            throw new ArgumentException("SQLite B-tree page is too small for its header.", nameof(page));

        ValidateLayout(
            contentBound,
            expectedOffset,
            expectedHeaderSize,
            FirstFreeblockOffset,
            CellCount,
            CellContentAreaOffset,
            FragmentedFreeBytes);

        page[expectedOffset] = (byte)PageType;
        BinaryPrimitives.WriteUInt16BigEndian(page[(expectedOffset + 1)..], FirstFreeblockOffset);
        BinaryPrimitives.WriteUInt16BigEndian(page[(expectedOffset + 3)..], CellCount);
        BinaryPrimitives.WriteUInt16BigEndian(
            page[(expectedOffset + 5)..],
            CellContentAreaOffset == SqlitePageSize.Maximum ? (ushort)0 : checked((ushort)CellContentAreaOffset));
        page[expectedOffset + 7] = FragmentedFreeBytes;

        if (!IsLeaf)
            BinaryPrimitives.WriteUInt32BigEndian(page[(expectedOffset + 8)..], RightMostChildPage);
    }

    private static SqliteBtreePageType ParsePageType(byte value)
    {
        return value switch
        {
            (byte)SqliteBtreePageType.IndexInterior => SqliteBtreePageType.IndexInterior,
            (byte)SqliteBtreePageType.TableInterior => SqliteBtreePageType.TableInterior,
            (byte)SqliteBtreePageType.IndexLeaf => SqliteBtreePageType.IndexLeaf,
            (byte)SqliteBtreePageType.TableLeaf => SqliteBtreePageType.TableLeaf,
            _ => throw new InvalidDataException($"Unsupported SQLite B-tree page type {value}."),
        };
    }

    private static bool IsLeafPage(SqliteBtreePageType pageType)
        => pageType is SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.TableLeaf;

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize < SqlitePageSize.Minimum
            || pageSize > SqlitePageSize.Maximum
            || (pageSize & (pageSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "SQLite B-tree page size is invalid.");
        }
    }

    private static void ValidateUsableSpace(int pageSize, int usableSpace)
    {
        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace || usableSpace > pageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usableSpace),
                usableSpace,
                $"SQLite usable page space must be between {SqliteDatabaseHeader.MinimumUsableSpace} and {pageSize} bytes.");
        }
    }

    private static int ResolveContentBound(int pageSize, int? usableSpace)
    {
        if (usableSpace is not { } usable)
            return pageSize;

        ValidateUsableSpace(pageSize, usable);
        return usable;
    }

    private static void ValidateLayout(
        int contentBound,
        int offset,
        int headerSize,
        ushort firstFreeblockOffset,
        ushort cellCount,
        int cellContentAreaOffset,
        byte fragmentedFreeBytes)
    {
        if (fragmentedFreeBytes > 60)
            throw new InvalidDataException("SQLite B-tree page has too many fragmented free bytes.");
        if (cellContentAreaOffset < offset + headerSize || cellContentAreaOffset > contentBound)
            throw new InvalidDataException("SQLite B-tree cell content area is invalid.");

        var cellPointerArrayEnd = offset + headerSize + cellCount * sizeof(ushort);
        if (cellPointerArrayEnd > cellContentAreaOffset)
            throw new InvalidDataException("SQLite B-tree cell pointer array overlaps cell content.");
        if (firstFreeblockOffset != 0
            && (firstFreeblockOffset < cellContentAreaOffset || firstFreeblockOffset > contentBound - 4))
        {
            throw new InvalidDataException("SQLite B-tree first freeblock offset is invalid.");
        }
    }
}
