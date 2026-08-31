using Ahtola.Core.Storage;

namespace Ahtola.Core;

/// <summary>
/// Per-table mutation bookkeeping for one classic (non-MVCC) file-backed transaction. Tracks the
/// final effect of every row this transaction has touched, keyed by the row's identity — its
/// rowid for an ordinary table, its primary-key tuple for a WITHOUT ROWID table — so a durable
/// index seek can suppress stale base rows and splice in this transaction's own current values
/// without materializing or re-sorting the whole index.
/// </summary>
/// <remarks>
/// Existence of an entry — tombstoned (<c>Current</c> is <see langword="null"/>) or live — is
/// itself the suppression signal: any row the pinned base pager snapshot would otherwise yield
/// for that identity is stale as of this transaction and must never surface unfiltered. Fed
/// exclusively through <see cref="TransactionMutationOverlay.RecordRowChange"/>, which every DML
/// path funnels through via <c>QueryContext.ReportRowChange</c> — plain statements, REPLACE/UPSERT
/// conflict resolution, trigger bodies, and foreign-key cascade actions alike.
/// </remarks>
internal sealed class TransactionTableOverlay
{
    private readonly Dictionary<long, SqlValue[]?>? _byRowId;
    private readonly List<(SqlValue[] Key, SqlValue[]? Current)>? _byPrimaryKey;
    private readonly SqliteIndexRecordComparer? _primaryKeyComparer;

    public TransactionTableOverlay(bool hasRowid, SqliteIndexRecordComparer? primaryKeyComparer = null)
    {
        HasRowid = hasRowid;
        if (hasRowid)
        {
            _byRowId = new Dictionary<long, SqlValue[]?>();
        }
        else
        {
            _byPrimaryKey = new List<(SqlValue[] Key, SqlValue[]? Current)>();
            _primaryKeyComparer = primaryKeyComparer
                ?? throw new ArgumentNullException(
                    nameof(primaryKeyComparer),
                    "A WITHOUT ROWID table overlay requires a primary-key comparer.");
        }
    }

    public bool HasRowid { get; }

    public bool IsEmpty => HasRowid ? _byRowId!.Count == 0 : _byPrimaryKey!.Count == 0;

    public void SetRowId(long rowId, SqlValue[]? current)
    {
        if (!HasRowid)
            throw new InvalidOperationException("This overlay tracks primary keys, not rowids.");
        _byRowId![rowId] = current;
    }

    public bool TryGetByRowId(long rowId, out SqlValue[]? current)
    {
        if (!HasRowid)
        {
            current = null;
            return false;
        }

        return _byRowId!.TryGetValue(rowId, out current);
    }

    public void SetPrimaryKey(SqlValue[] key, SqlValue[]? current)
    {
        if (HasRowid)
            throw new InvalidOperationException("This overlay tracks rowids, not primary keys.");

        var entries = _byPrimaryKey!;
        for (var position = 0; position < entries.Count; position++)
        {
            if (_primaryKeyComparer!.Compare(entries[position].Key, key) == 0)
            {
                entries[position] = (key, current);
                return;
            }
        }

        entries.Add((key, current));
    }

    public bool TryGetByPrimaryKey(SqlValue[] key, out SqlValue[]? current)
    {
        if (HasRowid)
        {
            current = null;
            return false;
        }

        var entries = _byPrimaryKey!;
        for (var position = 0; position < entries.Count; position++)
        {
            if (_primaryKeyComparer!.Compare(entries[position].Key, key) == 0)
            {
                current = entries[position].Current;
                return true;
            }
        }

        current = null;
        return false;
    }

    /// <summary>Whether two projected primary-key tuples identify the same WITHOUT ROWID row.</summary>
    public bool PrimaryKeyEquals(SqlValue[] left, SqlValue[] right)
    {
        if (HasRowid)
            throw new InvalidOperationException("This overlay tracks rowids, not primary keys.");
        return _primaryKeyComparer!.Compare(left, right) == 0;
    }

    /// <summary>Enumerates this table's current (non-tombstoned) overlay rows.</summary>
    public IEnumerable<EmbeddedFileIndexSeekRow> EnumerateCurrentRows()
    {
        if (HasRowid)
        {
            foreach (var pair in _byRowId!)
            {
                if (pair.Value is { } values)
                    yield return new EmbeddedFileIndexSeekRow(values, pair.Key);
            }

            yield break;
        }

        foreach (var entry in _byPrimaryKey!)
        {
            if (entry.Current is { } values)
                yield return new EmbeddedFileIndexSeekRow(values, RowId: null);
        }
    }

    /// <summary>Captures a point-in-time copy of this table's overlay for a SAVEPOINT.</summary>
    public TransactionTableOverlayCheckpoint CreateCheckpoint()
        => HasRowid
            ? new TransactionTableOverlayCheckpoint(true, new Dictionary<long, SqlValue[]?>(_byRowId!), null)
            : new TransactionTableOverlayCheckpoint(
                false,
                null,
                new List<(SqlValue[] Key, SqlValue[]? Current)>(_byPrimaryKey!));

