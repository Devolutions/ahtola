using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;

namespace Ahtola.Core;

internal enum ChangeDataCaptureMode
{
    Id,
    Before,
    After,
    Full,
}

internal enum ChangeDataCaptureVersion
{
    V1 = 1,
    V2 = 2,
}

internal sealed record ChangeDataCaptureConfiguration(
    ChangeDataCaptureMode Mode,
    string Table,
    ChangeDataCaptureVersion Version)
{
    internal const string DefaultTableName = "turso_cdc";
    internal const string VersionTableName = "turso_cdc_version";

    internal bool HasBefore => Mode is ChangeDataCaptureMode.Before or ChangeDataCaptureMode.Full;

    internal bool HasAfter => Mode is ChangeDataCaptureMode.After or ChangeDataCaptureMode.Full;

    internal bool HasUpdates => Mode == ChangeDataCaptureMode.Full;

    internal string ModeName => Mode switch
    {
        ChangeDataCaptureMode.Id => "id",
        ChangeDataCaptureMode.Before => "before",
        ChangeDataCaptureMode.After => "after",
        ChangeDataCaptureMode.Full => "full",
        _ => throw new InvalidOperationException($"Unknown CDC mode {Mode}."),
    };

    internal string VersionName => Version switch
    {
        ChangeDataCaptureVersion.V1 => "v1",
        ChangeDataCaptureVersion.V2 => "v2",
        _ => throw new InvalidOperationException($"Unknown CDC version {Version}."),
    };

    internal static ChangeDataCaptureConfiguration? Parse(
        string value,
        ChangeDataCaptureVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(',');
        var modeText = separator < 0 ? value : value[..separator];
        var table = separator < 0 ? DefaultTableName : value[(separator + 1)..];
        if (modeText.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(table)
            || ManagedSchemaName.TrySplit(table, out _, out _))
        {
            throw InvalidValue();
        }

        var mode = modeText.ToLowerInvariant() switch
        {
            "id" => ChangeDataCaptureMode.Id,
            "before" => ChangeDataCaptureMode.Before,
            "after" => ChangeDataCaptureMode.After,
            "full" => ChangeDataCaptureMode.Full,
            _ => throw InvalidValue(),
        };

        return new ChangeDataCaptureConfiguration(mode, table, version);
    }

    private static EmbeddedSqlException InvalidValue()
        => new(
            "unexpected pragma value: expected '<mode>' or '<mode>,<cdc-table-name>' parameter where mode is one of off|id|before|after|full");
}

internal readonly record struct ChangeDataCaptureTransactionSnapshot(
    long TransactionId,
    bool HasCapturedRows);

/// <summary>
/// Connection-owned Turso CDC state. Captured records are appended directly to
/// the current catalog's ordinary table, so existing statement/savepoint/WAL
/// rollback and concurrent-MVCC commit paths remain the source of atomicity.
/// </summary>
internal sealed class ChangeDataCaptureSession(ChangeDataCaptureConfiguration configuration)
{
    private readonly ChangeDataCaptureConfiguration _configuration = configuration;
    private long _transactionId = -1;
    private bool _hasCapturedRows;
    private bool _statementEligible;

    internal ChangeDataCaptureConfiguration Configuration => _configuration;

    internal void BeginStatement(ParsedStatement statement)
        => _statementEligible = IsCaptureableStatement(statement);

    internal void StartTransaction()
    {
        _transactionId = -1;
        _hasCapturedRows = false;
        _statementEligible = false;
    }

    internal void ResetTransaction()
    {
        _transactionId = -1;
        _hasCapturedRows = false;
        _statementEligible = false;
    }

    internal ChangeDataCaptureTransactionSnapshot Snapshot()
        => new(_transactionId, _hasCapturedRows);

    internal ChangeDataCaptureSession Reconfigure(ChangeDataCaptureConfiguration configuration)
        => new ChangeDataCaptureSession(configuration)
        {
            _transactionId = _transactionId,
            _hasCapturedRows = _hasCapturedRows,
            _statementEligible = _statementEligible,
        };

