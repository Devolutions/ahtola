using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ahtola.Core;

namespace Ahtola;

public class AhtolaCommand : DbCommand
{
    private AhtolaConnection? _connection;
    private readonly AhtolaParameterCollection _parameterCollection = new();

    private AhtolaTransaction? _transaction;
    private AhtolaNativeStatement? _nativeStatement;
    private IManagedStatementAdapter? _managedStatement;
    private int _commandTimeout = 30;
    private readonly CommandCancellationController _cancellation = new();

    public AhtolaCommand()
    {
    }

    public AhtolaCommand(AhtolaConnection connection, AhtolaTransaction? transaction = null)
    {
        _connection = connection;
        connection.CommandOpened(this);
        _transaction = transaction;
        _commandTimeout = connection.DefaultTimeout;
    }

    public AhtolaCommand(AhtolaConnection connection, string command)
    {
        _connection = connection;
        connection.CommandOpened(this);
        _transaction = null;
        _commandTimeout = connection.DefaultTimeout;
        CommandText = command;
    }

    [AllowNull]
    public override string CommandText { get; set; } = "";
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
                throw new NotSupportedException("AhtolaCommand only supports CommandType.Text.");
        }
    }

    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is null)
            {
                _connection?.CommandClosed(this);
                _connection = null;
                return;
            }

            var connection = value as AhtolaConnection
                            ?? throw new ArgumentException("Connection must be a AhtolaConnection.", nameof(value));
            if (ReferenceEquals(connection, _connection))
                return;

            _nativeStatement?.Dispose();
            _managedStatement?.Dispose();
            _nativeStatement = null;
            _managedStatement = null;
            _connection?.CommandClosed(this);
            _connection = connection;
            connection.CommandOpened(this);
            _commandTimeout = _connection.DefaultTimeout;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _parameterCollection;

    public new virtual AhtolaParameterCollection Parameters => _parameterCollection;


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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation.Cancel();
            _nativeStatement?.Dispose();
            _managedStatement?.Dispose();
        }

        base.Dispose(disposing);
        _nativeStatement = null;
        _managedStatement = null;
        _connection?.CommandClosed(this);
    }

    internal void ResetFromConnection()
    {
        _nativeStatement?.Dispose();
        _managedStatement?.Dispose();
        _nativeStatement = null;
        _managedStatement = null;
    }

    public override void Cancel() => _cancellation.Cancel();

    public override int ExecuteNonQuery()
    {
        ThrowIfSynchronousBrowserOperation("ExecuteNonQueryAsync");
        if (_connection?.IsRemote == true)
        {
            return _cancellation
                .RunAsync(ExecuteRemoteNonQueryAsync, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        using var reader = _cancellation.Run(token => Execute(CommandBehavior.Default, token));
        while (reader.Read())
        {
        }

        return reader.RecordsAffected;
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        if (_connection?.IsRemote == true)
        {
            return await _cancellation
                .RunAsync(ExecuteRemoteNonQueryAsync, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        return reader.RecordsAffected;
    }

    public override object? ExecuteScalar()
    {
        ThrowIfSynchronousBrowserOperation("ExecuteScalarAsync");
        using var reader = _cancellation.Run(token => Execute(CommandBehavior.Default, token));
        var result = reader.Read()
            ? reader.GetValue(0)
            : null;
        return result;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? reader.GetValue(0)
            : null;
        return result;
    }

    public override void Prepare()
    {
        ThrowIfSynchronousBrowserOperation("PrepareAsync");
        using var replicaOperation = _connection?.EnterManagedReplicaOperation(CancellationToken.None);
        PrepareCore();
    }

    internal bool RequiresAsyncExecution
        => _connection?.RequiresAsyncExecution == true;

    private void ThrowIfSynchronousBrowserOperation(string asyncAlternative)
    {
        if (RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                $"Synchronous command execution is not supported by the browser database source. "
                + $"Use {asyncAlternative}.");
        }
    }

    private void PrepareCore()
    {
        if (_connection is null)
            throw new InvalidOperationException("Connection must be set before preparing a command.");
        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must be set before preparing a command.");
        ValidateTransaction();
        _connection.ValidateCommandCapabilities(CommandText);
        if (_connection.IsManagedReadOnly)
            ManagedReadOnlySqlGuard.ThrowIfQueryOnlyIsDisabled(CommandText);
        if (_connection.IsRemote)
            return;

        if (_connection.IsManaged)
        {
            IManagedStatementAdapter? managedStatement = null;
            try
            {
                var sql = RewriteFacadePragmas(CommandText, _connection);
                _connection.ManagedConnection.BusyTimeout = CommandTimeout == 0
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromSeconds(CommandTimeout);
                managedStatement = _connection.ManagedConnection.Prepare(sql);
                BindManagedParameters(managedStatement, sql);
                _ = managedStatement.ResultMetadata.ColumnCount;

                _nativeStatement?.Dispose();
                _nativeStatement = null;
                _managedStatement?.Dispose();
                _managedStatement = managedStatement;
                managedStatement = null;
                return;
            }
            catch (EmbeddedSqlException exception)
            {
                throw AhtolaException.FromCorePreparation(exception);
            }
            finally
            {
                managedStatement?.Dispose();
            }
        }

        AhtolaNativeStatement? preparedStatement = null;
        try
        {
            var sql = RewriteFacadePragmas(CommandText, _connection);
            _connection.NativeDatabase.SetBusyTimeout(
                CommandTimeout == 0
                    ? TimeSpan.MaxValue
                    : TimeSpan.FromSeconds(CommandTimeout));
            preparedStatement = _connection.NativeDatabase.PrepareStatement(sql);
            var bindings = AhtolaParameterBindings.Create(sql, _parameterCollection);
            if (preparedStatement.ParameterCount != bindings.Map.Count)
            {
                throw new InvalidOperationException(
                    "The native provider reported parameter metadata that does not match the SQL statement.");
            }

            for (var index = 1; index <= bindings.Map.Count; index++)
            {
                var lexerName = bindings.Map.GetName(index);
                var nativeName = preparedStatement.GetParameterName(index);
                if (bindings.Map.IsReferenced(index)
                    && !string.Equals(lexerName, nativeName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The native provider reported parameter {nativeName ?? $"?{index}"} where the SQL statement references {lexerName ?? $"?{index}"}.");
                }
                if (bindings.Map.IsReferenced(index))
                    preparedStatement.BindParameter(index, bindings.GetParameter(index).ToValue());
            }

            _nativeStatement?.Dispose();
            _nativeStatement = preparedStatement;
            preparedStatement = null;
            _managedStatement?.Dispose();
            _managedStatement = null;
        }
        finally
        {
            preparedStatement?.Dispose();
        }
    }

    protected override DbParameter CreateDbParameter()
    {
        return new AhtolaParameter();
    }


    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ThrowIfSynchronousBrowserOperation("ExecuteReaderAsync");
        return _cancellation.Run(token => Execute(behavior, token));
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (_connection?.IsRemote == true)
        {
            return _cancellation.RunAsync(
                token => ExecuteRemoteAsync(behavior, token),
                cancellationToken);
        }

        if (_connection?.IsManaged == true)
        {
            return _cancellation.RunAsync<DbDataReader>(
                token => ExecuteManagedAsync(behavior, token),
                cancellationToken);
        }

        return _cancellation.RunAsync<DbDataReader>(
            token => Execute(behavior, token),
            cancellationToken);
    }

    private static string RewriteFacadePragmas(string sql, AhtolaConnection connection)
    {
        var normalized = sql.Trim().TrimEnd(';').Trim();
        const string prefix = "PRAGMA read_uncommitted";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return sql;
        if (normalized.Length == prefix.Length)
            return "SELECT " + (connection.ReadUncommitted ? "1" : "0");

        var value = normalized[prefix.Length..].TrimStart();
        if (value.StartsWith("=", StringComparison.Ordinal))
        {
            connection.ReadUncommitted = ParsePragmaEnabled(value[1..].Trim());
            return "SELECT 1 WHERE 0";
        }
        if (connection.IsManaged
            && value.StartsWith("(", StringComparison.Ordinal)
            && value.EndsWith(")", StringComparison.Ordinal))
        {
            connection.ReadUncommitted = ParsePragmaEnabled(value[1..^1].Trim());
            return "SELECT 1 WHERE 0";
        }

        return sql;
    }

    internal static bool ParsePragmaEnabled(string value)
    {
        var quoted = value.Length >= 2
                     && ((value[0] == '\'' && value[^1] == '\'')
                         || (value[0] == '"' && value[^1] == '"'));
        if (quoted)
            value = value[1..^1];
        else if (value.StartsWith("+", StringComparison.Ordinal))
            value = value[1..];
        if (value.Length > 0 && char.IsAsciiDigit(value[0]))
            return ParseSqlitePragmaInteger(value) is { } integer && (byte)integer != 0;

        return value.Equals("ON", StringComparison.OrdinalIgnoreCase)
               || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               || value.Equals("YES", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseSqlitePragmaInteger(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var end = 2;
            while (end < value.Length && Uri.IsHexDigit(value[end]))
                end++;
            if (end == 2)
                return 0;
            return uint.TryParse(
                    value.AsSpan(2, end - 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var hexadecimal)
                   && hexadecimal <= int.MaxValue
                ? (int)hexadecimal
                : null;
        }

        var length = 0;
        while (length < value.Length && char.IsAsciiDigit(value[length]))
            length++;
        return int.TryParse(
            value.AsSpan(0, length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var decimalInteger)
            ? decimalInteger
            : null;
    }

    private DbDataReader Execute(
        CommandBehavior behavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
            throw new InvalidOperationException("Connection must be set before executing a command.");

        if (_connection.IsRemote)
            return ExecuteRemoteAsync(behavior, cancellationToken).GetAwaiter().GetResult();

        IDisposable? replicaOperation = null;
        var beganManagedReplicaTransaction = false;
        try
        {
            beganManagedReplicaTransaction =
                _connection.BeginManagedReplicaSqlTransaction(CommandText, cancellationToken);
            replicaOperation = _connection.EnterManagedReplicaOperation(cancellationToken);
            Prepare();
            cancellationToken.ThrowIfCancellationRequested();

            var nativeStatement = _nativeStatement;
            var managedStatement = _managedStatement;
            if (managedStatement is null && nativeStatement is null)
                throw new InvalidOperationException("Command was not prepared.");
            _nativeStatement = null;
            _managedStatement = null;
            var transactionCompletion = SqlTransactionControl.GetCompletion(CommandText);
            _connection.ManagedReplicaStatementStarted(CommandText);
            var reader = new AhtolaDataReader(
                this,
                nativeStatement,
                managedStatement,
                behavior,
                () =>
                {
                    MarkTransactionCompletedExternally(transactionCompletion);
                    _connection.ManagedReplicaStatementCompleted(CommandText);
                },
                _connection.ManagedReplicaStatementFailed,
                _connection.ManagedReplicaStatementClosed,
                replicaOperation);
            replicaOperation = null;
            return reader;
        }
        catch
        {
            replicaOperation?.Dispose();
            if (beganManagedReplicaTransaction)
                _connection.ManagedReplicaStatementFailed();
            throw;
        }
    }

    private async ValueTask<DbDataReader> ExecuteManagedAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
            throw new InvalidOperationException("Connection must be set before executing a command.");

        IDisposable? replicaOperation = null;
        var beganManagedReplicaTransaction = false;
        try
        {
            beganManagedReplicaTransaction =
                _connection.BeginManagedReplicaSqlTransaction(CommandText, cancellationToken);
            replicaOperation = _connection.EnterManagedReplicaOperation(cancellationToken);
            await PrepareManagedCoreAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var managedStatement = _managedStatement
                ?? throw new InvalidOperationException("Command was not prepared.");
            _managedStatement = null;
            var transactionCompletion = SqlTransactionControl.GetCompletion(CommandText);
            _connection.ManagedReplicaStatementStarted(CommandText);
            var reader = new AhtolaDataReader(
                this,
                nativeStatement: null,
                managedStatement,
                behavior,
                () =>
                {
                    MarkTransactionCompletedExternally(transactionCompletion);
                    _connection.ManagedReplicaStatementCompleted(CommandText);
                },
                _connection.ManagedReplicaStatementFailed,
                _connection.ManagedReplicaStatementClosed,
                replicaOperation);
            replicaOperation = null;
            return reader;
        }
        catch
        {
            replicaOperation?.Dispose();
            if (beganManagedReplicaTransaction)
                _connection.ManagedReplicaStatementFailed();
            throw;
        }
    }

    private async ValueTask PrepareManagedCoreAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
            throw new InvalidOperationException("Connection must be set before preparing a command.");
        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must be set before preparing a command.");
        ValidateTransaction();
        _connection.ValidateCommandCapabilities(CommandText);
        if (_connection.IsManagedReadOnly)
            ManagedReadOnlySqlGuard.ThrowIfQueryOnlyIsDisabled(CommandText);

        IManagedStatementAdapter? managedStatement = null;
        try
        {
            var sql = RewriteFacadePragmas(CommandText, _connection);
            _connection.ManagedConnection.BusyTimeout = CommandTimeout == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(CommandTimeout);
            managedStatement = await _connection.ManagedConnection
                .PrepareAsync(sql, cancellationToken)
                .ConfigureAwait(false);
            BindManagedParameters(managedStatement, sql);
            _ = managedStatement.ResultMetadata.ColumnCount;

            _nativeStatement?.Dispose();
            _nativeStatement = null;
            if (_managedStatement is not null)
                await _managedStatement.DisposeAsync().ConfigureAwait(false);
            _managedStatement = managedStatement;
            managedStatement = null;
        }
        catch (EmbeddedSqlException exception)
        {
            throw AhtolaException.FromCorePreparation(exception);
        }
        finally
        {
            if (managedStatement is not null)
                await managedStatement.DisposeAsync().ConfigureAwait(false);
        }
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

    private void BindManagedParameters(IManagedStatementAdapter statement, string sql)
    {
        var bindings = AhtolaParameterBindings.Create(sql, _parameterCollection);
        for (var index = 1; index <= bindings.Map.Count; index++)
        {
            if (bindings.Map.IsReferenced(index))
                statement.Bind(index, bindings.GetParameter(index).ToSqlValue());
        }
    }

    private async Task<DbDataReader> ExecuteRemoteAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (_connection is null)
            throw new InvalidOperationException("Connection must be set before executing a command.");
        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must be set before executing a command.");
        ValidateTransaction();
        _connection.ValidateCommandCapabilities(CommandText);

        cancellationToken.ThrowIfCancellationRequested();

        var transactionCompletion = SqlTransactionControl.GetCompletion(CommandText);
        var sql = RewriteFacadePragmas(CommandText, _connection);
        var result = await _connection
            .ExecuteRemoteAsync(sql, _parameterCollection, wantRows: true, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        MarkTransactionCompletedExternally(transactionCompletion);
        return new AhtolaRemoteDataReader(this, result, behavior);
    }

    private async Task<int> ExecuteRemoteNonQueryAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
            throw new InvalidOperationException("Connection must be set before executing a command.");
        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must be set before executing a command.");
        ValidateTransaction();
        _connection.ValidateCommandCapabilities(CommandText);

        cancellationToken.ThrowIfCancellationRequested();

        var transactionCompletion = SqlTransactionControl.GetCompletion(CommandText);
        var sql = RewriteFacadePragmas(CommandText, _connection);
        var result = await _connection
            .ExecuteRemoteAsync(sql, _parameterCollection, wantRows: false, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        MarkTransactionCompletedExternally(transactionCompletion);
        return checked((int)result.AffectedRowCount);
    }

    private void MarkTransactionCompletedExternally(SqlTransactionCompletion completion)
    {
        _connection?.TransactionCompletedExternally(completion);
    }

    private void ValidateTransaction()
    {
        if (_transaction is null)
            return;
        if (_transaction.IsCompleted)
            throw new InvalidOperationException("The transaction associated with this command has completed.");
        _transaction.ThrowIfFaulted();
        if (!ReferenceEquals(_transaction.Connection, _connection))
            throw new InvalidOperationException("The transaction is not associated with the command's connection.");
    }
}
