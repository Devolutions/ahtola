namespace Ahtola;

/// <summary>
/// The kind of locally journaled managed-replica change described by an
/// <see cref="AhtolaReplicaConflictEntry"/>.
/// </summary>
public enum AhtolaReplicaChangeKind
{
    /// <summary>
    /// The journaled change could not be classified as either a row write or a schema change.
    /// Treated conservatively: such an entry is never eligible for automatic replay.
    /// </summary>
    Unknown,

    /// <summary>
    /// A committed local row insert, update, or delete.
    /// </summary>
    RowWrite,

    /// <summary>
    /// A committed local schema-changing statement.
    /// </summary>
    SchemaChange,
}

/// <summary>
/// Whether one journaled local change may be replayed automatically on a freshly pulled remote
/// base after a push conflict. Classification is deliberately conservative: an entry is only
/// <see cref="Eligible"/> when the server's conflict report proves it is unrelated to the
/// rejected operation.
/// </summary>
public enum AhtolaReplicaChangeEligibility
{
    /// <summary>
    /// The change the server explicitly rejected, or — when the server did not identify which
    /// step failed (<see cref="AhtolaReplicaConflictKind.Unknown"/>, or a reported sequence that
    /// is not in the recorded batch) — every change in the batch, because nothing is provably
    /// safe.
    /// </summary>
    Conflicting,

    /// <summary>
    /// The change overlaps the rejected operation (the same row, the same schema object, or a
    /// later write causally chained onto one of those) and is never replayed automatically. The
    /// application must reconcile it explicitly, or discard it with
    /// <see cref="AhtolaReplicaConflictResolution.DiscardUnresolvedChanges"/>.
    /// </summary>
    RequiresManualResolution,

    /// <summary>
    /// The change is provably unrelated to the rejected operation and may be replayed verbatim
    /// on a freshly pulled base by
    /// <see cref="AhtolaReplicaConflictResolution.PullAndRebaseEligible"/>.
    /// </summary>
    Eligible,
}

/// <summary>
/// One journaled local change in an <see cref="AhtolaReplicaConflictReport"/>, together with the
/// eligibility the classifier computed for it. The internal journal representation is never
/// exposed: only the durable sequence, the change kind, the target object, and — for row writes —
/// the affected row id.
/// </summary>
public sealed class AhtolaReplicaConflictEntry
{
    internal AhtolaReplicaConflictEntry(
        long sequence,
        AhtolaReplicaChangeKind kind,
        string table,
        long? rowId,
        AhtolaReplicaChangeEligibility eligibility)
    {
        Sequence = sequence;
        Kind = kind;
        Table = table;
        RowId = rowId;
        Eligibility = eligibility;
    }

    /// <summary>
    /// Gets the durable, strictly monotonic journal sequence of this change. Sequences are never
    /// reused and never rewritten.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    /// Gets the kind of local operation this change captured.
    /// </summary>
    public AhtolaReplicaChangeKind Kind { get; }

    /// <summary>
    /// Gets the table this change targets, or an empty string when the target could not be
    /// determined (for example a schema statement whose object name is not parsed).
    /// </summary>
    public string Table { get; }

    /// <summary>
    /// Gets the affected row id for a <see cref="AhtolaReplicaChangeKind.RowWrite"/>, or
    /// <see langword="null"/> for other kinds.
    /// </summary>
    public long? RowId { get; }

    /// <summary>
    /// Gets whether this change may be replayed automatically on a freshly pulled base.
    /// </summary>
    public AhtolaReplicaChangeEligibility Eligibility { get; }
}