    internal void Restore(ChangeDataCaptureTransactionSnapshot snapshot)
    {
        _transactionId = snapshot.TransactionId;
        _hasCapturedRows = snapshot.HasCapturedRows;
    }

    internal void RecordRow(
        EmbeddedDatabase.QueryContext context,
        SqliteChangeOperation operation,
        string tableName,
        EmbeddedTable table,
        long rowId,
        SqlValue[]? before,
        SqlValue[]? after)
    {
        if (IsExcludedTable(tableName))
            return;
        if (!table.HasRowid)
            throw new EmbeddedSqlException("CDC does not support WITHOUT ROWID tables");

        after ??= TryFindRow(table, rowId);
        var beforeRecord = _configuration.HasBefore
            && operation is SqliteChangeOperation.Update or SqliteChangeOperation.Delete
            && before is not null
                ? SqlValue.Blob(SqliteRecordCodec.Encode(before))
                : SqlValue.Null;
        var afterRecord = _configuration.HasAfter
            && operation is SqliteChangeOperation.Insert or SqliteChangeOperation.Update
            && after is not null
                ? SqlValue.Blob(SqliteRecordCodec.Encode(after))
                : SqlValue.Null;
        var updates = _configuration.HasUpdates
            && operation == SqliteChangeOperation.Update
            && before is not null
            && after is not null
                ? SqlValue.Blob(EncodeUpdateRecord(before, after))
                : SqlValue.Null;
        Append(
            context,
            ChangeType(operation),
            SqlValue.Text(tableName),
            SqlValue.Integer(rowId),
            beforeRecord,
            afterRecord,
            updates);
    }

    internal void RecordSchemaChanges(
        EmbeddedDatabase.QueryContext context,
        EmbeddedDatabase.SchemaCatalog before,
        EmbeddedDatabase.SchemaCatalog after,
        ParsedStatement statement)
    {
        var oldRows = EnumerateSchemaRows(before).ToDictionary(row => row.Key, StringComparer.Ordinal);
        var newRows = EnumerateSchemaRows(after).ToDictionary(row => row.Key, StringComparer.Ordinal);
        foreach (var key in oldRows.Keys.Union(newRows.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!ShouldCaptureSchemaKey(statement, key))
                continue;
            oldRows.TryGetValue(key, out var oldRow);
            newRows.TryGetValue(key, out var newRow);
            if (oldRow.Row is not null
                && newRow.Row is not null
                && oldRow.Row.SequenceEqual(newRow.Row))
            {
                continue;
            }

            if (oldRow.Row is null)
            {
                Append(
                    context,
                    1,
                    SqlValue.Text("sqlite_schema"),
                    SqlValue.Integer(newRow.RowId),
                    SqlValue.Null,
                    EncodeAfter(newRow.Row!),
                    SqlValue.Null);
            }
            else if (newRow.Row is null)
            {
                Append(
                    context,
                    -1,
                    SqlValue.Text("sqlite_schema"),
                    SqlValue.Integer(oldRow.RowId),
                    EncodeBefore(oldRow.Row),
                    SqlValue.Null,
                    SqlValue.Null);
            }
            else
            {
                Append(
                    context,
                    0,
                    SqlValue.Text("sqlite_schema"),
                    SqlValue.Integer(newRow.RowId),
                    EncodeBefore(oldRow.Row),
                    EncodeAfter(newRow.Row),
                    EncodeUpdates(oldRow.Row, newRow.Row));
            }
        }
    }

    internal bool CompleteAutocommit(EmbeddedDatabase.QueryContext context)
    {
        if (!_statementEligible || _configuration.Version != ChangeDataCaptureVersion.V2)
            return false;

        Append(context, 2, SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null);
        ResetTransaction();
        return true;
    }

    internal bool CompleteExplicitTransaction(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        MvStore? concurrentStore,
        MvccTxId? concurrentTxId)
    {
        if (_configuration.Version != ChangeDataCaptureVersion.V2 || !_hasCapturedRows)
            return false;

        Append(tables, concurrentStore, concurrentTxId, 2, SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null);
        ResetTransaction();
        return true;
    }

