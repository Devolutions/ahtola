namespace Ahtola.Core.Mvcc;

/// <summary>Opaque MVCC transaction identifier (Turso <c>TxID</c>).</summary>
internal readonly record struct MvccTxId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Row identity within an MVCC table (Turso <c>RowID</c>).</summary>
internal readonly record struct MvccRowId(long TableId, long RowId);

/// <summary>Lifecycle state of an MVCC transaction (Turso <c>TransactionState</c>).</summary>
internal enum MvccTransactionState : byte
{
    Active = 0,
    Preparing = 1,
    Committed = 2,
    Aborted = 3,
}

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

    internal MvccTransaction(MvccTxId id, ulong beginTimestamp)
    {
        Id = id;
        BeginTimestamp = beginTimestamp;
    }

    internal MvccTxId Id { get; }

    internal ulong BeginTimestamp { get; }

    internal MvccTransactionState State
    {
        get { lock (_gate) return _state; }
    }

    internal ulong? CommitTimestamp
    {
        get { lock (_gate) return _commitTimestamp; }
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
    private readonly ILogicalClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, MvccTransaction> _transactions = [];
    private readonly Dictionary<ulong, MvccTransactionState> _finalizedStates = [];
    private readonly Dictionary<ulong, ulong> _finalizedCommitTimestamps = [];
    private readonly Dictionary<MvccRowId, List<MvccRowVersion>> _rows = [];
    private readonly Dictionary<string, long> _tableIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _tableNames = [];
    private readonly Dictionary<long, long> _nextRowIds = [];
    private long _nextTableId = -2;
    private ulong _nextTxId = 1;
    private ulong _nextVersionId = 1;
    private ulong? _exclusiveTxId;
    private MvccLogicalLog? _logicalLog;

    internal MvStore(ILogicalClock? clock = null, MvccLogicalLog? logicalLog = null)
    {
        _clock = clock ?? new MvccClock();
        _logicalLog = logicalLog;
    }

    internal ILogicalClock Clock => _clock;

    internal MvccLogicalLog? LogicalLog => _logicalLog;

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
                if (!_rows.TryGetValue(op.RowId, out var chain))
                {
                    chain = [];
                    _rows[op.RowId] = chain;
                }

                if (op.IsDelete)
                {
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
        }
    }

    /// <summary>Stable negative table id for <paramref name="tableName"/>.</summary>
    internal long GetOrCreateTableId(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        lock (_gate)
        {
            if (_tableIds.TryGetValue(tableName, out var id))
                return id;
            id = _nextTableId--;
            _tableIds[tableName] = id;
            _tableNames[id] = tableName;
            return id;
        }
    }

    internal bool TryGetTableName(long tableId, out string? name)
    {
        lock (_gate)
            return _tableNames.TryGetValue(tableId, out name);
    }

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

    internal MvccTransaction BeginTransaction()
    {
        lock (_gate)
        {
            if (_exclusiveTxId is not null)
                throw new EmbeddedBusyException();

            var beginTs = NextBeginTimestamp();
            var id = new MvccTxId(_nextTxId++);
            var tx = new MvccTransaction(id, beginTs);
            _transactions.Add(id.Value, tx);
            return tx;
        }
    }

    internal MvccTransaction BeginExclusiveTransaction(MvccTxId? existing = null)
    {
        lock (_gate)
        {
            if (_exclusiveTxId is { } held
                && (existing is null || held != existing.Value.Value))
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
            var tx = new MvccTransaction(id, beginTs);
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

    /// <summary>Insert a new live version created by <paramref name="txId"/>.</summary>
    internal void Insert(MvccTxId txId, MvccRowId rowId, SqlValue[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        lock (_gate)
        {
            var tx = RequireActive(txId);
            var version = new MvccRowVersion(
                _nextVersionId++,
                begin: MvccStamp.FromTxId(txId),
                end: null,
                cells: (SqlValue[])cells.Clone());
            if (!_rows.TryGetValue(rowId, out var chain))
            {
                chain = [];
                _rows[rowId] = chain;
            }

            chain.Add(version);
            tx.RecordLogOp(MvccLogOp.Upsert(rowId, version.Cells));
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
                tx.RecordLogOp(MvccLogOp.Delete(rowId));
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
                    tx.RecordLogOp(MvccLogOp.Delete(rowId));
                    return;
                }

                ThrowIfConcurrentWriterOnRow(tx, chain);
            }
            else
            {
                chain = [];
                _rows[rowId] = chain;
            }

            chain.Add(new MvccRowVersion(
                _nextVersionId++,
                begin: MvccStamp.FromTxId(txId),
                end: null,
                cells: [],
                isTombstone: true));
            tx.RecordLogOp(MvccLogOp.Delete(rowId));
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
    /// Snapshot of every currently live committed version (end is null, begin is a
    /// timestamp). Used after a concurrent tx commits to merge into the classic catalog.
    /// </summary>
    internal IReadOnlyList<(MvccRowId RowId, SqlValue[] Cells)> SnapshotLiveCommittedRows()
    {
        lock (_gate)
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
    }

    /// <summary>
    /// Row ids whose latest committed state is deleted (ended version, or a live
    /// committed pure tombstone that marks a base-row delete) with no later live
    /// non-tombstone version.
    /// </summary>
    internal IReadOnlyCollection<MvccRowId> SnapshotCommittedDeletes()
    {
        lock (_gate)
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
    }

    /// <summary>Record a write-set entry without mutating version chains (catalog-path DML).</summary>
    internal void RecordWrite(MvccTxId id, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(id);
            tx.RecordWrite(rowId);
        }
    }

    /// <summary>
    /// Commit with first-committer-wins WW detection. Rewrites in-flight TxID
    /// stamps on version chains to the commit timestamp (Turso rewrite step).
    /// </summary>
    internal void Commit(MvccTxId id)
    {
        MvccTransaction tx;
        HashSet<MvccRowId> writes;
        lock (_gate)
        {
            tx = RequireActive(id);
            writes = tx.SnapshotWriteSet().ToHashSet();

            foreach (var rowId in writes)
            {
                if (!_rows.TryGetValue(rowId, out var chain))
                    continue;
                foreach (var version in chain)
                {
                    if (IsWriteWriteConflict(tx, version))
                        throw new EmbeddedWriteWriteConflictException();
                }
            }
        }

        _clock.GetTimestamp(ts => tx.MarkPreparing(ts));

        lock (_gate)
        {
            var commitTs = tx.CommitTimestamp
                ?? throw new InvalidOperationException("Preparing transaction missing commit timestamp.");

            foreach (var rowId in writes)
            {
                if (!_rows.TryGetValue(rowId, out var chain))
                    continue;
                foreach (var version in chain)
                {
                    if (IsWriteWriteConflict(tx, version))
                    {
                        AbortLocked(id, tx);
                        throw new EmbeddedWriteWriteConflictException();
                    }
                }
            }

            RewriteStampsLocked(id, commitTs);
            var logOps = tx.SnapshotLogOps();
            tx.MarkCommitted();
            _finalizedStates[id.Value] = MvccTransactionState.Committed;
            _finalizedCommitTimestamps[id.Value] = commitTs;
            ClearExclusive(id);
            _transactions.Remove(id.Value);
            PruneHistoryLocked(tx.BeginTimestamp);

            // Durable log after in-memory commit is published (Turso flushes then
            // advances visibility; we keep the store already committed and append).
            if (logOps.Count != 0)
                _logicalLog?.AppendCommit(commitTs, logOps);
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
            _rows.Remove(op.RowId);
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
                _rows.Remove(rowId);
        }

        tx.MarkAborted();
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

    /// <summary>Count of version chains currently held (test/diagnostic).</summary>
    internal int VersionChainCount
    {
        get { lock (_gate) return _rows.Count; }
    }

    /// <summary>
    /// Post-checkpoint GC after catalog materialization. When no concurrent
    /// transactions are open, drop the entire version store (rows now live in
    /// the classic catalog). Otherwise prune ended history past the reader LWM
    /// (Turso <c>GcTableRows</c> spirit without per-page btree walks).
    /// </summary>
    internal void GarbageCollectAfterCheckpoint()
    {
        lock (_gate)
        {
            if (!HasActiveTransactionsLocked())
            {
                _rows.Clear();
                return;
            }

            var lwm = ComputeReaderLowWaterMarkLocked();
            PruneHistoryLocked(lwm);

            // Committed pure tombstones with begin &lt; LWM are catalog-owned once
            // materialize has applied deletes; drop them so dual-cursor defers to base.
            foreach (var (rowId, chain) in _rows.ToArray())
            {
                chain.RemoveAll(version =>
                    version.IsTombstone
                    && version.End is null
                    && version.Begin is { IsTimestamp: true, Value: var beginTs }
                    && beginTs < lwm);

                if (chain.Count == 0)
                    _rows.Remove(rowId);
            }
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

        // No readers: LWM is a fresh clock tick so all ended history may drop.
        return lowest ?? _clock.GetTimestamp(static _ => { });
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
                _rows.Remove(rowId);
        }

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
}
