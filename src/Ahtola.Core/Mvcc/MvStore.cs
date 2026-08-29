using Ahtola.Core.Storage;

namespace Ahtola.Core.Mvcc;

/// <summary>Opaque MVCC transaction identifier (Turso <c>TxID</c>).</summary>
internal readonly record struct MvccTxId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Row identity within an MVCC table (Turso <c>RowID</c>).</summary>
internal readonly struct MvccRowId : IEquatable<MvccRowId>
{
    internal MvccRowId(long tableId, long rowId)
        : this(tableId, MvccKey.FromInteger(rowId))
    {
    }

    internal MvccRowId(long tableId, MvccKey key)
    {
        TableId = tableId;
        Key = key;
    }

    internal long TableId { get; }

    internal MvccKey Key { get; }

    /// <summary>
    /// Compatibility projection for the rowid-table path. Composite and typed
    /// keys must use <see cref="Key"/> instead.
    /// </summary>
    internal long RowId => Key.Integer;

    public bool Equals(MvccRowId other) => TableId == other.TableId && Key.Equals(other.Key);

    public override bool Equals(object? obj) => obj is MvccRowId other && Equals(other);

    public static bool operator ==(MvccRowId left, MvccRowId right) => left.Equals(right);

    public static bool operator !=(MvccRowId left, MvccRowId right) => !left.Equals(right);

    public override int GetHashCode() => HashCode.Combine(TableId, Key);

    public override string ToString() => $"{TableId}:{Key}";
}

/// <summary>Lifecycle state of an MVCC transaction (Turso <c>TransactionState</c>).</summary>
internal enum MvccTransactionState : byte
{
    Active = 0,
    Preparing = 1,
    Committed = 2,
    Aborted = 3,
}

/// <summary>
/// Versioned logical identity for a table binding. Turso reserves -1 for
/// <c>sqlite_schema</c> and allocates negative ids for unmaterialized objects.
/// </summary>
internal readonly record struct MvccSchemaObjectIdentity(
    ulong SchemaGeneration,
    string Name);

/// <summary>
/// One in-flight MVCC transaction: begin timestamp, write set, and lifecycle.
/// </summary>
internal sealed class MvccTransaction
{
    private readonly object _gate = new();
    private readonly HashSet<MvccRowId> _writeSet = [];
    private readonly List<MvccLogOp> _logOps = [];
    private readonly List<MvccSavepointMark> _savepoints = [];
    private MvccTransactionState _state = MvccTransactionState.Active;
    private ulong? _commitTimestamp;
    private bool _schemaChange;
    private ulong? _pendingSchemaGeneration;

    internal MvccTransaction(
        MvccTxId id,
        ulong beginTimestamp,
        ulong beginCommitGeneration,
        ulong beginSchemaGeneration)
    {
        Id = id;
        BeginTimestamp = beginTimestamp;
        BeginCommitGeneration = beginCommitGeneration;
        BeginSchemaGeneration = beginSchemaGeneration;
    }

    internal MvccTxId Id { get; }

    internal ulong BeginTimestamp { get; }

    internal ulong BeginCommitGeneration { get; }

    internal ulong BeginSchemaGeneration { get; }

    internal ulong EffectiveSchemaGeneration
    {
        get { lock (_gate) return _pendingSchemaGeneration ?? BeginSchemaGeneration; }
    }

    internal MvccTransactionState State
    {
        get { lock (_gate) return _state; }
    }

    internal ulong? CommitTimestamp
    {
        get { lock (_gate) return _commitTimestamp; }
    }

    internal bool HasSchemaChange
    {
        get { lock (_gate) return _schemaChange; }
    }

    internal void MarkSchemaChange(ulong pendingSchemaGeneration)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _schemaChange = true;
            _pendingSchemaGeneration = pendingSchemaGeneration;
        }
    }

    internal void CancelSchemaChange()
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _schemaChange = false;
            _pendingSchemaGeneration = null;
        }
    }

    internal void RecordWrite(MvccRowId rowId)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _writeSet.Add(rowId);
        }
    }

    internal void RecordLogOp(MvccLogOp op)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _writeSet.Add(op.RowId);
            _logOps.Add(op);
        }
    }

    internal IReadOnlyCollection<MvccRowId> SnapshotWriteSet()
    {
        lock (_gate)
            return _writeSet.ToArray();
    }

    internal IReadOnlyList<MvccLogOp> SnapshotLogOps()
    {
        lock (_gate)
            return _logOps.ToArray();
    }

    /// <summary>
    /// Records a named savepoint watermark (log-op count) for later ROLLBACK TO.
    /// </summary>
    internal void BeginNamedSavepoint(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            ThrowIfNotActive();
            _savepoints.Add(new MvccSavepointMark(name, _logOps.Count));
        }
    }

    /// <summary>
    /// Drops the named savepoint and every savepoint created after it (RELEASE).
    /// </summary>
    internal void ReleaseNamedSavepoint(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            ThrowIfNotActive();
            var index = FindSavepointIndexLocked(name);
            _savepoints.RemoveRange(index, _savepoints.Count - index);
        }
    }

    /// <summary>
    /// Returns the log-op watermark for ROLLBACK TO <paramref name="name"/> and
    /// drops every savepoint created after it (named mark is retained).
    /// </summary>
    internal int RollbackToNamedSavepoint(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            ThrowIfNotActive();
            var index = FindSavepointIndexLocked(name);
            var mark = _savepoints[index].LogOpCount;
            if (index + 1 < _savepoints.Count)
                _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);
            return mark;
        }
    }

    /// <summary>
    /// Truncates logical ops after a ROLLBACK TO watermark and rebuilds the write set.
    /// </summary>
    internal void TruncateLogOpsTo(int logOpCount)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            if (logOpCount < 0 || logOpCount > _logOps.Count)
                throw new InvalidOperationException("Invalid MVCC savepoint log watermark.");
            if (logOpCount == _logOps.Count)
                return;

            _logOps.RemoveRange(logOpCount, _logOps.Count - logOpCount);
            _writeSet.Clear();
            foreach (var op in _logOps)
                _writeSet.Add(op.RowId);
        }
    }

    private int FindSavepointIndexLocked(string name)
    {
        for (var index = _savepoints.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_savepoints[index].Name, name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        throw new EmbeddedSqlException($"no such savepoint: {name}");
    }

    private readonly record struct MvccSavepointMark(string Name, int LogOpCount);

    internal void MarkPreparing(ulong commitTimestamp)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _state = MvccTransactionState.Preparing;
            _commitTimestamp = commitTimestamp;
        }
    }

    internal void MarkCommitted()
    {
        lock (_gate)
        {
            if (_state != MvccTransactionState.Preparing)
                throw new InvalidOperationException("MVCC transaction is not preparing.");
            _state = MvccTransactionState.Committed;
        }
    }

    internal void MarkAborted()
    {
        lock (_gate)
        {
            if (_state is MvccTransactionState.Committed)
                throw new InvalidOperationException("Cannot abort a committed MVCC transaction.");
            _state = MvccTransactionState.Aborted;
        }
    }

    private void ThrowIfNotActive()
    {
        if (_state != MvccTransactionState.Active)
            throw new InvalidOperationException($"MVCC transaction is {_state}.");
    }
}

