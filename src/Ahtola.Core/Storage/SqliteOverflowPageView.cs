using System.Buffers.Binary;

namespace Ahtola.Core.Storage;

/// <summary>
/// An immutable snapshot of a SQLite overflow page.
/// </summary>
/// <remarks>
/// SQLite stores a big-endian next-page number in the first four usable bytes of
/// an overflow page. The remaining usable bytes are payload capacity; reserved
/// bytes are opaque and preserved when a parsed page is written back.
/// </remarks>
public sealed class SqliteOverflowPageView
{
    /// <summary>The number of bytes used by the next-overflow-page field.</summary>
    public const int HeaderLength = sizeof(uint);

    private readonly byte[] _page;

    private SqliteOverflowPageView(byte[] page, int usableSpace)
    {
        _page = page;
        PageSize = page.Length;
        UsableSpace = usableSpace;
        NextPageNumber = BinaryPrimitives.ReadUInt32BigEndian(page);
    }

    /// <summary>The exact physical page size in bytes.</summary>
    public int PageSize { get; }

    /// <summary>The number of bytes usable by SQLite on this page.</summary>
    public int UsableSpace { get; }

    /// <summary>The next overflow page, or zero when this page terminates the chain.</summary>
    public uint NextPageNumber { get; }

    /// <summary>The number of payload bytes available on this page.</summary>
    public int PayloadCapacity => UsableSpace - HeaderLength;

    /// <summary>
    /// The complete usable payload area. A chain reader determines how many bytes
    /// are logically present on the final page from the owning cell's payload length.
    /// </summary>
    public ReadOnlyMemory<byte> Payload => _page.AsMemory(HeaderLength, PayloadCapacity);

    /// <summary>
    /// Decodes an exact SQLite page and snapshots it before exposing its contents.
    /// </summary>
    public static SqliteOverflowPageView Parse(ReadOnlySpan<byte> page, int usableSpace)
    {
        ValidateGeometry(page.Length, usableSpace, invalidData: true);
        return new SqliteOverflowPageView(page.ToArray(), usableSpace);
    }

    /// <summary>
    /// Creates a zero-initialized SQLite overflow page with the supplied payload
    /// prefix. Unused usable bytes are zeroed; reserved bytes remain zero as well.
    /// </summary>
    public static SqliteOverflowPageView Create(
        int pageSize,
        int usableSpace,
        uint nextPageNumber,
        ReadOnlySpan<byte> payload)
    {
        ValidateGeometry(pageSize, usableSpace, invalidData: false);
        var payloadCapacity = usableSpace - HeaderLength;
        if (payload.Length > payloadCapacity)
        {
            throw new ArgumentException(
                $"SQLite overflow page payload cannot exceed {payloadCapacity} bytes.",
                nameof(payload));
        }

        var page = new byte[pageSize];
        BinaryPrimitives.WriteUInt32BigEndian(page, nextPageNumber);
        payload.CopyTo(page.AsSpan(HeaderLength));
        return new SqliteOverflowPageView(page, usableSpace);
    }

    /// <summary>Returns a copy of the exact physical page bytes.</summary>
    public byte[] ToArray() => _page.ToArray();

    /// <summary>
    /// Writes the exact physical page into <paramref name="destination"/>, including
    /// reserved bytes.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != PageSize)
        {
            throw new ArgumentException(
                $"SQLite overflow page destination must be exactly {PageSize} bytes.",
                nameof(destination));
        }

        _page.CopyTo(destination);
    }

    private static void ValidateGeometry(int pageSize, int usableSpace, bool invalidData)
    {
        if (pageSize < SqlitePageSize.Minimum
            || pageSize > SqlitePageSize.Maximum
            || (pageSize & (pageSize - 1)) != 0)
        {
            Throw(invalidData, nameof(pageSize), "SQLite overflow page size is invalid.");
        }

        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace
            || usableSpace > pageSize)
        {
            Throw(invalidData, nameof(usableSpace), "SQLite overflow page usable space is invalid.");
        }

        if (pageSize - usableSpace > byte.MaxValue)
        {
            Throw(invalidData, nameof(usableSpace), "SQLite overflow page reserved space is invalid.");
        }
    }

    private static void Throw(bool invalidData, string parameterName, string message)
    {
        if (invalidData)
            throw new InvalidDataException(message);

        throw new ArgumentOutOfRangeException(parameterName, message);
    }
}
