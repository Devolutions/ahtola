namespace Ahtola;

/// <summary>
/// Durable publication points that tests may interrupt to verify replica recovery.
/// </summary>
internal enum ManagedReplicaDurableBoundary
{
    BootstrapStagedDatabase,
    BootstrapSafetyStatePublished,
    BootstrapDatabasePublished,
    IncrementalApplyStagedDatabase,
    IncrementalApplyDatabasePublished,
    IncrementalApplyMetadataPublished,
    JournalAppendPersisted,
    JournalAcknowledgementPersisted,

    /// <summary>
    /// Hit immediately after an explicit, data-loss-acknowledged conflict discard has durably
    /// replaced the change journal (see
    /// <c>ManagedReplicaChangeJournal.DiscardUnacknowledged</c>), before the conflict marker is
    /// retired. Distinct from <see cref="JournalAcknowledgementPersisted"/> precisely because a
    /// discard is never a remote acknowledgement.
    /// </summary>
    JournalDiscardPersisted,

    /// <summary>
    /// Hit immediately after a push-conflict marker has been durably published (see
    /// <c>ManagedReplicaConflictState.Write</c>), before the typed conflict exception is
    /// rethrown, and again whenever a resolution rewrites the still-unresolved set.
    /// </summary>
    ConflictMarkerPublished,

    /// <summary>
    /// Hit immediately after a push-conflict marker has been removed, which is the single point
    /// at which ordinary synchronization becomes unblocked again.
    /// </summary>
    ConflictMarkerRetired,
    /// <summary>
    /// Hit immediately after the exclusive physical apply lease is acquired for a managed replica
    /// push publication step (batch selection, push-intent publication, journal acknowledgement,
    /// conflict restore, and conflict-marker publication), before that step re-validates the
    /// durable generation it was negotiated against. The network round trip itself is deliberately
    /// outside the lease.
    /// </summary>
    ReplicaPushPublicationLockAcquired,

    /// <summary>
    /// Hit after the physical-identity push-flight lease has excluded every competing process,
    /// before local state is selected or any remote request is made.
    /// </summary>
    ReplicaPushFlightLockAcquired,

    /// <summary>
    /// Hit immediately after metadata durably records the exact pending batch and source pull
    /// generation, before watermark verification or remote SQL replay.
    /// </summary>
    ReplicaPushIntentPublished,

    /// <summary>
    /// Hit after the remote push transaction returned success, before uncancellable local
    /// acknowledgement publication begins.
    /// </summary>
    ReplicaPushRemoteCommitObserved,

    /// <summary>
    /// Hit after metadata durably retires a push intent following acknowledgement or a definitive
    /// conflict.
    /// </summary>
    ReplicaPushIntentRetired,

    /// <summary>
    /// Hit after a pull response is fully available locally and immediately before it waits for
    /// the push-flight lease that protects publication.
    /// </summary>
    ReplicaPullPublicationLockWaiting,

    /// <summary>
    /// Hit immediately after the exclusive physical apply lease is acquired for an explicit
    /// conflict resolution's local publication (journal discard and marker retirement), before any
    /// irreversible work.
    /// </summary>
    ReplicaConflictResolutionLockAcquired,

    /// <summary>
    /// Hit immediately after a publication request has taken ownership of the per-path publication
    /// slot and before any host is closed. This is the boundary at which cancellation is still
    /// completely free of consequences.
    /// </summary>
    ReplicaPublicationOwnershipAcquired,
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

    /// <summary>
    /// Hit immediately after a bootstrap has durably published its (database, metadata) pair
    /// together with the recorded obligation to run the mandatory post-bootstrap logical
    /// catch-up, and strictly before that catch-up begins. Fires only for MVCC-logical
    /// bootstraps, which are the only ones that owe a catch-up. It is deliberately outside the
    /// bootstrap's own compensating cleanup, so an interruption here leaves exactly the durable
    /// state a crash would: an installed replica that is not yet exposable. Tests use it to prove
    /// the next open detects the owed catch-up and finishes it instead of re-downloading the
    /// bootstrap or, worse, exposing a never-caught-up replica.
    /// </summary>
    BootstrapCatchUpRequirementPublished,