/// <summary>
/// Per-database MVCC store (Turso <c>MvStore</c>): logical clock, version chains,
/// concurrent transactions, and first-committer-wins write-write conflicts.
/// </summary>
internal sealed class MvStore
{
    internal const long SqliteSchemaTableId = -1;

    private readonly ILogicalClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, MvccTransaction> _transactions = [];
    private readonly Dictionary<ulong, MvccTransactionState> _finalizedStates = [];
    private readonly Dictionary<ulong, ulong> _finalizedCommitTimestamps = [];
    private readonly Dictionary<MvccRowId, List<MvccRowVersion>> _rows = [];
    private readonly Dictionary<long, OrderedTableKeys> _orderedTableKeys = [];
    private readonly Dictionary<MvccSchemaObjectIdentity, long> _tableIds =
        new(MvccSchemaObjectIdentityComparer.Instance);
    private readonly Dictionary<long, string> _tableNames = [];
    private readonly Dictionary<long, ulong> _tableSchemaGenerations = [];
    private readonly Dictionary<long, long> _nextRowIds = [];
    private long _nextTableId = -2;
    private ulong _nextTxId = 1;
    private ulong _nextVersionId = 1;
    private ulong? _exclusiveTxId;
    private ulong _schemaGeneration;
    private ulong _commitGeneration;
    private ulong _lastCommittedTimestamp;
    private ulong _checkpointGeneration;
    private ulong? _schemaChangeTransaction;
    private bool _checkpointInProgress;
    private bool _hasUnresolvedLegacyRows;
    private bool _hasIndeterminateCommit;
    private MvccLogicalLog? _logicalLog;

    internal MvStore(
        ILogicalClock? clock = null,
        MvccLogicalLog? logicalLog = null,
        ulong schemaGeneration = 0)
    {
        _clock = clock ?? new MvccClock();
        _logicalLog = logicalLog;
        _schemaGeneration = schemaGeneration;
    }

    internal ILogicalClock Clock => _clock;

    internal MvccLogicalLog? LogicalLog => _logicalLog;

    /// <summary>
    /// A V3 frame does not carry the object name needed to bind its negative table
    /// id after a cold reopen. Such a log cannot be materialized safely, so the
    /// V3-to-V4 checkpoint upgrade must fail rather than discard its rows.
    /// </summary>
    internal bool CanUpgradeLegacyLog
    {
        get { lock (_gate) return !_hasUnresolvedLegacyRows; }
    }

    /// <summary>
    /// Monotonic in-process schema generation. Connections use it to fail a
    /// concurrent DDL attempt rather than applying a catalog mutation against an
    /// older MVCC snapshot.
    /// </summary>
    internal ulong SchemaGeneration
    {
        get { lock (_gate) return _schemaGeneration; }
    }

    internal ulong CommitGeneration
    {
        get { lock (_gate) return _commitGeneration; }
    }

    internal ulong LastCommittedTimestamp
    {
        get { lock (_gate) return _lastCommittedTimestamp; }
    }

    /// <summary>Attach durable log after construction (file-backed enable path).</summary>
    internal void AttachLogicalLog(MvccLogicalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (_gate)
            _logicalLog = log;
    }

    /// <summary>Replay a recovered commit frame into the version store.</summary>
    internal void ApplyRecoveredCommit(ulong commitTs, IReadOnlyList<MvccLogOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        lock (_gate)
        {
            foreach (var op in ops)
            {
                if (op.ObjectName is { } objectName)
                {
                    RegisterRecoveredTableId(op.RowId.TableId, objectName);
                }
                else if (!_tableNames.ContainsKey(op.RowId.TableId))
                {
                    _hasUnresolvedLegacyRows = true;
                }
                if (!_rows.TryGetValue(op.RowId, out var chain))
                {
                    chain = [];
                    _rows[op.RowId] = chain;
                    IndexRowLocked(op.RowId);
                }

                if (op.IsDelete)
                {
                    if (op.IsBaseTombstone && chain.Count == 0)
                    {
                        chain.Add(new MvccRowVersion(
                            _nextVersionId++,
                            begin: MvccStamp.FromTimestamp(commitTs),
                            end: null,
                            cells: [],
                            isTombstone: true));
                        continue;
                    }

                    // End the latest live version at commitTs.
                    for (var i = chain.Count - 1; i >= 0; i--)
                    {
                        if (chain[i].End is null)
                        {
                            chain[i].End = MvccStamp.FromTimestamp(commitTs);
                            break;
                        }
                    }
                }
                else
                {
                    chain.Add(new MvccRowVersion(
                        _nextVersionId++,
                        begin: MvccStamp.FromTimestamp(commitTs),
                        end: null,
                        cells: (SqlValue[])(op.Cells ?? []).Clone()));
                }
            }

            // Advance clock past recovered commits so new txs get higher timestamps.
            _clock.Reset(commitTs + 1);
            _lastCommittedTimestamp = Math.Max(_lastCommittedTimestamp, commitTs);
        }
    }

    /// <summary>
    /// Stable negative table id for <paramref name="tableName"/> in the currently
    /// published schema generation.
    /// </summary>
    internal long GetOrCreateTableId(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        lock (_gate)
            return GetOrCreateTableIdLocked(tableName, _schemaGeneration);
    }

    /// <summary>
    /// Resolves a table identity against the transaction's pinned (or provisional
    /// DDL) schema generation.
    /// </summary>
    internal long GetOrCreateTableId(MvccTxId txId, string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        lock (_gate)
        {
            var tx = RequireActive(txId);
            return GetOrCreateTableIdLocked(tableName, tx.EffectiveSchemaGeneration);
        }
    }

    internal bool TryGetTableName(long tableId, out string? name)
    {
        lock (_gate)
            return _tableNames.TryGetValue(tableId, out name);
    }

    private void RegisterRecoveredTableId(long tableId, string tableName)
    {
        if (_tableNames.TryGetValue(tableId, out var existingName)
            && !string.Equals(existingName, tableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"MVCC logical log maps table id {tableId} to both '{existingName}' and '{tableName}'.");
        }
        var identity = new MvccSchemaObjectIdentity(_schemaGeneration, tableName);
        if (_tableIds.TryGetValue(identity, out var existingId) && existingId != tableId)
        {
            throw new InvalidDataException(
                $"MVCC logical log maps table '{tableName}' to both {existingId} and {tableId}.");
        }

        _tableIds[identity] = tableId;
        _tableNames[tableId] = tableName;
        _tableSchemaGenerations[tableId] = _schemaGeneration;
        _nextTableId = Math.Min(_nextTableId, checked(tableId - 1));
    }

    private long GetOrCreateTableIdLocked(string tableName, ulong schemaGeneration)
    {
        var identity = new MvccSchemaObjectIdentity(schemaGeneration, tableName);
        if (_tableIds.TryGetValue(identity, out var id))
            return id;

        id = string.Equals(tableName, "sqlite_schema", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tableName, "sqlite_master", StringComparison.OrdinalIgnoreCase)
                ? SqliteSchemaTableId
                : _nextTableId--;
        _tableIds[identity] = id;
        _tableNames[id] = tableName;
        _tableSchemaGenerations[id] = schemaGeneration;
        return id;
    }

