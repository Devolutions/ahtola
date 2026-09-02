using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable SQLite table-leaf cell, including its local payload and optional
/// first overflow-page pointer.
/// </summary>
public sealed class SqliteTableLeafCell
{
    public const int MinimumStorageLength = sizeof(uint);

    private readonly byte[] _localPayload;

    private SqliteTableLeafCell(
        long rowId,
        ulong payloadLength,
        byte[] localPayload,
        uint? firstOverflowPage,
        int encodedLength)
    {
        RowId = rowId;
        PayloadLength = payloadLength;
        _localPayload = localPayload;
        FirstOverflowPage = firstOverflowPage;
        EncodedLength = encodedLength;
    }

    /// <summary>The signed SQLite rowid represented by the cell's unsigned varint.</summary>
    public long RowId { get; }

    /// <summary>The logical payload length, including bytes on overflow pages.</summary>
    public ulong PayloadLength { get; }

    /// <summary>The payload bytes stored directly in this cell.</summary>
    public ReadOnlyMemory<byte> LocalPayload => _localPayload;

    /// <summary>
    /// Offset of <see cref="LocalPayload"/> from the start of the encoded cell
    /// (payload-length varint plus rowid varint).
    /// </summary>
    public int LocalPayloadOffset
        => SqliteVarint.GetLength(PayloadLength) + SqliteVarint.GetLength(unchecked((ulong)RowId));

    /// <summary>The first overflow page, or <see langword="null"/> for local payloads.</summary>
    public uint? FirstOverflowPage { get; }

    /// <summary>
    /// The exact number of bytes occupied by the encoded cell, including SQLite's
    /// required four-byte minimum cell allocation.
    /// </summary>
    public int EncodedLength { get; }

    /// <summary>
    /// The byte count a table-leaf cell would occupy for the given rowid and
    /// payload length, without materializing the cell.
    /// </summary>
    /// <remarks>
    /// Page-packing decides leaf boundaries from this length alone; building a
    /// throwaway cell copies the whole payload just to read it back.
    /// </remarks>
    public static int CalculateEncodedLength(long rowId, ulong payloadLength, int usableSpace)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            payloadLength,
            usableSpace);
        var contentLength = checked(
            SqliteVarint.GetLength(payloadLength)
            + SqliteVarint.GetLength(unchecked((ulong)rowId))
            + layout.StoredPayloadLength);
        return Math.Max(contentLength, MinimumStorageLength);
    }

    /// <summary>
    /// Creates a cell whose complete payload fits locally.
    /// </summary>
    public static SqliteTableLeafCell Create(
        long rowId,
        ReadOnlySpan<byte> payload,
        int usableSpace)
    {
        return Create(rowId, (ulong)payload.Length, payload, firstOverflowPage: null, usableSpace);
    }

    /// <summary>
    /// Creates a cell from its logical payload length and already-local payload
    /// bytes. Overflow chains themselves are intentionally outside this primitive.
    /// </summary>
    public static SqliteTableLeafCell Create(
        long rowId,
        ulong payloadLength,
        ReadOnlySpan<byte> localPayload,
        uint? firstOverflowPage,
        int usableSpace)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            payloadLength,
            usableSpace);
        if (localPayload.Length != layout.LocalPayloadLength)
        {
            throw new ArgumentException(
                $"SQLite table-leaf cell requires {layout.LocalPayloadLength} local payload byte(s), but {localPayload.Length} were supplied.",
                nameof(localPayload));
        }

        if (layout.UsesOverflow)
        {
            if (firstOverflowPage is null or 0)
                throw new ArgumentOutOfRangeException(nameof(firstOverflowPage), "An overflowing SQLite cell requires a non-zero first overflow page.");
        }
        else if (firstOverflowPage is not null)
        {
            throw new ArgumentException("A fully local SQLite cell cannot have an overflow page.", nameof(firstOverflowPage));
        }

        var contentLength = checked(
            SqliteVarint.GetLength(payloadLength)
            + SqliteVarint.GetLength(unchecked((ulong)rowId))
            + layout.StoredPayloadLength);
        var encodedLength = Math.Max(contentLength, MinimumStorageLength);
        return new SqliteTableLeafCell(
            rowId,
            payloadLength,
            localPayload.ToArray(),
            firstOverflowPage,
            encodedLength);
    }

    /// <summary>
    /// Decodes one table-leaf cell from the beginning of <paramref name="source"/>.
    /// <see cref="EncodedLength"/> identifies the exact cell boundary.
    /// </summary>
    public static SqliteTableLeafCell Decode(ReadOnlySpan<byte> source, int usableSpace)
    {
        if (!SqliteVarint.TryRead(source, out var payloadLength, out var payloadLengthBytes))
            throw new InvalidDataException("SQLite table-leaf cell has an invalid payload-length varint.");
        if (!SqliteVarint.TryRead(source[payloadLengthBytes..], out var rowIdValue, out var rowIdBytes))
            throw new InvalidDataException("SQLite table-leaf cell has an invalid rowid varint.");

        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            payloadLength,
            usableSpace);
        var payloadOffset = checked(payloadLengthBytes + rowIdBytes);
        var contentLength = checked(payloadOffset + layout.StoredPayloadLength);
        var encodedLength = Math.Max(contentLength, MinimumStorageLength);
        if (encodedLength > source.Length)
            throw new InvalidDataException("SQLite table-leaf cell extends beyond its available page bytes.");

        var localPayload = source.Slice(payloadOffset, layout.LocalPayloadLength).ToArray();
        uint? firstOverflowPage = null;
        if (layout.UsesOverflow)
        {
            firstOverflowPage = BinaryPrimitives.ReadUInt32BigEndian(source[(payloadOffset + layout.LocalPayloadLength)..]);
            if (firstOverflowPage == 0)
                throw new InvalidDataException("SQLite table-leaf cell has a zero first overflow page.");
        }

        return new SqliteTableLeafCell(
            unchecked((long)rowIdValue),
            payloadLength,
            localPayload,
            firstOverflowPage,
            encodedLength);
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

        var payloadLengthBytes = SqliteVarint.Write(PayloadLength, destination);
        var rowIdBytes = SqliteVarint.Write(unchecked((ulong)RowId), destination[payloadLengthBytes..]);
        var payloadOffset = payloadLengthBytes + rowIdBytes;
        _localPayload.CopyTo(destination[payloadOffset..]);
        var contentLength = payloadOffset + _localPayload.Length;
        if (FirstOverflowPage is { } overflowPage)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[(payloadOffset + _localPayload.Length)..], overflowPage);
            contentLength += SqlitePayloadLayout.OverflowPointerLength;
        }

        destination[contentLength..EncodedLength].Clear();
    }
}
