namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// A point-in-time view of what a mirrored browser database owes its durable
/// store, and how many operations it has already sent there.
/// </summary>
/// <param name="PersistentOperations">
/// Operations issued to the persistent store since the mirror was created. In a
/// browser this is the number of OPFS worker round trips.
/// </param>
/// <param name="QueuedMutations">Mutations queued and not yet handed to a flush.</param>
/// <param name="InFlightMutations">
/// Mutations already handed to a running flush but not yet written to the durable store.
/// </param>
/// <param name="HasUnflushedWork">
/// Whether the mirror still owes the durable store anything, using exactly the predicate
/// synchronous teardown fails closed on.
/// </param>
internal readonly record struct BrowserMirrorMetrics(
    long PersistentOperations,
    int QueuedMutations,
    int InFlightMutations,
    bool HasUnflushedWork)
{
    /// <summary>Queued plus in-flight durable work.</summary>
    internal int PendingMutations => QueuedMutations + InFlightMutations;
}