    private MvccLogOp CreateLogUpsert(MvccRowId rowId, SqlValue[] cells)
        => MvccLogOp.Upsert(
            rowId,
            cells,
            _tableNames.TryGetValue(rowId.TableId, out var objectName) ? objectName : null);

    private MvccLogOp CreateLogDelete(MvccRowId rowId)
        => MvccLogOp.Delete(
            rowId,
            _tableNames.TryGetValue(rowId.TableId, out var objectName) ? objectName : null);

    private MvccLogOp CreateLogBaseTombstone(MvccRowId rowId)
        => MvccLogOp.BaseTombstone(
            rowId,
            _tableNames.TryGetValue(rowId.TableId, out var objectName) ? objectName : null);

    /// <summary>
    /// Process-wide unique rowid allocator for concurrent writers that each hold
    /// a private classic catalog snapshot (pooled connections).
    /// </summary>
    internal long AllocateRowId(long tableId, long minimumExclusive = 0)
    {
        lock (_gate)
        {
            if (!_nextRowIds.TryGetValue(tableId, out var next))
            {
                next = 1;
                foreach (var rowId in _rows.Keys)
                {
                    if (rowId.TableId == tableId && rowId.RowId >= next)
                        next = rowId.RowId + 1;
                }
            }

            if (minimumExclusive >= next)
                next = minimumExclusive + 1;
            if (next <= 0)
                next = 1;

            var allocated = next;
            _nextRowIds[tableId] = allocated + 1;
            return allocated;
        }
    }

    internal void ObserveRowId(long tableId, long rowId)
    {
        if (rowId <= 0)
            return;
        lock (_gate)
        {
            if (!_nextRowIds.TryGetValue(tableId, out var next) || rowId >= next)
                _nextRowIds[tableId] = rowId + 1;
        }
    }

    internal MvccTransaction BeginTransaction(ulong? expectedSchemaGeneration = null)
    {
        lock (_gate)
        {
            if (_hasIndeterminateCommit)
            {
                throw new EmbeddedSqlException(
                    "The MVCC store has an indeterminate logical-log commit; dispose and reopen the database before starting another transaction.");
            }
            if (_checkpointInProgress
                || _exclusiveTxId is not null
                || _schemaChangeTransaction is not null)
                throw new EmbeddedBusyException();
            if (expectedSchemaGeneration is { } expected
                && expected != _schemaGeneration)
            {
                throw new EmbeddedCatalogSnapshotStaleException();
            }

            var beginTs = NextBeginTimestamp();
            var id = new MvccTxId(_nextTxId++);
            var tx = new MvccTransaction(
                id,
                beginTs,
                _commitGeneration,
                _schemaGeneration);
            _transactions.Add(id.Value, tx);
            return tx;
        }
    }

    internal MvccTransaction BeginExclusiveTransaction(MvccTxId? existing = null)
    {
        lock (_gate)
        {
            if (_hasIndeterminateCommit)
            {
                throw new EmbeddedSqlException(
                    "The MVCC store has an indeterminate logical-log commit; dispose and reopen the database before starting another transaction.");
            }
            if (_checkpointInProgress
                || (_exclusiveTxId is { } held
                    && (existing is null || held != existing.Value.Value)))
            {
                throw new EmbeddedBusyException();
            }

            if (existing is { } existingId
                && _transactions.TryGetValue(existingId.Value, out var existingTx))
            {
                _exclusiveTxId = existingId.Value;
                return existingTx;
            }

            var beginTs = NextBeginTimestamp();
            var id = new MvccTxId(_nextTxId++);
            var tx = new MvccTransaction(
                id,
                beginTs,
                _commitGeneration,
                _schemaGeneration);
            _transactions.Add(id.Value, tx);
            _exclusiveTxId = id.Value;
            return tx;
        }
    }

    internal bool TryGetTransaction(MvccTxId id, out MvccTransaction? transaction)
    {
        lock (_gate)
            return _transactions.TryGetValue(id.Value, out transaction);
    }

    internal bool HasPendingWrites(MvccTxId id)
    {
        lock (_gate)
            return RequireActive(id).SnapshotWriteSet().Count != 0;
    }

