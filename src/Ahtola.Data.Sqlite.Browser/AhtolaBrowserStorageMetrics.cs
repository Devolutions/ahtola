namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// A point-in-time view of a browser data source's durable-transport activity.
/// </summary>
/// <param name="PersistentOperations">
/// Operations issued to OPFS through the storage worker since the data source
/// initialized. Supported synchronous reads are served from the managed
/// in-memory mirror and never change this value.
/// </param>
/// <param name="PendingMutations">
/// Mutations the mirror still owes OPFS: everything queued <em>plus</em> everything already
/// handed to a running flush but not yet written. Counting only the queue would report zero for
/// the whole duration of a flush — precisely when the most durable work is outstanding.
/// Synchronous close and disposal fail closed while this is non-zero.
/// </param>
/// <param name="HasUnflushedWork">
/// Whether the mirror owes OPFS anything at all, using exactly the predicate synchronous close
/// and disposal fail closed on. This is <see langword="true"/> whenever
/// <paramref name="PendingMutations"/> is non-zero, and can also be <see langword="true"/> for a
/// flush that has begun but not yet claimed the queue.
/// </param>
public readonly record struct AhtolaBrowserStorageMetrics(
    long PersistentOperations,
    int PendingMutations,
    bool HasUnflushedWork);