    /// <summary>
    /// Hit at the start of the mandatory post-bootstrap logical catch-up, after it has been
    /// determined to be owed but before any of its work (partial-image completion or the
    /// catch-up pull itself) runs.
    /// </summary>
    BootstrapCatchUpStarted,

    /// <summary>
    /// Hit once the mandatory post-bootstrap logical catch-up has fully succeeded and its own
    /// metadata is durable, strictly before the bootstrap completion marker is retired -- the
    /// single transition that makes the replica exposable. An interruption here leaves a replica
    /// whose data is already current but whose marker still asserts the obligation; the next open
    /// must repeat the (now no-op, same-revision) catch-up and retire the marker, never replay
    /// anything harmful.
    /// </summary>
    BootstrapCatchUpPublished,
    PageMutationIntentPersisted,
    PageMutationDatabasePersisted,
    PartialImageCompletionStarted,

    /// <summary>
    /// Hit immediately after a newly completed full image has atomically replaced the remote-base
    /// snapshot, and strictly before the metadata that records its hash is published. The
    /// superseded snapshot is still retained at this point, so metadata's older hash still
    /// resolves.
    /// </summary>
    PartialImageBaseSnapshotPublished,

    /// <summary>
    /// Hit immediately after the completed full image's metadata (carrying the new remote-base
    /// hash) is durable, and strictly before the superseded snapshot copy is retired.
    /// </summary>
    PartialImageMetadataPublished,
    RevertWalStaged,
    RevertWalPublished,
    RevertMetadataPublished,
    RevertCheckpointed,
    RevertRemoteApplyIntentPublished,
    RevertCommittedRestoreIntentPublished,
    RevertCommittedRestoreStagedDatabase,
    RevertCommittedRestoreDatabasePublished,
    RevertCommittedReadyMetadataPublished,
    RevertConflictRestoreIntentPublished,
    RevertRestoreStagedDatabase,
    RevertRestoreDatabasePublished,
    RevertRestoreMetadataPublished,
    RevertRetired,

    /// <summary>
    /// Hit after all remote pages are available and immediately before partial-image completion
    /// waits for the push/apply publication leases.
    /// </summary>
    PartialImagePublicationLockWaiting,

    /// <summary>
    /// Windows-only boundary after the private replacement inode's SQLite byte-range lease is
    /// released because ReplaceFile cannot rename a locked source, while the old destination inode
    /// remains locked.
    /// </summary>
    MainFileReplacementSourceLeaseReleased,

    /// <summary>
    /// Windows-only boundary after ReplaceFile publishes the replacement inode and before its
    /// SQLite byte-range lease is reacquired through the final path.
    /// </summary>
    MainFileReplacementPublishedBeforeLease,

    /// <summary>
    /// Windows-only boundary after rollback releases both SQLite inode leases and before ReplaceFile
    /// restores the retained backup.
    /// </summary>
    MainFileRollbackLeasesReleased,

    /// <summary>
    /// Hit after the deterministic replacement intent is durable and before the main-file swap.
    /// </summary>
    MainFileReplacementIntentPublished,

    /// <summary>
    /// Hit after completed publication deletes the old-image backup but before retiring the intent.
    /// </summary>
    MainFileReplacementBackupRetired,

    /// <summary>
    /// Hit after completed publication retires its durable replacement intent.
    /// </summary>
    MainFileReplacementIntentRetired,

    /// <summary>
    /// Hit after rollback restores the exact old database image but before retiring recovery state.
    /// </summary>
    MainFileRollbackDatabaseRestored,

    /// <summary>
    /// Hit after rollback retires its durable replacement intent and deterministic artifacts.
    /// </summary>
    MainFileRollbackIntentRetired,

    /// <summary>
    /// Hit when durable replacement recovery finds an intent, before it validates or mutates any
    /// database, metadata, or replacement artifact.
    /// </summary>
    MainFileReplacementRecoveryStarted,
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
