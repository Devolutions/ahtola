using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable SQLite index-leaf cell, containing a record payload and an
/// optional first overflow-page pointer.
/// </summary>
public sealed class SqliteIndexLeafCell
{
    /// <summary>The minimum cell allocation required by the SQLite file format.</summary>
    public const int MinimumStorageLength = sizeof(uint);

    private readonly byte[] _localPayload;

    private SqliteIndexLeafCell(
        ulong payloadLength,
        byte[] localPayload,
        uint? firstOverflowPage,
        int encodedLength)
    {
        PayloadLength = payloadLength;
        _localPayload = localPayload;
        FirstOverflowPage = firstOverflowPage;
        EncodedLength = encodedLength;
    }

    /// <summary>The complete logical record payload length.</summary>
    public ulong PayloadLength { get; }

    /// <summary>The record payload bytes stored on the b-tree page.</summary>
    public ReadOnlyMemory<byte> LocalPayload => _localPayload;

    /// <summary>The first overflow page, or <see langword="null"/> when fully local.</summary>
    public uint? FirstOverflowPage { get; }

    /// <summary>The exact byte count occupied by the encoded cell.</summary>
    public int EncodedLength { get; }

    /// <summary>Creates a fully local index-leaf cell.</summary>
    public static SqliteIndexLeafCell Create(ReadOnlySpan<byte> payload, int usableSpace)
        => Create((ulong)payload.Length, payload, firstOverflowPage: null, usableSpace);

    /// <summary>
    /// Creates a cell from its logical payload length and the payload bytes stored
    /// locally. Overflow-chain materialization is outside this primitive.
    /// </summary>
    public static SqliteIndexLeafCell Create(
        ulong payloadLength,
        ReadOnlySpan<byte> localPayload,
        uint? firstOverflowPage,
        int usableSpace)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            payloadLength,
            usableSpace);
        if (localPayload.Length != layout.LocalPayloadLength)
        {
            throw new ArgumentException(
                $"SQLite index-leaf cell requires {layout.LocalPayloadLength} local payload byte(s), but {localPayload.Length} were supplied.",
                nameof(localPayload));
        }

        if (layout.UsesOverflow)
        {
            if (firstOverflowPage is null or 0)
                throw new ArgumentOutOfRangeException(nameof(firstOverflowPage), "An overflowing SQLite index cell requires a non-zero first overflow page.");
        }
        else if (firstOverflowPage is not null)
        {
            throw new ArgumentException("A fully local SQLite index cell cannot have an overflow page.", nameof(firstOverflowPage));
        }

        var contentLength = checked(
            SqliteVarint.GetLength(payloadLength)
            + layout.StoredPayloadLength);
        return new SqliteIndexLeafCell(
            payloadLength,
            localPayload.ToArray(),
            firstOverflowPage,
            Math.Max(contentLength, MinimumStorageLength));
    }

    /// <summary>Decodes one index-leaf cell from the start of <paramref name="source"/>.</summary>
    public static SqliteIndexLeafCell Decode(ReadOnlySpan<byte> source, int usableSpace)
    {
        if (!SqliteVarint.TryRead(source, out var payloadLength, out var payloadLengthBytes))
            throw new InvalidDataException("SQLite index-leaf cell has an invalid payload-length varint.");

        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            payloadLength,
            usableSpace);
        var contentLength = checked(payloadLengthBytes + layout.StoredPayloadLength);
        var encodedLength = Math.Max(contentLength, MinimumStorageLength);
        if (encodedLength > source.Length)
            throw new InvalidDataException("SQLite index-leaf cell extends beyond its available page bytes.");

        var localPayload = source.Slice(payloadLengthBytes, layout.LocalPayloadLength).ToArray();
        uint? firstOverflowPage = null;
        if (layout.UsesOverflow)
        {
            firstOverflowPage = BinaryPrimitives.ReadUInt32BigEndian(source[(payloadLengthBytes + layout.LocalPayloadLength)..]);
            if (firstOverflowPage == 0)
                throw new InvalidDataException("SQLite index-leaf cell has a zero first overflow page.");
        }

        return new SqliteIndexLeafCell(
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

    /// <summary>Encodes this cell at the start of <paramref name="destination"/>.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
            throw new ArgumentException($"Destination must contain at least {EncodedLength} bytes.", nameof(destination));

        var payloadLengthBytes = SqliteVarint.Write(PayloadLength, destination);
        _localPayload.CopyTo(destination[payloadLengthBytes..]);
        var contentLength = payloadLengthBytes + _localPayload.Length;
        if (FirstOverflowPage is { } overflowPage)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[(payloadLengthBytes + _localPayload.Length)..], overflowPage);
            contentLength += SqlitePayloadLayout.OverflowPointerLength;
        }

        destination[contentLength..EncodedLength].Clear();
    }
}
