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
                managedStatement = _connection.ManagedConnection.Prepare(sql);
                BindManagedParameters(managedStatement);
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
            var parameterCount = preparedStatement.ParameterCount;
            var boundParameters = new bool[parameterCount + 1];

            for (var i = 0; i < _parameterCollection.Count; i++)
            {
                var parameter = _parameterCollection[i] as AhtolaParameter;
                if (parameter == null)
                    throw new ArgumentException("Parameter must be of type AhtolaParameter");

                if (!string.IsNullOrEmpty(parameter.ParameterName))
                {
                    var parameterIndex = preparedStatement.BindNamedParameter(parameter.ParameterName, parameter.ToValue());
                    if (parameterIndex == 0)
                        throw new InvalidOperationException($"Parameter {parameter.ParameterName} was not found in the SQL statement.");

                    boundParameters[parameterIndex] = true;
                }
                else
                {
                    var parameterIndex = i + 1;
                    if (parameterIndex > parameterCount)
                        throw new InvalidOperationException($"Parameter at position {parameterIndex} was not found in the SQL statement.");

                    preparedStatement.BindParameter(parameterIndex, parameter.ToValue());
                    boundParameters[parameterIndex] = true;
                }
            }

            for (var i = 1; i <= parameterCount; i++)
            {
                if (!boundParameters[i])
                {
                    var parameterName = preparedStatement.GetParameterName(i);
                    throw new InvalidOperationException(
                        parameterName is null
                            ? $"Missing value for parameter ?{i}."
                            : $"Missing value for parameter {parameterName}.");
                }
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

        Prepare();
        cancellationToken.ThrowIfCancellationRequested();

        var nativeStatement = _nativeStatement;
        var managedStatement = _managedStatement;
        if (managedStatement is null && nativeStatement is null)
            throw new InvalidOperationException("Command was not prepared.");
        _nativeStatement = null;
        _managedStatement = null;
        var transactionCompletion = SqlTransactionControl.GetCompletion(CommandText);
        var reader = new AhtolaDataReader(
            this,
            nativeStatement,
            managedStatement,
            behavior,
            () => MarkTransactionCompletedExternally(transactionCompletion));
        return reader;
    }

    internal T RunOperation<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
        => _cancellation.Run(operation, cancellationToken);

    internal Task<T> RunOperationAsync<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
        => _cancellation.RunAsync(operation, cancellationToken);

    private void BindManagedParameters(IManagedStatementAdapter statement)
    {
        var parameterMetadata = statement.ParameterMetadata;
        var parameterCount = parameterMetadata.Count;
        var boundParameters = new bool[parameterCount + 1];

        for (var i = 0; i < _parameterCollection.Count; i++)
        {
            var parameter = _parameterCollection[i] as AhtolaParameter
                ?? throw new ArgumentException("Parameter must be of type AhtolaParameter");

            if (!string.IsNullOrEmpty(parameter.ParameterName))
            {
                var parameterIndex = parameterMetadata.GetParameterIndex(parameter.ParameterName);
                if (parameterIndex == 0)
                    throw new InvalidOperationException($"Parameter {parameter.ParameterName} was not found in the SQL statement.");

                statement.Bind(parameterIndex, parameter.ToSqlValue());
                boundParameters[parameterIndex] = true;
            }
            else
            {
                var parameterIndex = i + 1;
                if (parameterIndex > parameterCount)
                    throw new InvalidOperationException($"Parameter at position {parameterIndex} was not found in the SQL statement.");

                statement.Bind(parameterIndex, parameter.ToSqlValue());
                boundParameters[parameterIndex] = true;
            }
        }

        for (var i = 1; i <= parameterCount; i++)
        {
            if (boundParameters[i])
                continue;

            var parameterName = parameterMetadata.GetParameter(i).Name;
            throw new InvalidOperationException(
                parameterName is null
                    ? $"Missing value for parameter ?{i}."
                    : $"Missing value for parameter {parameterName}.");
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
        if (!ReferenceEquals(_transaction.Connection, _connection))
            throw new InvalidOperationException("The transaction is not associated with the command's connection.");
    }
}
