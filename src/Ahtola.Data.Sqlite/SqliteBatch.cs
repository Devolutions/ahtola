using System.Data;
using System.Data.Common;
using System.Net.Http;
using Ahtola;

namespace Ahtola.Data.Sqlite;

public sealed class SqliteBatch : DbBatch
{
    private readonly SqliteBatchCommandCollection _batchCommands = new();
    private readonly object _executionSync = new();
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private BatchExecutionControl? _activeExecution;
    private int _timeout = 30;
    private bool _disposed;

    public SqliteBatch()
    {
    }

    public SqliteBatch(SqliteConnection connection)
    {
        _connection = connection;
        _transaction = connection.Transaction;
        _timeout = connection.DefaultTimeout;
    }

    protected override DbBatchCommandCollection DbBatchCommands => _batchCommands;

    public new SqliteBatchCommandCollection BatchCommands => _batchCommands;

    public new SqliteConnection? Connection
    {
        get => _connection;
        set => DbConnection = value;
    }

    public new SqliteTransaction? Transaction
    {
        get => _transaction;
        set => DbTransaction = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is null)
            {
                _connection = null;
                return;
            }

            _connection = value as SqliteConnection
                          ?? throw new ArgumentException("Connection must be a SqliteConnection.", nameof(value));
            _transaction ??= _connection.Transaction;
            _timeout = _connection.DefaultTimeout;
        }
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (value is null)
            {
                _transaction = null;
                return;
            }

            _transaction = value as SqliteTransaction
                           ?? throw new ArgumentException("Transaction must be a SqliteTransaction.", nameof(value));
        }
    }

    public override int Timeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _timeout = value;
        }
    }

    public override void Cancel()
    {
        BatchExecutionControl? execution;
        lock (_executionSync)
            execution = _activeExecution;

        execution?.Cancel();
    }

    public override int ExecuteNonQuery()
    {
        var connection = ValidateBatch();
        if (UsesRemoteBatch(connection))
            return ExecuteRemoteNonQueryAsync(connection, CancellationToken.None).GetAwaiter().GetResult();

        var (commands, execution) = CreateExecution(connection, CancellationToken.None);
        return SequentialBatchExecutor.ExecuteNonQuery(commands, execution);
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = ValidateBatch();
        if (UsesRemoteBatch(connection))
            return await ExecuteRemoteNonQueryAsync(connection, cancellationToken).ConfigureAwait(false);

        var (commands, execution) = CreateExecution(connection, cancellationToken);
        return await SequentialBatchExecutor
            .ExecuteNonQueryAsync(commands, execution)
            .ConfigureAwait(false);
    }

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader(CommandBehavior.Default);
        while (reader.FieldCount == 0 && reader.NextResult())
        {
        }

        return reader.Read() ? reader.GetValue(0) : null;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        while (reader.FieldCount == 0
               && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? reader.GetValue(0)
            : null;
    }

    public override void Prepare()
    {
        ValidateBatch();
    }

    public override Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        Prepare();
        return Task.CompletedTask;
    }

    protected override DbBatchCommand CreateDbBatchCommand() => new SqliteBatchCommand();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var connection = ValidateBatch();
        if (UsesRemoteBatch(connection))
            return ExecuteRemoteReaderAsync(connection, behavior, CancellationToken.None).GetAwaiter().GetResult();

        var (commands, execution) = CreateExecution(connection, CancellationToken.None);
        return SequentialBatchExecutor.ExecuteReader(commands, execution, behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = ValidateBatch();
        if (UsesRemoteBatch(connection))
            return await ExecuteRemoteReaderAsync(connection, behavior, cancellationToken).ConfigureAwait(false);

        var (commands, execution) = CreateExecution(connection, cancellationToken);
        return await SequentialBatchExecutor
            .ExecuteReaderAsync(commands, execution, behavior)
            .ConfigureAwait(false);
    }

    public override void Dispose()
    {
        BatchExecutionControl? execution;
        lock (_executionSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            execution = _activeExecution;
        }

        execution?.Cancel();
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection ValidateBatch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = _connection
            ?? throw new InvalidOperationException("Connection must be set before executing a batch.");
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection("ExecuteBatch"));
        if (!connection.CanCreateBatch)
            throw new NotSupportedException("Batch execution is not supported by this embedded replica connection.");
        if (_transaction is { IsCompleted: true } or { WasRolledBackExternally: true })
            throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
        if (_transaction is not null && !ReferenceEquals(_transaction.Connection, connection))
            throw new InvalidOperationException(Properties.Resources.TransactionConnectionMismatch);
        if (connection.Transaction is not null && !ReferenceEquals(_transaction, connection.Transaction))
            throw new InvalidOperationException(Properties.Resources.TransactionRequired);
        if (_batchCommands.Count == 0)
            throw new InvalidOperationException("Batch must contain at least one command.");

        foreach (var command in _batchCommands.AsReadOnly())
        {
            if (string.IsNullOrWhiteSpace(command.CommandText))
                throw new InvalidOperationException("Batch command text must be set before executing a batch.");
            if (command.CommandType != CommandType.Text)
                throw new ArgumentException(Properties.Resources.InvalidCommandType(command.CommandType));
        }

        return connection;
    }

    private static bool UsesRemoteBatch(SqliteConnection connection)
        => connection.Capabilities.Mode == AhtolaConnectionMode.RemoteHrana;

    private async Task<int> ExecuteRemoteNonQueryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var execution = BeginExecution(cancellationToken);
        using var batch = CreateRemoteBatch(connection);
        try
        {
            var recordsAffected = await batch.ExecuteNonQueryAsync(execution.Token).ConfigureAwait(false);
            CopyRemoteRecordsAffected(batch);
            return recordsAffected;
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            throw SqliteRemoteExceptionClassifier.From(ex, SqliteCommand.ToSqliteException(ex));
        }
    }

    private async Task<DbDataReader> ExecuteRemoteReaderAsync(
        SqliteConnection connection,
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        using var execution = BeginExecution(cancellationToken);
        var batch = CreateRemoteBatch(connection);
        try
        {
            var reader = await batch.ExecuteReaderAsync(behavior, execution.Token).ConfigureAwait(false);
            CopyRemoteRecordsAffected(batch);
            var command = new SqliteCommand(connection);
            return new SqliteDataReader(
                command,
                reader,
                behavior,
                () =>
                {
                    batch.Dispose();
                    command.Dispose();
                },
                // Every explicitly-added DbBatchCommand must be exposed 1:1, including a plain
                // write with no RETURNING clause (0 columns) — this is the raw DbBatch contract
                // callers rely on (mirrored by the local/replica SequentialBatchDataReader), and
                // is why the general SqliteCommand skip-to-first-column-result behavior must not
                // apply here.
                skipToFirstColumnResult: false);
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            batch.Dispose();
            throw SqliteRemoteExceptionClassifier.From(ex, SqliteCommand.ToSqliteException(ex));
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    private AhtolaBatch CreateRemoteBatch(SqliteConnection connection)
    {
        var batch = new AhtolaBatch(connection.AhtolaConnection!)
        {
            Timeout = Timeout,
        };
        var index = 0;
        foreach (var batchCommand in _batchCommands.AsReadOnly())
        {
            var remoteCommand = new AhtolaBatchCommand(batchCommand.CommandText);
            CopyParameters(batchCommand.Parameters, remoteCommand.Parameters);
            if (index > 0)
                remoteCommand.RemoteCondition = AhtolaRemoteBatchCondition.StepSucceeded(index - 1);
            batch.BatchCommands.Add(remoteCommand);
            index++;
        }

        return batch;
    }

    private void CopyRemoteRecordsAffected(AhtolaBatch batch)
    {
        var remoteCommands = batch.BatchCommands.AsReadOnly();
        for (var i = 0; i < remoteCommands.Count; i++)
            _batchCommands.AsReadOnly()[i].SetRecordsAffected(remoteCommands[i].RecordsAffected);
    }

    private (
        IReadOnlyList<SequentialBatchCommand> Commands,
        BatchExecutionControl Execution) CreateExecution(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var execution = BeginExecution(cancellationToken);
        try
        {
            return (CreateCommands(connection), execution);
        }
        catch
        {
            execution.Dispose();
            throw;
        }
    }

    private IReadOnlyList<SequentialBatchCommand> CreateCommands(SqliteConnection connection)
    {
        var commands = new List<SequentialBatchCommand>(_batchCommands.Count);
        try
        {
            foreach (var batchCommand in _batchCommands.AsReadOnly())
            {
                var command = new SqliteCommand(batchCommand.CommandText, connection)
                {
                    CommandTimeout = Timeout,
                };
                CopyParameters(batchCommand.Parameters, command.Parameters);
                commands.Add(new SequentialBatchCommand(
                    command,
                    batchCommand.SetRecordsAffected,
                    () => GetActiveTransaction(connection)));
            }

            return commands;
        }
        catch
        {
            foreach (var command in commands)
                command.Dispose();
            throw;
        }
    }

    private BatchExecutionControl BeginExecution(CancellationToken cancellationToken)
    {
        lock (_executionSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeExecution is not null)
                throw new InvalidOperationException("The batch is already executing.");

            var execution = new BatchExecutionControl(cancellationToken, EndExecution);
            _activeExecution = execution;
            return execution;
        }
    }

    private void EndExecution(BatchExecutionControl execution)
    {
        lock (_executionSync)
        {
            if (ReferenceEquals(_activeExecution, execution))
                _activeExecution = null;
        }
    }

    private static void CopyParameters(
        SqliteParameterCollection source,
        SqliteParameterCollection destination)
    {
        foreach (SqliteParameter parameter in source)
        {
            var copy = new SqliteParameter
            {
                ParameterName = parameter.ParameterName,
                SqliteType = parameter.SqliteType,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                SourceColumn = parameter.SourceColumn,
                SourceColumnNullMapping = parameter.SourceColumnNullMapping,
            };
            if (parameter.HasSize)
                copy.Size = parameter.Size;
            if (parameter.HasValue)
                copy.Value = SnapshotValue(parameter.Value);

            destination.Add(copy);
        }
    }

    private static void CopyParameters(
        SqliteParameterCollection source,
        AhtolaParameterCollection destination)
    {
        foreach (SqliteParameter parameter in source)
        {
            if (!parameter.HasValue)
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.Value)));

            destination.Add(new AhtolaParameter
            {
                ParameterName = parameter.ParameterName,
                DbType = parameter.DbType,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                SourceColumn = parameter.SourceColumn,
                SourceColumnNullMapping = parameter.SourceColumnNullMapping,
                Size = parameter.Size,
                Value = SqliteCommand.SnapshotAhtolaParameterValue(parameter.GetResolvedStorageValue()),
            });
        }
    }

    private static object? SnapshotValue(object? value)
        => value switch
        {
            byte[] bytes => bytes.ToArray(),
            Memory<byte> memory => memory.ToArray(),
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            _ => value,
        };

    private static SqliteTransaction? GetActiveTransaction(SqliteConnection connection)
    {
        var transaction = connection.Transaction;
        if (transaction?.WasRolledBackExternally == true)
        {
            transaction.Rollback();
            transaction = connection.Transaction;
        }

        return transaction;
    }
}
