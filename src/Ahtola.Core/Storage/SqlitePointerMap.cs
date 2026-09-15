namespace Ahtola.Core.Storage;

/// <summary>
/// SQLite pointer-map layout for auto-vacuum databases, mirroring Turso's
/// <c>ptrmap</c> module. Auto-vacuum reserves pointer-map pages so pages can
/// be relocated by number; a reader that does not relocate pages only needs
/// to identify them, never to interpret their entries.
/// </summary>
internal static class SqlitePointerMap
{
    /// <summary>Each entry is one type byte plus a four-byte parent page number.</summary>
    public const int EntryLength = 5;

    /// <summary>Page 2 is the first pointer-map page in every auto-vacuum database.</summary>
    public const uint FirstPage = 2;

    /// <summary>The number of database pages one pointer-map page maps.</summary>
    public static int EntriesPerPage(int usableSpace) => usableSpace / EntryLength;

    /// <summary>
    /// The number of database pages spanned by one pointer-map cycle: the
    /// mapped pages plus the pointer-map page itself.
    /// </summary>
    public static int CycleLength(int usableSpace) => EntriesPerPage(usableSpace) + 1;

    /// <summary>
    /// Returns whether <paramref name="pageNumber"/> (1-based) is a pointer-map
    /// page. Page 1 (the schema page) never is.
    /// </summary>
    public static bool IsPointerMapPage(uint pageNumber, int usableSpace)
    {
        if (pageNumber < FirstPage)
            return false;

        var cycleLength = CycleLength(usableSpace);
        return ((pageNumber - FirstPage) % (ulong)cycleLength) == 0;
    }
}
