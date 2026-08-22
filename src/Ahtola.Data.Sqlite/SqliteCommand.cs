using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ahtola;
using Ahtola.Core;
using Ahtola.Core.Parsing;

namespace Ahtola.Data.Sqlite;

public class SqliteCommand : DbCommand
{
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private SqliteStatementAdapter? _statement;
    private AhtolaCommand? _ahtolaCommand;
    private AhtolaBatch? _ahtolaBatch;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private bool _hasOpenReader;
    private readonly CommandCancellationController _cancellation = new();

    public SqliteCommand()
    {
    }

    public SqliteCommand(string? commandText)
    {
        CommandText = commandText;
    }

    public SqliteCommand(SqliteConnection? connection)
    {
        Connection = connection;
    }

    public SqliteCommand(string? commandText, SqliteConnection? connection)
        : this(commandText)
    {
        Connection = connection;
    }

    public SqliteCommand(string? commandText, SqliteConnection? connection, SqliteTransaction? transaction)
        : this(commandText, connection)
    {
        Transaction = transaction;
    }

    public SqliteCommand(string? commandText, SqliteConnection? connection, DbTransaction? transaction)
        : this(commandText, connection)
    {
        Transaction = transaction as SqliteTransaction
                      ?? (transaction is null ? null : throw new ArgumentException("Transaction must be a SqliteTransaction.", nameof(transaction)));
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            ThrowIfReaderOpen(nameof(CommandText));
            _commandText = value ?? string.Empty;
        }
    }

    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _commandTimeout = value;
        }
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
                throw new ArgumentException(Properties.Resources.InvalidCommandType(value));
        }
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public new SqliteConnection? Connection
    {
        get => _connection;
        set
        {
            ThrowIfReaderOpen(nameof(Connection));
            if (ReferenceEquals(_connection, value))
                return;

            _statement?.Dispose();
            _statement = null;
            _connection?.CommandClosed(this);
            _connection = value;
            if (value is not null)
            {
                value.CommandOpened(this);
                _commandTimeout = value.DefaultTimeout;
                _transaction ??= value.Transaction;
            }
        }
    }

    public new SqliteParameterCollection Parameters { get; } = new();

    public new SqliteTransaction? Transaction
    {
        get => _transaction;
        set
        {
            ThrowIfReaderOpen(nameof(Transaction));
            _transaction = value;
        }
    }

    protected override DbConnection? DbConnection
    {
        get => Connection;
        set => Connection = value as SqliteConnection
                            ?? (value is null ? null : throw new ArgumentException("Connection must be a SqliteConnection.", nameof(value)));
    }

    protected override DbParameterCollection DbParameterCollection => Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => Transaction;
        set => Transaction = value as SqliteTransaction
                            ?? (value is null ? null : throw new ArgumentException("Transaction must be a SqliteTransaction.", nameof(value)));
    }

    public override void Cancel()
    {
        _cancellation.Cancel();
        _ahtolaCommand?.Cancel();
        _ahtolaBatch?.Cancel();
    }

    public override int ExecuteNonQuery()
    {
        ThrowIfSynchronousBrowserOperation("ExecuteNonQueryAsync");
        using var reader = _cancellation.Run(
            token => Execute("ExecuteNonQuery", CommandBehavior.Default, token));
        while (reader.Read())
        {
        }

        reader.Close();
        MarkTransactionCompletedExternally(CommandText);

        return reader.RecordsAffected;
    }

    public override object? ExecuteScalar()
    {
        ThrowIfSynchronousBrowserOperation("ExecuteScalarAsync");
        using var reader = _cancellation.Run(
            token => Execute("ExecuteScalar", CommandBehavior.Default, token));
        var result = reader.Read() ? reader.GetValue(0) : null;
        reader.Close();
        MarkTransactionCompletedExternally(CommandText);
        return result;
    }

    public override void Prepare()
    {
        ThrowIfSynchronousBrowserOperation("PrepareAsync");
        EnsureExecutable("Prepare");
        if (Connection?.AhtolaConnection is { } ahtolaConnection)
        {
            using var command = new AhtolaCommand(ahtolaConnection)
            {
                CommandText = CommandText,
                CommandTimeout = CommandTimeout,
            };
            CopyAhtolaParameters(command.Parameters);
            try
            {
                command.Prepare();
                return;
            }
            catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
            {
                throw MapAhtolaException(ex, CommandText);
            }
        }

        var statements = SplitStatements(CommandText);
        if (statements.Count != 1)
        {
            _statement?.Dispose();
            _statement = null;
            return;
        }

        SqliteStatementAdapter? preparedStatement = null;
        try
        {
            preparedStatement = PrepareSingleStatement(statements[0]);
            _statement?.Dispose();
            _statement = preparedStatement;
            preparedStatement = null;
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            throw MapAhtolaException(ex);
        }
        finally
        {
            preparedStatement?.Dispose();
        }
    }

    protected override DbParameter CreateDbParameter() => new SqliteParameter();

    public new SqliteDataReader ExecuteReader()
    {
        ThrowIfSynchronousBrowserOperation("ExecuteReaderAsync");
        return _cancellation.Run(token => Execute("ExecuteReader", CommandBehavior.Default, token));
    }

    public new SqliteDataReader ExecuteReader(CommandBehavior behavior)
    {
        ThrowIfSynchronousBrowserOperation("ExecuteReaderAsync");
        return _cancellation.Run(token => Execute("ExecuteReader", behavior, token));
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ThrowIfSynchronousBrowserOperation("ExecuteReaderAsync");
        return _cancellation.Run<DbDataReader>(token => Execute("ExecuteReader", behavior, token));
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            // Base DbCommand parity: the non-reader paths surface exact TaskCanceledException.
            ? Task.FromCanceled<int>(cancellationToken)
            : ExecuteNonQueryAsyncCore(cancellationToken);

    private async Task<int> ExecuteNonQueryAsyncCore(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        MarkTransactionCompletedExternally(CommandText);
        return reader.RecordsAffected;
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            // Base DbCommand parity: the non-reader paths surface exact TaskCanceledException.
            ? Task.FromCanceled<object?>(cancellationToken)
            : ExecuteScalarAsyncCore(cancellationToken);

    private async Task<object?> ExecuteScalarAsyncCore(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? reader.GetValue(0)
            : null;
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        MarkTransactionCompletedExternally(CommandText);
        return result;
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        // Microsoft.Data.Sqlite parity: a pre-canceled token throws exact
        // OperationCanceledException from the reader-execution path (EF Core's
        // async query contract), not derived TaskCanceledException.
        cancellationToken.ThrowIfCancellationRequested();
        if (Connection?.AhtolaConnection is not null)
            return ExecuteAhtolaAsync(behavior, cancellationToken);
        if (Connection?.IsManagedConnection == true)
        {
            return _cancellation.RunAsync<DbDataReader>(
                token => ExecuteManagedLocalAsync(behavior, token),
                cancellationToken);
        }
        return _cancellation.RunAsync<DbDataReader>(
            token => Execute("ExecuteReader", behavior, token),
            cancellationToken);
    }

    private async Task<DbDataReader> ExecuteAhtolaAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        EnsureExecutable("ExecuteReader");
        if (Connection?.IsReadOnly == true && IsWriteCommand(CommandText))
            throw new SqliteException(Properties.Resources.SqliteNativeError(8, "attempt to write a readonly database"), 8);
        if (SplitStatements(CommandText).Count > 1)
            return await ExecuteAhtolaBatchAsync(behavior, cancellationToken).ConfigureAwait(false);

        var connection = Connection!.AhtolaConnection!;
        var command = new AhtolaCommand(connection)
        {
            CommandText = CommandText,
            CommandTimeout = CommandTimeout,
        };
        try
        {
            if (Transaction?.IsCompleted == true)
                throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
            if (Transaction?.AhtolaTransaction is { } transaction)
                command.Transaction = transaction;
            CopyAhtolaParameters(command.Parameters);

            _ahtolaCommand?.Dispose();
            _ahtolaCommand = command;
            _hasOpenReader = true;
            var reader = await command.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
            return new SqliteDataReader(this, reader, behavior, CloseAhtolaReader, skipToFirstColumnResult: true);
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            if (ReferenceEquals(_ahtolaCommand, command))
                _ahtolaCommand = null;
            _hasOpenReader = false;
            command.Dispose();
            Connection?.ObserveRemoteInvalidation();
            throw MapAhtolaException(ex, CommandText);
        }
        catch
        {
            if (ReferenceEquals(_ahtolaCommand, command))
                _ahtolaCommand = null;
            _hasOpenReader = false;
            command.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statement?.Dispose();
            _statement = null;
            _ahtolaCommand?.Dispose();
            _ahtolaCommand = null;
            _ahtolaBatch?.Dispose();
            _ahtolaBatch = null;
            _connection?.CommandClosed(this);
        }

        base.Dispose(disposing);
    }

    internal void ResetFromConnection()
    {
        _statement?.Dispose();
        _statement = null;
        _ahtolaCommand?.Dispose();
        _ahtolaCommand = null;
        _ahtolaBatch?.Dispose();
        _ahtolaBatch = null;
        _hasOpenReader = false;
    }

    private SqliteDataReader Execute(
        string method,
        CommandBehavior behavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExecutable(method);
        if (Connection?.IsReadOnly == true && IsWriteCommand(CommandText))
            throw new SqliteException(Properties.Resources.SqliteNativeError(8, "attempt to write a readonly database"), 8);
        if (Connection?.AhtolaConnection is not null)
            return ExecuteAhtola(behavior, cancellationToken);
        if (IsEmptyCommand(CommandText))
        {
            _hasOpenReader = true;
            return new SqliteDataReader(this, -1, behavior, CloseReader);
        }

        if (Connection?.HasOpenReader == true && IsReaderBlockingCommand(CommandText))
        {
            var timeout = CommandTimeout == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(CommandTimeout);
            if (!Connection.WaitForNoOpenReader(timeout, cancellationToken))
                throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        }
        var recordsAffected = 0;
        var hadRecordsAffectedStatement = false;
        var statements = SplitStatements(CommandText);
        try
        {
            for (var i = 0; i < statements.Count; i++)
            {
                if (TryHandleFacadeStatement(statements[i], out var sql))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                var statement = PrepareSingleStatement(sql);
                if (cancellationToken.IsCancellationRequested)
                {
                    statement.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (statement.ColumnCount > 0)
                {
                    _hasOpenReader = true;
                    return new SqliteDataReader(
                        this,
                        statement,
                        statements[i],
                        statements.Skip(i + 1).ToList(),
                        recordsAffected,
                        hadRecordsAffectedStatement,
                        behavior,
                        CloseReader);
                }

                while (statement.Read(cancellationToken))
                {
                }

                if (CountsRowsAffected(statements[i]))
                {
                    hadRecordsAffectedStatement = true;
                    recordsAffected += statement.RowsAffected;
                }
                MarkTransactionCompletedExternally(statements[i]);
                statement.Dispose();
            }
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            throw ToSqliteException(ex);
        }
        _hasOpenReader = true;
        return new SqliteDataReader(this, recordsAffected, behavior, CloseReader);
    }

    internal bool RequiresAsyncExecution
        => Connection?.RequiresAsyncExecution == true;

    private void ThrowIfSynchronousBrowserOperation(string asyncAlternative)
    {
        if (RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                $"Synchronous command execution is not supported by the browser database source. "
                + $"Use {asyncAlternative}.");
        }
    }

    private async ValueTask<DbDataReader> ExecuteManagedLocalAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExecutable("ExecuteReader");
        if (Connection?.IsReadOnly == true && IsWriteCommand(CommandText))
            throw new SqliteException(Properties.Resources.SqliteNativeError(8, "attempt to write a readonly database"), 8);
        if (IsEmptyCommand(CommandText))
        {
            _hasOpenReader = true;
            return new SqliteDataReader(this, -1, behavior, CloseReader);
        }

        if (Connection?.HasOpenReader == true && IsReaderBlockingCommand(CommandText))
        {
            var timeout = CommandTimeout == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(CommandTimeout);
            if (!Connection.WaitForNoOpenReader(timeout, cancellationToken))
                throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        }

        var recordsAffected = 0;
        var hadRecordsAffectedStatement = false;
        var statements = SplitStatements(CommandText);
        try
        {
            for (var i = 0; i < statements.Count; i++)
            {
                if (TryHandleFacadeStatement(statements[i], out var sql))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                var statement = await PrepareSingleStatementAsync(sql, cancellationToken).ConfigureAwait(false);
                if (statement.ColumnCount > 0)
                {
                    _hasOpenReader = true;
                    return new SqliteDataReader(
                        this,
                        statement,
                        statements[i],
                        statements.Skip(i + 1).ToList(),
                        recordsAffected,
                        hadRecordsAffectedStatement,
                        behavior,
                        CloseReader);
                }

                try
                {
                    while (await statement.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                    }

                    if (CountsRowsAffected(statements[i]))
                    {
                        hadRecordsAffectedStatement = true;
                        recordsAffected += statement.RowsAffected;
                    }
                    MarkTransactionCompletedExternally(statements[i]);
                }
                finally
                {
                    await statement.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            throw ToSqliteException(ex);
        }

        _hasOpenReader = true;
        return new SqliteDataReader(this, recordsAffected, behavior, CloseReader);
    }

    private SqliteDataReader ExecuteAhtola(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var statements = SplitStatements(CommandText);
        if (statements.Count > 1)
            return ExecuteAhtolaBatch(behavior, cancellationToken);

        var connection = Connection!.AhtolaConnection!;
        var command = new AhtolaCommand(connection)
        {
            CommandText = CommandText,
            CommandTimeout = CommandTimeout,
        };
        try
        {
            if (Transaction?.IsCompleted == true)
                throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
            if (Transaction?.AhtolaTransaction is { } transaction)
                command.Transaction = transaction;

            CopyAhtolaParameters(command.Parameters);

            _ahtolaCommand?.Dispose();
            _ahtolaCommand = command;
            _hasOpenReader = true;
            var reader = command.ExecuteReader(behavior);
            return new SqliteDataReader(this, reader, behavior, CloseAhtolaReader, skipToFirstColumnResult: true);
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            if (ReferenceEquals(_ahtolaCommand, command))
                _ahtolaCommand = null;
            _hasOpenReader = false;
            command.Dispose();
            Connection?.ObserveRemoteInvalidation();
            throw MapAhtolaException(ex, CommandText);
        }
        catch
        {
            if (ReferenceEquals(_ahtolaCommand, command))
                _ahtolaCommand = null;
            _hasOpenReader = false;
            command.Dispose();
            throw;
        }

    }

    private SqliteDataReader ExecuteAhtolaBatch(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var batch = CreateAhtolaBatch();
            try
            {
                _ahtolaBatch?.Dispose();
                _ahtolaBatch = batch;
                _hasOpenReader = true;
                var reader = batch.ExecuteReader(behavior);
                return new SqliteDataReader(this, reader, behavior, CloseAhtolaReader, skipToFirstColumnResult: true);
            }
            catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
            {
                if (ReferenceEquals(_ahtolaBatch, batch))
                    _ahtolaBatch = null;
                _hasOpenReader = false;
                batch.Dispose();
                Connection?.ObserveRemoteInvalidation();
                throw MapAhtolaException(ex, CommandText);
            }
            catch
            {
                if (ReferenceEquals(_ahtolaBatch, batch))
                    _ahtolaBatch = null;
                _hasOpenReader = false;
                batch.Dispose();
                throw;
            }
        }

    private async Task<DbDataReader> ExecuteAhtolaBatchAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken)
        {
            var batch = CreateAhtolaBatch();
            try
            {
                _ahtolaBatch?.Dispose();
                _ahtolaBatch = batch;
                _hasOpenReader = true;
                var reader = await batch.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
                return new SqliteDataReader(this, reader, behavior, CloseAhtolaReader, skipToFirstColumnResult: true);
            }
            catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
            {
                if (ReferenceEquals(_ahtolaBatch, batch))
                    _ahtolaBatch = null;
                _hasOpenReader = false;
                batch.Dispose();
                Connection?.ObserveRemoteInvalidation();
                throw MapAhtolaException(ex, CommandText);
            }
            catch
            {
                if (ReferenceEquals(_ahtolaBatch, batch))
                    _ahtolaBatch = null;
                _hasOpenReader = false;
                batch.Dispose();
                throw;
            }
        }

    private AhtolaBatch CreateAhtolaBatch()
    {
        var batch = new AhtolaBatch(Connection!.AhtolaConnection!)
        {
            Timeout = CommandTimeout,
        };
        var statements = SplitStatements(CommandText);
        var namedParameters = statements
            .Select(SqlParameterMap.Parse)
            .SelectMany(map => Enumerable.Range(1, map.Count).Select(map.GetName))
            .Where(static name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positionalParameters = Parameters.Cast<SqliteParameter>()
            .Where(parameter => !ParameterMatchesAnyName(parameter, namedParameters))
            .ToList();
        var positionalIndex = 0;

        for (var i = 0; i < statements.Count; i++)
        {
            var command = new AhtolaBatchCommand(statements[i]);
            CopyStatementParameters(
                command.Parameters,
                SqlParameterMap.Parse(statements[i]),
                positionalParameters,
                ref positionalIndex);
            if (i > 0 && Connection!.Mode == AhtolaConnectionMode.RemoteHrana)
                command.RemoteCondition = AhtolaRemoteBatchCondition.StepSucceeded(i - 1);
            batch.BatchCommands.Add(command);
        }

        return batch;
    }

    private void CopyStatementParameters(
        AhtolaParameterCollection destination,
        SqlParameterMap parameterMap,
        IReadOnlyList<SqliteParameter> positionalParameters,
        ref int positionalIndex)
    {
        for (var index = 1; index <= parameterMap.Count; index++)
        {
            var name = parameterMap.GetName(index);
            var parameter = name is null
                ? positionalIndex < positionalParameters.Count
                    ? positionalParameters[positionalIndex++]
                    : throw new InvalidOperationException(Properties.Resources.MissingParameters(index))
                : FindParameter(name)
                    ?? throw new InvalidOperationException(Properties.Resources.MissingParameters(name));
            CopyAhtolaParameter(destination, parameter);
        }
    }

    private SqliteParameter? FindParameter(string name)
        => Parameters.Cast<SqliteParameter>().FirstOrDefault(parameter =>
            string.Equals(parameter.ParameterName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(TrimParameterPrefix(parameter.ParameterName), TrimParameterPrefix(name), StringComparison.OrdinalIgnoreCase));

    private static bool ParameterMatchesAnyName(SqliteParameter parameter, IReadOnlySet<string> names)
        => names.Any(name =>
            string.Equals(parameter.ParameterName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(TrimParameterPrefix(parameter.ParameterName), TrimParameterPrefix(name), StringComparison.OrdinalIgnoreCase));

    private static string? TrimParameterPrefix(string? name)
        => string.IsNullOrEmpty(name) ? name : name[0] is '@' or ':' or '$' or '?' ? name[1..] : name;
    private void CloseAhtolaReader()
    {
        _hasOpenReader = false;
        _ahtolaCommand?.Dispose();
        _ahtolaCommand = null;
        _ahtolaBatch?.Dispose();
        _ahtolaBatch = null;
    }

    internal SqliteException MapAhtolaException(Exception exception, string? sql = null)
    {
        var mapped = ToSqliteException(exception, sql);
        return Connection?.Mode == AhtolaConnectionMode.RemoteHrana
            ? SqliteRemoteExceptionClassifier.From(exception, mapped)
            : mapped;
    }

    internal void CopyAhtolaParameters(AhtolaParameterCollection destination)
    {
        var parameterMap = SqlParameterMap.Parse(CommandText);
        var namedParameters = Enumerable.Range(1, parameterMap.Count)
            .Select(parameterMap.GetName)
            .Where(static name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positionalParameters = Parameters.Cast<SqliteParameter>()
            .Where(parameter => !ParameterMatchesAnyName(parameter, namedParameters))
            .ToList();
        var positionalIndex = 0;
        CopyStatementParameters(destination, parameterMap, positionalParameters, ref positionalIndex);
    }

    private void CopyAhtolaParameter(AhtolaParameterCollection destination, SqliteParameter parameter)
    {
        if (!parameter.HasValue)
            throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.Value)));

        var nonFinite = parameter.Value switch
        {
            double value => !double.IsFinite(value),
            float value => !float.IsFinite(value),
            _ => false,
        };
        if (Connection?.Mode == AhtolaConnectionMode.RemoteHrana && nonFinite)
        {
            var exception = new AhtolaParameterException(
                "Only finite numbers (not Infinity or NaN) can be passed as remote arguments.");
            throw SqliteRemoteExceptionClassifier.From(
                exception,
                ToSqliteException(exception, CommandText));
        }

        destination.Add(new AhtolaParameter
        {
            ParameterName = parameter.ParameterName,
            DbType = parameter.DbType,
            Direction = parameter.Direction,
            IsNullable = parameter.IsNullable,
            SourceColumn = parameter.SourceColumn,
            SourceColumnNullMapping = parameter.SourceColumnNullMapping,
            Size = parameter.Size,
            Value = SnapshotAhtolaParameterValue(parameter.GetResolvedStorageValue()),
        });
    }

    internal static object? SnapshotAhtolaParameterValue(object? value)
        => value switch
        {
            byte[] bytes => bytes.ToArray(),
            Memory<byte> memory => memory.ToArray(),
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            _ => value,
        };

    private void ThrowIfReaderOpen(string property)
    {
        if (_hasOpenReader)
            throw new InvalidOperationException(Properties.Resources.SetRequiresNoOpenReader(property));
    }

    private void EnsureExecutable(string method)
    {
        if (_hasOpenReader)
            throw new InvalidOperationException(Properties.Resources.DataReaderOpen);
        if (Connection is null || Connection.State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(method));

            // EF Core's SQLite migration lock is released after the migrator commits. A command may
            // still hold a completed DbTransaction while connection.Transaction is already null.
            // Treat that leftover as autocommit so lock-release DELETE can succeed. External rollbacks
            // and completed leftovers while another connection transaction is active stay rejected.
            if (Transaction is { } assignedTransaction
                && (assignedTransaction.IsCompleted || assignedTransaction.WasRolledBackExternally))
            {
                if (assignedTransaction.WasRolledBackExternally || Connection.Transaction is not null)
                    throw new InvalidOperationException(Properties.Resources.TransactionCompleted);

                _transaction = null;
            }

            if (Transaction is not null && !ReferenceEquals(Transaction.Connection, Connection))
                throw new InvalidOperationException(Properties.Resources.TransactionConnectionMismatch);

            var connectionTransaction = Connection.Transaction;
            if (connectionTransaction is null || ReferenceEquals(Transaction, connectionTransaction))
                return;
            if (connectionTransaction.IsCompleted)
                throw new InvalidOperationException(Properties.Resources.TransactionCompleted);
            if (!IsTransactionControlCommand(CommandText))
                throw new InvalidOperationException(Properties.Resources.TransactionRequired);
        }

    private void CloseReader()
    {
        _hasOpenReader = false;
    }

    internal T RunOperation<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
        => _cancellation.Run(operation, cancellationToken);

    internal Task<T> RunOperationAsync<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
        => _cancellation.RunAsync(operation, cancellationToken);

    internal Task<T> RunOperationAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
        => _cancellation.RunAsync(operation, cancellationToken);

    internal void MarkTransactionCompletedExternally(string commandText)
    {
        var completion = SqlTransactionControl.GetCompletion(commandText);
        if (completion != SqlTransactionCompletion.None)
            Connection?.Transaction?.MarkCompletedExternally(completion == SqlTransactionCompletion.Rollback);
    }

    internal SqliteStatementAdapter PrepareSingleStatement(string sql)
    {
        var connection = Connection!;
        if (connection.IsManagedReadOnly)
            ManagedReadOnlySqlGuard.ThrowIfQueryOnlyIsDisabled(sql);
        sql = RewriteFacadeStatement(sql, connection);
        if (connection.IsManagedConnection)
        {
            // Mirror the native path below: Microsoft.Data.Sqlite maps CommandTimeout onto
            // sqlite3_busy_timeout per command, so managed lock contention waits the same way.
            connection.ManagedConnection.BusyTimeout =
                CommandTimeout == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(CommandTimeout);
            IManagedStatementAdapter? managedStatement = null;
            try
            {
                if (Environment.GetEnvironmentVariable("TURSO_TRACE_SQL") is not null)
                {
                    // A spinning statement is diagnosed from a hard-killed test host, so the
                    // trace must be durable per statement. Console.Error alone is unreliable
                    // here: vstest captures the testhost's stderr instead of streaming it to
                    // the redirecting console, and an unflushed buffer loses the guilty SQL.
                    // Append to a file (TURSO_TRACE_SQL_FILE, or a temp default) so the last
                    // prepared statement before a freeze is always recoverable.
                    var traced = $"[Ahtola-SQL] {sql.ReplaceLineEndings(" ")}";
                    Console.Error.WriteLine(traced);
                    Console.Error.Flush();
                    var traceFile = Environment.GetEnvironmentVariable("TURSO_TRACE_SQL_FILE");
                    if (string.IsNullOrEmpty(traceFile))
                    {
                        traceFile = Path.Combine(Path.GetTempPath(), "Ahtola-trace-sql.log");
                    }

                    try
                    {
                        using var stream = new FileStream(traceFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(stream);
                        writer.WriteLine(traced);
                    }
                    catch
                    {
                        // Tracing is diagnostic-only and must never break execution.
                    }
                }
                managedStatement = connection.ManagedConnection.Prepare(sql);
                BindManagedParameters(managedStatement);

                var statement = SqliteStatementAdapter.FromManaged(managedStatement);
                managedStatement = null;
                return statement;
            }
            catch (EmbeddedSqlException ex)
            {
                throw ToSqliteException(ex, sql);
            }
            finally
            {
                managedStatement?.Dispose();
            }
        }

        SqliteStatementAdapter? nativeStatement = null;
        try
        {
            connection.NativeDatabase.SetBusyTimeout(
                CommandTimeout == 0
                    ? TimeSpan.MaxValue
                    : TimeSpan.FromSeconds(CommandTimeout));
            nativeStatement = SqliteStatementAdapter.FromNative(connection.NativeDatabase.PrepareStatement(sql));
            BindNativeParameters(nativeStatement);
            var statement = nativeStatement;
            nativeStatement = null;
            return statement;
        }
        catch (AhtolaException ex)
        {
            throw ToSqliteException(ex, sql);
        }
        finally
        {
            nativeStatement?.Dispose();
        }
    }

    internal async ValueTask<SqliteStatementAdapter> PrepareSingleStatementAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        var connection = Connection!;
        if (connection.IsManagedReadOnly)
            ManagedReadOnlySqlGuard.ThrowIfQueryOnlyIsDisabled(sql);
        sql = RewriteFacadeStatement(sql, connection);
        if (!connection.IsManagedConnection)
            return PrepareSingleStatement(sql);

        connection.ManagedConnection.BusyTimeout =
            CommandTimeout == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(CommandTimeout);
        IManagedStatementAdapter? managedStatement = null;
        try
        {
            managedStatement = await connection.ManagedConnection
                .PrepareAsync(sql, cancellationToken)
                .ConfigureAwait(false);
            BindManagedParameters(managedStatement);

            var statement = SqliteStatementAdapter.FromManaged(managedStatement);
            managedStatement = null;
            return statement;
        }
        catch (EmbeddedSqlException ex)
        {
            throw ToSqliteException(ex, sql);
        }
        finally
        {
            if (managedStatement is not null)
                await managedStatement.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void BindNativeParameters(SqliteStatementAdapter statement)
    {
        var parameterCount = statement.NativeParameterCount;
        var boundParameters = new bool[parameterCount + 1];
        List<SqliteParameter>? positionalParameters = null;

        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            if (string.IsNullOrEmpty(parameter.ParameterName))
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.ParameterName)));
            if (!parameter.HasValue)
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.Value)));

            var parameterIndex = FindNativeParameterIndex(statement, parameter.ParameterName, parameterCount);
            if (parameterIndex == 0)
            {
                // Legacy ADO.NET callers commonly assign descriptive parameter names even when
                // their SQL uses anonymous placeholders. SQLite binds those in collection order.
                if (HasAnonymousNativeParameters(statement, parameterCount))
                    (positionalParameters ??= []).Add(parameter);
                continue;
            }

            statement.BindNative(parameterIndex, parameter.ToNativeValue());
            boundParameters[parameterIndex] = true;
        }

        var positionalParameterIndex = 0;
        for (var i = 1; i <= parameterCount; i++)
        {
            if (statement.GetNativeParameterName(i) is null
                && positionalParameters is not null
                && positionalParameterIndex < positionalParameters.Count)
            {
                statement.BindNative(i, positionalParameters[positionalParameterIndex++].ToNativeValue());
                boundParameters[i] = true;
            }

            if (!boundParameters[i])
            {
                var parameterName = statement.GetNativeParameterName(i);
                throw new InvalidOperationException(
                    parameterName is null
                        ? Properties.Resources.MissingParameters(i)
                        : Properties.Resources.MissingParameters(parameterName));
            }
        }

        if (positionalParameters is not null && positionalParameterIndex != positionalParameters.Count)
        {
            throw new InvalidOperationException(
                Properties.Resources.ParameterNotFound($"at position {positionalParameterIndex + 1}"));
        }
    }

    private void BindManagedParameters(IManagedStatementAdapter statement)
    {
        var parameterMetadata = statement.ParameterMetadata;
        var parameterCount = parameterMetadata.Count;
        var boundParameters = new bool[parameterCount + 1];
        var statementParameterNames = new string?[parameterCount + 1];
        var highestNumberedParameterIndex = 0;
        for (var i = 1; i <= parameterCount; i++)
        {
            var parameterName = parameterMetadata.GetParameter(i).Name;
            statementParameterNames[i] = parameterName;
            if (IsNumberedParameterName(parameterName, i))
                highestNumberedParameterIndex = i;
        }

        for (var i = 1; i < highestNumberedParameterIndex; i++)
        {
            if (statementParameterNames[i] is null)
            {
                throw new NotSupportedException(
                    "Numbered parameters with gaps or preceding unnamed parameters are not supported by Local Provider=Managed.");
            }
        }

        List<SqliteParameter>? positionalParameters = null;

        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            if (!parameter.HasValue)
                throw new InvalidOperationException(Properties.Resources.RequiresSet(nameof(parameter.Value)));

            if (string.IsNullOrEmpty(parameter.ParameterName))
            {
                (positionalParameters ??= []).Add(parameter);
                continue;
            }

            var parameterIndex = IsNumberedParameterName(parameter.ParameterName)
                ? parameterMetadata.GetParameterIndex(parameter.ParameterName)
                : FindManagedParameterIndex(parameterMetadata, parameter.ParameterName);
            if (parameterIndex == 0)
            {
                // See BindNativeParameters: named ADO.NET parameters bind anonymous SQLite
                // placeholders in collection order when no exact placeholder name exists.
                if (statementParameterNames.Skip(1).Any(static name => name is null))
                    (positionalParameters ??= []).Add(parameter);
                continue;
            }

            statement.Bind(parameterIndex, parameter.ToSqlValue());
            boundParameters[parameterIndex] = true;
        }

        var positionalParameterIndex = 0;
        for (var statementParameterIndex = 1; statementParameterIndex <= parameterCount; statementParameterIndex++)
        {
            if (statementParameterNames[statementParameterIndex] is not null)
                continue;
            if (positionalParameters is null || positionalParameterIndex == positionalParameters.Count)
                continue;

            var parameter = positionalParameters[positionalParameterIndex++];
            statement.Bind(statementParameterIndex, parameter.ToSqlValue());
            boundParameters[statementParameterIndex] = true;
        }

        if (positionalParameters is not null && positionalParameterIndex != positionalParameters.Count)
        {
            throw new InvalidOperationException(
                Properties.Resources.ParameterNotFound($"at position {positionalParameterIndex + 1}"));
        }

        for (var i = 1; i <= parameterCount; i++)
        {
            if (!boundParameters[i])
            {
                var parameterName = statementParameterNames[i];
                throw new InvalidOperationException(
                    parameterName is null
                        ? Properties.Resources.MissingParameters(i)
                        : Properties.Resources.MissingParameters(parameterName));
            }
        }
    }

    private static bool HasAnonymousNativeParameters(SqliteStatementAdapter statement, int parameterCount)
    {
        for (var i = 1; i <= parameterCount; i++)
        {
            if (statement.GetNativeParameterName(i) is null)
                return true;
        }

        return false;
    }

    private static bool IsEmptyCommand(string commandText)
    {
        foreach (var line in commandText.Split('\n'))
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.Length != 0 && !trimmedLine.StartsWith("--", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsTransactionControlCommand(string commandText)
        => SqlTransactionControl.GetCompletion(commandText) != SqlTransactionCompletion.None;

    private static bool IsRollbackCommand(string commandText)
        => SqlTransactionControl.GetCompletion(commandText) == SqlTransactionCompletion.Rollback;

    private static bool IsWriteCommand(string commandText)
        => SplitStatements(commandText).Any(IsWriteStatement);

    private static bool IsReaderBlockingCommand(string commandText)
        => SplitStatements(commandText).Any(statement =>
        {
            var firstKeyword = SqlTransactionControl.GetFirstKeyword(statement);
            return IsWriteStatement(statement)
                   || firstKeyword?.Equals("ATTACH", StringComparison.OrdinalIgnoreCase) == true
                   || firstKeyword?.Equals("DETACH", StringComparison.OrdinalIgnoreCase) == true;
        });

    private static bool IsWriteStatement(string statement)
    {
        var firstKeyword = SqlTransactionControl.GetFirstKeyword(statement);
        return firstKeyword is not null
               && (firstKeyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("DROP", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("VACUUM", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("WITH", StringComparison.OrdinalIgnoreCase)
                   && IsWithDmlStatement(statement));
    }

    internal bool TryHandleFacadeStatement(string sql, out string rewrittenSql)
    {
        var connection = Connection!;
        var normalized = NormalizeSql(sql);
        if (TryParseReadUncommittedSetter(normalized, out var enabled))
        {
            connection.ReadUncommitted = enabled;
            rewrittenSql = EmptyResultSql;
            return true;
        }

        rewrittenSql = RewriteUnsupportedPragmas(normalized, sql, connection);
        return false;
    }

    private const string EmptyResultSql = "SELECT 1 WHERE 0";

    private static string RewriteFacadeStatement(string sql, SqliteConnection connection)
        => RewriteUnsupportedPragmas(NormalizeSql(sql), sql, connection);

    private static string RewriteUnsupportedPragmas(string normalized, string sql, SqliteConnection connection)
    {
        if (normalized.Equals("PRAGMA recursive_triggers", StringComparison.OrdinalIgnoreCase))
            return "SELECT " + (connection.RecursiveTriggers ? "1" : "0");
        if (TryParseReadUncommittedSetter(normalized, out _))
            return EmptyResultSql;
        if (normalized.Equals("PRAGMA read_uncommitted", StringComparison.OrdinalIgnoreCase))
            return "SELECT " + (connection.ReadUncommitted ? "1" : "0");
        if (normalized.Equals("PRAGMA compile_options", StringComparison.OrdinalIgnoreCase))
            return "SELECT CAST(NULL AS TEXT) AS compile_options WHERE 0";
        if (normalized.IndexOf("pragma_compile_options", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return normalized.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0
                ? "SELECT 0"
                : "SELECT CAST(NULL AS TEXT) AS compile_options WHERE 0";
        }

        return RewriteBinaryGuidTextCasts(sql, connection);
    }

    private static string RewriteBinaryGuidTextCasts(string sql, SqliteConnection connection)
    {
        if (!connection.BinaryGuid)
            return sql;

        StringBuilder? rewritten = null;
        var copiedThrough = 0;
        var offset = 0;
        while (TryReadScriptToken(sql, ref offset, out var token))
        {
            if (!IsKeyword(sql, token, "CAST")
                || !TryReadBinaryGuidTextCast(sql, token, out var column, out var castEnd))
            {
                continue;
            }

            rewritten ??= new StringBuilder(sql.Length + 96);
            rewritten.Append(sql, copiedThrough, token.Offset - copiedThrough);
            rewritten.Append(CreateBinaryGuidTextExpression(column));
            copiedThrough = castEnd;
            offset = castEnd;
        }

        if (rewritten is null)
            return sql;

        rewritten.Append(sql, copiedThrough, sql.Length - copiedThrough);
        return rewritten.ToString();
    }

    private static bool TryReadBinaryGuidTextCast(
        string sql,
        ScriptToken cast,
        out string column,
        out int castEnd)
    {
        column = string.Empty;
        castEnd = cast.Offset;
        var offset = cast.Offset + cast.Length;
        if (!TryReadScriptToken(sql, ref offset, out var token)
            || !IsCharacter(sql, token, '(')
            || !TryReadScriptToken(sql, ref offset, out var firstColumnToken)
            || !IsIdentifier(firstColumnToken))
        {
            return false;
        }

        var columnStart = firstColumnToken.Offset;
        var terminalColumnToken = firstColumnToken;
        while (TryReadScriptToken(sql, ref offset, out token) && IsDot(sql, token))
        {
            if (!TryReadScriptToken(sql, ref offset, out terminalColumnToken)
                || !IsIdentifier(terminalColumnToken))
            {
                return false;
            }
        }

        if (!IsKeyword(sql, token, "AS")
            || !TryReadScriptToken(sql, ref offset, out token)
            || !IsKeyword(sql, token, "TEXT")
            || !TryReadScriptToken(sql, ref offset, out token)
            || !IsCharacter(sql, token, ')'))
        {
            return false;
        }

        var identifier = UnquoteIdentifier(sql.AsSpan(terminalColumnToken.Offset, terminalColumnToken.Length));
        if (!identifier.EndsWith("ID", StringComparison.OrdinalIgnoreCase))
            return false;

        column = sql[columnStart..(terminalColumnToken.Offset + terminalColumnToken.Length)];
        castEnd = token.Offset + token.Length;
        return true;
    }

    private static string CreateBinaryGuidTextExpression(string column)
    {
        var hex = $"hex({column})";
        return $"CASE WHEN typeof({column}) = 'blob' AND length({column}) = 16 THEN lower("
            + $"substr({hex}, 7, 2) || substr({hex}, 5, 2) || substr({hex}, 3, 2) || substr({hex}, 1, 2) || '-' || "
            + $"substr({hex}, 11, 2) || substr({hex}, 9, 2) || '-' || "
            + $"substr({hex}, 15, 2) || substr({hex}, 13, 2) || '-' || "
            + $"substr({hex}, 17, 4) || '-' || substr({hex}, 21, 12)) "
            + $"ELSE CAST({column} AS TEXT) END";
    }

    private static bool IsCharacter(string sql, ScriptToken token, char value)
        => token.Kind == ScriptTokenKind.Other
           && token.Length == 1
           && sql[token.Offset] == value;

    private static string UnquoteIdentifier(ReadOnlySpan<char> identifier)
    {
        if (identifier.Length < 2)
            return identifier.ToString();

        return (identifier[0], identifier[^1]) switch
        {
            ('"', '"') => identifier[1..^1].ToString().Replace("\"\"", "\"", StringComparison.Ordinal),
            ('[', ']') => identifier[1..^1].ToString().Replace("]]", "]", StringComparison.Ordinal),
            ('`', '`') => identifier[1..^1].ToString().Replace("``", "`", StringComparison.Ordinal),
            _ => identifier.ToString()
        };
    }

    private static string NormalizeSql(string sql)
        => sql.Trim().TrimEnd(';').Trim();

    private static bool TryParseReadUncommittedSetter(string normalized, out bool enabled)
    {
        enabled = false;
        const string prefix = "PRAGMA read_uncommitted";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (normalized.Length == prefix.Length)
            return false;

        var value = normalized[prefix.Length..].TrimStart();
        if (value.StartsWith("=", StringComparison.Ordinal))
            value = value[1..].Trim();
        else if (value.StartsWith("(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
            value = value[1..^1].Trim();
        else
            return false;

        enabled = AhtolaCommand.ParsePragmaEnabled(value);
        return true;
    }

    internal static bool CountsRowsAffected(string commandText)
    {
        var firstStatement = SplitStatements(commandText).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstStatement))
            return false;

        var firstKeyword = SqlTransactionControl.GetFirstKeyword(firstStatement);
        return firstKeyword is not null
               && (firstKeyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
                   || firstKeyword.Equals("WITH", StringComparison.OrdinalIgnoreCase)
                   && IsWithDmlStatement(firstStatement));
    }

    private static bool IsWithDmlStatement(string statement)
    {
        var offset = 0;
        if (!TryReadScriptToken(statement, ref offset, out var token)
            || !IsKeyword(statement, token, "WITH"))
        {
            return false;
        }

        var parenthesisDepth = 0;
        var completedCte = false;
        while (TryReadScriptToken(statement, ref offset, out token))
        {
            if (token.Kind == ScriptTokenKind.Semicolon)
                return false;
            if (token.Kind == ScriptTokenKind.Other && token.Length == 1)
            {
                switch (statement[token.Offset])
                {
                    case '(':
                        parenthesisDepth++;
                        continue;
                    case ')' when parenthesisDepth > 0:
                        parenthesisDepth--;
                        completedCte |= parenthesisDepth == 0;
                        continue;
                    case ',' when parenthesisDepth == 0:
                        completedCte = false;
                        continue;
                }
            }

            if (parenthesisDepth != 0 || !completedCte)
                continue;
            if (IsKeyword(statement, token, "INSERT")
                || IsKeyword(statement, token, "UPDATE")
                || IsKeyword(statement, token, "DELETE")
                || IsKeyword(statement, token, "REPLACE"))
            {
                return true;
            }
            if (IsKeyword(statement, token, "SELECT")
                || IsKeyword(statement, token, "VALUES"))
            {
                return false;
            }
        }

        return false;
    }

    private static List<string> SplitStatements(string commandText)
    {
        var statements = new List<string>();
        var start = 0;
        var firstTokenInStatement = true;
        var triggerHeader = TriggerHeader.None;
        var triggerBlockDepth = 0;
        var triggerBodyAtStatementStart = false;
        var offset = 0;

        while (TryReadScriptToken(commandText, ref offset, out var token))
        {
            if (token.Kind == ScriptTokenKind.Semicolon)
            {
                if (triggerBlockDepth > 0)
                {
                    triggerBodyAtStatementStart = true;
                }
                else
                {
                    AddStatement(commandText, start, token.Offset, statements);
                    start = token.Offset + token.Length;
                    firstTokenInStatement = true;
                    triggerHeader = TriggerHeader.None;
                }

                continue;
            }

            if (triggerBlockDepth > 0)
            {
                if (triggerBodyAtStatementStart)
                {
                    // Only complete trigger-body statements can close a trigger, so CASE ... END
                    // expressions and words inside strings never affect the outer boundary.
                    if (IsKeyword(commandText, token, "BEGIN"))
                        triggerBlockDepth++;
                    else if (IsKeyword(commandText, token, "END"))
                        triggerBlockDepth--;

                    triggerBodyAtStatementStart = false;
                }

                continue;
            }

            if (firstTokenInStatement)
            {
                firstTokenInStatement = false;
                triggerHeader = IsKeyword(commandText, token, "CREATE")
                    ? TriggerHeader.ExpectTrigger
                    : TriggerHeader.NotTrigger;
            }
            else
            {
                triggerHeader = AdvanceTriggerHeader(
                    commandText,
                    triggerHeader,
                    token,
                    ref triggerBlockDepth,
                    ref triggerBodyAtStatementStart);
            }
        }

        AddStatement(commandText, start, commandText.Length, statements);
        return statements;
    }

    private static TriggerHeader AdvanceTriggerHeader(
        string sql,
        TriggerHeader header,
        ScriptToken token,
        ref int triggerBlockDepth,
        ref bool triggerBodyAtStatementStart)
    {
        return header switch
        {
            TriggerHeader.ExpectTrigger => IsKeyword(sql, token, "TEMP")
                || IsKeyword(sql, token, "TEMPORARY")
                    ? TriggerHeader.ExpectTrigger
                    : IsKeyword(sql, token, "TRIGGER")
                        ? TriggerHeader.ExpectNameOrIf
                        : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectNameOrIf => IsKeyword(sql, token, "IF")
                ? TriggerHeader.ExpectNot
                : IsIdentifier(token)
                    ? TriggerHeader.SeekOn
                    : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectNot => IsKeyword(sql, token, "NOT")
                ? TriggerHeader.ExpectExists
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectExists => IsKeyword(sql, token, "EXISTS")
                ? TriggerHeader.ExpectName
                : TriggerHeader.NotTrigger,
            TriggerHeader.ExpectName => IsIdentifier(token)
                ? TriggerHeader.SeekOn
                : TriggerHeader.NotTrigger,
            TriggerHeader.SeekOn => IsKeyword(sql, token, "ON")
                ? TriggerHeader.ExpectTable
                : TriggerHeader.SeekOn,
            TriggerHeader.ExpectTable => IsIdentifier(token)
                ? TriggerHeader.AfterTableName
                : TriggerHeader.NotTrigger,
            TriggerHeader.AfterTableName => IsDot(sql, token)
                ? TriggerHeader.ExpectTableLocal
                : IsKeyword(sql, token, "BEGIN")
                    ? EnterTriggerBody(sql, token, ref triggerBlockDepth, ref triggerBodyAtStatementStart)
                    : TriggerHeader.SeekBegin,
            TriggerHeader.ExpectTableLocal => IsIdentifier(token)
                ? TriggerHeader.SeekBegin
                : TriggerHeader.NotTrigger,
            TriggerHeader.SeekBegin => IsKeyword(sql, token, "BEGIN")
                ? EnterTriggerBody(sql, token, ref triggerBlockDepth, ref triggerBodyAtStatementStart)
                : TriggerHeader.SeekBegin,
            _ => header,
        };
    }

    private static TriggerHeader EnterTriggerBody(
        string sql,
        ScriptToken token,
        ref int triggerBlockDepth,
        ref bool triggerBodyAtStatementStart)
    {
        if (!IsKeyword(sql, token, "BEGIN"))
            return TriggerHeader.NotTrigger;

        triggerBlockDepth = 1;
        triggerBodyAtStatementStart = true;
        return TriggerHeader.None;
    }

    private static bool IsKeyword(string sql, ScriptToken token, string keyword)
        => token.Kind == ScriptTokenKind.Identifier
           && token.Length == keyword.Length
           && sql.AsSpan(token.Offset, token.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifier(ScriptToken token)
        => token.Kind is ScriptTokenKind.Identifier or ScriptTokenKind.QuotedIdentifier;

    private static bool IsDot(string sql, ScriptToken token)
        => token.Kind == ScriptTokenKind.Other
            && token.Length == 1
            && sql[token.Offset] == '.';

    private static bool TryReadScriptToken(string sql, ref int offset, out ScriptToken token)
    {
        SkipWhitespaceAndComments(sql, ref offset, out var unterminatedComment);
        if (unterminatedComment)
        {
            token = new ScriptToken(ScriptTokenKind.Malformed, offset, 0);
            return true;
        }

        if (offset == sql.Length)
        {
            token = default;
            return false;
        }

        var start = offset;
        var current = sql[offset++];
        switch (current)
        {
            case ';':
                token = new ScriptToken(ScriptTokenKind.Semicolon, start, 1);
                return true;
            case '\'':
                token = new ScriptToken(
                    ReadDelimitedToken(sql, ref offset, '\'')
                        ? ScriptTokenKind.Other
                        : ScriptTokenKind.Malformed,
                    start,
                    offset - start);
                return true;
            case '"':
            case '[':
            case '`':
                var closingCharacter = current == '[' ? ']' : current;
                token = new ScriptToken(
                    ReadDelimitedToken(sql, ref offset, closingCharacter)
                        ? ScriptTokenKind.QuotedIdentifier
                        : ScriptTokenKind.Malformed,
                    start,
                    offset - start);
                return true;
            default:
                if (IsIdentifierStart(current))
                {
                    while (offset < sql.Length && IsIdentifierContinuation(sql[offset]))
                        offset++;

                    token = new ScriptToken(ScriptTokenKind.Identifier, start, offset - start);
                    return true;
                }

                token = new ScriptToken(ScriptTokenKind.Other, start, 1);
                return true;
        }
    }

    private static void SkipWhitespaceAndComments(string sql, ref int offset, out bool unterminatedComment)
    {
        unterminatedComment = false;
        while (offset < sql.Length)
        {
            if (char.IsWhiteSpace(sql[offset]))
            {
                offset++;
                continue;
            }

            if (offset + 1 < sql.Length && sql[offset] == '-' && sql[offset + 1] == '-')
            {
                offset += 2;
                while (offset < sql.Length && sql[offset] is not '\r' and not '\n')
                    offset++;
                continue;
            }

            if (offset + 1 < sql.Length && sql[offset] == '/' && sql[offset + 1] == '*')
            {
                offset += 2;
                while (offset + 1 < sql.Length && (sql[offset] != '*' || sql[offset + 1] != '/'))
                    offset++;

                if (offset + 1 >= sql.Length)
                {
                    offset = sql.Length;
                    unterminatedComment = true;
                    return;
                }

                offset += 2;
                continue;
            }

            return;
        }
    }

    private static bool ReadDelimitedToken(string sql, ref int offset, char closingCharacter)
    {
        while (offset < sql.Length)
        {
            if (sql[offset++] != closingCharacter)
                continue;

            if (offset < sql.Length && sql[offset] == closingCharacter)
            {
                offset++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsIdentifierStart(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '_';

    private static bool IsIdentifierContinuation(char value)
        => IsIdentifierStart(value)
            || value is >= '0' and <= '9'
            or '$';

    private static void AddStatement(string sql, int start, int end, List<string> statements)
    {
        var statement = sql[start..end].Trim();
        var offset = 0;
        if (statement.Length != 0 && TryReadScriptToken(statement, ref offset, out _))
            statements.Add(statement);
    }

    private enum TriggerHeader
    {
        None,
        NotTrigger,
        ExpectTrigger,
        ExpectNameOrIf,
        ExpectNot,
        ExpectExists,
        ExpectName,
        SeekOn,
        ExpectTable,
        AfterTableName,
        ExpectTableLocal,
        SeekBegin,
    }

    private enum ScriptTokenKind
    {
        Identifier,
        QuotedIdentifier,
        Semicolon,
        Other,
        Malformed,
    }

    private readonly record struct ScriptToken(ScriptTokenKind Kind, int Offset, int Length);

    internal static SqliteException ToSqliteException(Exception ex, string? sql = null)
    {
        if (ex is EmbeddedBusyException)
            return new SqliteException(Properties.Resources.SqliteNativeError(5, ex.Message), 5);

        if (TryGetSqliteErrorCode(ex) is { } sqliteErrorCode)
        {
            var codedMessage = UnwrapMessage(ex);
            return new SqliteException(
                Properties.Resources.SqliteNativeError(sqliteErrorCode, codedMessage),
                sqliteErrorCode);
        }

        var message = ex.Message;
        foreach (var prefix in new[] { "Unable to prepare statement: Parse error: ", "Parse error: " })
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                message = message[prefix.Length..];
                break;
            }
        }
        if (message.StartsWith("Extension error: ", StringComparison.OrdinalIgnoreCase))
            message = message["Extension error: ".Length..];
        if (message.StartsWith("Error: cannot use aggregate, window functions or reference other tables in WHERE clause of CREATE INDEX", StringComparison.Ordinal))
            message = "non-deterministic functions prohibited in partial index WHERE clauses";
        const string sqliteErrorPrefix = "__ahtola_sqlite_error__:";
        if (message.StartsWith(sqliteErrorPrefix, StringComparison.Ordinal))
        {
            var codeEnd = message.IndexOf(':', sqliteErrorPrefix.Length);
            if (codeEnd > sqliteErrorPrefix.Length
                && int.TryParse(message[sqliteErrorPrefix.Length..codeEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorCode))
            {
                var sqliteMessage = message[(codeEnd + 1)..];
                return new SqliteException(Properties.Resources.SqliteNativeError(errorCode, sqliteMessage), errorCode);
            }
        }

        if (sql is not null)
            message = PreserveNoSuchTableCase(message, sql);

        return new SqliteException(Properties.Resources.SqliteNativeError(1, message), 1);
    }

    private static int? TryGetSqliteErrorCode(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is EmbeddedSqlException { SqliteErrorCode: int code })
                return code;
        }

        return null;
    }

    private static string UnwrapMessage(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is EmbeddedSqlException { SqliteErrorCode: not null })
                return current.Message;
        }

        return ex.Message;
    }

    private static string PreserveNoSuchTableCase(string message, string sql)
    {
        const string noSuchTable = "no such table: ";
        if (!message.StartsWith(noSuchTable, StringComparison.OrdinalIgnoreCase))
            return message;

        var tableName = message[noSuchTable.Length..];
        var sqlSpan = sql.AsSpan();
        for (var i = 0; i <= sqlSpan.Length - tableName.Length; i++)
        {
            if (MemoryExtensions.Equals(sqlSpan.Slice(i, tableName.Length), tableName, StringComparison.OrdinalIgnoreCase))
                return noSuchTable + sql.Substring(i, tableName.Length);
        }

        return message;
    }

    private static int FindNativeParameterIndex(SqliteStatementAdapter statement, string parameterName, int parameterCount)
    {
        var index = FindExactNativeParameterIndex(statement, parameterName, parameterCount);
        if (index != 0 || IsPrefixed(parameterName))
            return index;

        foreach (var prefix in new[] { '@', '$', ':' })
        {
            var prefixedIndex = FindExactNativeParameterIndex(statement, prefix + parameterName, parameterCount);
            if (prefixedIndex == 0)
                continue;

            if (index != 0)
                throw new InvalidOperationException(Properties.Resources.AmbiguousParameterName(parameterName));

            index = prefixedIndex;
        }

        return index;
    }

    private static int FindExactNativeParameterIndex(SqliteStatementAdapter statement, string parameterName, int parameterCount)
    {
        for (var i = 1; i <= parameterCount; i++)
        {
            if (string.Equals(statement.GetNativeParameterName(i), parameterName, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static int FindManagedParameterIndex(ManagedParameterMetadata parameterMetadata, string parameterName)
    {
        var index = FindExactManagedParameterIndex(parameterMetadata, parameterName);
        if (index != 0 || IsPrefixed(parameterName))
            return index;

        foreach (var prefix in new[] { '@', '$', ':' })
        {
            var prefixedIndex = FindExactManagedParameterIndex(parameterMetadata, prefix + parameterName);
            if (prefixedIndex == 0)
                continue;

            if (index != 0)
                throw new InvalidOperationException(Properties.Resources.AmbiguousParameterName(parameterName));

            index = prefixedIndex;
        }

        return index;
    }

    private static int FindExactManagedParameterIndex(ManagedParameterMetadata parameterMetadata, string parameterName)
    {
        for (var i = 1; i <= parameterMetadata.Count; i++)
        {
            if (string.Equals(parameterMetadata.GetParameter(i).Name, parameterName, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static bool IsPrefixed(string parameterName)
        => parameterName.Length > 0 && parameterName[0] is '@' or '$' or ':';

    private static bool IsNumberedParameterName(string? parameterName, int? expectedIndex = null)
        => parameterName is { Length: > 1 }
           && parameterName[0] == '?'
           && int.TryParse(parameterName.AsSpan(1), out var index)
           && index > 0
           && (expectedIndex is null || index == expectedIndex);
}
