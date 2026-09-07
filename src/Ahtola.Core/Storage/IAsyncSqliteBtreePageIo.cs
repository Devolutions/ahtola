namespace Ahtola.Core.Storage;

/// <summary>
/// The read-only, asynchronous counterpart of <see cref="ISqliteBtreePageIo"/>: a page-access
/// boundary for code that walks a b-tree via genuinely asynchronous, positional page reads
/// instead of the synchronous <c>ReadPage</c> the rest of the engine uses.
/// </summary>
/// <remarks>
/// Everything above this boundary (page-header parsing, table/index page views, overflow-chain
/// traversal) is the exact same pure, buffer-in/struct-out logic the synchronous engine already
/// uses (<see cref="SqliteBtreePageHeader.Parse"/>, <see cref="SqliteTableLeafPageView.Parse"/>,
/// <see cref="SqliteTableInteriorPageView.Parse"/>, <see cref="SqliteOverflowPageView.Parse"/>).
/// Only the page-fetch primitive differs, matching the async-io-port porting rule of changing
/// the call style, not the semantics.
/// </remarks>
internal interface IAsyncSqliteBtreePageIo
{
    /// <summary>The portion of each page usable by SQLite (page size minus reserved space).</summary>
    int UsableSpace { get; }

    /// <summary>The committed database size, in pages.</summary>
    uint PageCount { get; }

    /// <summary>Reads one page, awaiting exactly one positional I/O operation per call.</summary>
    ValueTask<byte[]> ReadPageAsync(uint pageNumber, CancellationToken cancellationToken = default);
}
