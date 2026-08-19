using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola;

/// <summary>
/// Filters that decide which schema objects and rows participate in managed embedded-replica
/// logical replay, mirroring Turso's <c>is_logically_replayable_table</c>/
/// <c>should_replay_local_change</c> internal-object exclusions.
/// </summary>
internal static class ManagedReplicaLogicalFilters
{
    private const string SqliteInternalPrefix = "sqlite_";
    private const string TursoInternalPrefix = "__turso_internal_";
    internal const string TursoSyncLastChangeIdTable = "turso_sync_last_change_id";
    private const string TursoCdcTableName = "turso_cdc";
    private const string TursoCdcVersionTableName = "turso_cdc_version";

    public static bool IsLogicallyReplayable(string name)
        => !name.StartsWith(SqliteInternalPrefix, StringComparison.Ordinal)
           && !name.StartsWith(TursoInternalPrefix, StringComparison.Ordinal)
           && name != TursoSyncLastChangeIdTable
           && name != TursoCdcTableName
           && name != TursoCdcVersionTableName;
}

/// <summary>
/// A persisted, stable table-id-to-name map used to resolve row operations that omit
/// <c>table_name</c> in favor of a portable <c>stable_table_id</c>. Cloned before decoding a
/// transaction and committed back only when that transaction is not excluded as a client echo,
/// matching Turso's <c>LogicalReplayTableMap</c>.
/// </summary>
internal sealed class ManagedReplicaLogicalTableMap
{
    private readonly Dictionary<ulong, string> _namesByStableId;

    public ManagedReplicaLogicalTableMap(IReadOnlyDictionary<ulong, string> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _namesByStableId = new Dictionary<ulong, string>(initial);
    }

    public IReadOnlyDictionary<ulong, string> Snapshot() => _namesByStableId;

    public ManagedReplicaLogicalTableMap Clone() => new(_namesByStableId);

    public string ResolveRowTableName(ManagedReplicaLogicalOp op)
    {
        if (!string.IsNullOrEmpty(op.TableName))
            return op.TableName;
        if (op.StableTableId == 0)
        {
            throw new InvalidDataException(
                "A logical row operation must include a table name or a stable table id.");
        }

        if (_namesByStableId.TryGetValue(op.StableTableId, out var name))
            return name;

        throw new InvalidDataException(
            $"A logical row operation references an unknown stable table id {op.StableTableId}.");
    }

    public void ObserveSchemaOp(
        ulong stableTableId,
        string schemaName,
        ManagedReplicaLogicalSchemaKind schemaKind,
        ManagedReplicaLogicalSchemaAction schemaAction)
    {
        if (stableTableId == 0 || schemaKind != ManagedReplicaLogicalSchemaKind.Table)
            return;

        switch (schemaAction)
        {
            case ManagedReplicaLogicalSchemaAction.Create:
            case ManagedReplicaLogicalSchemaAction.Refresh:
            case ManagedReplicaLogicalSchemaAction.Alter:
                _namesByStableId[stableTableId] = schemaName;
                break;
            case ManagedReplicaLogicalSchemaAction.Drop:
                _namesByStableId.Remove(stableTableId);
                break;
        }
    }
}

/// <summary>Result of applying a batch of decoded logical transactions.</summary>
internal readonly record struct ManagedReplicaLogicalApplyResult(
    IReadOnlyDictionary<ulong, string> TableNamesByStableId,
    long TransactionCount,
    long OperationCount);

