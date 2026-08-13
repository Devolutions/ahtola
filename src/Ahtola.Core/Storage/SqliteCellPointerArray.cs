using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable, validated SQLite b-tree cell-pointer array.
/// </summary>
public sealed class SqliteCellPointerArray
{
    private readonly ushort[] _offsets;

    private SqliteCellPointerArray(ushort[] offsets)
    {
        _offsets = offsets;
        Offsets = new ReadOnlyCollection<ushort>(_offsets);
    }

    /// <summary>Cell offsets in logical key order.</summary>
    public IReadOnlyList<ushort> Offsets { get; }

    /// <summary>Number of cell offsets.</summary>
    public int Count => _offsets.Length;

    /// <summary>Gets a validated cell offset by logical cell index.</summary>
    public ushort this[int index] => _offsets[index];

    /// <summary>
    /// Parses the cell-pointer array of <paramref name="header"/> and validates
    /// every offset against the usable portion of the page.
    /// </summary>
    public static SqliteCellPointerArray Parse(
        ReadOnlySpan<byte> page,
        SqliteBtreePageHeader header,
        int usableSpace)
    {
        ArgumentNullException.ThrowIfNull(header);
        ValidatePageAndHeader(page, header, usableSpace, invalidData: true);

        var offsets = new ushort[header.CellCount];
        for (var index = 0; index < offsets.Length; index++)
        {
            var pointerOffset = header.CellPointerArrayOffset + (index * sizeof(ushort));
            offsets[index] = BinaryPrimitives.ReadUInt16BigEndian(page[pointerOffset..]);
        }

        ValidateOffsets(offsets, header, usableSpace, invalidData: true);
        return new SqliteCellPointerArray(offsets);
    }

    /// <summary>
    /// Serializes <paramref name="offsets"/> as the cell-pointer array for
    /// <paramref name="header"/>.
    /// </summary>
    public static void WriteTo(
        Span<byte> page,
        SqliteBtreePageHeader header,
        IReadOnlyList<ushort> offsets,
        int usableSpace)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(offsets);
        ValidatePageAndHeader(page, header, usableSpace, invalidData: false);

        if (offsets.Count != header.CellCount)
        {
            throw new ArgumentException(
                $"SQLite B-tree header declares {header.CellCount} cell(s), but {offsets.Count} offset(s) were supplied.",
                nameof(offsets));
        }

        var copy = new ushort[offsets.Count];
        for (var index = 0; index < copy.Length; index++)
            copy[index] = offsets[index];

        ValidateOffsets(copy, header, usableSpace, invalidData: false);
        for (var index = 0; index < copy.Length; index++)
        {
            var pointerOffset = header.CellPointerArrayOffset + (index * sizeof(ushort));
            BinaryPrimitives.WriteUInt16BigEndian(page[pointerOffset..], copy[index]);
        }
    }

    private static void ValidatePageAndHeader(
        ReadOnlySpan<byte> page,
        SqliteBtreePageHeader header,
        int usableSpace,
        bool invalidData)
    {
        if (page.Length < SqlitePageSize.Minimum
            || page.Length > SqlitePageSize.Maximum
            || (page.Length & (page.Length - 1)) != 0)
        {
            Throw(invalidData, "SQLite B-tree page size is invalid.");
        }
        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace || usableSpace > page.Length)
            Throw(invalidData, "SQLite usable page space is outside the page.");
        if (header.Offset is not (0 or SqliteBtreePageHeader.FirstPageOffset))
            Throw(invalidData, "SQLite B-tree header offset is invalid.");
        if (header.PageType is not SqliteBtreePageType.IndexInterior
            and not SqliteBtreePageType.TableInterior
            and not SqliteBtreePageType.IndexLeaf
            and not SqliteBtreePageType.TableLeaf)
        {
            Throw(invalidData, "SQLite B-tree page type is invalid.");
        }
        if (header.Offset + header.HeaderSize > usableSpace)
            Throw(invalidData, "SQLite B-tree header extends into reserved page space.");
        if (header.CellContentAreaOffset > usableSpace)
            Throw(invalidData, "SQLite B-tree cell content area extends into reserved space.");

        var pointerArrayEnd = header.CellPointerArrayOffset + (header.CellCount * sizeof(ushort));
        if (pointerArrayEnd > header.CellContentAreaOffset || pointerArrayEnd > usableSpace)
            Throw(invalidData, "SQLite B-tree cell pointer array overlaps cell content or reserved space.");
    }

    private static void ValidateOffsets(
        IReadOnlyList<ushort> offsets,
        SqliteBtreePageHeader header,
        int usableSpace,
        bool invalidData)
    {
        var seen = new HashSet<ushort>();
        for (var index = 0; index < offsets.Count; index++)
        {
            var offset = offsets[index];
            if (offset < header.CellContentAreaOffset || offset >= usableSpace)
            {
                Throw(
                    invalidData,
                    $"SQLite B-tree cell offset {offset} is outside the cell-content area or usable page space.");
            }

            if (!seen.Add(offset))
                Throw(invalidData, $"SQLite B-tree cell offset {offset} is duplicated.");
        }
    }

    private static void Throw(bool invalidData, string message)
    {
        if (invalidData)
            throw new InvalidDataException(message);

        throw new ArgumentException(message);
    }
}
