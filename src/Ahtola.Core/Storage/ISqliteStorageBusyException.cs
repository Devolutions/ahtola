namespace Ahtola.Core.Storage;

/// <summary>
/// Marks storage-level lock contention (pager roles, main-file lock bytes, WAL-index locks and
/// read snapshots) that SQLite reports as <c>SQLITE_BUSY</c> (5). Providers map any exception
/// carrying it to their busy error, so callers' retry loops keyed on error code 5 keep working.
/// </summary>
public interface ISqliteStorageBusyException
{
}