/// <summary>
/// Replays decoded MVCC logical-log transactions onto a managed connection using plain SQL,
/// mirroring Turso's <c>DatabaseReplaySession</c>/<c>DatabaseReplayGenerator</c>. This is a
/// pull-only replay engine: the wire decode never produces an "Update" row change (only
/// Insert/Delete), so no delta-update SQL path is implemented.
/// </summary>
internal static class ManagedReplicaLogicalReplayer
{
    /// <summary>
    /// Applies every operation of every non-excluded transaction in <paramref name="transactions"/>
    /// against <paramref name="connection"/>. The caller is responsible for wrapping this call in
    /// its own <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c> so the whole response applies atomically.
    /// </summary>
    public static ManagedReplicaLogicalApplyResult Apply(
        IManagedConnectionAdapter connection,
        IReadOnlyList<ManagedReplicaLogicalTxn> transactions,
        IReadOnlyDictionary<ulong, string> tableNamesByStableId,
        string excludedClientId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(tableNamesByStableId);

        var tableMap = new ManagedReplicaLogicalTableMap(tableNamesByStableId);
        long transactionCount = 0;
        long operationCount = 0;

        foreach (var txn in transactions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AcknowledgesClient(txn, excludedClientId))
                continue;

            var workingMap = tableMap.Clone();
            var operations = ResolveTxnOperations(txn, workingMap);
            foreach (var op in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplayOperation(connection, op);
                operationCount++;
            }

            tableMap = workingMap;
            transactionCount++;
        }