/// <summary>
/// An immutable, durably backed description of one open managed-replica push conflict: which
/// remote error was reported, which journal batch was rejected, and how every change in that
/// batch was classified. Produced by
/// <see cref="AhtolaConnection.InspectReplicaConflictAsync(CancellationToken)"/> without any
/// network access or local mutation.
/// </summary>
public sealed class AhtolaReplicaConflictReport
{
    internal AhtolaReplicaConflictReport(
        AhtolaReplicaConflictKind conflictKind,
        string? remoteErrorCode,
        long? conflictingSequence,
        long batchFirstSequence,
        long batchWatermark,
        IReadOnlyList<AhtolaReplicaConflictEntry> entries)
    {
        ConflictKind = conflictKind;
        RemoteErrorCode = remoteErrorCode;
        ConflictingSequence = conflictingSequence;
        BatchFirstSequence = batchFirstSequence;
        BatchWatermark = batchWatermark;
        Entries = entries;
        var unresolved = new List<AhtolaReplicaConflictEntry>();
        var eligible = new List<AhtolaReplicaConflictEntry>();
        foreach (var entry in entries)
        {
            if (entry.Eligibility == AhtolaReplicaChangeEligibility.Eligible)
                eligible.Add(entry);
            else
                unresolved.Add(entry);
        }

        UnresolvedEntries = unresolved;
        EligibleEntries = eligible;
    }

    /// <summary>
    /// Gets the kind of local operation the server rejected.
    /// </summary>
    public AhtolaReplicaConflictKind ConflictKind { get; }

    /// <summary>
    /// Gets the optional remote error code reported with the conflict.
    /// </summary>
    public string? RemoteErrorCode { get; }

    /// <summary>
    /// Gets the journal sequence the server associated with the rejected step, or
    /// <see langword="null"/> when the server did not identify one.
    /// </summary>
    public long? ConflictingSequence { get; }

    /// <summary>
    /// Gets the first journal sequence of the rejected push batch.
    /// </summary>
    public long BatchFirstSequence { get; }

    /// <summary>
    /// Gets the exclusive journal sequence boundary of the rejected push batch. This is the
    /// watermark the push would have acknowledged had it succeeded; because it failed, the
    /// journal watermark was deliberately left where it was.
    /// </summary>
    public long BatchWatermark { get; }

    /// <summary>
    /// Gets every journaled change in the rejected batch, in ascending sequence order, with its
    /// computed eligibility.
    /// </summary>
    public IReadOnlyList<AhtolaReplicaConflictEntry> Entries { get; }

    /// <summary>
    /// Gets the entries that are still unresolved — every change that is not
    /// <see cref="AhtolaReplicaChangeEligibility.Eligible"/>. Ordinary, manual, and automatic
    /// synchronization stay blocked while this is non-empty.
    /// </summary>
    public IReadOnlyList<AhtolaReplicaConflictEntry> UnresolvedEntries { get; }

    /// <summary>
    /// Gets the entries that may be replayed automatically by
    /// <see cref="AhtolaReplicaConflictResolution.PullAndRebaseEligible"/>.
    /// </summary>
    public IReadOnlyList<AhtolaReplicaConflictEntry> EligibleEntries { get; }
}

/// <summary>
/// The explicit resolution an application chooses for an open managed-replica push conflict.
/// Synchronization never picks one automatically.
/// </summary>
public enum AhtolaReplicaConflictResolution
{
    /// <summary>
    /// Pull a fresh remote base and replay only the provably
    /// <see cref="AhtolaReplicaChangeEligibility.Eligible"/> changes on top of it, using the
    /// same transactional logical replay and compensation an ordinary pull uses. Unresolved
    /// changes stay durably journaled and quarantined, and the conflict marker is retained, so
    /// ordinary synchronization remains blocked until they are resolved or discarded.
    /// </summary>
    PullAndRebaseEligible,

    /// <summary>
    /// Durably remove the still-unresolved journal entries without ever pushing them. This
    /// permanently drops those local writes from the replication stream and requires
    /// <see cref="AhtolaReplicaConflictResolutionOptions.AcknowledgeDataLoss"/>. Journal discard
    /// never advances the remote acknowledgement watermark, so it can always be distinguished
    /// from a remote-confirmed push.
    /// </summary>
    DiscardUnresolvedChanges,
}

