using Ahtola.Core;
using Ahtola;

namespace Ahtola.Data.Sqlite;

internal sealed class SqliteStatementAdapter : IDisposable, IAsyncDisposable
{
    private readonly AhtolaNativeStatement? _nativeStatement;
    private readonly IManagedStatementAdapter? _managedStatement;
    private bool _disposed;

    private SqliteStatementAdapter(AhtolaNativeStatement nativeStatement)
    {
        _nativeStatement = nativeStatement;
    }

    private SqliteStatementAdapter(IManagedStatementAdapter managedStatement)
    {
        _managedStatement = managedStatement;
    }

    public static SqliteStatementAdapter FromNative(AhtolaNativeStatement statement)
        => new(statement ?? throw new ArgumentNullException(nameof(statement)));

    public static SqliteStatementAdapter FromManaged(IManagedStatementAdapter statement)
        => new(statement ?? throw new ArgumentNullException(nameof(statement)));

    public bool IsInvalid => _disposed || (_nativeStatement?.IsInvalid ?? false);

    public bool UsesManagedResults => _managedStatement is not null;

    public int NativeParameterCount => GetNativeStatement().ParameterCount;

    public int RowsAffected
        => _managedStatement?.RowsAffected ?? GetNativeStatement().RowsAffected;

    public int ColumnCount
        => _managedStatement?.ResultMetadata.ColumnCount ?? GetNativeStatement().FieldCount;

    public void BindNative(int index, Ahtola.AhtolaValue value)
        => GetNativeStatement().BindParameter(index, value);

    public string? GetNativeParameterName(int index)
        => GetNativeStatement().GetParameterName(index);

    public bool Read()
        => Read(CancellationToken.None);

    public bool Read(CancellationToken cancellationToken)
        => _managedStatement is null
            ? ReadNative(cancellationToken)
            : _managedStatement.Step(cancellationToken) == StatementStepResult.Row;

    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        => _managedStatement is null
            ? ReadNative(cancellationToken)
            : await _managedStatement.StepAsync(cancellationToken).ConfigureAwait(false)
              == StatementStepResult.Row;

    public bool HasRows()
        => _managedStatement?.HasRows() ?? GetNativeStatement().HasRows;

    public string GetName(int ordinal)
        => _managedStatement?.ResultMetadata.GetColumn(ordinal).Name ?? GetNativeStatement().GetName(ordinal);

    public ManagedResultMetadata ManagedResultMetadata
        => _managedStatement?.ResultMetadata
           ?? throw new InvalidOperationException("The managed statement is unavailable.");

    public ManagedResultRow ManagedCurrentRow
        => _managedStatement?.CurrentRow
           ?? throw new InvalidOperationException("The managed statement is unavailable.");

    public AhtolaValue GetNativeValue(int ordinal)
    {
        return GetNativeStatement().GetValue(ordinal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_managedStatement is null)
            _nativeStatement?.Dispose();
        else
            _managedStatement.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_managedStatement is null)
            _nativeStatement?.Dispose();
        else
            await _managedStatement.DisposeAsync().ConfigureAwait(false);
    }

    private AhtolaNativeStatement GetNativeStatement()
        => _nativeStatement ?? throw new InvalidOperationException("The native statement is unavailable.");

    private bool ReadNative(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statement = GetNativeStatement();
        using var registration = cancellationToken.UnsafeRegister(
            static state => ((AhtolaNativeStatement)state!).Interrupt(),
            statement);
        bool result;
        try
        {
            result = statement.Read();
        }
        catch (AhtolaException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
