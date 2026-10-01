namespace Ahtola.Core.Storage;

/// <summary>
/// Raised by a <see cref="SqlitePager"/> in <see cref="SqlitePager.PlanCommitsOnly"/> mode at the
/// point a write would begin, carrying the database size that write was about to publish.
/// Nothing has been written, locked or staged in the pager when it is thrown.
/// </summary>
/// <remarks>
/// Deliberately derives from <see cref="Exception"/> rather than
/// <see cref="InvalidOperationException"/> or <see cref="EmbeddedSqlException"/>: persist paths
/// catch those to fall back to a different page layout, and a planning probe must travel
/// straight back to the caller that armed it instead of being mistaken for a layout failure.
/// </remarks>
internal sealed class SqlitePagerCommitPlannedException(uint targetDatabaseSizeInPages)
    : Exception("A planned SQLite pager commit was intercepted before it wrote any page.")
{
    /// <summary>The database size, in pages, the intercepted commit would have published.</summary>
    public uint TargetDatabaseSizeInPages { get; } = targetDatabaseSizeInPages;
}
