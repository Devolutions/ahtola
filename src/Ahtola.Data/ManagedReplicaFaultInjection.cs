namespace Ahtola;

/// <summary>
/// Durable publication points that tests may interrupt to verify replica recovery.
/// </summary>
internal enum ManagedReplicaDurableBoundary
{
    BootstrapStagedDatabase,
    BootstrapDatabasePublished,
    IncrementalApplyStagedDatabase,
    IncrementalApplyDatabasePublished,
    IncrementalApplyMetadataPublished,
    JournalAppendPersisted,
    JournalAcknowledgementPersisted,
    LogicalApplyCommitted,
    LogicalApplyCheckpointed,
    LogicalApplyMetadataPublished,

    /// <summary>
    /// Hit immediately after the single exclusive apply lease (see
    /// <c>ManagedReplicaApplyLock</c>) is acquired, before the caller re-checks sidecars/
    /// fingerprint and applies. Shared by all three acquisition sites (bootstrap install,
    /// logical apply, incremental/replace-base page apply): tests distinguish which site fired
    /// by which API they invoked, not by a separate boundary value.
    /// </summary>
    ReplicaApplyLockAcquired,

    /// <summary>
    /// Hit at the very start of the failed-bootstrap-catch-up rollback path (see
    /// <c>ManagedReplicaConnectionHost.RollBackFailedCatchUpIfStillThisGenerationAsync</c>),
    /// after the mandatory post-bootstrap catch-up has thrown but before the rollback
    /// (re)acquires the apply lease to verify the on-disk revision is still the exact
    /// bootstrapped generation it set out to undo. Tests use this to publish a competing, newer
    /// revision for the same path in that window and prove the rollback detects it and backs
    /// off instead of deleting unconditionally.
    /// </summary>
    BootstrapCatchUpFailureObserved,
}

/// <summary>
/// Async-flow-local fault injection for deterministic managed replica durability tests.
/// </summary>
internal static class ManagedReplicaFaultInjection
{
    private static readonly AsyncLocal<Action<ManagedReplicaDurableBoundary>?> Callback = new();

    internal static IDisposable Push(Action<ManagedReplicaDurableBoundary> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var previous = Callback.Value;
        Callback.Value = callback;
        return new Scope(previous);
    }

    internal static void Hit(ManagedReplicaDurableBoundary boundary)
        => Callback.Value?.Invoke(boundary);

    private sealed class Scope(Action<ManagedReplicaDurableBoundary>? previous) : IDisposable
    {
        private Action<ManagedReplicaDurableBoundary>? _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _previous, null) is { } previous)
                Callback.Value = previous;
            else
                Callback.Value = null;
        }
    }
}
