using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola;

/// <summary>
/// Projects a managed embedded replica connection's still-pending local change journal batch
/// (<see cref="ReplicaLocalChangeBatch"/>) into Ahtola's public change-data-capture row contract
/// (<see cref="AhtolaReplicaChangeRow"/>). This is a pure, read-only adapter: it never writes to
/// a real <c>turso_cdc</c> table, never appends to the replica change journal, and never
/// acknowledges its watermark. See <see cref="AhtolaConnection.PeekPendingChangeCapture"/> for
/// the public entry point and the exact guarantees/limitations documented on
/// <see cref="AhtolaReplicaChangeRow"/>.
/// </summary>
internal static class ManagedReplicaChangeCaptureProjector
{
    public static AhtolaReplicaChangeCaptureBatch Project(
        IManagedConnectionAdapter connection,
        ReplicaLocalChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var changes = batch.Changes;
        foreach (var change in changes)
        {
            if (change.Kind == ReplicaLocalChangeKind.Schema)
            {
                throw new AhtolaReplicaChangeCaptureException(
                    "The pending managed embedded replica change journal contains a schema "
                    + "(DDL) change, which has no row-level before/after image and cannot be "
                    + "represented in the change-data-capture row contract. Push the pending "
                    + "changes before peeking pending change-data-capture.");
            }
        }

        // The live row for an insert/update is only guaranteed to reflect that specific change's
        // own result when no later pending change touches the same (table, rowid) key again.
        // This must be computed against the full pending batch (never a caller-truncated slice)
        // or a later change outside the analyzed window could silently make an "after" image
        // look correct when it is actually stale.
        var lastSequenceByKey = new Dictionary<(string Table, long RowId), long>();
        foreach (var change in changes)
        {
            var key = (change.Table, change.RowId);
            if (!lastSequenceByKey.TryGetValue(key, out var existing) || change.Sequence > existing)
                lastSequenceByKey[key] = change.Sequence;
        }

        var rows = new List<AhtolaReplicaChangeRow>(changes.Count);
        foreach (var change in changes)
            rows.Add(ProjectRow(connection, change, lastSequenceByKey));

        return new AhtolaReplicaChangeCaptureBatch(batch.FirstSequence, batch.Watermark, rows);
    }

    private static AhtolaReplicaChangeRow ProjectRow(
        IManagedConnectionAdapter connection,
        ReplicaLocalChange change,
        IReadOnlyDictionary<(string Table, long RowId), long> lastSequenceByKey)
    {
        var changeType = change.Operation switch
        {
            SqliteChangeOperation.Insert => AhtolaReplicaChangeType.Insert,
            SqliteChangeOperation.Update => AhtolaReplicaChangeType.Update,
            SqliteChangeOperation.Delete => AhtolaReplicaChangeType.Delete,
            _ => throw new AhtolaReplicaChangeCaptureException(
                $"Managed embedded replica change journal has an entry for table "
                + $"'{change.Table}' with an unrecognized operation "
                + $"({change.Operation}) that cannot be represented in the "
                + "change-data-capture row contract."),
        };

        byte[]? before = null;
        byte[]? after = null;

        if (change.Operation == SqliteChangeOperation.Delete)
        {
            var beforeRecord = change.BeforeRecord
                ?? throw new AhtolaReplicaChangeCaptureException(
                    "Managed embedded replica change journal has a delete entry for table "
                    + $"'{change.Table}' (rowid {change.RowId}) with no captured before-image. "
                    + "This happens only for entries written by an older journal format that "
                    + "did not capture delete pre-images; push the pending changes to advance "
                    + "past it before peeking pending change-data-capture.");

            // change.BeforeRecord is the change journal's own stored buffer (the same array
            // instance is returned by every ReadBatch call until the entry is acknowledged), so
            // it must be cloned before handing it to a caller: mutating the returned array must
            // never corrupt the journal's still-durable state or a later delete replay that
            // decodes the very same buffer.
            before = (byte[])beforeRecord.Clone();
        }
        else
        {
            // An earlier touch to the same key has since been overwritten locally by a later
            // pending change: the live row no longer reflects this entry's own result, so its
            // "after" image cannot be safely reconstructed. Rather than fabricate stale data,
            // this row degrades to id-only image availability (before/after both null), which
            // matches the real CDC contract's "id" capture mode for that row.
            var isFinalTouch = lastSequenceByKey.TryGetValue((change.Table, change.RowId), out var lastSequence)
                && lastSequence == change.Sequence;
            if (isFinalTouch)
            {
                // includeGeneratedColumns: true so this "after" image includes VIRTUAL/STORED
                // generated columns in table-declaration order, matching the real turso_cdc
                // row's full in-memory row image instead of pragma_table_info's subset.
                var values = ManagedReplicaLogicalReplayer.TryCaptureCurrentRowValues(
                    connection,
                    change.Table,
                    change.RowId,
                    includeGeneratedColumns: true);
                if (values is not null)
                {
                    // SqliteRecordCodec.Encode always allocates a fresh, exactly-sized array
                    // (never a cached/pooled buffer), so - unlike the before-image above - no
                    // additional clone is needed here for caller-mutation isolation.
                    after = SqliteRecordCodec.Encode(values);
                }
            }
        }

        return new AhtolaReplicaChangeRow(
            ChangeId: change.Sequence,
            ChangeTransactionId: change.Sequence,
            ChangeType: changeType,
            TableName: change.Table,
            RowId: change.RowId,
            Before: before,
            After: after);
    }
}