    /// <summary>
    /// Restores this table's overlay from a previously captured checkpoint without mutating the
    /// checkpoint, so a later ROLLBACK TO the same savepoint remains valid (mirrors
    /// <c>SchemaCatalog.Clone()</c> for the catalog snapshot).
    /// </summary>
    public void RestoreCheckpoint(TransactionTableOverlayCheckpoint checkpoint)
    {
        if (HasRowid)
        {
            _byRowId!.Clear();
            if (checkpoint.ByRowId is { } byRowId)
            {
                foreach (var pair in byRowId)
                    _byRowId[pair.Key] = pair.Value;
            }

            return;
        }

        _byPrimaryKey!.Clear();
        if (checkpoint.ByPrimaryKey is { } byPrimaryKey)
            _byPrimaryKey.AddRange(byPrimaryKey);
    }

    /// <summary>Discards all recorded mutations, used when restoring a checkpoint predating this table's first touch.</summary>
    public void Clear()
    {
        _byRowId?.Clear();
        _byPrimaryKey?.Clear();
    }
}

/// <summary>An immutable point-in-time copy of one table's mutation overlay for SAVEPOINT support.</summary>
internal sealed record TransactionTableOverlayCheckpoint(
    bool HasRowid,
    Dictionary<long, SqlValue[]?>? ByRowId,
    List<(SqlValue[] Key, SqlValue[]? Current)>? ByPrimaryKey);

/// <summary>
/// Owns one <see cref="TransactionTableOverlay"/> per table touched by a classic (non-MVCC)
/// file-backed transaction. Attached to <c>TransactionDatabaseState</c> and threaded through
/// <c>QueryContext</c> so every DML path can feed it via <see cref="RecordRowChange"/> and every
/// eligible index seek can consult it while the pinned durable pager snapshot is open.
/// </summary>
internal sealed class TransactionMutationOverlay
{
    private readonly Dictionary<string, TransactionTableOverlay> _tables = new(StringComparer.OrdinalIgnoreCase);

    public TransactionTableOverlay GetOrCreate(EmbeddedDatabase database, EmbeddedTable table, string tableName)
    {
        if (_tables.TryGetValue(tableName, out var existing))
        {
            if (existing.HasRowid == table.HasRowid)
                return existing;

            // DDL can replace a table inside one transaction. The pinned direct-access path is
            // already disabled after a schema change, but DML still reports through this shared
            // funnel; replace incompatible bookkeeping rather than throwing on the next write.
            _tables.Remove(tableName);
        }

        var overlay = table.HasRowid
            ? new TransactionTableOverlay(hasRowid: true)
            : new TransactionTableOverlay(
                hasRowid: false,
                primaryKeyComparer: database.CreatePrimaryKeyRecordComparer(
                    table.PrimaryKeySchema
                        ?? throw new InvalidOperationException(
                            $"WITHOUT ROWID table '{tableName}' is missing its primary-key metadata.")));
        _tables[tableName] = overlay;
        return overlay;
    }

    public bool TryGet(string tableName, out TransactionTableOverlay overlay)
        => _tables.TryGetValue(tableName, out overlay!);

    /// <summary>
    /// Records the final effect of one row change reported through
    /// <c>QueryContext.ReportRowChange</c>, the common DML funnel for plain INSERT/UPDATE/DELETE,
    /// REPLACE/UPSERT conflict resolution, trigger bodies, and foreign-key cascade actions.
    /// </summary>
    public void RecordRowChange(
        EmbeddedDatabase database,
        EmbeddedTable table,
        string tableName,
        SqliteChangeOperation operation,
        long rowId,
        SqlValue[]? before,
        SqlValue[]? after)
    {
        var overlay = GetOrCreate(database, table, tableName);
        if (table.HasRowid)
        {
            switch (operation)
            {
                case SqliteChangeOperation.Delete:
                    overlay.SetRowId(rowId, null);
                    break;
                case SqliteChangeOperation.Insert:
                    if ((after ?? LookupCurrentRow(table, rowId)) is { } insertedValues)
                        overlay.SetRowId(rowId, insertedValues);
                    break;
                case SqliteChangeOperation.Update:
                    // A rowid-changing UPDATE decomposes into paired Delete+Insert calls plus an
                    // informational no-op Update (before and after both null) that this overlay
                    // does not need: the Delete/Insert calls already recorded both identities.
                    if (before is null && after is null)
                        break;
                    if ((after ?? LookupCurrentRow(table, rowId)) is { } updatedValues)
                        overlay.SetRowId(rowId, updatedValues);
                    break;
            }

            return;
        }

        var primaryKeySchema = table.PrimaryKeySchema
            ?? throw new InvalidOperationException(
                $"WITHOUT ROWID table '{tableName}' is missing its primary-key metadata.");
        switch (operation)
        {
            case SqliteChangeOperation.Delete:
                if ((before ?? LookupCurrentRow(table, rowId)) is { } deletedValues)
                    overlay.SetPrimaryKey(primaryKeySchema.ProjectKey(deletedValues), null);
                break;
            case SqliteChangeOperation.Insert:
                if ((after ?? LookupCurrentRow(table, rowId)) is { } insertedValues)
                    overlay.SetPrimaryKey(primaryKeySchema.ProjectKey(insertedValues), insertedValues);
                break;
            case SqliteChangeOperation.Update:
                if (before is null && after is null)
                    break;
                if ((after ?? LookupCurrentRow(table, rowId)) is not { } newValues)
                    break;

                var newKey = primaryKeySchema.ProjectKey(newValues);
                if (before is not null)
                {
                    var oldKey = primaryKeySchema.ProjectKey(before);
                    if (!overlay.PrimaryKeyEquals(oldKey, newKey))
                        overlay.SetPrimaryKey(oldKey, null);
                }

                overlay.SetPrimaryKey(newKey, newValues);
                break;
        }
    }

