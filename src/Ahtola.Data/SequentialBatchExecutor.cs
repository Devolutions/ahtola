using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Ahtola;

internal interface IConnectionOwnedReader
{
    void CloseFromConnection();

    ValueTask CloseFromConnectionAsync()
    {
        CloseFromConnection();
        return ValueTask.CompletedTask;
    }
}

internal interface ILocalReaderConnection
{
    void ReaderOpened(IConnectionOwnedReader reader);

    void ReaderClosed(IConnectionOwnedReader reader);
}

internal interface IAsyncExecutionConnection
{
    bool RequiresAsyncExecution { get; }
}

internal sealed class BatchExecutionControl : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly Action<BatchExecutionControl> _completed;
    private DbCommand? _activeCommand;
    private bool _disposed;

    internal BatchExecutionControl(
        CancellationToken cancellationToken,
        Action<BatchExecutionControl> completed)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _completed = completed;
    }

    internal CancellationToken Token => _cancellation.Token;

    internal void SetActiveCommand(DbCommand command)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCommand = command;
        }

        Token.ThrowIfCancellationRequested();
    }

    internal void ClearActiveCommand(DbCommand command)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeCommand, command))
                _activeCommand = null;
        }
    }

    internal void Cancel()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _cancellation.Cancel();
            _activeCommand?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _activeCommand = null;
        }

        _cancellation.Dispose();
        _completed(this);
    }
}

internal sealed class SequentialBatchCommand : IDisposable, IAsyncDisposable
{
    private readonly Action<int> _setRecordsAffected;
    private readonly Func<DbTransaction?> _getTransaction;
    private bool _disposed;

    internal SequentialBatchCommand(
        DbCommand command,
        Action<int> setRecordsAffected,
        Func<DbTransaction?> getTransaction)
    {
        Command = command;
        _setRecordsAffected = setRecordsAffected;
        _getTransaction = getTransaction;
    }

    internal DbCommand Command { get; }

    internal bool IsCompleted { get; private set; }

    internal void PrepareForExecution()
    {
        Command.Transaction = _getTransaction();
    }

    internal void Complete(int recordsAffected)
    {
        if (IsCompleted)
            return;

        IsCompleted = true;
        _setRecordsAffected(recordsAffected);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Command.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Command.DisposeAsync().ConfigureAwait(false);
    }
}

internal static class SequentialBatchExecutor
{
    internal static int ExecuteNonQuery(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution)
    {
        var total = 0;
        try
        {
            ThrowIfSynchronousBrowserBatchRejected(commands);
            foreach (var entry in commands)
            {
                execution.Token.ThrowIfCancellationRequested();
                entry.PrepareForExecution();
                execution.SetActiveCommand(entry.Command);
                try
                {
                    var recordsAffected = entry.Command.ExecuteNonQuery();
                    entry.Complete(recordsAffected);
                    total = AddRecordsAffected(total, recordsAffected);
                }
                finally
                {
                    execution.ClearActiveCommand(entry.Command);
                }
            }

            return total;
        }
        finally
        {
            DisposeCommandsAndExecution(commands, execution);
        }
    }

