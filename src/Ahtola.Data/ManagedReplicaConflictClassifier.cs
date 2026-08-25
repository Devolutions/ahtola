namespace Ahtola;

/// <summary>
/// Classifies every journaled local change in a rejected push batch as eligible for automatic
/// replay, requiring manual resolution, or itself conflicting. Pure: no I/O, no ambient state, so
/// the same durable (journal batch, conflict marker) pair always yields the same classification —
/// which is exactly what makes conflict resolution idempotent across crashes and retries.
/// </summary>
/// <remarks>
/// Every rule below fails toward "not eligible". Turso's own sync engine (see
/// <c>turso-src/sync/engine/src/database_sync_operations.rs</c>, <c>wal_push</c>) treats a push
/// conflict as terminal and never rebases, so there is no upstream classification to mirror; this
/// is an Ahtola managed extension and is deliberately conservative rather than clever.
/// </remarks>
internal static class ManagedReplicaConflictClassifier
{
    /// <summary>
    /// Classifies <paramref name="batch"/> against the conflict the server reported.
    /// </summary>
    internal static IReadOnlyList<AhtolaReplicaConflictEntry> Classify(
        IReadOnlyList<ReplicaLocalChange> batch,
        AhtolaReplicaConflictKind conflictKind,
        long? conflictingSequence)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var conflictingIndex = -1;
        if (conflictingSequence is { } sequence)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].Sequence == sequence)
                {
                    conflictingIndex = i;
                    break;
                }
            }
        }

        // Nothing is provably safe when the server did not correlate its rejection to a specific
        // replayed step, or when the sequence it named is not in this batch at all (a stale or
        // foreign reference). Fail closed: the whole batch is conflicting.
        if (conflictKind == AhtolaReplicaConflictKind.Unknown || conflictingIndex < 0)
        {
            var all = new AhtolaReplicaConflictEntry[batch.Count];
            for (var i = 0; i < batch.Count; i++)
                all[i] = Describe(batch[i], AhtolaReplicaChangeEligibility.Conflicting);
            return all;
        }

        var eligibility = new AhtolaReplicaChangeEligibility[batch.Count];
        for (var i = 0; i < batch.Count; i++)
            eligibility[i] = AhtolaReplicaChangeEligibility.Eligible;
        eligibility[conflictingIndex] = AhtolaReplicaChangeEligibility.Conflicting;

        var conflicting = batch[conflictingIndex];
        if (conflictKind == AhtolaReplicaConflictKind.SchemaChange)
        {
            // Schema DDL is never auto-rebased once the server has rejected a schema write: the
            // local and remote catalogs are already known to disagree, and the pull path's own
            // rule (ManagedReplicaBootstrapper.RejectIfLocalSchemaChangesConflictWithRemoteChanges)
            // proves only additive DDL can survive a remote refresh at all. Every remaining schema
            // entry is therefore manual, and so is every row write on a table whose schema fate is
            // undecided.
            for (var i = 0; i < batch.Count; i++)
            {
                if (i != conflictingIndex && batch[i].Kind == ReplicaLocalChangeKind.Schema)
                    eligibility[i] = AhtolaReplicaChangeEligibility.RequiresManualResolution;
            }
        }
        else
        {
            // Row-write conflict: any other write to the same physical row is ambiguous overlap.
            for (var i = 0; i < batch.Count; i++)
            {
                if (i != conflictingIndex && IsSameRow(batch[i], conflicting))
                    eligibility[i] = AhtolaReplicaChangeEligibility.RequiresManualResolution;
            }

            // A pending schema change on the conflicting row's table is equally undecided: the
            // conflicting row write and that DDL may depend on each other in either order.
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].Kind == ReplicaLocalChangeKind.Schema
                    && TargetsTable(batch[i], conflicting.Table))
                {
                    eligibility[i] = AhtolaReplicaChangeEligibility.RequiresManualResolution;
                }
            }
        }

        // Row writes on a table whose schema entry is undecided cannot be replayed against a
        // schema whose fate is unknown.
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch[i].Kind != ReplicaLocalChangeKind.Schema
                || eligibility[i] == AhtolaReplicaChangeEligibility.Eligible)
            {
                continue;
            }

            for (var j = 0; j < batch.Count; j++)
            {
                if (batch[j].Kind == ReplicaLocalChangeKind.Row
                    && eligibility[j] == AhtolaReplicaChangeEligibility.Eligible
                    && TargetsTable(batch[i], batch[j].Table))
                {
                    eligibility[j] = AhtolaReplicaChangeEligibility.RequiresManualResolution;
                }
            }
        }

        // Causal chains: a later write to a row whose earlier write is undecided would silently
        // reorder semantics if replayed without its prerequisite (for example update-then-delete
        // of the same row). Propagate forward to a fixed point so a chain of any length is caught.
        // Statement groups propagate in the same loop: one SQL statement can touch many rows and
        // is replayed (or discarded) as a unit, so an undecided member makes the whole statement
        // undecided. Anything else would let a resolution keep half a statement, whose remaining
        // rows could never be transmitted on their own.
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < batch.Count; i++)
            {
                if (eligibility[i] == AhtolaReplicaChangeEligibility.Eligible
                    || batch[i].Kind != ReplicaLocalChangeKind.Row)
                {
                    continue;
                }

                for (var j = i + 1; j < batch.Count; j++)
                {
                    if (eligibility[j] == AhtolaReplicaChangeEligibility.Eligible
                        && IsSameRow(batch[j], batch[i]))
                    {
                        eligibility[j] = AhtolaReplicaChangeEligibility.RequiresManualResolution;
                        changed = true;
                    }
                }
            }

            for (var i = 0; i < batch.Count; i++)
            {
                if (eligibility[i] == AhtolaReplicaChangeEligibility.Eligible
                    || batch[i].StatementSequence == 0)
                {
                    continue;
                }

                for (var j = 0; j < batch.Count; j++)
                {
                    if (j != i
                        && eligibility[j] == AhtolaReplicaChangeEligibility.Eligible
                        && batch[j].StatementSequence == batch[i].StatementSequence)
                    {
                        eligibility[j] = AhtolaReplicaChangeEligibility.RequiresManualResolution;
                        changed = true;
                    }
                }
            }
        }

        var entries = new AhtolaReplicaConflictEntry[batch.Count];
        for (var i = 0; i < batch.Count; i++)
            entries[i] = Describe(batch[i], eligibility[i]);
        return entries;
    }

    private static bool IsSameRow(ReplicaLocalChange candidate, ReplicaLocalChange reference)
        => candidate.Kind == ReplicaLocalChangeKind.Row
           && reference.Kind == ReplicaLocalChangeKind.Row
           && candidate.RowId == reference.RowId
           && string.Equals(candidate.Database, reference.Database, StringComparison.OrdinalIgnoreCase)
           && string.Equals(candidate.Table, reference.Table, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a schema change provably targets <paramref name="table"/>. Only the object names
    /// the existing DDL text helpers can parse are compared; an unparsable statement is treated as
    /// targeting every table, because guessing is exactly what this classifier must never do.
    /// </summary>
    private static bool TargetsTable(ReplicaLocalChange schemaChange, string table)
    {
        if (string.IsNullOrEmpty(table))
            return true;
        return ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget(schemaChange.Sql) is not { } target
               || string.Equals(target, table, StringComparison.OrdinalIgnoreCase);
    }

    private static AhtolaReplicaConflictEntry Describe(
        ReplicaLocalChange change,
        AhtolaReplicaChangeEligibility eligibility)
        => change.Kind switch
        {
            ReplicaLocalChangeKind.Row => new AhtolaReplicaConflictEntry(
                change.Sequence,
                AhtolaReplicaChangeKind.RowWrite,
                change.Table,
                change.RowId,
                eligibility),
            ReplicaLocalChangeKind.Schema => new AhtolaReplicaConflictEntry(
                change.Sequence,
                AhtolaReplicaChangeKind.SchemaChange,
                ManagedReplicaSchemaDdlText.TryGetSchemaStatementTarget(change.Sql) ?? string.Empty,
                null,
                eligibility),
            _ => new AhtolaReplicaConflictEntry(
                change.Sequence,
                AhtolaReplicaChangeKind.Unknown,
                string.Empty,
                null,
                AhtolaReplicaChangeEligibility.Conflicting),
        };
}
