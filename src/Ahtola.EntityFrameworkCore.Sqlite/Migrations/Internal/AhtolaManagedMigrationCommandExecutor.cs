using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ahtola.EntityFrameworkCore.Sqlite.Migrations.Internal;

public sealed class AhtolaManagedMigrationCommandExecutor(
    IExecutionStrategy executionStrategy,
    IRawSqlCommandBuilder rawSqlCommandBuilder,
    ICurrentDbContext currentDbContext,
    IRelationalCommandDiagnosticsLogger commandLogger)
    : MigrationCommandExecutor(executionStrategy)
{
    private const string RebuildMarker = "-- AHTOLA_REBUILD:";

    public override int ExecuteNonQuery(
        IReadOnlyList<MigrationCommand> migrationCommands,
        IRelationalConnection connection,
        MigrationExecutionState executionState,
        bool commitTransaction,
        IsolationLevel? isolationLevel = null)
    {
        var prepared = PrepareCommands(migrationCommands);
        if (!prepared.IsRebuild)
        {
            return base.ExecuteNonQuery(
                prepared.Commands,
                connection,
                executionState,
                commitTransaction,
                isolationLevel);
        }

        PrepareTransactionBoundary(connection, executionState);
        var opened = connection.Open();
        try
        {
            var foreignKeys = ReadForeignKeys(connection.DbConnection);
            try
            {
                WriteForeignKeys(connection.DbConnection, enabled: false);
                EnsureForeignKeysDisabled(connection.DbConnection);
                return base.ExecuteNonQuery(
                    prepared.Commands,
                    connection,
                    executionState,
                    commitTransaction: true,
                    isolationLevel);
            }
            finally
            {
                try
                {
                    RollBackPendingTransaction(executionState);
                }
                finally
                {
                    WriteForeignKeys(connection.DbConnection, foreignKeys);
                }
            }
        }
        finally
        {
            if (opened)
                connection.Close();
        }
    }

    public override async Task<int> ExecuteNonQueryAsync(
        IReadOnlyList<MigrationCommand> migrationCommands,
        IRelationalConnection connection,
        MigrationExecutionState executionState,
        bool commitTransaction,
        IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = PrepareCommands(migrationCommands);
        if (!prepared.IsRebuild)
        {
            return await base.ExecuteNonQueryAsync(
                    prepared.Commands,
                    connection,
                    executionState,
                    commitTransaction,
                    isolationLevel,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await PrepareTransactionBoundaryAsync(connection, executionState, cancellationToken)
            .ConfigureAwait(false);
        var opened = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var foreignKeys = await ReadForeignKeysAsync(connection.DbConnection, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await WriteForeignKeysAsync(connection.DbConnection, enabled: false, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureForeignKeysDisabledAsync(connection.DbConnection, cancellationToken)
                    .ConfigureAwait(false);
                return await base.ExecuteNonQueryAsync(
                        prepared.Commands,
                        connection,
                        executionState,
                        commitTransaction: true,
                        isolationLevel,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await RollBackPendingTransactionAsync(executionState, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await WriteForeignKeysAsync(
                            connection.DbConnection,
                            foreignKeys,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (opened)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static void PrepareTransactionBoundary(
        IRelationalConnection connection,
        MigrationExecutionState executionState)
    {
        if (executionState.Transaction is { } migrationTransaction)
        {
            migrationTransaction.Commit();
            migrationTransaction.Dispose();
            executionState.Transaction = null;
        }

        if (connection.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Managed SQLite table rebuilds cannot run inside an existing transaction because foreign key enforcement must be disabled before the rebuild transaction starts.");
        }
    }

    private static async Task PrepareTransactionBoundaryAsync(
        IRelationalConnection connection,
        MigrationExecutionState executionState,
        CancellationToken cancellationToken)
    {
        if (executionState.Transaction is { } migrationTransaction)
        {
            await migrationTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await migrationTransaction.DisposeAsync().ConfigureAwait(false);
            executionState.Transaction = null;
        }

        if (connection.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Managed SQLite table rebuilds cannot run inside an existing transaction because foreign key enforcement must be disabled before the rebuild transaction starts.");
        }
    }

    private static void EnsureForeignKeysDisabled(DbConnection connection)
    {
        if (ReadForeignKeys(connection))
        {
            throw new InvalidOperationException(
                "Managed SQLite could not disable foreign key enforcement before starting the table rebuild. An unregistered ambient transaction may be active.");
        }
    }

    private static async Task EnsureForeignKeysDisabledAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (await ReadForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Managed SQLite could not disable foreign key enforcement before starting the table rebuild. An unregistered ambient transaction may be active.");
        }
    }

    private PreparedCommands PrepareCommands(IReadOnlyList<MigrationCommand> migrationCommands)
    {
        var rebuildTables = migrationCommands
            .Select(command => TryReadRebuildMarker(command.CommandText, out var table)
                ? (RebuildTable?)table
                : null)
            .Where(table => table.HasValue)
            .Select(table => table!.Value)
            .ToArray();
        if (rebuildTables.Length == 0)
            return new PreparedCommands(migrationCommands, IsRebuild: false);

        var commands = migrationCommands
            .Where(command => !IsForeignKeysPragma(command.CommandText))
            .Select(
                command => command.TransactionSuppressed && IsHistoryInsert(command.CommandText)
                    ? new TransactionEnlistingMigrationCommand(
                        command,
                        rawSqlCommandBuilder,
                        currentDbContext.Context,
                        commandLogger)
                    : command)
            .ToArray();
        if (commands.Any(command => command.TransactionSuppressed))
        {
            throw new NotSupportedException(
                "Managed SQLite table rebuilds cannot be combined with other transaction-suppressed migration commands.");
        }

        var prepared = AddTriggerPreservationCommands(commands).ToList();
        prepared.Add(
            new ForeignKeyCheckMigrationCommand(
                rebuildTables
                    .Select(table => table.Schema ?? "main")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                rawSqlCommandBuilder,
                currentDbContext.Context,
                commandLogger));
        return new PreparedCommands(prepared, IsRebuild: true);
    }

    private static bool IsForeignKeysPragma(string commandText)
    {
        var normalized = string.Concat(commandText.Where(character => !char.IsWhiteSpace(character)))
            .TrimEnd(';');
        return normalized.Equals("PRAGMAforeign_keys=0", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("PRAGMAforeign_keys=1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoryInsert(string commandText)
        => commandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("__EFMigrationsHistory", StringComparison.Ordinal);

    private static bool ReadForeignKeys(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> ReadForeignKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void WriteForeignKeys(DbConnection connection, bool enabled)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = {(enabled ? 1 : 0)};";
        command.ExecuteNonQuery();
    }

    private static async Task WriteForeignKeysAsync(
        DbConnection connection,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = {(enabled ? 1 : 0)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RollBackPendingTransaction(MigrationExecutionState executionState)
    {
        if (executionState.Transaction is not { } transaction)
            return;

        try
        {
            transaction.Rollback();
        }
        finally
        {
            transaction.Dispose();
            executionState.Transaction = null;
        }
    }

    private static async Task RollBackPendingTransactionAsync(
        MigrationExecutionState executionState,
        CancellationToken cancellationToken)
    {
        if (executionState.Transaction is not { } transaction)
            return;

        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            executionState.Transaction = null;
        }
    }

    private static IReadOnlyList<string> ReadTriggers(
        DbConnection connection,
        IDbContextTransaction? transaction,
        RebuildTable table)
    {
        using var command = CreateTriggerQuery(connection, transaction, table);
        using var reader = command.ExecuteReader();
        var triggers = new List<string>();
        while (reader.Read())
        {
            triggers.Add(QualifyTriggerSql(reader.GetString(1), reader.GetString(0), table.Schema));
        }
        return triggers;
    }

    private static async Task<IReadOnlyList<string>> ReadTriggersAsync(
        DbConnection connection,
        IDbContextTransaction? transaction,
        RebuildTable table,
        CancellationToken cancellationToken)
    {
        await using var command = CreateTriggerQuery(connection, transaction, table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var triggers = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            triggers.Add(QualifyTriggerSql(reader.GetString(1), reader.GetString(0), table.Schema));
        }
        return triggers;
    }

    private static DbCommand CreateTriggerQuery(
        DbConnection connection,
        IDbContextTransaction? transaction,
        RebuildTable table)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction?.GetDbTransaction();
        command.CommandText =
            $"SELECT \"name\", \"sql\" FROM {QuoteIdentifier(table.Schema ?? "main")}.\"sqlite_master\" "
            + $"WHERE \"type\" = 'trigger' AND \"tbl_name\" = {QuoteLiteral(table.Table)} "
            + "AND \"sql\" IS NOT NULL ORDER BY \"name\";";
        return command;
    }

    private static IReadOnlyList<string> ReadWritableColumns(
        DbConnection connection,
        IDbContextTransaction? transaction,
        RebuildTable table)
    {
        using var command = CreateTableXInfoQuery(connection, transaction, table);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            if (reader.GetInt64(6) == 0)
                columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static async Task<IReadOnlyList<string>> ReadWritableColumnsAsync(
        DbConnection connection,
        IDbContextTransaction? transaction,
        RebuildTable table,
        CancellationToken cancellationToken)
    {
        await using var command = CreateTableXInfoQuery(connection, transaction, table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt64(6) == 0)
                columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static DbCommand CreateTableXInfoQuery(
        DbConnection connection,
        IDbContextTransaction? transaction,
        RebuildTable table)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction?.GetDbTransaction();
        command.CommandText =
            $"PRAGMA {QuoteIdentifier(table.Schema ?? "main")}.table_xinfo({QuoteLiteral(table.Table)});";
        return command;
    }

    private static IEnumerable<string> CreateTriggerValidationSql(
        RebuildTable table,
        IReadOnlyList<string> writableColumns)
    {
        if (writableColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot validate triggers for rebuilt table '{table.Table}' because it has no writable columns.");
        }

        var qualifiedTable = table.Schema is null
            ? QuoteIdentifier(table.Table)
            : QuoteIdentifier(table.Schema) + "." + QuoteIdentifier(table.Table);
        var columns = string.Join(", ", writableColumns.Select(QuoteIdentifier));
        var nulls = string.Join(", ", writableColumns.Select(_ => "NULL"));
        var firstColumn = QuoteIdentifier(writableColumns[0]);
        yield return $"INSERT INTO {qualifiedTable} ({columns}) SELECT {nulls} WHERE 0;";
        yield return $"UPDATE {qualifiedTable} SET {firstColumn} = {firstColumn} WHERE 0;";
        yield return $"DELETE FROM {qualifiedTable} WHERE 0;";
    }

    private IReadOnlyList<MigrationCommand> AddTriggerPreservationCommands(
        IReadOnlyList<MigrationCommand> migrationCommands)
    {
        var result = new List<MigrationCommand>(migrationCommands.Count);
        TriggerSnapshot? pending = null;
        TriggerSnapshot? awaitingIndexes = null;
        foreach (var migrationCommand in migrationCommands)
        {
            if (awaitingIndexes is { } awaiting)
            {
                if (IsCreateIndexForTable(migrationCommand.CommandText, awaiting.Table))
                {
                    result.Add(migrationCommand);
                    continue;
                }

                result.Add(CreateTriggerRecreatingCommand(awaiting));
                awaitingIndexes = null;
            }

            if (TryReadRebuildMarker(migrationCommand.CommandText, out var table))
            {
                if (pending is not null)
                    throw new InvalidOperationException("Managed SQLite rebuild markers overlapped.");

                pending = new TriggerSnapshot(table);
                result.Add(
                    new TriggerCapturingMigrationCommand(
                        migrationCommand,
                        pending,
                        rawSqlCommandBuilder,
                        currentDbContext.Context,
                        commandLogger));
                continue;
            }

            result.Add(migrationCommand);
            if (pending is not { } snapshot)
                continue;
            if (!IsRebuildRename(migrationCommand.CommandText))
            {
                throw new InvalidOperationException(
                    $"The managed SQLite rebuild for table '{snapshot.Table.Table}' did not place its rename immediately after the drop.");
            }

            awaitingIndexes = snapshot;
            pending = null;
        }

        if (pending is { } incomplete)
        {
            throw new InvalidOperationException(
                $"The managed SQLite rebuild for table '{incomplete.Table.Table}' did not contain a completing rename.");
        }
        if (awaitingIndexes is { } remaining)
            result.Add(CreateTriggerRecreatingCommand(remaining));

        return result;
    }

    private TriggerRecreatingMigrationCommand CreateTriggerRecreatingCommand(TriggerSnapshot snapshot)
        => new(
            snapshot,
            rawSqlCommandBuilder,
            currentDbContext.Context,
            commandLogger);

    private static bool IsCreateIndexForTable(string commandText, RebuildTable table)
        => (commandText.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase)
                || commandText.Contains("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase))
            && commandText.Contains(
                " ON " + QuoteIdentifier(table.Table) + " (",
                StringComparison.OrdinalIgnoreCase);

    private static bool TryReadRebuildMarker(string commandText, out RebuildTable table)
    {
        table = default;
        var marker = commandText.IndexOf(RebuildMarker, StringComparison.Ordinal);
        if (marker < 0)
            return false;

        var payloadStart = marker + RebuildMarker.Length;
        var payloadEnd = commandText.IndexOfAny(['\r', '\n'], payloadStart);
        var payload = commandText.AsSpan(
            payloadStart,
            payloadEnd < 0 ? commandText.Length - payloadStart : payloadEnd - payloadStart);
        var separator = payload.IndexOf(':');
        if (separator < 0)
            throw new InvalidOperationException("The managed SQLite rebuild marker is malformed.");

        var schemaToken = payload[..separator];
        table = new RebuildTable(
            DecodeMarkerValue(payload[(separator + 1)..]),
            schemaToken.SequenceEqual("-")
                ? null
                : DecodeMarkerValue(schemaToken));
        return true;
    }

    private static string DecodeMarkerValue(ReadOnlySpan<char> value)
        => Encoding.UTF8.GetString(Convert.FromHexString(value));

    private static bool IsRebuildRename(string commandText)
        => commandText.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("RENAME TO", StringComparison.OrdinalIgnoreCase);

    private static string QualifyTriggerSql(string sql, string name, string? schema)
    {
        if (schema is null || schema.Equals("main", StringComparison.OrdinalIgnoreCase))
            return sql;

        var trigger = FindSqlWord(sql, "TRIGGER");
        if (trigger < 0)
            throw new InvalidOperationException($"Stored trigger '{name}' has malformed SQL.");

        var identifierStart = SkipWhitespace(sql, trigger + "TRIGGER".Length);
        if (MatchesSqlWord(sql, identifierStart, "IF"))
        {
            identifierStart = SkipWhitespace(sql, identifierStart + 2);
            if (!MatchesSqlWord(sql, identifierStart, "NOT"))
                throw new InvalidOperationException($"Stored trigger '{name}' has malformed SQL.");
            identifierStart = SkipWhitespace(sql, identifierStart + 3);
            if (!MatchesSqlWord(sql, identifierStart, "EXISTS"))
                throw new InvalidOperationException($"Stored trigger '{name}' has malformed SQL.");
            identifierStart = SkipWhitespace(sql, identifierStart + 6);
        }

        var identifierEnd = FindIdentifierEnd(sql, identifierStart);
        if (identifierEnd <= identifierStart)
            throw new InvalidOperationException($"Stored trigger '{name}' has malformed SQL.");
        if (SkipWhitespace(sql, identifierEnd) < sql.Length
            && sql[SkipWhitespace(sql, identifierEnd)] == '.')
        {
            return sql;
        }

        return sql[..identifierStart]
            + QuoteIdentifier(schema)
            + "."
            + QuoteIdentifier(name)
            + sql[identifierEnd..];
    }

    private static int FindIdentifierEnd(string sql, int start)
    {
        if (start >= sql.Length)
            return start;

        var opening = sql[start];
        if (opening is '"' or '\'' or '`' or '[')
        {
            var closing = opening == '[' ? ']' : opening;
            var index = start + 1;
            while (index < sql.Length)
            {
                if (sql[index++] != closing)
                    continue;
                if (opening != '[' && index < sql.Length && sql[index] == closing)
                {
                    index++;
                    continue;
                }
                return index;
            }
            return start;
        }

        var end = start;
        while (end < sql.Length && !char.IsWhiteSpace(sql[end]) && sql[end] != '.')
            end++;
        return end;
    }

    private static int FindSqlWord(string sql, string word)
    {
        for (var index = 0; index <= sql.Length - word.Length; index++)
        {
            if (MatchesSqlWord(sql, index, word))
                return index;
        }
        return -1;
    }

    private static bool MatchesSqlWord(string sql, int start, string word)
        => start >= 0
            && start + word.Length <= sql.Length
            && sql.AsSpan(start, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)
            && (start == 0 || !IsIdentifierCharacter(sql[start - 1]))
            && (start + word.Length == sql.Length || !IsIdentifierCharacter(sql[start + word.Length]));

    private static int SkipWhitespace(string sql, int start)
    {
        while (start < sql.Length && char.IsWhiteSpace(sql[start]))
            start++;
        return start;
    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$';

    private static string QuoteIdentifier(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed class ForeignKeyCheckMigrationCommand : MigrationCommand
    {
        private readonly IReadOnlyList<string> _schemas;

        public ForeignKeyCheckMigrationCommand(
            IReadOnlyList<string> schemas,
            IRawSqlCommandBuilder rawSqlCommandBuilder,
            DbContext currentContext,
            IRelationalCommandDiagnosticsLogger commandLogger)
            : base(
                rawSqlCommandBuilder.Build("-- AHTOLA_FOREIGN_KEY_CHECK"),
                currentContext,
                commandLogger,
                transactionSuppressed: false)
        {
            _schemas = schemas;
        }

        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null)
        {
            foreach (var schema in _schemas)
            {
                using var command = CreateCheckCommand(connection, schema);
                using var reader = command.ExecuteReader();
                ThrowIfViolation(reader, schema);
            }
            return 0;
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var schema in _schemas)
            {
                await using var command = CreateCheckCommand(connection, schema);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    ThrowViolation(reader, schema);
            }
            return 0;
        }

        private static DbCommand CreateCheckCommand(
            IRelationalConnection connection,
            string schema)
        {
            var command = connection.DbConnection.CreateCommand();
            command.Transaction = connection.CurrentTransaction?.GetDbTransaction();
            command.CommandText = $"PRAGMA {QuoteIdentifier(schema)}.foreign_key_check;";
            return command;
        }

        private static void ThrowIfViolation(DbDataReader reader, string schema)
        {
            if (reader.Read())
                ThrowViolation(reader, schema);
        }

        private static void ThrowViolation(DbDataReader reader, string schema)
        {
            var table = reader.GetString(0);
            var rowId = reader.IsDBNull(1) ? "unknown" : reader.GetValue(1).ToString();
            var parent = reader.GetString(2);
            throw new InvalidOperationException(
                $"Managed SQLite table rebuild failed foreign key check in schema '{schema}': table '{table}', rowid '{rowId}', parent '{parent}'.");
        }
    }

    private sealed class TriggerSnapshot(RebuildTable table)
    {
        public RebuildTable Table { get; } = table;

        public IReadOnlyList<string> Sql { get; set; } = [];
    }

    private sealed class TransactionEnlistingMigrationCommand : MigrationCommand
    {
        private readonly MigrationCommand _inner;

        public TransactionEnlistingMigrationCommand(
            MigrationCommand inner,
            IRawSqlCommandBuilder rawSqlCommandBuilder,
            DbContext currentContext,
            IRelationalCommandDiagnosticsLogger commandLogger)
            : base(
                rawSqlCommandBuilder.Build(inner.CommandText),
                currentContext,
                commandLogger,
                transactionSuppressed: false)
        {
            _inner = inner;
        }

        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null)
            => _inner.ExecuteNonQuery(connection, parameterValues);

        public override Task<int> ExecuteNonQueryAsync(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default)
            => _inner.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken);
    }

    private sealed class TriggerCapturingMigrationCommand : MigrationCommand
    {
        private readonly MigrationCommand _inner;
        private readonly TriggerSnapshot _snapshot;

        public TriggerCapturingMigrationCommand(
            MigrationCommand inner,
            TriggerSnapshot snapshot,
            IRawSqlCommandBuilder rawSqlCommandBuilder,
            DbContext currentContext,
            IRelationalCommandDiagnosticsLogger commandLogger)
            : base(
                rawSqlCommandBuilder.Build(inner.CommandText),
                currentContext,
                commandLogger,
                inner.TransactionSuppressed)
        {
            _inner = inner;
            _snapshot = snapshot;
        }

        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null)
        {
            _snapshot.Sql = ReadTriggers(
                connection.DbConnection,
                connection.CurrentTransaction,
                _snapshot.Table);
            return _inner.ExecuteNonQuery(connection, parameterValues);
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default)
        {
            _snapshot.Sql = await ReadTriggersAsync(
                    connection.DbConnection,
                    connection.CurrentTransaction,
                    _snapshot.Table,
                    cancellationToken)
                .ConfigureAwait(false);
            return await _inner.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class TriggerRecreatingMigrationCommand : MigrationCommand
    {
        private readonly TriggerSnapshot _snapshot;
        private readonly IRawSqlCommandBuilder _rawSqlCommandBuilder;
        private readonly DbContext _currentContext;
        private readonly IRelationalCommandDiagnosticsLogger _commandLogger;

        public TriggerRecreatingMigrationCommand(
            TriggerSnapshot snapshot,
            IRawSqlCommandBuilder rawSqlCommandBuilder,
            DbContext currentContext,
            IRelationalCommandDiagnosticsLogger commandLogger)
            : base(
                rawSqlCommandBuilder.Build("-- AHTOLA_RECREATE_TRIGGERS"),
                currentContext,
                commandLogger,
                transactionSuppressed: false)
        {
            _snapshot = snapshot;
            _rawSqlCommandBuilder = rawSqlCommandBuilder;
            _currentContext = currentContext;
            _commandLogger = commandLogger;
        }

        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null)
        {
            var affected = 0;
            foreach (var sql in _snapshot.Sql)
            {
                affected += CreateCommand(sql).ExecuteNonQuery(connection, parameterValues);
            }
            if (_snapshot.Sql.Count != 0)
            {
                var columns = ReadWritableColumns(
                    connection.DbConnection,
                    connection.CurrentTransaction,
                    _snapshot.Table);
                foreach (var sql in CreateTriggerValidationSql(_snapshot.Table, columns))
                    affected += CreateCommand(sql).ExecuteNonQuery(connection, parameterValues);
            }
            return affected;
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default)
        {
            var affected = 0;
            foreach (var sql in _snapshot.Sql)
            {
                affected += await CreateCommand(sql)
                    .ExecuteNonQueryAsync(connection, parameterValues, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (_snapshot.Sql.Count != 0)
            {
                var columns = await ReadWritableColumnsAsync(
                        connection.DbConnection,
                        connection.CurrentTransaction,
                        _snapshot.Table,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var sql in CreateTriggerValidationSql(_snapshot.Table, columns))
                {
                    affected += await CreateCommand(sql)
                        .ExecuteNonQueryAsync(connection, parameterValues, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            return affected;
        }

        private MigrationCommand CreateCommand(string sql)
            => new(
                _rawSqlCommandBuilder.Build(sql),
                _currentContext,
                _commandLogger,
                transactionSuppressed: false);
    }

    private readonly record struct PreparedCommands(
        IReadOnlyList<MigrationCommand> Commands,
        bool IsRebuild);

    private readonly record struct RebuildTable(string Table, string? Schema);
}
