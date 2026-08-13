using System.Collections;
using System.Data;
using System.Data.Common;

namespace Ahtola;

internal interface IConnectionOwnedReader
{
    void CloseFromConnection();
}

internal interface ILocalReaderConnection
{
    void ReaderOpened(IConnectionOwnedReader reader);

    void ReaderClosed(IConnectionOwnedReader reader);
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

internal sealed class SequentialBatchCommand : IDisposable
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

            return total;
        }
        finally
        {
            DisposeCommandsAndExecution(commands, execution);
        }
    }

    internal static DbDataReader ExecuteReader(
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
            return new SequentialBatchDataReader(commands, execution, reader, behavior);
        }
        catch
        {
            try
            {
                if (reader is not null)
                    await reader.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                DisposeCommandsAndExecution(commands, execution);
            }
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
}

internal sealed class SequentialBatchDataReader : DbDataReader, IConnectionOwnedReader
{
    private readonly IReadOnlyList<SequentialBatchCommand> _commands;
    private readonly BatchExecutionControl _execution;
    private readonly CommandBehavior _behavior;
    private readonly DbConnection? _connection;
    private readonly ILocalReaderConnection? _readerConnection;
    private int _commandIndex;
    private DbDataReader? _reader;
    private int _recordsAffected = -1;
    private bool _finished;
    private bool _isClosed;

    internal SequentialBatchDataReader(
        IReadOnlyList<SequentialBatchCommand> commands,
        BatchExecutionControl execution,
        DbDataReader reader,
        CommandBehavior behavior)
    {
        _commands = commands;
        _execution = execution;
        _reader = reader;
        _behavior = behavior;
        _connection = commands[0].Command.Connection;
        CompleteCurrentWithoutResultSet();
        _readerConnection = _connection as ILocalReaderConnection;
        _readerConnection?.ReaderOpened(this);
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
                Finish();
                return false;
            }

            DisposeCurrent();
            _commandIndex++;
            var next = _commands[_commandIndex];
            next.PrepareForExecution();
            _execution.SetActiveCommand(next.Command);
            transitionToken.ThrowIfCancellationRequested();
            _reader = await next.Command
                .ExecuteReaderAsync(WithoutCloseConnection(_behavior), transitionToken)
                .ConfigureAwait(false);
            CompleteCurrentWithoutResultSet();
            return true;
        }
        catch
        {
            CloseCore(drain: false);
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

    public override void Close() => CloseCore(drain: true);

    void IConnectionOwnedReader.CloseFromConnection() => CloseCore(drain: false, closeConnection: false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCore(drain: true);

        base.Dispose(disposing);
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

    private void EnsureOpen()
    {
        if (IsClosed)
            throw new InvalidOperationException("The batch data reader is closed.");
    }

    private static CommandBehavior WithoutCloseConnection(CommandBehavior behavior)
        => behavior & ~CommandBehavior.CloseConnection;
}
