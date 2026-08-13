namespace Ahtola.Core.Storage;

/// <summary>
/// Describes the portion of a SQLite cell payload stored on its b-tree page.
/// </summary>
public sealed class SqlitePayloadLayout
{
    public const int OverflowPointerLength = sizeof(uint);

    private SqlitePayloadLayout(
        ulong payloadLength,
        int minimumLocalPayloadLength,
        int maximumLocalPayloadLength,
        int localPayloadLength,
        bool usesOverflow)
    {
        PayloadLength = payloadLength;
        MinimumLocalPayloadLength = minimumLocalPayloadLength;
        MaximumLocalPayloadLength = maximumLocalPayloadLength;
        LocalPayloadLength = localPayloadLength;
        UsesOverflow = usesOverflow;
    }

    /// <summary>The logical payload length, including bytes on overflow pages.</summary>
    public ulong PayloadLength { get; }

    /// <summary>The SQLite minimum local payload threshold (M).</summary>
    public int MinimumLocalPayloadLength { get; }

    /// <summary>The SQLite maximum local payload threshold (X).</summary>
    public int MaximumLocalPayloadLength { get; }

    /// <summary>The count of logical payload bytes present in the cell.</summary>
    public int LocalPayloadLength { get; }

    /// <summary>Whether the cell must carry a four-byte overflow-page pointer.</summary>
    public bool UsesOverflow { get; }

    /// <summary>The local payload bytes plus a possible overflow-page pointer.</summary>
    public int StoredPayloadLength => LocalPayloadLength + (UsesOverflow ? OverflowPointerLength : 0);

    /// <summary>
    /// Calculates SQLite's local-payload layout for a b-tree cell.
    /// </summary>
    public static SqlitePayloadLayout Calculate(
        SqliteBtreePageType pageType,
        ulong payloadLength,
        int usableSpace)
    {
        ValidateUsableSpace(usableSpace);

        var minimum = ((usableSpace - 12) * 32 / 255) - 23;
        var maximum = pageType switch
        {
            SqliteBtreePageType.IndexInterior or SqliteBtreePageType.IndexLeaf
                => ((usableSpace - 12) * 64 / 255) - 23,
            SqliteBtreePageType.TableInterior or SqliteBtreePageType.TableLeaf
                => usableSpace - 35,
            _ => throw new ArgumentOutOfRangeException(nameof(pageType), pageType, "Unsupported SQLite B-tree page type."),
        };

        if (payloadLength <= (ulong)maximum)
        {
            return new SqlitePayloadLayout(
                payloadLength,
                minimum,
                maximum,
                checked((int)payloadLength),
                usesOverflow: false);
        }

        var candidate = (ulong)minimum
            + ((payloadLength - (ulong)minimum) % (ulong)(usableSpace - OverflowPointerLength));
        var local = candidate <= (ulong)maximum ? checked((int)candidate) : minimum;
        return new SqlitePayloadLayout(payloadLength, minimum, maximum, local, usesOverflow: true);
    }

    private static void ValidateUsableSpace(int usableSpace)
    {
        if (usableSpace < SqliteDatabaseHeader.MinimumUsableSpace
            || usableSpace > SqlitePageSize.Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usableSpace),
                usableSpace,
                $"SQLite usable page space must be between {SqliteDatabaseHeader.MinimumUsableSpace} and {SqlitePageSize.Maximum} bytes.");
        }
    }
}
