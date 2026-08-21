namespace Ahtola;

/// <summary>
/// The kind of row-level operation a projected replica change-data-capture row represents,
/// numbered to match the <c>change_type</c> column of Ahtola's real <c>turso_cdc</c> table
/// (Delete = -1, Update = 0, Insert = 1). The real table also uses <c>2</c> as an
/// autocommit/explicit-transaction-boundary marker row with no table/id/image data; that marker
/// has no equivalent here because the private replica journal does not persist transaction
/// boundaries (see <see cref="AhtolaReplicaChangeRow.ChangeTransactionId"/>).
/// </summary>
public enum AhtolaReplicaChangeType
{
    /// <summary>The row was deleted.</summary>
    Delete = -1,

    /// <summary>The row was updated.</summary>
    Update = 0,

    /// <summary>The row was inserted.</summary>
    Insert = 1,
}

/// <summary>
/// A single row of Ahtola's public change-data-capture row contract (the same shape as a row of
/// the real <c>turso_cdc</c> table), projected read-only from a managed embedded replica
/// connection's still-pending local change journal. See
/// <see cref="AhtolaConnection.PeekPendingChangeCapture"/>.
/// </summary>
/// <param name="ChangeId">
/// The change's position in the pending journal, matching the <c>change_id</c> column. Strictly
/// increasing and gap-free across one <see cref="AhtolaReplicaChangeCaptureBatch"/>, and stable
/// across repeated peeks as long as nothing has been pushed/acknowledged.
/// </param>
/// <param name="ChangeTransactionId">
/// Matches the real <c>change_txn_id</c> column's contract for a single-row transaction (a
/// transaction's rows share the id of its first change). The private replica journal does not
/// persist which local SQL transaction a committed row belonged to once reloaded from disk, so
/// this bridge cannot recover multi-row grouping: every row reports its own <see cref="ChangeId"/>
/// here, i.e. it is always represented as if it were the sole row of its own transaction. Do not
/// use this field to detect that two rows were part of the same original local transaction.
/// </param>
/// <param name="ChangeType">The row operation, matching the <c>change_type</c> column.</param>
/// <param name="TableName">The affected table, matching the <c>table_name</c> column.</param>
/// <param name="RowId">The affected row's rowid, matching the <c>id</c> column.</param>
/// <param name="Before">
/// The row's pre-image as an encoded SQLite record, matching the <c>before</c> column, or
/// <see langword="null"/> when not applicable (insert, or update — see remarks). Always
/// non-null for delete: a delete whose pre-image was not captured (only possible for a change
/// journal written by a pre-v4 journal format) makes the whole peek fail closed with
/// <see cref="AhtolaReplicaChangeCaptureException"/> instead of returning a misleadingly empty row.
/// This is always an independent copy of the change journal's own stored buffer, never an alias
/// to it: mutating the returned array cannot corrupt the journal's still-durable state or a
/// later delete replay that decodes the same underlying entry.
/// </param>
/// <param name="After">
/// The row's post-image as an encoded SQLite record, matching the <c>after</c> column, or
/// <see langword="null"/> when not applicable (delete) or not safely reconstructable. An
/// insert/update's "after" image is reconstructed by reading the row's current live state, which
/// is only correct when no later pending change in the same batch touches the same
/// (<see cref="TableName"/>, <see cref="RowId"/>) key again; when a later change supersedes it,
/// this is left <see langword="null"/> (the row degrades to id-only image availability) rather
/// than returning stale or fabricated data. Includes every column declared on the table in
/// declaration order — including VIRTUAL and STORED generated columns — matching the real
/// <c>turso_cdc</c> row's full in-memory row image, not the narrower subset a schema
/// introspection limited to storable columns would report.
/// </param>
/// <remarks>
/// <c>updates</c> (the real CDC table's packed all-columns-changed column, only ever populated in
/// "full" capture mode) has no equivalent here: it requires both the before- and after-images of
/// an update simultaneously, and the private replica journal never captures an update's
/// pre-image. There is intentionally no property for it.
/// </remarks>
public sealed record AhtolaReplicaChangeRow(
    long ChangeId,
    long ChangeTransactionId,
    AhtolaReplicaChangeType ChangeType,
    string TableName,
    long RowId,
    byte[]? Before,
    byte[]? After);

/// <summary>
/// A read-only snapshot of a managed embedded replica connection's currently pending (not yet
/// pushed) local changes, projected into Ahtola's public change-data-capture row contract. See
/// <see cref="AhtolaConnection.PeekPendingChangeCapture"/>.
/// </summary>
/// <param name="FirstChangeId">The first pending row's <see cref="AhtolaReplicaChangeRow.ChangeId"/>, or the next unassigned change id when there are no pending rows.</param>
/// <param name="AcknowledgementWatermark">
/// The exclusive change-id boundary a genuine push may later acknowledge, identical to the
/// underlying journal's push watermark. Peeking this batch never advances it and has no other
/// side effect: it is a pure read of already-durable state, so acknowledging a later push is
/// unaffected by, and cannot be corrupted by, having peeked it first.
/// </param>
/// <param name="Rows">The pending rows, in strictly increasing <see cref="AhtolaReplicaChangeRow.ChangeId"/> order.</param>
public sealed record AhtolaReplicaChangeCaptureBatch(
    long FirstChangeId,
    long AcknowledgementWatermark,
    IReadOnlyList<AhtolaReplicaChangeRow> Rows);

/// <summary>
/// Indicates that a still-pending managed embedded replica local change cannot be safely
/// represented as a row in Ahtola's public change-data-capture contract, or that the whole peek
/// cannot be safely performed right now (a local transaction is open). Peeking pending
/// change-data-capture fails closed rather than silently omitting, approximating, or projecting
/// not-yet-committed state; see <see cref="AhtolaConnection.PeekPendingChangeCapture"/>.
/// </summary>
public sealed class AhtolaReplicaChangeCaptureException(string message) : AhtolaException(message);