/// <summary>
/// Caller-supplied policy for
/// <see cref="AhtolaConnection.ResolveReplicaConflictAsync(AhtolaReplicaConflictResolution, AhtolaReplicaConflictResolutionOptions?, CancellationToken)"/>.
/// </summary>
public sealed class AhtolaReplicaConflictResolutionOptions
{
    /// <summary>
    /// Gets whether the caller has explicitly acknowledged that
    /// <see cref="AhtolaReplicaConflictResolution.DiscardUnresolvedChanges"/> permanently drops
    /// locally committed writes that the server will never observe. Required for that
    /// resolution; ignored by every other resolution.
    /// </summary>
    public bool AcknowledgeDataLoss { get; init; }

    /// <summary>
    /// Gets an optional progress receiver for the pull performed by
    /// <see cref="AhtolaReplicaConflictResolution.PullAndRebaseEligible"/>.
    /// </summary>
    public IProgress<AhtolaSyncProgress>? Progress { get; init; }
}

/// <summary>
/// The immutable outcome of one explicit conflict resolution.
/// </summary>
public sealed class AhtolaReplicaConflictResolutionResult
{
    internal AhtolaReplicaConflictResolutionResult(
        AhtolaReplicaConflictResolution resolution,
        bool conflictCleared,
        int rebasedChangeCount,
        int discardedChangeCount,
        AhtolaReplicaConflictReport? remainingConflict,
        AhtolaSyncResult? syncResult)
    {
        Resolution = resolution;
        ConflictCleared = conflictCleared;
        RebasedChangeCount = rebasedChangeCount;
        DiscardedChangeCount = discardedChangeCount;
        RemainingConflict = remainingConflict;
        SyncResult = syncResult;
    }

    /// <summary>
    /// Gets the resolution that was applied.
    /// </summary>
    public AhtolaReplicaConflictResolution Resolution { get; }

    /// <summary>
    /// Gets whether the durable conflict marker was removed, unblocking ordinary
    /// synchronization. This is only ever <see langword="true"/> once every unresolved change has
    /// been explicitly resolved or discarded.
    /// </summary>
    public bool ConflictCleared { get; }

    /// <summary>
    /// Gets how many eligible journaled changes were replayed onto the freshly pulled base. They
    /// remain journaled and unacknowledged, and are pushed by the next ordinary synchronization.
    /// </summary>
    public int RebasedChangeCount { get; }

    /// <summary>
    /// Gets how many unresolved journaled changes were durably discarded.
    /// </summary>
    public int DiscardedChangeCount { get; }

    /// <summary>
    /// Gets the conflict that is still open after this resolution, or <see langword="null"/> when
    /// <see cref="ConflictCleared"/> is <see langword="true"/>.
    /// </summary>
    public AhtolaReplicaConflictReport? RemainingConflict { get; }

    /// <summary>
    /// Gets the result of the pull performed by
    /// <see cref="AhtolaReplicaConflictResolution.PullAndRebaseEligible"/>, or
    /// <see langword="null"/> for resolutions that never contact the remote endpoint.
    /// </summary>
    public AhtolaSyncResult? SyncResult { get; }
}

/// <summary>
/// Thrown by explicit and automatic managed-replica synchronization while a durable push-conflict
/// marker is still open. Synchronization fails closed rather than re-pushing a batch the server
/// already rejected; resolve the conflict with
/// <see cref="AhtolaConnection.InspectReplicaConflictAsync(CancellationToken)"/> and
/// <see cref="AhtolaConnection.ResolveReplicaConflictAsync(AhtolaReplicaConflictResolution, AhtolaReplicaConflictResolutionOptions?, CancellationToken)"/>
/// first.
/// </summary>
public sealed class AhtolaReplicaConflictPendingException : AhtolaException
{
    internal AhtolaReplicaConflictPendingException(
        string message,
        AhtolaReplicaConflictKind conflictKind,
        int unresolvedChangeCount)
        : base(message, AhtolaReplicaPushFailureKind.Conflict)
    {
        ConflictKind = conflictKind;
        UnresolvedChangeCount = unresolvedChangeCount;
    }

    /// <summary>
    /// Gets the kind of local operation the server rejected when the conflict was recorded.
    /// </summary>
    public AhtolaReplicaConflictKind ConflictKind { get; }

    /// <summary>
    /// Gets how many journaled local changes are still quarantined and unresolved.
    /// </summary>
    public int UnresolvedChangeCount { get; }
}
