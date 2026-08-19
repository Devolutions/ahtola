using System.Data;
using System.Data.Common;

namespace Ahtola;

public sealed class AhtolaBatch : DbBatch
{
    private readonly AhtolaBatchCommandCollection _batchCommands = new();
    private readonly object _executionSync = new();
    private AhtolaConnection? _connection;
    private AhtolaTransaction? _transaction;
    private BatchExecutionControl? _activeExecution;
    private int _timeout = 30;
    private bool _disposed;

    public AhtolaBatch()
    {
    }

    public AhtolaBatch(AhtolaConnection connection)
    {
        _connection = connection;
        _transaction = connection.Transaction;
        _timeout = connection.DefaultTimeout;
    }

    protected override DbBatchCommandCollection DbBatchCommands => _batchCommands;

    public new AhtolaBatchCommandCollection BatchCommands => _batchCommands;

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

            _connection = value as AhtolaConnection
                          ?? throw new ArgumentException("Connection must be a AhtolaConnection.", nameof(value));
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

            _transaction = value as AhtolaTransaction
                           ?? throw new ArgumentException("Transaction must be a AhtolaTransaction.", nameof(value));
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
        if (_connection?.IsRemote != true)
        {
            var connection = ValidateBatch();
            var (commands, execution) = CreateLocalExecution(connection, CancellationToken.None);
            return SequentialBatchExecutor.ExecuteNonQuery(commands, execution);
        }

        var results = ExecuteBatch(wantRows: false, CancellationToken.None).GetAwaiter().GetResult();
        return SetRecordsAffected(results);
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection?.IsRemote != true)
        {
            var connection = ValidateBatch();
            var (commands, execution) = CreateLocalExecution(connection, cancellationToken);
            return await SequentialBatchExecutor
                .ExecuteNonQueryAsync(commands, execution)
                .ConfigureAwait(false);
        }

        var results = await ExecuteBatch(wantRows: false, cancellationToken).ConfigureAwait(false);
        return SetRecordsAffected(results);
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
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        while (reader.FieldCount == 0
               && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetValue(0) : null;
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

    protected override DbBatchCommand CreateDbBatchCommand()
    {
        return new AhtolaBatchCommand();
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (_connection?.IsRemote != true)
        {
            var connection = ValidateBatch();
            var (commands, execution) = CreateLocalExecution(connection, CancellationToken.None);
            return SequentialBatchExecutor.ExecuteReader(commands, execution, behavior);
        }

        var results = ExecuteBatch(wantRows: true, CancellationToken.None).GetAwaiter().GetResult();
        SetRecordsAffected(results);
        return new AhtolaRemoteDataReader(_connection, results, behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection?.IsRemote != true)
        {
            var connection = ValidateBatch();
            var (commands, execution) = CreateLocalExecution(connection, cancellationToken);
            return await SequentialBatchExecutor
                .ExecuteReaderAsync(commands, execution, behavior)
                .ConfigureAwait(false);
        }

        var results = await ExecuteBatch(wantRows: true, cancellationToken).ConfigureAwait(false);
        SetRecordsAffected(results);
        return new AhtolaRemoteDataReader(_connection, results, behavior);
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

    private async Task<IReadOnlyList<RemoteStatementResult>> ExecuteBatch(
        bool wantRows,
        CancellationToken cancellationToken)
    {
        var connection = ValidateBatch();
        if (!connection.IsRemote)
            throw new NotSupportedException("Ahtola batch execution is currently supported only for remote connections.");

        using var execution = BeginExecution(cancellationToken);
        var results = await connection
            .ExecuteRemoteBatchAsync(_batchCommands.AsReadOnly(), Timeout, wantRows, execution.Token)
            .ConfigureAwait(false);
        return results;
    }

    private AhtolaConnection ValidateBatch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = _connection ?? throw new InvalidOperationException("Connection must be set before executing a batch.");
        if (_transaction is { IsCompleted: true })
            throw new InvalidOperationException("The transaction associated with this batch has completed.");
        _transaction?.ThrowIfFaulted();
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Ahtola database is closed.");
        if (_transaction is not null && !ReferenceEquals(_transaction.Connection, connection))
            throw new InvalidOperationException("The transaction is not associated with the batch's connection.");
        if (connection.Transaction is not null && !ReferenceEquals(_transaction, connection.Transaction))
            throw new InvalidOperationException("The batch must be associated with the connection's active transaction.");
        if (_batchCommands.Count == 0)
            throw new InvalidOperationException("Batch must contain at least one command.");

        foreach (var command in _batchCommands.AsReadOnly())
        {
            if (string.IsNullOrWhiteSpace(command.CommandText))
                throw new InvalidOperationException("Batch command text must be set before executing a batch.");
            if (command.CommandType != CommandType.Text)
                throw new NotSupportedException("AhtolaBatchCommand only supports CommandType.Text.");
            if (command.RemoteCondition is not null && !connection.IsRemote)
                throw new NotSupportedException("RemoteCondition requires a remote Ahtola connection.");
            connection.ValidateCommandCapabilities(command.CommandText);
        }

        return connection;
    }

    private (
        IReadOnlyList<SequentialBatchCommand> Commands,
        BatchExecutionControl Execution) CreateLocalExecution(
        AhtolaConnection connection,
        CancellationToken cancellationToken)
    {
        var execution = BeginExecution(cancellationToken);
        try
        {
            return (CreateLocalCommands(connection), execution);
        }
        catch
        {
            execution.Dispose();
            throw;
        }
    }

    private IReadOnlyList<SequentialBatchCommand> CreateLocalCommands(AhtolaConnection connection)
    {
        var commands = new List<SequentialBatchCommand>(_batchCommands.Count);
        try
        {
            foreach (var batchCommand in _batchCommands.AsReadOnly())
            {
                var command = new AhtolaCommand(connection)
                {
                    CommandText = batchCommand.CommandText,
                    CommandTimeout = Timeout,
                };
                CopyParameters(batchCommand.Parameters, command.Parameters);
                commands.Add(new SequentialBatchCommand(
                    command,
                    batchCommand.SetRecordsAffected,
                    () => connection.Transaction));
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
        AhtolaParameterCollection source,
        AhtolaParameterCollection destination)
    {
        foreach (AhtolaParameter parameter in source)
        {
            destination.Add(new AhtolaParameter
            {
                ParameterName = parameter.ParameterName,
                DbType = parameter.DbType,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                SourceColumn = parameter.SourceColumn,
                SourceColumnNullMapping = parameter.SourceColumnNullMapping,
                Size = parameter.Size,
                Value = SnapshotValue(parameter.Value),
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

    private int SetRecordsAffected(IReadOnlyList<RemoteStatementResult> results)
    {
        if (results.Count != _batchCommands.Count)
            throw new AhtolaException($"Batch result count {results.Count} did not match command count {_batchCommands.Count}.");

        var total = 0;
        for (var i = 0; i < results.Count; i++)
        {
            var recordsAffected = checked((int)results[i].AffectedRowCount);
            _batchCommands.AsReadOnly()[i].SetRecordsAffected(recordsAffected);
            total = checked(total + recordsAffected);
        }

        return total;
    }
}
