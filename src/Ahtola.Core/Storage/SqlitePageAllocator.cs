namespace Ahtola.Core.Storage;

/// <summary>
/// A page number reserved for a pending SQLite page mutation.
/// </summary>
public readonly record struct SqlitePageAllocation
{
    /// <summary>Validates and creates one page allocation.</summary>
    public SqlitePageAllocation(uint pageNumber, uint databaseSizeInPages)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        if (databaseSizeInPages < pageNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(databaseSizeInPages),
                "The database page count cannot be smaller than an allocated page number.");
        }

        PageNumber = pageNumber;
        DatabaseSizeInPages = databaseSizeInPages;
    }

    /// <summary>The non-zero SQLite page number reserved for the mutation.</summary>
    public uint PageNumber { get; }

    /// <summary>The database page count after this allocation is made durable.</summary>
    public uint DatabaseSizeInPages { get; }
}

/// <summary>
/// Reserves unique page numbers for a pending SQLite mutation.
/// </summary>
/// <remarks>
/// Allocation only reserves a page number; it does not write a page image. A
/// future freelist-backed implementation can return a reusable page while
/// preserving the same mutation contract.
/// </remarks>
public interface ISqlitePageAllocator
{
    /// <summary>Reserves one unique page for a pending mutation.</summary>
    SqlitePageAllocation Allocate();
}

/// <summary>
/// A reservation-only allocator that assigns new page numbers at the end of a
/// <see cref="SqlitePageStore"/>.
/// </summary>
/// <remarks>
/// This low-level helper remains append-only for callers that stage page images
/// without freelist header ownership (split writers, unit tests). Ordinary DML
/// allocation goes through <see cref="SqliteStagedBtreePageIo.AllocatePage"/>,
/// which reuses freelist leaves/trunks before growing the file, matching Turso
/// <c>Pager::allocate_page</c>. Reserved page numbers become durable only when
/// the caller writes the corresponding mutation through a WAL or page-store
/// checkpoint path.
/// </remarks>
public sealed class SqliteAppendOnlyPageAllocator : ISqlitePageAllocator
{
    private uint _nextPageNumber;

    /// <summary>Creates an allocator beginning one page after the store's current end.</summary>
    public SqliteAppendOnlyPageAllocator(SqlitePageStore pageStore)
        : this(RequirePageStore(pageStore).PageCount, pageStore.PageSize)
    {
    }

    /// <summary>
    /// Creates an allocator beginning one page after a committed pager view.
    /// </summary>
    /// <param name="sourceDatabaseSizeInPages">The committed page count.</param>
    /// <param name="pageSize">
    /// The database page size, used to locate the unusable pending-byte page.
    /// </param>
    /// <param name="maximumPageCount">
    /// The growth ceiling; clamped to at least <paramref name="sourceDatabaseSizeInPages"/>
    /// and at most <see cref="SqlitePageLimits.AbsoluteMaximumPageCount"/>.
    /// </param>
    public SqliteAppendOnlyPageAllocator(
        uint sourceDatabaseSizeInPages,
        int pageSize = SqlitePageSize.Default,
        uint maximumPageCount = SqlitePageLimits.DefaultMaximumPageCount)
    {
        if (sourceDatabaseSizeInPages == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDatabaseSizeInPages));

        SqlitePageSize.Validate(pageSize);

        PageSize = pageSize;
        PendingBytePageNumber = SqlitePageLimits.PendingBytePage(pageSize);
        MaximumPageCount = SqlitePageLimits.ClampMaximumPageCount(
            maximumPageCount,
            sourceDatabaseSizeInPages);
        SourceDatabaseSizeInPages = sourceDatabaseSizeInPages;
        _nextPageNumber = Advance(sourceDatabaseSizeInPages);
    }

    /// <summary>The page count observed when this allocator was created.</summary>
    public uint SourceDatabaseSizeInPages { get; }

    /// <summary>The page size used to locate the unusable pending-byte page.</summary>
    public int PageSize { get; }

    /// <summary>The 1-based page number that contains the SQLite pending byte.</summary>
    public uint PendingBytePageNumber { get; }

    /// <summary>The clamped growth ceiling enforced by <see cref="Allocate"/>.</summary>
    public uint MaximumPageCount { get; }

    /// <summary>The next append-only page number, or zero once the range is exhausted.</summary>
    public uint NextPageNumber => _nextPageNumber;

    /// <summary>
    /// The page number that would be produced <paramref name="ahead"/> allocations
    /// after the next one, skipping the pending-byte page. <c>Peek(0)</c> equals
    /// <see cref="NextPageNumber"/>.
    /// </summary>
    public uint Peek(uint ahead)
    {
        var pageNumber = _nextPageNumber;
        for (var index = 0u; index < ahead && pageNumber != 0; index++)
            pageNumber = Advance(pageNumber);

        return pageNumber;
    }

    /// <inheritdoc />
    public SqlitePageAllocation Allocate()
    {
        if (_nextPageNumber == 0)
            throw new InvalidOperationException("SQLite cannot allocate a page beyond UInt32.MaxValue.");

        var pageNumber = _nextPageNumber;
        if (pageNumber > MaximumPageCount)
        {
            throw new InvalidOperationException(
                $"SQLite database is full: page {pageNumber} exceeds the maximum page count {MaximumPageCount}.");
        }

        _nextPageNumber = Advance(pageNumber);
        return new SqlitePageAllocation(pageNumber, pageNumber);
    }

    private uint Advance(uint pageNumber)
    {
        if (pageNumber == uint.MaxValue)
            return 0;

        var next = pageNumber + 1;
        if (next == PendingBytePageNumber)
            next = next == uint.MaxValue ? 0 : next + 1;

        return next;
    }

    private static SqlitePageStore RequirePageStore(SqlitePageStore pageStore)
    {
        ArgumentNullException.ThrowIfNull(pageStore);
        return pageStore;
    }
}