    /// <summary>
    /// Reserves the schema publication slot for an active concurrent transaction.
    /// DDL deliberately fails busy when another reader or writer has already
    /// pinned an MVCC snapshot; publishing a schema against that snapshot would
    /// otherwise leave compiled cursors with stale object bindings.
    /// </summary>
    internal void BeginSchemaChange(MvccTxId id)
    {
        lock (_gate)
        {
            var tx = RequireActive(id);
            if (tx.HasSchemaChange && _schemaChangeTransaction == id.Value)
                return;
            if (_checkpointInProgress)
                throw new EmbeddedBusyException();
            if (tx.BeginCommitGeneration != _commitGeneration)
                throw new EmbeddedBusyException();
            if (_schemaChangeTransaction is { } owner && owner != id.Value)
                throw new EmbeddedBusyException();

            foreach (var candidate in _transactions.Values)
            {
                if (candidate.Id != id
                    && candidate.State is MvccTransactionState.Active or MvccTransactionState.Preparing)
                {
                    throw new EmbeddedBusyException();
                }
            }

            if (_schemaGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The MVCC schema generation is exhausted.");
            tx.MarkSchemaChange(_schemaGeneration + 1);
            _schemaChangeTransaction = id.Value;
        }
    }

    internal bool HasSchemaChange(MvccTxId id)
    {
        lock (_gate)
        {
            return _transactions.TryGetValue(id.Value, out var tx)
                ? tx.HasSchemaChange
                : _schemaChangeTransaction == id.Value;
        }
    }

    internal void CancelSchemaChange(MvccTxId id)
    {
        lock (_gate)
        {
            if (_schemaChangeTransaction == id.Value)
            {
                _schemaChangeTransaction = null;
                if (_transactions.TryGetValue(id.Value, out var tx)
                    && tx.State == MvccTransactionState.Active)
                {
                    tx.CancelSchemaChange();
                    RemoveUnpublishedSchemaIdentitiesLocked(tx.BeginSchemaGeneration);
                }
            }
        }
    }

    /// <summary>
    /// Validates and timestamps a schema-changing transaction without appending
    /// its row operations to the logical log. Its complete private catalog is
    /// persisted through the pager before <see cref="CompleteSchemaCommit"/>.
    /// </summary>
    internal void PrepareSchemaCommit(MvccTxId id)
    {
        MvccTransaction tx;
        HashSet<MvccRowId> writes;
        lock (_gate)
        {
            if (_checkpointInProgress)
                throw new EmbeddedBusyException();
            tx = RequireActive(id);
            if (!tx.HasSchemaChange || _schemaChangeTransaction != id.Value)
            {
                throw new InvalidOperationException(
                    "The MVCC transaction does not own a pending schema publication.");
            }

            writes = tx.SnapshotWriteSet().ToHashSet();
            ValidateCommitLocked(tx, writes);
            if (writes.Count != 0 && _commitGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The MVCC commit generation is exhausted.");
        }

        _clock.GetTimestamp(ts => tx.MarkPreparing(ts));

        lock (_gate)
        {
            ValidatePreparingCommitLocked(tx, writes);
        }
    }

    /// <summary>
    /// Publishes a prepared schema generation after the catalog and page-one
    /// schema cookie are durable. No I/O occurs here, so the post-pager publish
    /// window cannot expose a discarded catalog.
    /// </summary>
    internal void CompleteSchemaCommit(MvccTxId id)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(id.Value, out var tx)
                || tx.State != MvccTransactionState.Preparing
                || !tx.HasSchemaChange
                || _schemaChangeTransaction != id.Value)
            {
                throw new InvalidOperationException(
                    "The MVCC schema transaction is not prepared for publication.");
            }

            var commitTs = tx.CommitTimestamp
                ?? throw new InvalidOperationException(
                    "Preparing schema transaction is missing its commit timestamp.");
            var writes = tx.SnapshotWriteSet();
            tx.MarkCommitted();
            _finalizedStates[id.Value] = MvccTransactionState.Committed;
            _finalizedCommitTimestamps[id.Value] = commitTs;
            _lastCommittedTimestamp = Math.Max(_lastCommittedTimestamp, commitTs);
            ClearExclusive(id);
            _transactions.Remove(id.Value);
            if (writes.Count != 0)
                _commitGeneration++;

            _schemaGeneration = tx.EffectiveSchemaGeneration;
            _schemaChangeTransaction = null;

            // The just-published catalog is a complete page-native image of this
            // transaction, including its DML. The pre-DDL checkpoint retired every
            // older logical frame, so no version or negative object identity remains.
            ClearMaterializedStateLocked();
        }
    }
    /// <summary>Insert a new live version created by <paramref name="txId"/>.</summary>
    internal void Insert(MvccTxId txId, MvccRowId rowId, SqlValue[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        lock (_gate)
        {
            var tx = RequireActive(txId);
            ThrowIfCheckpointBlocksWriteLocked();
            var version = new MvccRowVersion(
                _nextVersionId++,
                begin: MvccStamp.FromTxId(txId),
                end: null,
                cells: (SqlValue[])cells.Clone());
            if (!_rows.TryGetValue(rowId, out var chain))
            {
                chain = [];
                _rows[rowId] = chain;
                IndexRowLocked(rowId);
            }
            else if (!rowId.Key.IsInteger)
            {
                ThrowIfTypedKeyInsertConflict(tx, chain);
            }

            chain.Add(version);
            tx.RecordLogOp(CreateLogUpsert(rowId, version.Cells));
        }
    }

    /// <summary>
    /// Delete the version visible to <paramref name="txId"/> by setting its end
    /// stamp. Returns false when no visible version exists.
    /// </summary>
    internal bool Delete(MvccTxId txId, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            ThrowIfCheckpointBlocksWriteLocked();
            if (!_rows.TryGetValue(rowId, out var chain))
                return false;

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (!IsVisibleTo(version, tx))
                    continue;
                if (IsWriteWriteConflict(tx, version))
                    throw new EmbeddedWriteWriteConflictException();

                version.End = MvccStamp.FromTxId(txId);
                tx.RecordLogOp(CreateLogDelete(rowId));
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Delete a store-visible version when present; otherwise plant a tombstone that
    /// invalidates a classic base-table row for this concurrent transaction (Turso
    /// dual-cursor delete of btree-only rows).
    /// </summary>
    internal void DeleteOrTombstoneBase(MvccTxId txId, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            ThrowIfCheckpointBlocksWriteLocked();
            if (_rows.TryGetValue(rowId, out var chain))
            {
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var version = chain[i];
                    if (!IsVisibleTo(version, tx))
                        continue;
                    if (IsWriteWriteConflict(tx, version))
                        throw new EmbeddedWriteWriteConflictException();

                    // Already a pure base tombstone from this tx — idempotent.
                    if (version.IsTombstone && version.End is null
                        && version.Begin is { IsTimestamp: false, Value: var beginTx }
                        && beginTx == txId.Value)
                    {
                        return;
                    }

                    version.End = MvccStamp.FromTxId(txId);
                    tx.RecordLogOp(CreateLogDelete(rowId));
                    return;
                }

                ThrowIfConcurrentWriterOnRow(tx, chain);
            }
            else
            {
                chain = [];
                _rows[rowId] = chain;
                IndexRowLocked(rowId);
            }

            chain.Add(new MvccRowVersion(
                _nextVersionId++,
                begin: MvccStamp.FromTxId(txId),
                end: null,
                cells: [],
                isTombstone: true));
            tx.RecordLogOp(CreateLogBaseTombstone(rowId));
        }
    }

    /// <summary>Delete-then-insert update (Turso <c>update</c>).</summary>
    internal bool Update(MvccTxId txId, MvccRowId rowId, SqlValue[] cells)
    {
        if (!Delete(txId, rowId))
            return false;
        Insert(txId, rowId, cells);
        return true;
    }

    /// <summary>
    /// Update including classic base-only rows: tombstone/end the prior image, then
    /// insert the new cells under <paramref name="txId"/>.
    /// </summary>
    internal void UpdateIncludingBase(MvccTxId txId, MvccRowId rowId, SqlValue[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        DeleteOrTombstoneBase(txId, rowId);
        Insert(txId, rowId, cells);
    }

    /// <summary>Read the version visible to <paramref name="txId"/>, if any.</summary>
    internal bool TryRead(MvccTxId txId, MvccRowId rowId, out SqlValue[]? cells)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            cells = null;
            if (!_rows.TryGetValue(rowId, out var chain))
                return false;

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (!IsVisibleTo(version, tx))
                    continue;
                if (version.IsTombstone)
                    return false;
                cells = (SqlValue[])version.Cells.Clone();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Resolves whether the version store has a visible upsert or delete that
    /// shadows the physical/base row at <paramref name="rowId"/>.
    /// </summary>
    internal bool TryReadVisibleEffect(
        MvccTxId txId,
        MvccRowId rowId,
        out SqlValue[]? cells,
        out bool isDelete)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            if (_rows.TryGetValue(rowId, out var chain)
                && TryResolveVisibleEffectLocked(tx, chain, out cells, out isDelete))
            {
                return true;
            }

            cells = null;
            isDelete = false;
            return false;
        }
    }

    /// <summary>
    /// True when the version store says a classic base-table row must be hidden
    /// from <paramref name="txId"/> (deleted or superseded for this snapshot).
    /// Turso dual-cursor "btree invalidating" simplified.
    /// </summary>
    internal bool IsBaseRowInvalidated(MvccTxId txId, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            if (!_rows.TryGetValue(rowId, out var chain) || chain.Count == 0)
                return false;

            // A live visible store version always overrides the base image.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (IsVisibleTo(chain[i], tx))
                    return true;
            }

            // Deletion visible to this reader (end stamp at/before begin) invalidates base.
            foreach (var version in chain)
            {
                if (version.End is null)
                    continue;
                var end = version.End.Value;
                if (end.IsTimestamp)
                {
                    if (end.Value <= tx.BeginTimestamp)
                        return true;
                }
                else if (end.Value == tx.Id.Value
                    || LookupCreatorVisibility(end.Value, tx))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Scan every row id that has at least one version visible to
    /// <paramref name="txId"/> (newest visible non-tombstone wins).
    /// </summary>
    internal IReadOnlyList<(MvccRowId RowId, SqlValue[] Cells)> ScanVisible(MvccTxId txId)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            var results = new List<(MvccRowId, SqlValue[])>();
            foreach (var (rowId, chain) in _rows)
            {
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var version = chain[i];
                    if (!IsVisibleTo(version, tx))
                        continue;
                    if (!version.IsTombstone)
                        results.Add((rowId, (SqlValue[])version.Cells.Clone()));
                    break;
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Enumerates this table's visible MVCC effects in SQLite key order. Each
    /// move locks only long enough to locate and clone the next entry, so the
    /// caller retains no store lock and buffers no table-sized snapshot.
    /// </summary>
    internal IEnumerable<MvccVisibleRow> EnumerateVisible(
        MvccTxId txId,
        long tableId,
        IComparer<MvccKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        RegisterTableKeyComparer(tableId, comparer);

        var hasPrevious = false;
        var previous = default(MvccKey);
        while (TryGetNextVisible(txId, tableId, comparer, hasPrevious, previous, out var row))
        {
            yield return row;
            previous = row.Key;
            hasPrevious = true;
        }
    }

    /// <summary>
    /// Snapshot of every currently live committed version (end is null, begin is a
    /// timestamp). Used after a concurrent tx commits to merge into the classic catalog.
    /// </summary>
    internal IReadOnlyList<(MvccRowId RowId, SqlValue[] Cells)> SnapshotLiveCommittedRows()
    {
        lock (_gate)
            return SnapshotLiveCommittedRowsLocked();
    }

    /// <summary>
    /// Row ids whose latest committed state is deleted (ended version, or a live
    /// committed pure tombstone that marks a base-row delete) with no later live
    /// non-tombstone version.
    /// </summary>
    internal IReadOnlyCollection<MvccRowId> SnapshotCommittedDeletes()
    {
        lock (_gate)
            return SnapshotCommittedDeletesLocked();
    }

    private IReadOnlyList<(MvccRowId RowId, SqlValue[] Cells)> SnapshotLiveCommittedRowsLocked()
    {
        var results = new List<(MvccRowId, SqlValue[])>();
        foreach (var (rowId, chain) in _rows)
        {
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (version.End is not null || version.IsTombstone)
                    continue;
                if (version.Begin is not { IsTimestamp: true })
                    continue;
                results.Add((rowId, (SqlValue[])version.Cells.Clone()));
                break;
            }
        }

        return results;
    }

    private IReadOnlyCollection<MvccRowId> SnapshotCommittedDeletesLocked()
    {
        var deleted = new HashSet<MvccRowId>();
        foreach (var (rowId, chain) in _rows)
        {
            var live = false;
            var sawDelete = false;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (version.Begin is not { IsTimestamp: true })
                    continue;
                if (version.End is null && !version.IsTombstone)
                {
                    live = true;
                    break;
                }

                if (version.End is null && version.IsTombstone)
                    sawDelete = true;
                else if (version.End is { IsTimestamp: true })
                    sawDelete = true;
            }

            if (!live && sawDelete)
                deleted.Add(rowId);
        }

        return deleted;
    }

    /// <summary>Record a write-set entry without mutating version chains (catalog-path DML).</summary>
    internal void RecordWrite(MvccTxId id, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(id);
            ThrowIfCheckpointBlocksWriteLocked();
            tx.RecordWrite(rowId);
        }
    }

    /// <summary>
    /// Commit with first-committer-wins WW detection. Rewrites in-flight TxID
    /// stamps on version chains to the commit timestamp (Turso rewrite step).
    /// </summary>
    internal void Commit(
        MvccTxId id,
        SqliteSynchronousMode synchronousMode = SqliteSynchronousMode.Full)
    {
        synchronousMode.Validate(nameof(synchronousMode));
        MvccTransaction tx;
        HashSet<MvccRowId> writes;
        lock (_gate)
        {
            if (_checkpointInProgress)
                throw new EmbeddedBusyException();
            tx = RequireActive(id);
            if (tx.HasSchemaChange)
            {
                throw new InvalidOperationException(
                    "Schema-changing MVCC transactions must use the pager-first schema commit path.");
            }
            writes = tx.SnapshotWriteSet().ToHashSet();
            var preflightOps = tx.SnapshotLogOps();
            if (_logicalLog is { RequiresVersion4Upgrade: true }
                && preflightOps.Any(static op => !op.RowId.Key.IsInteger))
            {
                throw new MvccLogicalLogUpgradeRequiredException(
                    "MVCC typed-key writes require a checkpoint that upgrades the logical log before commit.");
            }

            ValidateCommitLocked(tx, writes);
            if (writes.Count != 0 && _commitGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The MVCC commit generation is exhausted.");
        }

        _clock.GetTimestamp(ts => tx.MarkPreparing(ts));

        lock (_gate)
        {
            ValidatePreparingCommitLocked(tx, writes);
            var commitTs = tx.CommitTimestamp
                ?? throw new InvalidOperationException("Preparing transaction missing commit timestamp.");

            var logOps = tx.SnapshotLogOps();
            MvccLogicalLogCommitIndeterminateException? indeterminateCommit = null;
            try
            {
                if (logOps.Count != 0)
                    _logicalLog?.AppendCommit(commitTs, logOps, synchronousMode);
            }
            catch (MvccLogicalLogCommitIndeterminateException exception)
            {
                indeterminateCommit = exception;
            }
            catch
            {
                AbortLocked(id, tx);
                throw;
            }

            RewriteStampsLocked(id, commitTs);
            tx.MarkCommitted();
            _finalizedStates[id.Value] = MvccTransactionState.Committed;
            _finalizedCommitTimestamps[id.Value] = commitTs;
            _lastCommittedTimestamp = Math.Max(_lastCommittedTimestamp, commitTs);
            ClearExclusive(id);
            _transactions.Remove(id.Value);
            if (writes.Count != 0)
                _commitGeneration++;
            PruneHistoryLocked(tx.BeginTimestamp);
            if (indeterminateCommit is not null)
            {
                _hasIndeterminateCommit = true;
                throw indeterminateCommit;
            }

        }
    }

    internal void Rollback(MvccTxId id)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(id.Value, out var tx))
                return;
            if (tx.State is MvccTransactionState.Committed)
                throw new InvalidOperationException("Cannot roll back a committed MVCC transaction.");
            AbortLocked(id, tx);
        }
    }

    /// <summary>Named SAVEPOINT mark on an active MVCC transaction (Turso begin_named_savepoint).</summary>
    internal void BeginNamedSavepoint(MvccTxId id, string name)
    {
        lock (_gate)
            RequireActive(id).BeginNamedSavepoint(name);
    }

    /// <summary>RELEASE a named MVCC savepoint (keeps later log ops).</summary>
    internal void ReleaseNamedSavepoint(MvccTxId id, string name)
    {
        lock (_gate)
            RequireActive(id).ReleaseNamedSavepoint(name);
    }

    /// <summary>
    /// ROLLBACK TO a named MVCC savepoint: undo version-chain effects of log ops
    /// after the mark, then truncate the transaction log (Turso rollback_to_named_savepoint).
    /// </summary>
    internal void RollbackToNamedSavepoint(MvccTxId id, string name)
    {
        lock (_gate)
        {
            var tx = RequireActive(id);
            var mark = tx.RollbackToNamedSavepoint(name);
            var logOps = tx.SnapshotLogOps();
            for (var i = logOps.Count - 1; i >= mark; i--)
                UndoLogOpLocked(id, logOps[i]);
            tx.TruncateLogOpsTo(mark);
        }
    }

    private void UndoLogOpLocked(MvccTxId id, MvccLogOp op)
    {
        if (!_rows.TryGetValue(op.RowId, out var chain))
            return;

        if (op.IsDelete)
        {
            // Undo end-stamp / pure tombstone created by this tx for the row.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (version.End is { IsTimestamp: false, Value: var endTx } && endTx == id.Value)
                {
                    version.End = null;
                    break;
                }

                if (version.IsTombstone
                    && version.End is null
                    && version.Begin is { IsTimestamp: false, Value: var beginTx }
                    && beginTx == id.Value)
                {
                    chain.RemoveAt(i);
                    break;
                }
            }
        }
        else
        {
            // Undo the newest insert version created by this tx for the row.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (version.Begin is { IsTimestamp: false, Value: var beginTx }
                    && beginTx == id.Value
                    && !version.IsTombstone)
                {
                    chain.RemoveAt(i);
                    break;
                }
            }
        }

        if (chain.Count == 0)
            RemoveRowLocked(op.RowId);
    }

    private void AbortLocked(MvccTxId id, MvccTransaction tx)
    {
        foreach (var (rowId, chain) in _rows.ToArray())
        {
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (version.Begin is { IsTimestamp: false, Value: var beginTx } && beginTx == id.Value)
                {
                    chain.RemoveAt(i);
                    continue;
                }

                if (version.End is { IsTimestamp: false, Value: var endTx } && endTx == id.Value)
                    version.End = null;
            }

            if (chain.Count == 0)
                RemoveRowLocked(rowId);
        }

        tx.MarkAborted();
        if (_schemaChangeTransaction == id.Value)
        {
            _schemaChangeTransaction = null;
            RemoveUnpublishedSchemaIdentitiesLocked(tx.BeginSchemaGeneration);
        }
        _finalizedStates[id.Value] = MvccTransactionState.Aborted;
        ClearExclusive(id);
        _transactions.Remove(id.Value);
    }

    private void RewriteStampsLocked(MvccTxId id, ulong commitTs)
    {
        var stamp = MvccStamp.FromTimestamp(commitTs);
        foreach (var chain in _rows.Values)
        {
            foreach (var version in chain)
            {
                if (version.Begin is { IsTimestamp: false, Value: var beginTx } && beginTx == id.Value)
                    version.Begin = stamp;
                if (version.End is { IsTimestamp: false, Value: var endTx } && endTx == id.Value)
                    version.End = stamp;
            }
        }
    }

    private MvccTransaction RequireActive(MvccTxId id)
    {
        if (!_transactions.TryGetValue(id.Value, out var tx))
            throw new InvalidOperationException($"Unknown MVCC transaction {id}.");
        if (tx.State != MvccTransactionState.Active)
            throw new InvalidOperationException($"MVCC transaction {id} is {tx.State}.");
        return tx;
    }

    private ulong NextBeginTimestamp()
        => _clock is MvccClock mvccClock
            ? mvccClock.GetBeginTimestamp()
            : _clock.GetTimestamp(static _ => { });

    private bool IsVisibleTo(MvccRowVersion version, MvccTransaction tx)
        => IsBeginVisible(version, tx) && IsEndVisible(version, tx);

    private bool IsBeginVisible(MvccRowVersion version, MvccTransaction tx)
    {
        if (version.Begin is null)
            return true;

        var begin = version.Begin.Value;
        if (begin.IsTimestamp)
            return begin.Value <= tx.BeginTimestamp;

        if (begin.Value == tx.Id.Value)
            return true;

        return LookupCreatorVisibility(begin.Value, tx);
    }

    private bool IsEndVisible(MvccRowVersion version, MvccTransaction tx)
    {
        // True means the version is still live for this reader (deletion not yet visible).
        if (version.End is null)
            return true;

        var end = version.End.Value;
        if (end.IsTimestamp)
            return end.Value > tx.BeginTimestamp;

        if (end.Value == tx.Id.Value)
            return false;

        return !LookupCreatorVisibility(end.Value, tx);
    }

    private bool LookupCreatorVisibility(ulong otherTxId, MvccTransaction reader)
    {
        if (_transactions.TryGetValue(otherTxId, out var other))
        {
            return other.State switch
            {
                MvccTransactionState.Committed =>
                    other.CommitTimestamp is { } cts && cts <= reader.BeginTimestamp,
                MvccTransactionState.Preparing =>
                    other.CommitTimestamp is { } pts && pts <= reader.BeginTimestamp,
                MvccTransactionState.Active => false,
                MvccTransactionState.Aborted => false,
                _ => false,
            };
        }

        if (_finalizedStates.TryGetValue(otherTxId, out var finalized))
        {
            if (finalized != MvccTransactionState.Committed)
                return false;
            return _finalizedCommitTimestamps.TryGetValue(otherTxId, out var cts)
                && cts <= reader.BeginTimestamp;
        }

        return false;
    }

    private bool IsWriteWriteConflict(MvccTransaction tx, MvccRowVersion version)
    {
        if (version.End is null)
            return false;

        var end = version.End.Value;
        if (end.IsTimestamp)
            return end.Value > tx.BeginTimestamp;

        if (end.Value == tx.Id.Value)
            return false;

        if (_transactions.TryGetValue(end.Value, out var other))
        {
            return other.State is MvccTransactionState.Active
                or MvccTransactionState.Preparing
                or MvccTransactionState.Committed;
        }

        if (_finalizedStates.TryGetValue(end.Value, out var finalized)
            && finalized == MvccTransactionState.Committed
            && _finalizedCommitTimestamps.TryGetValue(end.Value, out var cts))
        {
            return cts > tx.BeginTimestamp;
        }

        return false;
    }

    /// <summary>
    /// Concurrent pure base tombstones/inserts share End=null, so the end-stamp WW
    /// path never fires. Detect peer Active/Preparing begins (or ends) on the chain.
    /// </summary>
    private void ThrowIfConcurrentWriterOnRow(MvccTransaction tx, List<MvccRowVersion> chain)
    {
        foreach (var version in chain)
        {
            if (version.Begin is { IsTimestamp: false, Value: var beginTx }
                && beginTx != tx.Id.Value
                && IsActiveOrPreparingTx(beginTx))
            {
                throw new EmbeddedWriteWriteConflictException();
            }

            if (version.End is { IsTimestamp: false, Value: var endTx }
                && endTx != tx.Id.Value
                && IsActiveOrPreparingTx(endTx))
            {
                throw new EmbeddedWriteWriteConflictException();
            }
        }
    }

    /// <summary>
    /// A rowid allocator makes ordinary concurrent inserts distinct. A typed
    /// primary key has no such allocator: two live versions for the same key
    /// would violate the table's uniqueness contract even when the writers
    /// started from independent catalog snapshots.
    /// </summary>
    private static void ThrowIfTypedKeyInsertConflict(
        MvccTransaction tx,
        IReadOnlyList<MvccRowVersion> chain)
    {
        foreach (var version in chain)
        {
            if (version.IsTombstone || version.End is not null)
                continue;
            if (version.Begin is { IsTimestamp: false, Value: var beginTx }
                && beginTx == tx.Id.Value)
            {
                continue;
            }

            throw new EmbeddedWriteWriteConflictException();
        }
    }

    private bool IsActiveOrPreparingTx(ulong otherTxId)
        => _transactions.TryGetValue(otherTxId, out var other)
            && other.State is MvccTransactionState.Active or MvccTransactionState.Preparing;

    private void ClearExclusive(MvccTxId id)
    {
        if (_exclusiveTxId == id.Value)
            _exclusiveTxId = null;
    }

    /// <summary>True when any Active/Preparing concurrent transaction is open.</summary>
    internal bool HasActiveTransactions()
    {
        lock (_gate)
            return HasActiveTransactionsLocked();
    }

    /// <summary>
    /// Acquires the process-shared checkpoint boundary. Read-only snapshots may
    /// remain active; the lease freezes their mutation path while collection and
    /// page publication run. A schema owner may be explicitly admitted for the
    /// pre-DDL retirement checkpoint.
    /// </summary>
    internal bool TryAcquireCheckpoint(
        out CheckpointLease? lease,
        MvccTxId? permittedTransaction = null)
    {
        lock (_gate)
        {
            if (_checkpointInProgress
                || (_schemaChangeTransaction is { } schemaOwner
                    && schemaOwner != permittedTransaction?.Value)
                || HasCheckpointBlockingTransactionLocked(permittedTransaction))
            {
                lease = null;
                return false;
            }

            _checkpointInProgress = true;
            lease = new CheckpointLease(this);
            return true;
        }
    }

    /// <summary>Collects one stable committed checkpoint input under its lease.</summary>
    internal MvccCheckpointSnapshot CollectCheckpointSnapshot()
    {
        lock (_gate)
        {
            if (!_checkpointInProgress)
                throw new InvalidOperationException("The MVCC checkpoint lease is not held.");

            return new MvccCheckpointSnapshot(
                SnapshotLiveCommittedRowsLocked(),
                SnapshotCommittedDeletesLocked(),
                _lastCommittedTimestamp);
        }
    }

    /// <summary>Count of version chains currently held (test/diagnostic).</summary>
    internal int VersionChainCount
    {
        get { lock (_gate) return _rows.Count; }
    }

    internal int VersionCount
    {
        get { lock (_gate) return _rows.Values.Sum(static chain => chain.Count); }
    }

    internal ulong OldestActiveSnapshot
    {
        get { lock (_gate) return ComputeReaderLowWaterMarkLocked(); }
    }

    /// <summary>
    /// Stamps the collected committed prefix as page-materialized, then reclaims
    /// only versions older than the oldest active snapshot. A current version is
    /// dropped only when every active reader can use its pinned base catalog.
    /// </summary>
    internal void GarbageCollectAfterCheckpoint(MvccCheckpointSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!_checkpointInProgress)
                throw new InvalidOperationException("The MVCC checkpoint lease is not held.");
            if (_checkpointGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The MVCC checkpoint generation is exhausted.");
            var materializedAt = ++_checkpointGeneration;
            var lwm = ComputeReaderLowWaterMarkLocked();

            foreach (var (rowId, chain) in _rows.ToArray())
            {
                foreach (var version in chain)
                {
                    if (VersionIsCommittedThrough(version, snapshot.DurableTimestamp))
                        version.MaterializedAt = materializedAt;
                }

                chain.RemoveAll(version =>
                    CanCollectMaterializedVersion(version, lwm, materializedAt));

                if (chain.Count == 0)
                    RemoveRowLocked(rowId);
            }

            PruneFinalizedTransactionsLocked(lwm);
        }
    }

    private bool HasActiveTransactionsLocked()
    {
        foreach (var tx in _transactions.Values)
        {
            if (tx.State is MvccTransactionState.Active or MvccTransactionState.Preparing)
                return true;
        }

        return false;
    }

    private bool HasCheckpointBlockingTransactionLocked(MvccTxId? permittedTransaction)
    {
        foreach (var tx in _transactions.Values)
        {
            if (permittedTransaction is { } permitted && tx.Id == permitted)
                continue;
            if (tx.State == MvccTransactionState.Preparing
                || tx.HasSchemaChange
                || tx.SnapshotWriteSet().Count != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void ReleaseCheckpoint()
    {
        lock (_gate)
        {
            if (!_checkpointInProgress)
                throw new InvalidOperationException("The MVCC checkpoint lease is not held.");
            _checkpointInProgress = false;
        }
    }

    internal sealed class CheckpointLease : IDisposable
    {
        private MvStore? _store;

        internal CheckpointLease(MvStore store)
        {
            _store = store;
        }

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            store?.ReleaseCheckpoint();
        }
    }

    private ulong ComputeReaderLowWaterMarkLocked()
    {
        ulong? lowest = null;
        foreach (var active in _transactions.Values)
        {
            if (active.State is not (MvccTransactionState.Active or MvccTransactionState.Preparing))
                continue;
            lowest = lowest is null
                ? active.BeginTimestamp
                : Math.Min(lowest.Value, active.BeginTimestamp);
        }

        return lowest ?? ulong.MaxValue;
    }

    private void PruneHistoryLocked(ulong minBegin)
    {
        ulong lowestActiveBegin = minBegin;
        foreach (var active in _transactions.Values)
        {
            if (active.State is MvccTransactionState.Active or MvccTransactionState.Preparing)
                lowestActiveBegin = Math.Min(lowestActiveBegin, active.BeginTimestamp);
        }

        foreach (var (rowId, chain) in _rows.ToArray())
        {
            chain.RemoveAll(version =>
                version.End is { IsTimestamp: true, Value: var endTs }
                && endTs < lowestActiveBegin);

            if (chain.Count == 0)
                RemoveRowLocked(rowId);
        }

        PruneFinalizedTransactionsLocked(lowestActiveBegin);
    }

    private void PruneFinalizedTransactionsLocked(ulong lowestActiveBegin)
    {
        if (_finalizedStates.Count > 4096)
        {
            var stale = _finalizedCommitTimestamps
                .Where(pair => pair.Value < lowestActiveBegin)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in stale)
            {
                _finalizedStates.Remove(key);
                _finalizedCommitTimestamps.Remove(key);
            }
        }
    }

    private void ValidateCommitLocked(
        MvccTransaction tx,
        IReadOnlyCollection<MvccRowId> writes)
    {
        foreach (var rowId in writes)
        {
            if (!_rows.TryGetValue(rowId, out var chain))
                continue;
            foreach (var version in chain)
            {
                if (IsWriteWriteConflict(tx, version))
                    throw new EmbeddedWriteWriteConflictException();
            }
            if (!rowId.Key.IsInteger)
                ThrowIfTypedKeyInsertConflict(tx, chain);
        }
    }

    private void ValidatePreparingCommitLocked(
        MvccTransaction tx,
        IReadOnlyCollection<MvccRowId> writes)
    {
        foreach (var rowId in writes)
        {
            if (!_rows.TryGetValue(rowId, out var chain))
                continue;
            foreach (var version in chain)
            {
                if (!IsWriteWriteConflict(tx, version))
                    continue;
                AbortLocked(tx.Id, tx);
                throw new EmbeddedWriteWriteConflictException();
            }
        }
    }

    private void ThrowIfCheckpointBlocksWriteLocked()
    {
        if (_checkpointInProgress)
            throw new EmbeddedBusyException();
    }

    private static bool VersionIsCommittedThrough(
        MvccRowVersion version,
        ulong durableTimestamp)
    {
        if (version.Begin is { IsTimestamp: false }
            || version.End is { IsTimestamp: false })
        {
            return false;
        }

        var latestTimestamp = Math.Max(
            version.Begin is { IsTimestamp: true, Value: var beginTs } ? beginTs : 0,
            version.End is { IsTimestamp: true, Value: var endTs } ? endTs : 0);
        return latestTimestamp <= durableTimestamp;
    }

    private static bool CanCollectMaterializedVersion(
        MvccRowVersion version,
        ulong readerLowWaterMark,
        ulong materializedAt)
    {
        if (version.MaterializedAt == 0 || version.MaterializedAt > materializedAt)
            return false;
        if (version.Begin is not { IsTimestamp: true, Value: var beginTs })
            return false;

        if (version.End is { IsTimestamp: true, Value: var endTs })
            return endTs <= readerLowWaterMark;

        return version.End is null && beginTs < readerLowWaterMark;
    }

    private void ClearMaterializedStateLocked()
    {
        _rows.Clear();
        _orderedTableKeys.Clear();
        _tableIds.Clear();
        _tableNames.Clear();
        _tableSchemaGenerations.Clear();
        _nextRowIds.Clear();
    }

    private void RemoveUnpublishedSchemaIdentitiesLocked(ulong publishedGeneration)
    {
        var staleIds = _tableSchemaGenerations
            .Where(pair => pair.Value != publishedGeneration)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var tableId in staleIds)
        {
            if (_rows.Keys.Any(rowId => rowId.TableId == tableId))
                continue;
            var generation = _tableSchemaGenerations[tableId];
            if (!_tableNames.Remove(tableId, out var tableName))
                continue;
            _tableSchemaGenerations.Remove(tableId);
            _tableIds.Remove(new MvccSchemaObjectIdentity(
                generation,
                tableName));
            _orderedTableKeys.Remove(tableId);
            _nextRowIds.Remove(tableId);
        }
    }

    private void RegisterTableKeyComparer(long tableId, IComparer<MvccKey> comparer)
    {
        lock (_gate)
        {
            if (_orderedTableKeys.TryGetValue(tableId, out var existing))
            {
                var compatible = ReferenceEquals(existing.Comparer, comparer)
                    || existing.Comparer is MvccKeyComparer registered
                        && comparer is MvccKeyComparer requested
                        && registered.IsCompatibleWith(requested);
                if (!compatible)
                {
                    throw new InvalidOperationException(
                        "The MVCC table key descriptor changed while its identity remained live.");
                }
                return;
            }

            var ordered = new OrderedTableKeys(comparer);
            foreach (var rowId in _rows.Keys)
            {
                if (rowId.TableId == tableId)
                    AddOrderedKey(ordered.Keys, rowId.Key);
            }
            _orderedTableKeys.Add(tableId, ordered);
        }
    }

    private bool TryGetNextVisible(
        MvccTxId txId,
        long tableId,
        IComparer<MvccKey> comparer,
        bool hasPrevious,
        MvccKey previous,
        out MvccVisibleRow row)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            if (!_orderedTableKeys.TryGetValue(tableId, out var ordered))
                throw new InvalidOperationException("MVCC ordered table registration was lost.");

            var hasCandidate = TryGetNextKey(ordered.Keys, comparer, hasPrevious, previous, out var candidate);
            while (hasCandidate)
            {
                var rowId = new MvccRowId(tableId, candidate);
                if (_rows.TryGetValue(rowId, out var chain)
                    && TryResolveVisibleEffectLocked(tx, chain, out var cells, out var isDelete))
                {
                    row = new MvccVisibleRow(candidate, cells, isDelete);
                    return true;
                }

                previous = candidate;
                hasPrevious = true;
                hasCandidate = TryGetNextKey(ordered.Keys, comparer, hasPrevious, previous, out candidate);
            }

            row = default;
            return false;
        }
    }

    private bool TryResolveVisibleEffectLocked(
        MvccTransaction tx,
        IReadOnlyList<MvccRowVersion> chain,
        out SqlValue[]? cells,
        out bool isDelete)
    {
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var version = chain[index];
            if (!IsVisibleTo(version, tx))
                continue;

            isDelete = version.IsTombstone;
            cells = isDelete ? null : (SqlValue[])version.Cells.Clone();
            return true;
        }

        foreach (var version in chain)
        {
            if (version.End is null)
                continue;

            var end = version.End.Value;
            if (end.IsTimestamp
                    ? end.Value <= tx.BeginTimestamp
                    : end.Value == tx.Id.Value || LookupCreatorVisibility(end.Value, tx))
            {
                cells = null;
                isDelete = true;
                return true;
            }
        }

        cells = null;
        isDelete = false;
        return false;
    }

    private static bool TryGetNextKey(
        SortedSet<MvccKey> keys,
        IComparer<MvccKey> comparer,
        bool hasPrevious,
        MvccKey previous,
        out MvccKey key)
    {
        if (keys.Count == 0)
        {
            key = default;
            return false;
        }

        if (!hasPrevious)
        {
            key = keys.Min;
            return true;
        }

        var maximum = keys.Max;
        if (comparer.Compare(previous, maximum) >= 0)
        {
            key = default;
            return false;
        }

        foreach (var candidate in keys.GetViewBetween(previous, maximum))
        {
            if (comparer.Compare(candidate, previous) > 0)
            {
                key = candidate;
                return true;
            }
        }

        key = default;
        return false;
    }

    private void IndexRowLocked(MvccRowId rowId)
    {
        if (_orderedTableKeys.TryGetValue(rowId.TableId, out var ordered))
            AddOrderedKey(ordered.Keys, rowId.Key);
    }

    private static void AddOrderedKey(SortedSet<MvccKey> keys, MvccKey key)
    {
        if (keys.Add(key))
            return;
        if (keys.TryGetValue(key, out var existing) && existing.Equals(key))
            return;
        throw new InvalidDataException(
            "Distinct MVCC identities compare equal under the table's SQLite key descriptor.");
    }

    private void RemoveRowLocked(MvccRowId rowId)
    {
        _rows.Remove(rowId);
        if (_orderedTableKeys.TryGetValue(rowId.TableId, out var ordered))
            ordered.Keys.Remove(rowId.Key);
    }

    private sealed class OrderedTableKeys(IComparer<MvccKey> comparer)
    {
        internal IComparer<MvccKey> Comparer { get; } =
            comparer ?? throw new ArgumentNullException(nameof(comparer));

        internal SortedSet<MvccKey> Keys { get; } =
            new(comparer);
    }

    private sealed class MvccSchemaObjectIdentityComparer :
        IEqualityComparer<MvccSchemaObjectIdentity>
    {
        internal static MvccSchemaObjectIdentityComparer Instance { get; } = new();

        public bool Equals(
            MvccSchemaObjectIdentity left,
            MvccSchemaObjectIdentity right)
            => left.SchemaGeneration == right.SchemaGeneration
                && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(MvccSchemaObjectIdentity identity)
            => HashCode.Combine(
                identity.SchemaGeneration,
                StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Name));
    }
}

internal readonly record struct MvccVisibleRow(MvccKey Key, SqlValue[]? Cells, bool IsDelete);