        return new ManagedReplicaLogicalApplyResult(tableMap.Snapshot(), transactionCount, operationCount);
    }

    /// <summary>
    /// A transaction is excluded when it originated from this client, either via the portable
    /// <c>client</c> metadata key or (fallback, for compatibility with the existing push path)
    /// because it contains an upsert into <c>turso_sync_last_change_id</c> acknowledging this
    /// client's id.
    /// </summary>
    private static bool AcknowledgesClient(ManagedReplicaLogicalTxn txn, string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
            return false;
        if (txn.OriginClientId == clientId)
            return true;

        foreach (var op in txn.Ops)
        {
            if (op.OpType != ManagedReplicaLogicalOpType.UpsertRow
                || op.TableName != ManagedReplicaLogicalFilters.TursoSyncLastChangeIdTable
                || op.Record.Length == 0)
            {
                continue;
            }

            var values = SqliteRecordCodec.Decode(op.Record);
            if (values.Length > 0
                && values[0].Kind == SqlValueKind.Text
                && values[0].AsText() == clientId)
            {
                return true;
            }
        }

        return false;
    }

    private static List<ManagedReplicaLogicalOp> ResolveTxnOperations(
        ManagedReplicaLogicalTxn txn,
        ManagedReplicaLogicalTableMap tableMap)
    {
        var resolved = new List<ManagedReplicaLogicalOp>(txn.Ops.Count);
        foreach (var op in txn.Ops)
        {
            switch (op.OpType)
            {
                case ManagedReplicaLogicalOpType.Unspecified:
                    throw new InvalidDataException("A logical operation type must not be unspecified.");

                case ManagedReplicaLogicalOpType.UpsertRow:
                case ManagedReplicaLogicalOpType.DeleteRow:
                    {
                        var tableName = tableMap.ResolveRowTableName(op);
                        if (!ManagedReplicaLogicalFilters.IsLogicallyReplayable(tableName))
                            continue;
                        resolved.Add(op with { TableName = tableName });
                        break;
                    }

                case ManagedReplicaLogicalOpType.Schema:
                    {
                        if (!ManagedReplicaLogicalFilters.IsLogicallyReplayable(op.SchemaName))
                            continue;
                        var action = op.SchemaAction
                            ?? throw new InvalidDataException("A logical schema operation must include a schema action.");
                        var kind = op.SchemaKind
                            ?? throw new InvalidDataException("A logical schema operation must include a schema kind.");
                        tableMap.ObserveSchemaOp(op.StableTableId, op.SchemaName, kind, action);
                        resolved.Add(op);
                        break;
                    }

                case ManagedReplicaLogicalOpType.UpdateHeader:
                    resolved.Add(op);
                    break;

                default:
                    throw new InvalidDataException($"A logical operation has an unknown op type {op.OpType}.");
            }
        }

        return resolved;
    }

    private static void ReplayOperation(IManagedConnectionAdapter connection, ManagedReplicaLogicalOp op)
    {
        switch (op.OpType)
        {
            case ManagedReplicaLogicalOpType.UpdateHeader:
                ReplayHeaderOp(connection, op);
                return;
            case ManagedReplicaLogicalOpType.Schema:
                ReplaySchemaOp(connection, op);
                return;
            case ManagedReplicaLogicalOpType.UpsertRow:
                ReplayRowUpsert(connection, op.TableName, op.RowId, op.Record);
                return;
            case ManagedReplicaLogicalOpType.DeleteRow:
                ReplayRowDelete(connection, op.TableName, op.RowId, op.Record);
                return;
            default:
                throw new InvalidDataException($"A logical operation has an unknown op type {op.OpType}.");
        }
    }

    private static void ReplayHeaderOp(IManagedConnectionAdapter connection, ManagedReplicaLogicalOp op)
    {
        if (op.UserVersion is null && op.ApplicationId is null)
        {
            throw new InvalidDataException(
                "A logical update_header operation must include at least one header field.");
        }

        if (op.UserVersion is { } userVersion)
            ExecuteDdl(connection, $"PRAGMA user_version = {userVersion}");
        if (op.ApplicationId is { } applicationId)
            ExecuteDdl(connection, $"PRAGMA application_id = {applicationId}");
    }

    private static void ReplaySchemaOp(IManagedConnectionAdapter connection, ManagedReplicaLogicalOp op)
    {
        var kind = op.SchemaKind ?? throw new InvalidDataException("A logical schema operation is missing its schema kind.");
        var action = op.SchemaAction ?? throw new InvalidDataException("A logical schema operation is missing its schema action.");

        switch (action)
        {
            case ManagedReplicaLogicalSchemaAction.Create:
            case ManagedReplicaLogicalSchemaAction.Alter:
                ExecuteDdlIdempotent(connection, kind, op.SchemaName, op.Sql);
                return;
            case ManagedReplicaLogicalSchemaAction.Refresh:
                if (kind != ManagedReplicaLogicalSchemaKind.Table)
                    ExecuteDdl(connection, SchemaDropSql(kind, op.SchemaName));
                ExecuteDdlIdempotent(connection, kind, op.SchemaName, op.Sql);
                return;
            case ManagedReplicaLogicalSchemaAction.Drop:
                ExecuteDdl(connection, SchemaDropSql(kind, op.SchemaName));
                return;
            default:
                throw new InvalidDataException($"A logical schema operation has an unsupported action {action}.");
        }
    }

    private static string SchemaDropSql(ManagedReplicaLogicalSchemaKind kind, string name)
    {
        var objectKeyword = kind switch
        {
            ManagedReplicaLogicalSchemaKind.Table => "TABLE",
            ManagedReplicaLogicalSchemaKind.Index => "INDEX",
            ManagedReplicaLogicalSchemaKind.Trigger => "TRIGGER",
            ManagedReplicaLogicalSchemaKind.View => "VIEW",
            _ => throw new InvalidDataException($"A logical schema operation has an unsupported kind {kind}."),
        };
        return $"DROP {objectKeyword} IF EXISTS {QuoteIdentifier(name)}";
    }

    private static void ExecuteDdlIdempotent(
        IManagedConnectionAdapter connection,
        ManagedReplicaLogicalSchemaKind kind,
        string name,
        string sql)
    {
        if (kind != ManagedReplicaLogicalSchemaKind.Table)
        {
            if (SchemaObjectExists(connection, SchemaObjectTypeName(kind), name))
                return;
            ExecuteDdl(connection, sql);
            return;
        }

        var incomingColumns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(sql);
        if (incomingColumns is not null)
        {
            var currentColumns = GetTableColumnNames(connection, name);
            if (currentColumns.Count == 0)
            {
                ExecuteDdl(connection, sql);
                return;
            }

            foreach (var (columnName, definition) in incomingColumns)
            {
                if (currentColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
                    continue;
                ExecuteDdl(connection, $"ALTER TABLE {QuoteIdentifier(name)} ADD COLUMN {definition}");
            }

            return;
        }

        if (ManagedReplicaSchemaDdlText.TryParseAlterTableAddColumn(sql) is { } addColumn)
        {
            var currentColumns = GetTableColumnNames(connection, addColumn.TableName);
            if (!currentColumns.Contains(addColumn.ColumnName, StringComparer.OrdinalIgnoreCase))
                ExecuteDdl(connection, sql);
            return;
        }

        // Unrecognized DDL shape (e.g. ALTER TABLE RENAME/DROP COLUMN): replay directly. A second
        // replay of the identical statement is not guaranteed to be idempotent, matching upstream.
        ExecuteDdl(connection, sql);
    }

    private static string SchemaObjectTypeName(ManagedReplicaLogicalSchemaKind kind) => kind switch
    {
        ManagedReplicaLogicalSchemaKind.Index => "index",
        ManagedReplicaLogicalSchemaKind.Trigger => "trigger",
        ManagedReplicaLogicalSchemaKind.View => "view",
        _ => throw new InvalidDataException($"A logical schema operation has an unsupported kind {kind}."),
    };

    private static bool SchemaObjectExists(IManagedConnectionAdapter connection, string objectType, string name)
    {
        using var statement = connection.Prepare(
            "SELECT 1 FROM sqlite_schema WHERE type = ? AND name = ?");
        statement.Bind(1, SqlValue.Text(objectType));
        statement.Bind(2, SqlValue.Text(name));
        return statement.Step() == StatementStepResult.Row;
    }

    private static void ReplayRowUpsert(IManagedConnectionAdapter connection, string tableName, long rowId, byte[] record)
    {
        var info = GetTableColumnsInfo(connection, tableName);
        var decoded = SqliteRecordCodec.Decode(record);
        var recordColumnCount = Math.Min(decoded.Length, info.ColumnNames.Count);
        var recordColumns = info.ColumnNames.Take(recordColumnCount).ToArray();

        if (info.PkColumnIndices.Count > 0)
        {
            foreach (var pkIndex in info.PkColumnIndices)
            {
                if (pkIndex >= recordColumns.Length)
                {
                    throw new InvalidDataException(
                        $"Primary key column index {pkIndex} is outside the replayed record with {recordColumns.Length} column(s) for table '{tableName}'.");
                }
            }

            var columnList = string.Join(", ", recordColumns.Select(QuoteIdentifier));
            var placeholders = string.Join(", ", Enumerable.Repeat("?", recordColumnCount));
            var pkNames = info.PkColumnIndices.Select(i => QuoteIdentifier(recordColumns[i]));
            var updateClauses = recordColumns.Select(c => $"{QuoteIdentifier(c)} = excluded.{QuoteIdentifier(c)}");
            var sql = $"INSERT INTO {QuoteIdentifier(tableName)}({columnList}) VALUES ({placeholders}) "
                + $"ON CONFLICT({string.Join(",", pkNames)}) DO UPDATE SET {string.Join(",", updateClauses)}";

            using var statement = connection.Prepare(sql);
            for (var i = 0; i < recordColumnCount; i++)
            {
                var value = i == info.RowidAliasColumnIndex ? SqlValue.Integer(rowId) : decoded[i];
                statement.Bind(i + 1, value);
            }

            statement.Step();
            return;
        }

        var insertColumnList = string.Join(", ", recordColumns.Select(QuoteIdentifier).Append("\"rowid\""));
        var insertPlaceholders = string.Join(", ", Enumerable.Repeat("?", recordColumnCount + 1));
        var insertSql = $"INSERT OR REPLACE INTO {QuoteIdentifier(tableName)}({insertColumnList}) VALUES ({insertPlaceholders})";
        using var insertStatement = connection.Prepare(insertSql);
        for (var i = 0; i < recordColumnCount; i++)
            insertStatement.Bind(i + 1, decoded[i]);
        insertStatement.Bind(recordColumnCount + 1, SqlValue.Integer(rowId));
        insertStatement.Step();
    }

    private static void ReplayRowDelete(IManagedConnectionAdapter connection, string tableName, long rowId, byte[] keyRecord)
    {
        var info = GetTableColumnsInfo(connection, tableName);
        var useRowid = keyRecord.Length == 0;

        if (useRowid)
        {
            if (info.PkColumnIndices.Count > 0 && info.RowidAliasColumnIndex is null)
            {
                throw new InvalidDataException(
                    $"DELETE for table '{tableName}' has no primary-key projection and no before image, but its PRIMARY KEY is not the rowid; refusing rowid-based replay.");
            }

            using var statement = connection.Prepare($"DELETE FROM {QuoteIdentifier(tableName)} WHERE rowid = ?");
            statement.Bind(1, SqlValue.Integer(rowId));
            statement.Step();
            return;
        }

        if (info.PkColumnIndices.Count == 0)
        {
            throw new InvalidDataException(
                $"DELETE for table '{tableName}' supplied a primary-key projection, but the table has no declared primary key.");
        }

        var key = SqliteRecordCodec.Decode(keyRecord);
        if (key.Length != info.PkColumnIndices.Count)
        {
            throw new InvalidDataException(
                $"DELETE for table '{tableName}' supplied {key.Length} primary-key value(s) but the table has {info.PkColumnIndices.Count}.");
        }

        var predicates = string.Join(" AND ", info.PkColumnIndices.Select(i => $"{QuoteIdentifier(info.ColumnNames[i])} = ?"));
        using var deleteStatement = connection.Prepare($"DELETE FROM {QuoteIdentifier(tableName)} WHERE {predicates}");
        for (var i = 0; i < key.Length; i++)
            deleteStatement.Bind(i + 1, key[i]);
        deleteStatement.Step();
    }

    private readonly record struct TableColumnsInfo(
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<int> PkColumnIndices,
        int? RowidAliasColumnIndex);

    private static TableColumnsInfo GetTableColumnsInfo(IManagedConnectionAdapter connection, string tableName)
    {
        var columnNames = new List<string>();
        var columnTypes = new List<string>();
        var pkColumns = new List<(int Ordinal, int ColumnIndex)>();

        using var statement = connection.Prepare("SELECT cid, name, type, pk FROM pragma_table_info(?)");
        statement.Bind(1, SqlValue.Text(tableName));
        while (statement.Step() == StatementStepResult.Row)
        {
            var columnId = checked((int)statement.GetValue(0).AsInteger());
            if (columnId != columnNames.Count)
            {
                throw new InvalidDataException(
                    $"pragma_table_info returned a non-contiguous column index {columnId} for table '{tableName}'.");
            }

            var name = statement.GetValue(1).AsText();
            var type = statement.GetValue(2).Kind == SqlValueKind.Text ? statement.GetValue(2).AsText() : string.Empty;
            var pk = checked((int)statement.GetValue(3).AsInteger());
            if (pk > 0)
                pkColumns.Add((pk, columnId));

            columnNames.Add(name);
            columnTypes.Add(type);
        }

        pkColumns.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        var pkColumnIndices = pkColumns.Select(p => p.ColumnIndex).ToArray();
        int? rowidAliasColumnIndex = null;
        if (pkColumnIndices.Length == 1)
        {
            var pk = pkColumnIndices[0];
            if (pk < columnTypes.Count && columnTypes[pk].Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
                rowidAliasColumnIndex = pk;
        }

        return new TableColumnsInfo(columnNames, pkColumnIndices, rowidAliasColumnIndex);
    }

    private static List<string> GetTableColumnNames(IManagedConnectionAdapter connection, string tableName)
        => GetTableColumnsInfo(connection, tableName).ColumnNames.ToList();

    private static void ExecuteDdl(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
