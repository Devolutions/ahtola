namespace Ahtola.Core.Storage;

/// <summary>
/// File-format limits shared by every SQLite page-allocation path.
/// </summary>
/// <remarks>
/// Mirrors Turso <c>core/storage/pager.rs</c>: <c>PENDING_BYTE</c>,
/// <c>pending_byte_page_id()</c> and <c>DEFAULT_MAX_PAGE_COUNT</c>. The byte at
/// offset 0x40000000 is never part of a real page, so the page containing it is
/// permanently unusable and must be skipped by every allocator, freelist and
/// split reservation rather than being written or handed to a b-tree.
/// </remarks>
public static class SqlitePageLimits
{
    /// <summary>The file offset SQLite reserves for the legacy locking byte.</summary>
    public const uint PendingByte = 0x4000_0000;

    /// <summary>
    /// The largest page count SQLite allows by default (<c>0xfffffffe</c>).
    /// </summary>
    public const uint DefaultMaximumPageCount = 0xffff_fffe;

    /// <summary>
    /// The absolute upper bound on <c>max_page_count</c>.
    /// </summary>
    public const uint AbsoluteMaximumPageCount = 0xffff_fffe;

    /// <summary>
    /// The 1-based page number that contains <see cref="PendingByte"/>.
    /// </summary>
    public static uint PendingBytePage(int pageSize)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        return (PendingByte / (uint)pageSize) + 1;
    }

    /// <summary>Whether <paramref name="pageNumber"/> is the unusable pending-byte page.</summary>
    public static bool IsPendingBytePage(uint pageNumber, int pageSize)
        => pageNumber == PendingBytePage(pageSize);

    /// <summary>
    /// Clamps a requested maximum page count the way SQLite's
    /// <c>max_page_count</c> pragma does: it never shrinks below the pages the
    /// database already occupies and never exceeds the format ceiling.
    /// </summary>
    public static uint ClampMaximumPageCount(uint requested, uint currentPageCount)
    {
        if (requested == 0)
            requested = DefaultMaximumPageCount;
        if (requested > AbsoluteMaximumPageCount)
            requested = AbsoluteMaximumPageCount;
        return requested < currentPageCount ? currentPageCount : requested;
    }
}