    internal static async Task<int> ExecuteNonQueryAsync(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution)
    {
        var total = 0;
        Exception? failure = null;
        try
        {
            foreach (var entry in commands)
            {
                execution.Token.ThrowIfCancellationRequested();
                entry.PrepareForExecution();
                execution.SetActiveCommand(entry.Command);
                try
                {
                    var recordsAffected = await entry.Command
                        .ExecuteNonQueryAsync(execution.Token)
                        .ConfigureAwait(false);
                    entry.Complete(recordsAffected);
                    total = AddRecordsAffected(total, recordsAffected);
                }
                finally
                {
                    execution.ClearActiveCommand(entry.Command);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await DisposeCommandsAndExecutionAsync(commands, execution, failure)
            .ConfigureAwait(false);
        ThrowIfFailure(failure);
        return total;
    }

    internal static DbDataReader ExecuteReader(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution,
        CommandBehavior behavior)
    {
        DbDataReader? reader = null;
        try
        {
            // Fail closed before the first command runs: a batch that is not provably read-only
            // end to end must never execute even its leading SELECT synchronously, or a browser
            // caller would get a partially applied batch it cannot finish.
            ThrowIfSynchronousBrowserBatchRejected(commands);
            var first = commands[0];
            first.PrepareForExecution();
            execution.SetActiveCommand(first.Command);
            reader = first.Command.ExecuteReader(WithoutCloseConnection(behavior));
            return new SequentialBatchDataReader(commands, execution, reader, behavior);
        }
        catch
        {
            try
            {
                reader?.Dispose();
            }
            finally
            {
                DisposeCommandsAndExecution(commands, execution);
            }
            throw;
        }
    }

    /// <summary>
    /// Refuses a synchronous batch on an asynchronous-only connection unless every command in it
    /// was proven read-only. A fully proven batch is served from the managed in-memory mirror and
    /// needs no durable-store crossing, so it stays synchronous.
    /// </summary>
    internal static void ThrowIfSynchronousBrowserBatchRejected(
        IReadOnlyList<SequentialBatchCommand> commands)
    {
        if (commands.Count == 0)
            return;
        if (commands[0].Command.Connection is not IAsyncExecutionConnection { RequiresAsyncExecution: true })
            return;
        if (CaptureAggregateAuthorization(commands).AllowsSynchronousExecution)
            return;

        throw new PlatformNotSupportedException(
            "Synchronous batch execution requires every command in the batch to be proven "
            + "read-only ("
            + BrowserSynchronousExecutionContract.ProvenReadOnlyShapes
            + "). Use the corresponding asynchronous API.");
    }

    /// <summary>
    /// Folds every batch command's synchronous-execution decision into one, classifying each
    /// command's text exactly once.
    /// </summary>
    internal static BrowserSynchronousAuthorization CaptureAggregateAuthorization(
        IReadOnlyList<SequentialBatchCommand> commands)
    {
        var authorization = BrowserSynchronousAuthorization.Allowed;
        foreach (var entry in commands)
        {
            authorization = authorization.And(
                BrowserSynchronousAuthorization.Capture(
                    entry.Command.Connection as IBrowserSynchronousExecutionPolicy,
                    entry.Command.CommandText));
        }

        return authorization;
    }

    internal static async Task<DbDataReader> ExecuteReaderAsync(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution,
        CommandBehavior behavior)
    {
        DbDataReader? reader = null;
        try
        {
            var first = commands[0];
            first.PrepareForExecution();
            execution.SetActiveCommand(first.Command);
            reader = await first.Command
                .ExecuteReaderAsync(WithoutCloseConnection(behavior), execution.Token)
                .ConfigureAwait(false);
            var ownedReader = reader;
            reader = null;
            return await SequentialBatchDataReader
                .CreateAsync(commands, execution, ownedReader, behavior)
                .ConfigureAwait(false);
        }
        catch (Exception operationFailure)
        {
            Exception? failure = operationFailure;
            if (reader is not null)
            {
                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    failure = CombineFailure(
                        "Batch execution and reader cleanup both failed.",
                        failure,
                        cleanupFailure);
                }
            }

            failure = await DisposeCommandsAndExecutionAsync(commands, execution, failure)
                .ConfigureAwait(false);
            ThrowIfFailure(failure);
            throw;
        }
    }

    internal static int AddRecordsAffected(int total, int recordsAffected)
    {
        if (recordsAffected < 0)
            return total;

        return total < 0 ? recordsAffected : checked(total + recordsAffected);
    }

    private static CommandBehavior WithoutCloseConnection(CommandBehavior behavior)
        => behavior & ~CommandBehavior.CloseConnection;

    private static void DisposeCommands(IReadOnlyList<SequentialBatchCommand> commands)
    {
        foreach (var command in commands)
            command.Dispose();
    }

    private static void DisposeCommandsAndExecution(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution)
    {
        try
        {
            DisposeCommands(commands);
        }
        finally
        {
            execution.Dispose();
        }
    }

    private static async ValueTask<Exception?> DisposeCommandsAndExecutionAsync(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution,
        Exception? failure)
    {
        foreach (var command in commands)
        {
            try
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = CombineFailure(
                    "Batch execution and command cleanup both failed.",
                    failure,
                    cleanupFailure);
            }
        }
        try
        {
            execution.Dispose();
        }
        catch (Exception cleanupFailure)
        {
            failure = CombineFailure(
                "Batch execution and execution-context cleanup both failed.",
                failure,
                cleanupFailure);
        }

        return failure;
    }

    internal static Exception CombineFailure(
        string message,
        Exception? existing,
        Exception current)
        => existing is null
            ? current
            : new AggregateException(message, existing, current);

    internal static void ThrowIfFailure(Exception? failure)
    {
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed class SequentialBatchDataReader : DbDataReader, IConnectionOwnedReader
{
    private readonly IReadOnlyList<SequentialBatchCommand> _commands;
    private readonly BatchExecutionControl _execution;
    private readonly CommandBehavior _behavior;
    private readonly DbConnection? _connection;
    private readonly ILocalReaderConnection? _readerConnection;
    private readonly BrowserSynchronousAuthorization _synchronousAuthorization;
    private int _commandIndex;
    private DbDataReader? _reader;
    private int _recordsAffected = -1;
    private bool _finished;
    private bool _isClosed;

    internal SequentialBatchDataReader(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution,
        DbDataReader reader,
        CommandBehavior behavior,
        bool completeInitialResult = true)
    {
        _commands = commands;
        _execution = execution;
        _reader = reader;
        _behavior = behavior;
        _connection = commands[0].Command.Connection;

        // Classify the whole batch exactly once, from the command texts that are about to be
        // executed. A batch may be driven synchronously only when *every* command is provably
        // read-only, so one unproven command still fails closed — but a batch of proven reads is
        // served from the managed mirror and needs no OPFS crossing, so refusing it outright
        // would be wrong. Folding here also keeps the classifier off the per-row path.
        _synchronousAuthorization = SequentialBatchExecutor.CaptureAggregateAuthorization(commands);
        if (completeInitialResult)
            CompleteCurrentWithoutResultSet();
        _readerConnection = _connection as ILocalReaderConnection;
        _readerConnection?.ReaderOpened(this);
    }

    internal static async ValueTask<SequentialBatchDataReader> CreateAsync(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution,
        DbDataReader reader,
        CommandBehavior behavior)
    {
        SequentialBatchDataReader? result = null;
        try
        {
            result = new SequentialBatchDataReader(
                commands,
                execution,
                reader,
                behavior,
                completeInitialResult: false);
            await result
                .CompleteCurrentWithoutResultSetAsync(execution.Token)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception operationFailure)
        {
            Exception? failure = operationFailure;
            try
            {
                if (result is null)
                    await reader.DisposeAsync().ConfigureAwait(false);
                else
                    await result.CloseCoreAsync(drain: false, closeConnection: false).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = SequentialBatchExecutor.CombineFailure(
                    "Batch initialization and cleanup both failed.",
                    failure,
                    cleanupFailure);
            }

            SequentialBatchExecutor.ThrowIfFailure(failure);
            throw;
        }
    }

    public override int Depth => _finished ? 0 : Current.Depth;

    public override int FieldCount => _finished ? 0 : Current.FieldCount;

    public override bool HasRows => !_finished && Current.HasRows;

    public override bool IsClosed => _isClosed
        || _reader?.IsClosed == true
        || _connection?.State != ConnectionState.Open;

    public override int RecordsAffected => _recordsAffected;

    public override object this[int ordinal] => Current[ordinal];

    public override object this[string name] => Current[name];

    public override bool GetBoolean(int ordinal) => Current.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => Current.GetByte(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => Current.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => Current.GetChar(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => Current.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => Current.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => Current.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => Current.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => Current.GetDouble(ordinal);

    public override IEnumerator GetEnumerator() => Current.GetEnumerator();

    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => Current.GetFieldType(ordinal);

    public override T GetFieldValue<T>(int ordinal) => Current.GetFieldValue<T>(ordinal);

    public override float GetFloat(int ordinal) => Current.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => Current.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => Current.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => Current.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => Current.GetInt64(ordinal);

    public override string GetName(int ordinal) => Current.GetName(ordinal);

    public override int GetOrdinal(string name) => Current.GetOrdinal(name);

    public override DataTable? GetSchemaTable() => _finished ? null : Current.GetSchemaTable();

    public override string GetString(int ordinal) => Current.GetString(ordinal);

    public override Stream GetStream(int ordinal) => Current.GetStream(ordinal);

    public override TextReader GetTextReader(int ordinal) => Current.GetTextReader(ordinal);

    public override object GetValue(int ordinal) => Current.GetValue(ordinal);

    public override int GetValues(object[] values) => Current.GetValues(values);

    public override bool IsDBNull(int ordinal) => Current.IsDBNull(ordinal);

    public override bool Read()
    {
        EnsureOpen();
        ThrowIfSynchronousBrowserOperation();
        if (_finished)
            return false;

        _execution.Token.ThrowIfCancellationRequested();
        return Current.Read();
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_finished)
            return false;
        if (cancellationToken.IsCancellationRequested)
            return await Task.FromCanceled<bool>(cancellationToken).ConfigureAwait(false);

        _execution.Token.ThrowIfCancellationRequested();
        return await Current.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public override bool NextResult()
    {
        EnsureOpen();
        ThrowIfSynchronousBrowserOperation();
        if (_finished)
            return false;

        _execution.Token.ThrowIfCancellationRequested();
        try
        {
            if (Current.NextResult())
                return true;

            CompleteCurrent();
            if (_commandIndex + 1 == _commands.Count)
            {
                Finish();
                return false;
            }

            DisposeCurrent();
            _commandIndex++;
            var next = _commands[_commandIndex];
            next.PrepareForExecution();
            _execution.SetActiveCommand(next.Command);
            _reader = next.Command.ExecuteReader(WithoutCloseConnection(_behavior));
            CompleteCurrentWithoutResultSet();
            return true;
        }
        catch
        {
            CloseCore(drain: false);
            throw;
        }
    }

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_finished)
            return false;
        if (cancellationToken.IsCancellationRequested)
            return await Task.FromCanceled<bool>(cancellationToken).ConfigureAwait(false);

        using var transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _execution.Token,
            cancellationToken);
        var transitionToken = transitionCancellation.Token;
        transitionToken.ThrowIfCancellationRequested();
        try
        {
            if (await Current.NextResultAsync(transitionToken).ConfigureAwait(false))
                return true;

            transitionToken.ThrowIfCancellationRequested();
            CompleteCurrent();
            if (_commandIndex + 1 == _commands.Count)
            {
                await FinishAsync().ConfigureAwait(false);
                return false;
            }

            var cleanupFailure = await DisposeCurrentAsync(closeFromConnection: false)
                .ConfigureAwait(false);
            SequentialBatchExecutor.ThrowIfFailure(cleanupFailure);
            _commandIndex++;
            var next = _commands[_commandIndex];
            next.PrepareForExecution();
            _execution.SetActiveCommand(next.Command);
            transitionToken.ThrowIfCancellationRequested();
            _reader = await next.Command
                .ExecuteReaderAsync(WithoutCloseConnection(_behavior), transitionToken)
                .ConfigureAwait(false);
            await CompleteCurrentWithoutResultSetAsync(transitionToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception operationFailure)
        {
            Exception? failure = operationFailure;
            try
            {
                await CloseCoreAsync(drain: false).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = SequentialBatchExecutor.CombineFailure(
                    "Batch result transition and cleanup both failed.",
                    failure,
                    cleanupFailure);
            }

            SequentialBatchExecutor.ThrowIfFailure(failure);
            throw;
        }
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        EnsureOpen();
        _execution.Token.ThrowIfCancellationRequested();
        return Current.IsDBNullAsync(ordinal, cancellationToken);
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        EnsureOpen();
        _execution.Token.ThrowIfCancellationRequested();
        return Current.GetFieldValueAsync<T>(ordinal, cancellationToken);
    }

    public override void Close()
    {
        if (!_isClosed)
            ThrowIfSynchronousBrowserOperation();
        CloseCore(drain: true);
    }

    public override async Task CloseAsync()
        => await CloseCoreAsync(drain: true).ConfigureAwait(false);

    void IConnectionOwnedReader.CloseFromConnection() => CloseCore(drain: false, closeConnection: false);

    ValueTask IConnectionOwnedReader.CloseFromConnectionAsync()
        => CloseCoreAsync(drain: false, closeConnection: false);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isClosed)
            ThrowIfSynchronousBrowserOperation();
        if (disposing)
            CloseCore(drain: true);

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseCoreAsync(drain: true).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private DbDataReader Current
    {
        get
        {
            EnsureOpen();
            return _reader ?? throw new InvalidOperationException("The batch reader has no current result.");
        }
    }

    private SequentialBatchCommand CurrentCommand => _commands[_commandIndex];

    private void CompleteCurrentWithoutResultSet()
    {
        if (Current.FieldCount != 0)
            return;

        while (Current.Read())
        {
        }

        CompleteCurrent();
    }

    private async ValueTask CompleteCurrentWithoutResultSetAsync(
        CancellationToken cancellationToken)
    {
        if (Current.FieldCount != 0)
            return;

        while (await Current.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        CompleteCurrent();
    }

    private void CompleteCurrent()
    {
        if (CurrentCommand.IsCompleted)
            return;

        var recordsAffected = Current.RecordsAffected;
        CurrentCommand.Complete(recordsAffected);
        _recordsAffected = SequentialBatchExecutor.AddRecordsAffected(_recordsAffected, recordsAffected);
    }

    private void Finish()
    {
        DisposeCurrent();
        _finished = true;
        _execution.Dispose();
    }

    private async ValueTask FinishAsync()
    {
        var failure = await DisposeCurrentAsync(closeFromConnection: false)
            .ConfigureAwait(false);
        _finished = true;
        try
        {
            _execution.Dispose();
        }
        catch (Exception cleanupFailure)
        {
            failure = SequentialBatchExecutor.CombineFailure(
                "Batch reader and execution-context cleanup both failed.",
                failure,
                cleanupFailure);
        }
        SequentialBatchExecutor.ThrowIfFailure(failure);
    }

    private void DisposeCurrent()
    {
        var reader = _reader;
        _reader = null;
        try
        {
            reader?.Dispose();
        }
        finally
        {
            var command = CurrentCommand;
            _execution.ClearActiveCommand(command.Command);
            command.Dispose();
        }
    }

    private async ValueTask<Exception?> DisposeCurrentAsync(
        bool closeFromConnection,
        Exception? failure = null)
    {
        var reader = _reader;
        _reader = null;
        try
        {
            if (reader is not null)
            {
                if (closeFromConnection && reader is IConnectionOwnedReader ownedReader)
                    await ownedReader.CloseFromConnectionAsync().ConfigureAwait(false);
                else
                    await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception cleanupFailure)
        {
            failure = SequentialBatchExecutor.CombineFailure(
                "Batch reader cleanup failed.",
                failure,
                cleanupFailure);
        }
        finally
        {
            var command = CurrentCommand;
            _execution.ClearActiveCommand(command.Command);
            try
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = SequentialBatchExecutor.CombineFailure(
                    "Batch reader and command cleanup both failed.",
                    failure,
                    cleanupFailure);
            }
        }

        return failure;
    }

    private void CloseCore(bool drain, bool closeConnection = true)
    {
        if (_isClosed)
            return;

        try
        {
            if (drain
                && !_finished
                && !_execution.Token.IsCancellationRequested
                && !IsClosed)
            {
                while (NextResult())
                {
                }
            }
        }
        finally
        {
            try
            {
                DisposeCurrent();
                for (var i = _commandIndex + 1; i < _commands.Count; i++)
                    _commands[i].Dispose();
            }
            finally
            {
                _finished = true;
                _isClosed = true;
                _readerConnection?.ReaderClosed(this);
                _execution.Dispose();
                if (closeConnection
                    && (_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
                {
                    _connection?.Close();
                }
            }
        }
    }

    private async ValueTask CloseCoreAsync(bool drain, bool closeConnection = true)
    {
        if (_isClosed)
            return;

        Exception? failure = null;
        try
        {
            if (drain
                && !_finished
                && !_execution.Token.IsCancellationRequested
                && !IsClosed)
            {
                while (await NextResultAsync(CancellationToken.None).ConfigureAwait(false))
                {
                }
            }
        }
        catch (Exception operationFailure)
        {
            failure = operationFailure;
        }

        failure = await DisposeCurrentAsync(
                closeFromConnection: !drain,
                failure)
            .ConfigureAwait(false);
        for (var i = _commandIndex + 1; i < _commands.Count; i++)
        {
            try
            {
                await _commands[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = SequentialBatchExecutor.CombineFailure(
                    "Batch reader and command cleanup both failed.",
                    failure,
                    cleanupFailure);
            }
        }

        _finished = true;
        _isClosed = true;
        try
        {
            _readerConnection?.ReaderClosed(this);
        }
        catch (Exception cleanupFailure)
        {
            failure = SequentialBatchExecutor.CombineFailure(
                "Batch reader cleanup and connection notification both failed.",
                failure,
                cleanupFailure);
        }
        try
        {
            _execution.Dispose();
        }
        catch (Exception cleanupFailure)
        {
            failure = SequentialBatchExecutor.CombineFailure(
                "Batch reader and execution-context cleanup both failed.",
                failure,
                cleanupFailure);
        }
        if (closeConnection
            && (_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection
            && _connection is not null)
        {
            try
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure = SequentialBatchExecutor.CombineFailure(
                    "Batch reader cleanup and connection close both failed.",
                    failure,
                    cleanupFailure);
            }
        }

        SequentialBatchExecutor.ThrowIfFailure(failure);
    }

    private void EnsureOpen()
    {
        if (IsClosed)
            throw new InvalidOperationException("The batch data reader is closed.");
    }

    /// <summary>
    /// Refuses a synchronous batch-reader operation unless every command in the batch was proven
    /// read-only when the batch was executed. A proven read-only batch is served entirely from the
    /// managed in-memory mirror, so it may be iterated, closed and disposed synchronously; any
    /// mixed or unproven batch still owes the persistent store durable work and fails closed
    /// before a single step happens.
    /// </summary>
    private void ThrowIfSynchronousBrowserOperation()
    {
        if (_connection is not IAsyncExecutionConnection { RequiresAsyncExecution: true })
            return;
        if (_synchronousAuthorization.AllowsSynchronousExecution)
            return;

        throw new PlatformNotSupportedException(
            "Synchronous batch reader operations require every command in the batch to be proven "
            + "read-only ("
            + BrowserSynchronousExecutionContract.ProvenReadOnlyShapes
            + "). Use the corresponding asynchronous API.");
    }

    private static CommandBehavior WithoutCloseConnection(CommandBehavior behavior)
        => behavior & ~CommandBehavior.CloseConnection;
}