    private static SqlValue[]? LookupCurrentRow(EmbeddedTable table, long rowId)
    {
        var index = table.RowIds.IndexOf(rowId);
        return index >= 0 && index < table.Rows.Count ? table.Rows[index] : null;
    }

    /// <summary>Captures a point-in-time copy of every table's overlay for a SAVEPOINT.</summary>
    public TransactionMutationOverlayCheckpoint CreateCheckpoint()
    {
        var tables = new Dictionary<string, TransactionTableOverlayCheckpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _tables)
            tables[pair.Key] = pair.Value.CreateCheckpoint();
        return new TransactionMutationOverlayCheckpoint(tables);
    }

    /// <summary>
    /// Restores every table's overlay from a previously captured checkpoint for ROLLBACK TO. A
    /// table first touched only after the checkpoint was taken has no entry in it and is removed
    /// outright, matching "nothing had happened to it yet" at checkpoint time — leaving a
    /// stale-but-cleared entry behind would keep it around under the wrong identity forever. A
    /// table whose rowid-vs-WITHOUT-ROWID shape no longer matches the checkpoint — because it was
    /// dropped and recreated with a different shape after the checkpoint was taken but before this
    /// rollback — is replaced with a freshly shaped overlay rather than restored in place, since an
    /// overlay's internal storage (a by-rowid dictionary vs. a by-primary-key list plus comparer)
    /// is tied to its current shape and cannot represent the checkpoint's rows under the wrong one.
    /// </summary>
    /// <param name="checkpoint">The point-in-time overlay state to restore.</param>
    /// <param name="primaryKeyComparerResolver">
    /// Resolves the primary-key record comparer for a WITHOUT ROWID table by name. Consulted only
    /// when a replacement overlay must be constructed for a checkpointed WITHOUT ROWID table that
    /// is missing or shape-mismatched in the live overlay; a caller that cannot reach this
    /// situation (e.g. no schema changes could have occurred since the checkpoint) may omit it.
    /// </param>
    public void RestoreCheckpoint(
        TransactionMutationOverlayCheckpoint checkpoint,
        Func<string, SqliteIndexRecordComparer>? primaryKeyComparerResolver = null)
    {
        List<string>? stale = null;
        foreach (var name in _tables.Keys)
        {
            if (!checkpoint.Tables.ContainsKey(name))
                (stale ??= new List<string>()).Add(name);
        }

        if (stale is not null)
        {
            foreach (var name in stale)
                _tables.Remove(name);
        }

        foreach (var pair in checkpoint.Tables)
        {
            var tableCheckpoint = pair.Value;
            if (_tables.TryGetValue(pair.Key, out var existing) && existing.HasRowid == tableCheckpoint.HasRowid)
            {
                existing.RestoreCheckpoint(tableCheckpoint);
                continue;
            }

            // Either this table has no live overlay at all yet, or its shape changed since the
            // checkpoint (dropped and recreated with a different rowid-ness). Build a fresh
            // overlay in the checkpoint's own shape instead of forcing mismatched data into the
            // wrong storage.
            var comparer = tableCheckpoint.HasRowid
                ? null
                : primaryKeyComparerResolver?.Invoke(pair.Key)
                    ?? throw new InvalidOperationException(
                        $"Restoring the mutation overlay for WITHOUT ROWID table '{pair.Key}' requires a primary-key comparer.");
            var replacement = new TransactionTableOverlay(tableCheckpoint.HasRowid, comparer);
            replacement.RestoreCheckpoint(tableCheckpoint);
            _tables[pair.Key] = replacement;
        }
    }
}

/// <summary>An immutable point-in-time copy of an entire transaction's mutation overlay for SAVEPOINT support.</summary>
internal sealed record TransactionMutationOverlayCheckpoint(
    IReadOnlyDictionary<string, TransactionTableOverlayCheckpoint> Tables);
