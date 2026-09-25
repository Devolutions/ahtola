namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Options for a bounded, page-limited asynchronous scan connection (see
/// <see cref="AhtolaBrowserDataSource.OpenBoundedScanConnectionAsync"/>).
/// </summary>
public sealed class AhtolaBrowserBoundedScanOptions
{
    /// <summary>
    /// The default maximum number of SQLite pages a bounded scan connection's cursors may hold
    /// resident at once.
    /// </summary>
    public const int DefaultPageBudget = 16;

    /// <summary>
    /// The maximum number of SQLite pages a cursor opened by this connection may hold resident
    /// at once (root-to-leaf traversal stack, plus the current leaf and any overflow page in
    /// flight). This is an enforced ceiling, not an advisory default: a cursor whose actual
    /// required stack depth exceeds it throws
    /// <see cref="AhtolaBrowserBoundedQueryException"/> rather than silently exceeding it.
    /// For encrypted WAL databases, committed and in-flight WAL page-location maps are each
    /// capped at this number of entries during open; an oversized WAL fails before scanning.
    /// </summary>
    public int PageBudget { get; init; } = DefaultPageBudget;
}