    private void Append(
        EmbeddedDatabase.QueryContext context,
        long changeType,
        SqlValue tableName,
        SqlValue id,
        SqlValue before,
        SqlValue after,
        SqlValue updates)
        => Append(
            context.Tables,
            context.ConcurrentMvStore,
            context.ConcurrentMvccTxId,
            changeType,
            tableName,
            id,
            before,
            after,
            updates);

    private void Append(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        MvStore? concurrentStore,
        MvccTxId? concurrentTxId,
        long changeType,
        SqlValue tableName,
        SqlValue id,
        SqlValue before,
        SqlValue after,
        SqlValue updates)
    {
        if (!tables.TryGetValue(_configuration.Table, out var cdcTable))
            throw new EmbeddedSqlException($"no such table: {_configuration.Table}");
        if (!cdcTable.HasRowid || !cdcTable.IsAutoIncrement)
            throw new EmbeddedSqlException($"CDC table '{_configuration.Table}' has an unsupported schema");

        var changeId = AllocateChangeId(
            tables,
            cdcTable,
            concurrentStore,
            concurrentTxId);
        var transactionId = _configuration.Version == ChangeDataCaptureVersion.V2
            ? GetOrSetTransactionId(changeId)
            : -1;
        SqlValue[] row = _configuration.Version switch
        {
            ChangeDataCaptureVersion.V1 =>
            [
                SqlValue.Null,
                SqlValue.Integer(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                SqlValue.Integer(changeType),
                tableName,
                id,
                before,
                after,
                updates,
            ],
            ChangeDataCaptureVersion.V2 =>
            [
                SqlValue.Null,
                SqlValue.Integer(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                SqlValue.Integer(transactionId),
                SqlValue.Integer(changeType),
                tableName,
                id,
                before,
                after,
                updates,
            ],
            _ => throw new InvalidOperationException($"Unknown CDC version {_configuration.Version}."),
        };
        if (cdcTable.ColumnDefinitions.Length != row.Length)
            throw new EmbeddedSqlException($"CDC table '{_configuration.Table}' has an unsupported schema");

        if (cdcTable.RowidAliasColumnIndex >= 0)
            row[cdcTable.RowidAliasColumnIndex] = SqlValue.Integer(changeId);
        cdcTable.Rows.Add(row);
        cdcTable.RowIds.Add(changeId);
        if (concurrentStore is { } store && concurrentTxId is { } txId)
        {
            store.Insert(
                txId,
                new MvccRowId(
                    store.GetOrCreateTableId(txId, _configuration.Table),
                    changeId),
                row);
        }

        if (changeType != 2)
            _hasCapturedRows = true;
    }

    private long AllocateChangeId(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        EmbeddedTable cdcTable,
        MvStore? concurrentStore,
        MvccTxId? concurrentTxId)
    {
        var allocator = new EmbeddedDatabase.AutoIncrementStatementState();
        var tracker = allocator.GetTracker(_configuration.Table, cdcTable, tables);
        var largest = cdcTable.RowIds.Count == 0 ? 0 : cdcTable.RowIds.Max();
        long changeId;
        if (concurrentStore is { } store && concurrentTxId is { } txId)
        {
            changeId = store.AllocateRowId(
                store.GetOrCreateTableId(txId, _configuration.Table),
                minimumExclusive: largest);
            tracker.Observe(changeId);
        }
        else
        {
            changeId = tracker.NextRowId(cdcTable.RowIds.Count != 0, largest);
        }

        _ = allocator.Commit(tables);
        return changeId;
    }

    private long GetOrSetTransactionId(long candidate)
    {
        if (_transactionId < 0)
            _transactionId = candidate;
        return _transactionId;
    }

    private SqlValue EncodeBefore(IReadOnlyList<SqlValue> row)
        => _configuration.HasBefore ? SqlValue.Blob(SqliteRecordCodec.Encode(row)) : SqlValue.Null;

    private SqlValue EncodeAfter(IReadOnlyList<SqlValue> row)
        => _configuration.HasAfter ? SqlValue.Blob(SqliteRecordCodec.Encode(row)) : SqlValue.Null;

    private SqlValue EncodeUpdates(IReadOnlyList<SqlValue> before, IReadOnlyList<SqlValue> after)
        => _configuration.HasUpdates
            ? SqlValue.Blob(EncodeUpdateRecord(before, after))
            : SqlValue.Null;

    private bool IsCaptureableStatement(ParsedStatement statement)
    {
        var target = TryGetTargetName(statement);
        if (target is not null && IsExcludedTable(target))
            return false;

        return statement is InsertStatement or UpdateStatement or DeleteStatement or WithDmlStatement
            || EmbeddedDatabase.MayChangeSchema(statement);
    }

    private static bool ShouldCaptureSchemaKey(ParsedStatement statement, string key)
    {
        return statement switch
        {
            DropTableStatement drop => key.Equals("table:" + drop.Name, StringComparison.OrdinalIgnoreCase),
            CreateTableStatement create => key.Equals("table:" + create.Name, StringComparison.OrdinalIgnoreCase),
            CreateVirtualTableStatement create => key.Equals("table:" + create.Name, StringComparison.OrdinalIgnoreCase),
            CreateTableAsSelectStatement create => key.Equals("table:" + create.Name, StringComparison.OrdinalIgnoreCase),
            DropIndexStatement drop => key.Equals("index:" + drop.Name, StringComparison.OrdinalIgnoreCase),
            CreateIndexStatement create => key.Equals("index:" + create.Name, StringComparison.OrdinalIgnoreCase),
            DropViewStatement drop => key.Equals("view:" + drop.Name, StringComparison.OrdinalIgnoreCase),
            CreateViewStatement create => key.Equals("view:" + create.Name, StringComparison.OrdinalIgnoreCase),
            DropTriggerStatement drop => key.Equals("trigger:" + drop.Name, StringComparison.OrdinalIgnoreCase),
            CreateTriggerStatement create => key.Equals("trigger:" + create.Name, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private bool IsExcludedTable(string table)
        => string.Equals(table, _configuration.Table, StringComparison.OrdinalIgnoreCase)
            || string.Equals(table, ChangeDataCaptureConfiguration.VersionTableName, StringComparison.OrdinalIgnoreCase);

    private static string? TryGetTargetName(ParsedStatement statement)
    {
        return statement switch
        {
            InsertStatement insert => insert.TableName,
            UpdateStatement update => update.TableName,
            DeleteStatement delete => delete.TableName,
            WithDmlStatement with => TryGetTargetName(with.Dml),
            CreateTableStatement create => create.Name,
            CreateVirtualTableStatement create => create.Name,
            CreateTableAsSelectStatement create => create.Name,
            DropTableStatement drop => drop.Name,
            CreateIndexStatement create => create.TableName,
            DropIndexStatement drop => drop.Name,
            CreateViewStatement create => create.Name,
            DropViewStatement drop => drop.Name,
            CreateTriggerStatement create => create.TableName,
            DropTriggerStatement drop => drop.Name,
            AlterTableAddColumnStatement alter => alter.TableName,
            AlterTableRenameStatement alter => alter.TableName,
            AlterTableRenameColumnStatement alter => alter.TableName,
            AlterTableAlterColumnStatement alter => alter.TableName,
            AlterTableDropColumnStatement alter => alter.TableName,
            _ => null,
        };
    }

    private static SqlValue[]? TryFindRow(EmbeddedTable table, long rowId)
    {
        var index = table.RowIds.IndexOf(rowId);
        return index >= 0 && index < table.Rows.Count ? table.Rows[index].ToArray() : null;
    }

    private static long ChangeType(SqliteChangeOperation operation) => operation switch
    {
        SqliteChangeOperation.Insert => 1,
        SqliteChangeOperation.Update => 0,
        SqliteChangeOperation.Delete => -1,
        _ => throw new InvalidOperationException($"Unknown CDC operation {operation}."),
    };

    private static byte[] EncodeUpdateRecord(IReadOnlyList<SqlValue> before, IReadOnlyList<SqlValue> after)
    {
        var count = Math.Min(before.Count, after.Count);
        var values = new SqlValue[count * 2];
        for (var index = 0; index < count; index++)
        {
            var changed = !before[index].Equals(after[index]);
            values[index] = SqlValue.Integer(changed ? 1 : 0);
            values[count + index] = changed ? after[index] : SqlValue.Null;
        }

        return SqliteRecordCodec.Encode(values);
    }

    private IEnumerable<SchemaRow> EnumerateSchemaRows(EmbeddedDatabase.SchemaCatalog catalog)
    {
        long rowId = 0;
        foreach (var table in catalog.Tables)
        {
            var tableIsCapturable = !IsExcludedSchemaTable(table.Key);
            var tableRowId = ++rowId;

            if (tableIsCapturable)
            {
                yield return new SchemaRow(
                    "table:" + table.Key,
                    tableRowId,
                    [
                        SqlValue.Text("table"),
                        SqlValue.Text(table.Key),
                        SqlValue.Text(table.Key),
                        SqlValue.Integer(0),
                        SqlValue.Text(table.Value.Sql ?? EmbeddedDatabase.BuildCreateTableSql(table.Key, table.Value)),
                    ]);
            }
            foreach (var index in table.Value.Indexes
                         .Where(index => index.Origin == EmbeddedIndexOrigin.Explicit)
                         .OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase))
            {
                var indexRowId = ++rowId;
                if (!tableIsCapturable)
                    continue;
                yield return new SchemaRow(
                    "index:" + index.Name,
                    indexRowId,
                    [
                        SqlValue.Text("index"),
                        SqlValue.Text(index.Name),
                        SqlValue.Text(table.Key),
                        SqlValue.Integer(0),
                        SqlValue.Text(index.Sql ?? string.Empty),
                    ]);
            }
        }

        foreach (var view in catalog.Views.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SchemaRow(
                "view:" + view.Key,
                ++rowId,
                [
                    SqlValue.Text("view"),
                    SqlValue.Text(view.Key),
                    SqlValue.Text(view.Key),
                    SqlValue.Integer(0),
                    SqlValue.Text(view.Value.Sql),
                ]);
        }

        foreach (var trigger in catalog.Triggers
                     .OrderBy(entry => entry.Value.DeclarationOrder)
                     .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SchemaRow(
                "trigger:" + trigger.Key,
                ++rowId,
                [
                    SqlValue.Text("trigger"),
                    SqlValue.Text(trigger.Key),
                    SqlValue.Text(trigger.Value.TableName),
                    SqlValue.Integer(0),
                    SqlValue.Text(trigger.Value.Sql),
                ]);
        }
    }

    private bool IsExcludedSchemaTable(string tableName)
        => IsExcludedTable(tableName)
            || tableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
            || EmbeddedDatabase.IsAutoIncrementSequenceBackingTable(tableName);

    private readonly record struct SchemaRow(string Key, long RowId, SqlValue[]? Row);
}

public sealed partial class EmbeddedConnection
{
    private ExecutionResult ExecutePragmaCaptureDataChangesConnection(
        PragmaCaptureDataChangesConnectionStatement statement)
    {
        if (statement.Schema is not null
            && !statement.Schema.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException("CDC tables must be in the main database");
        }

        if (statement.Value is null)
        {
            return _changeDataCapture is { } session
                ? new ExecutionResult(
                    ["capture_data_changes_conn", "table_name", "version"],
                    [[
                        SqlValue.Text(session.Configuration.ModeName),
                        SqlValue.Text(session.Configuration.Table),
                        SqlValue.Text(session.Configuration.VersionName),
                    ]],
                    0)
                : new ExecutionResult(
                    ["capture_data_changes_conn", "table_name", "version"],
                    [[SqlValue.Text("off"), SqlValue.Null, SqlValue.Null]],
                    0);
        }

        var requested = ChangeDataCaptureConfiguration.Parse(
            statement.Value,
            ChangeDataCaptureVersion.V2);
        if (requested is null)
        {
            _changeDataCapture = null;
            return ExecutionResult.Empty;
        }

        if (_changeDataCapture is { } active)
        {
            _changeDataCapture = active.Reconfigure(
                requested with { Version = active.Configuration.Version });
            return ExecutionResult.Empty;
        }

        var ownsProvisioningTransaction = _transactionDatabases is null;
        if (ownsProvisioningTransaction)
            BeginTransaction(openedBySavepoint: false, TransactionMode.Deferred);

        try
        {
            var beforeProvisioning = CurrentMainCatalog();
            var cdcTableExisted = beforeProvisioning.Tables.ContainsKey(requested.Table);
            ExecuteCdcProvisioningSql(BuildCdcCreateTableSql(requested));
            ExecuteCdcProvisioningSql(
                $"CREATE TABLE IF NOT EXISTS {SqlIdentifierFormatter.Quote(ChangeDataCaptureConfiguration.VersionTableName)} " +
                "(table_name TEXT PRIMARY KEY, version TEXT NOT NULL)");

            var catalog = CurrentMainCatalog();
            var actualVersion = ReadCdcVersion(catalog, requested.Table);
            if (actualVersion is null)
            {
                var initialVersion = cdcTableExisted
                    ? ChangeDataCaptureVersion.V1
                    : ChangeDataCaptureVersion.V2;
                ExecuteCdcProvisioningSql(
                    $"INSERT OR IGNORE INTO {SqlIdentifierFormatter.Quote(ChangeDataCaptureConfiguration.VersionTableName)} " +
                    $"(table_name, version) VALUES ({SqlLiteral(requested.Table)}, {SqlLiteral(VersionName(initialVersion))})");
                actualVersion = ReadCdcVersion(CurrentMainCatalog(), requested.Table)
                    ?? throw new EmbeddedSqlException("CDC version initialization did not persist");
            }

            if (ownsProvisioningTransaction)
                CommitTransaction();
            _changeDataCapture = new ChangeDataCaptureSession(requested with { Version = actualVersion.Value });
            return ExecutionResult.Empty;
        }
        catch
        {
            if (ownsProvisioningTransaction && _transactionDatabases is not null)
                ResetTransactionState();
            throw;
        }
    }

    private EmbeddedDatabase.SchemaCatalog CurrentMainCatalog()
        => GetTransactionState(_database)?.Catalog ?? _database.SnapshotCatalog();

    private void ExecuteCdcProvisioningSql(string sql)
    {
        var previousSuppression = _suppressUpdateHook;
        _suppressUpdateHook = true;
        try
        {
            Execute(
                SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
                [],
                CancellationToken.None);
        }
        finally
        {
            _suppressUpdateHook = previousSuppression;
        }
    }

    private static string BuildCdcCreateTableSql(ChangeDataCaptureConfiguration configuration)
    {
        var columns = configuration.Version switch
        {
            ChangeDataCaptureVersion.V1 =>
                "change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB",
            ChangeDataCaptureVersion.V2 =>
                "change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, change_txn_id INTEGER, change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB",
            _ => throw new InvalidOperationException($"Unknown CDC version {configuration.Version}."),
        };
        return $"CREATE TABLE IF NOT EXISTS {SqlIdentifierFormatter.Quote(configuration.Table)} ({columns})";
    }

    private static ChangeDataCaptureVersion? ReadCdcVersion(
        EmbeddedDatabase.SchemaCatalog catalog,
        string tableName)
    {
        if (!catalog.Tables.TryGetValue(ChangeDataCaptureConfiguration.VersionTableName, out var versions))
            return null;

        for (var index = 0; index < versions.Rows.Count; index++)
        {
            var row = versions.Rows[index];
            if (row.Length < 2
                || row[0].Kind != SqlValueKind.Text
                || !string.Equals(row[0].AsText(), tableName, StringComparison.Ordinal))
            {
                continue;
            }

            if (row[1].Kind != SqlValueKind.Text)
                throw new EmbeddedSqlException("unexpected CDC version");
            return row[1].AsText() switch
            {
                "v1" => ChangeDataCaptureVersion.V1,
                "v2" => ChangeDataCaptureVersion.V2,
                _ => throw new EmbeddedSqlException($"unexpected CDC version: {row[1].AsText()}"),
            };
        }

        return null;
    }

    private static string VersionName(ChangeDataCaptureVersion version)
        => version == ChangeDataCaptureVersion.V1 ? "v1" : "v2";

    private static string SqlLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
