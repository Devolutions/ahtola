using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace Ahtola;

internal sealed class AhtolaRemoteDataReader : DbDataReader, IConnectionOwnedReader
{
    private readonly AhtolaConnection? _connection;
    private readonly IReadOnlyList<RemoteStatementResult> _results;
    private readonly CommandBehavior _behavior;
    private readonly int _recordsAffected;
    private readonly string? _commandText;
    private readonly AhtolaRemoteClient.RemoteCursor? _cursor;
    private readonly AhtolaCommand? _command;
    private readonly ILocalReaderConnection? _readerConnection;
    private List<RemoteResponseValue>? _currentStreamingRow;
    private bool _streamHasRows;
    private int _resultIndex;
    private int _rowIndex = -1;
    private bool _isClosed;

    public AhtolaRemoteDataReader(AhtolaCommand command, RemoteStatementResult result, CommandBehavior behavior)
        : this(command.Connection as AhtolaConnection, [result], behavior, command.CommandText, cursor: null, command: null)
    {
    }

    public AhtolaRemoteDataReader(
        AhtolaCommand command,
        AhtolaRemoteClient.RemoteCursor cursor,
        CommandBehavior behavior)
        : this(
            command.Connection as AhtolaConnection,
            [new RemoteStatementResult { Columns = cursor.Columns }],
            behavior,
            command.CommandText,
            cursor,
            command)
    {
    }

    public AhtolaRemoteDataReader(AhtolaConnection? connection, IReadOnlyList<RemoteStatementResult> results, CommandBehavior behavior)
        : this(connection, results, behavior, null, cursor: null, command: null)
    {
    }

    private AhtolaRemoteDataReader(
        AhtolaConnection? connection,
        IReadOnlyList<RemoteStatementResult> results,
        CommandBehavior behavior,
        string? commandText,
        AhtolaRemoteClient.RemoteCursor? cursor,
        AhtolaCommand? command)
    {
        ArgumentNullException.ThrowIfNull(results);

        _connection = connection;
        _results = results;
        _behavior = behavior;
        _commandText = commandText;
        _cursor = cursor;
        _command = command;
        foreach (var result in results)
            _recordsAffected = checked(_recordsAffected + (int)result.AffectedRowCount);
        if (cursor is not null)
        {
            _readerConnection = connection as ILocalReaderConnection;
            _readerConnection?.ReaderOpened(this);
        }
    }

    public override bool GetBoolean(int ordinal)
    {
        return CurrentValue(ordinal).GetInt64() != 0;
    }

    public override byte GetByte(int ordinal)
    {
        return checked((byte)CurrentValue(ordinal).GetInt64());
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        return CopyArray((byte[])CurrentValue(ordinal).ToClrValue(), dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal)
    {
        var value = CurrentValue(ordinal);
        if (value.Type == "text")
        {
            var text = (string)value.ToClrValue();
            if (text.Length == 1)
                return text[0];
        }

        return checked((char)value.GetInt64());
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        return CopyArray(GetString(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
    }

    public override string GetDataTypeName(int ordinal)
    {
        var value = HasCurrentRow ? CurrentValue(ordinal) : null;
        if (value is not null)
            return GetTypeName(value.Type);

        return ordinal >= 0 && ordinal < CurrentResult.Columns.Count
            ? CurrentResult.Columns[ordinal].DeclType ?? string.Empty
            : string.Empty;
    }

    public override DateTime GetDateTime(int ordinal)
    {
        var value = CurrentValue(ordinal);
        return value.Type == "text"
            ? DateTime.Parse((string)value.ToClrValue(), CultureInfo.InvariantCulture)
            : throw new InvalidCastException($"Cannot convert remote {value.Type} value to DateTime.");
    }

    public override decimal GetDecimal(int ordinal)
    {
        return CurrentValue(ordinal).GetDecimal();
    }

    public override double GetDouble(int ordinal)
    {
        return CurrentValue(ordinal).GetDouble();
    }

    public override Type GetFieldType(int ordinal)
    {
        EnsureOpen();
        ValidateOrdinal(ordinal);

        // Declared type first: DbDataAdapter types one DataTable column from this call, so it must be stable across
        // rows. Microsoft.Data.Sqlite answers from the declared type for the same reason.
        if (ordinal < CurrentResult.Columns.Count
            && TryGetClrTypeFromDeclaredType(CurrentResult.Columns[ordinal].DeclType, out var declaredType))
        {
            return declaredType;
        }

        // No declared type (expression or aggregate): use the data, skipping NULLs. DataTable rejects DBNull as a
        // column type.
        if (_currentStreamingRow is not null)
        {
            var currentType = GetClrType(_currentStreamingRow[ordinal].Type);
            if (currentType != typeof(DBNull))
                return currentType;
        }
        else if (_cursor is not null
                 && FindFirstStreamingNonNullValue(ordinal) is { } streamingValue)
        {
            return GetClrType(streamingValue.Type);
        }
        else if (HasCurrentRow)
        {
            var currentType = GetClrType(CurrentResult.Rows[_rowIndex][ordinal].Type);
            if (currentType != typeof(DBNull))
                return currentType;
        }

        foreach (var row in CurrentResult.Rows)
        {
            if (ordinal >= row.Count)
                continue;

            var rowType = GetClrType(row[ordinal].Type);
            if (rowType != typeof(DBNull))
                return rowType;
        }

        return typeof(object);
    }

    public override float GetFloat(int ordinal)
    {
        return (float)CurrentValue(ordinal).GetDouble();
    }

    public override Guid GetGuid(int ordinal)
    {
        // Mirrors AhtolaDataReader.ToGuid. Both storage classes occur, and Microsoft.Data.Sqlite reads either.
        var value = CurrentValue(ordinal);
        if (value.Type == "blob")
        {
            var blob = (byte[])value.ToClrValue();
            if (blob.Length == 16)
                return new Guid(blob);

            if (Guid.TryParse(Encoding.UTF8.GetString(blob), out var blobGuid))
                return blobGuid;

            throw new InvalidOperationException(
                $"Unable to parse GUID for column '{GetName(ordinal)}' (ordinal {ordinal}, storage BLOB ({blob.Length} bytes)).");
        }

        if (value.Type == "text" && Guid.TryParse(GetString(ordinal), out var textGuid))
            return textGuid;

        throw new InvalidOperationException(
            $"Unable to parse GUID for column '{GetName(ordinal)}' (ordinal {ordinal}, storage {value.Type.ToUpperInvariant()}).");
    }

    public override short GetInt16(int ordinal)
    {
        return checked((short)CurrentValue(ordinal).GetInt64());
    }

    public override int GetInt32(int ordinal)
    {
        return checked((int)CurrentValue(ordinal).GetInt64());
    }

    public override long GetInt64(int ordinal)
    {
        return CurrentValue(ordinal).GetInt64();
    }

    public override string GetName(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return ordinal < CurrentResult.Columns.Count
            ? CurrentResult.Columns[ordinal].Name ?? string.Empty
            : string.Empty;
    }

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < FieldCount; i++)
        {
            if (GetName(i) == name)
                return i;
        }

        throw new IndexOutOfRangeException($"column {name} not found");
    }

    public override string GetString(int ordinal)
    {
        var value = CurrentValue(ordinal).ToClrValue();
        return value switch
        {
            string text => text,
            DBNull => throw new InvalidCastException("Cannot convert remote null value to String."),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    public override object GetValue(int ordinal)
    {
        return CurrentValue(ordinal).ToClrValue();
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);

        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        return CurrentValue(ordinal).Type == "null";
    }

    public override int FieldCount => CurrentResult.Columns.Count > 0
        ? CurrentResult.Columns.Count
        : CurrentResult.Rows.Count > 0
            ? CurrentResult.Rows[0].Count
            : 0;

    public override DataTable GetSchemaTable()
    {
        return AhtolaSchemaCollections.BuildReaderSchemaTable(
            _cursor is null ? _connection : null,
            _commandText,
            FieldCount,
            GetName,
            GetFieldType);
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int RecordsAffected => _cursor?.RecordsAffected ?? _recordsAffected;

    public override bool HasRows
        => _cursor is null
            ? CurrentResult.Rows.Count > 0
            : _streamHasRows || EnsureCursorHasRows();

    public override bool IsClosed => _isClosed;

    public override bool NextResult()
    {
        EnsureOpen();
        if (_cursor is not null)
        {
            while (Read())
            {
            }
            return false;
        }
        if (_resultIndex + 1 >= _results.Count)
        {
            _rowIndex = CurrentResult.Rows.Count;
            return false;
        }

        _resultIndex++;
        _rowIndex = -1;
        return true;
    }

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cursor is not null)
        {
            while (await ReadAsync(cancellationToken).ConfigureAwait(false))
            {
            }
            return false;
        }
        return NextResult();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCoreAsync(closeConnection: true).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseCoreAsync(closeConnection: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override Task CloseAsync()
        => CloseCoreAsync(closeConnection: true).AsTask();

    void IConnectionOwnedReader.CloseFromConnection()
        => CloseCoreAsync(closeConnection: false).AsTask().GetAwaiter().GetResult();

    ValueTask IConnectionOwnedReader.CloseFromConnectionAsync()
        => CloseCoreAsync(closeConnection: false);

    public override bool Read()
    {
        EnsureOpen();
        if (_cursor is not null)
        {
            _currentStreamingRow = (_command
                ?? throw new InvalidOperationException("Remote cursor reader has no command."))
                .RunOperation(_cursor.ReadRow);
            _streamHasRows |= _currentStreamingRow is not null;
            return _currentStreamingRow is not null;
        }
        if (_rowIndex + 1 >= CurrentResult.Rows.Count)
        {
            _rowIndex = CurrentResult.Rows.Count;
            return false;
        }

        _rowIndex++;
        return true;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cursor is not null)
        {
            _currentStreamingRow = await (_command
                    ?? throw new InvalidOperationException("Remote cursor reader has no command."))
                .RunOperationAsync(_cursor.ReadRowAsync, cancellationToken)
                .ConfigureAwait(false);
            _streamHasRows |= _currentStreamingRow is not null;
            return _currentStreamingRow is not null;
        }
        return Read();
    }

    public override int Depth => 0;

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    private RemoteStatementResult CurrentResult
    {
        get
        {
            if (_results.Count == 0)
                throw new InvalidOperationException("The data reader has no result sets.");

            return _results[_resultIndex];
        }
    }

    private bool HasCurrentRow
        => _currentStreamingRow is not null
           || _rowIndex >= 0 && _rowIndex < CurrentResult.Rows.Count;

    private RemoteResponseValue CurrentValue(int ordinal)
    {
        EnsureOpen();
        if (!HasCurrentRow)
            throw new InvalidOperationException("No current row. Call Read before accessing values.");

        ValidateOrdinal(ordinal);
        var row = _currentStreamingRow ?? CurrentResult.Rows[_rowIndex];
        if (ordinal >= row.Count)
            throw new IndexOutOfRangeException($"column ordinal {ordinal} is out of range");

        return row[ordinal];
    }

    private static long CopyArray<T>(T[] source, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (dataOffset >= source.LongLength)
            return 0;

        var available = source.LongLength - dataOffset;

        if (buffer is null)
            return available;

        if (bufferOffset >= buffer.Length)
            return 0;

        var count = checked((int)Math.Min(Math.Min(available, length), buffer.Length - bufferOffset));
        if (count <= 0)
            return 0;

        Array.Copy(source, checked((int)dataOffset), buffer, bufferOffset, count);

        return count;
    }

    private static string GetTypeName(string valueType)
    {
        return valueType switch
        {
            "null" => "NULL",
            "integer" => "INTEGER",
            "float" => "REAL",
            "text" => "TEXT",
            "blob" => "BLOB",
            _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null),
        };
    }

    private void ValidateOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (ordinal >= FieldCount)
            throw new IndexOutOfRangeException($"column ordinal {ordinal} is out of range");
    }

    private static Type GetClrType(string valueType)
    {
        return valueType switch
        {
            "integer" => typeof(long),
            "float" => typeof(double),
            "text" => typeof(string),
            "blob" => typeof(byte[]),
            "null" => typeof(DBNull),
            _ => typeof(object),
        };
    }

    private static bool TryGetClrTypeFromDeclaredType(string? declaredType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? clrType)
    {
        clrType = null;
        if (string.IsNullOrWhiteSpace(declaredType))
            return false;

        var normalized = declaredType.Trim().ToUpperInvariant();
        if (normalized.Contains("INT", StringComparison.Ordinal))
            clrType = typeof(long);
        else if (normalized.Contains("REAL", StringComparison.Ordinal)
                 || normalized.Contains("FLOA", StringComparison.Ordinal)
                 || normalized.Contains("DOUB", StringComparison.Ordinal))
            clrType = typeof(double);
        else if (normalized.Contains("TEXT", StringComparison.Ordinal)
                 || normalized.Contains("CHAR", StringComparison.Ordinal)
                 || normalized.Contains("CLOB", StringComparison.Ordinal))
            clrType = typeof(string);
        else if (normalized.Contains("BLOB", StringComparison.Ordinal))
            clrType = typeof(byte[]);

        return clrType is not null;
    }

    private void EnsureOpen()
    {
        if (IsClosed)
            throw new InvalidOperationException("The data reader is closed.");
    }

    private bool EnsureCursorHasRows()
        => (_command
            ?? throw new InvalidOperationException("Remote cursor reader has no command."))
            .RunOperation(
                token => (_cursor
                    ?? throw new InvalidOperationException("Remote cursor reader has no cursor."))
                    .EnsureHasRows(token));

    private RemoteResponseValue? FindFirstStreamingNonNullValue(int ordinal)
        => (_command
            ?? throw new InvalidOperationException("Remote cursor reader has no command."))
            .RunOperation(
                token => (_cursor
                    ?? throw new InvalidOperationException("Remote cursor reader has no cursor."))
                    .FindFirstNonNullValue(ordinal, token));

    private async ValueTask CloseCoreAsync(bool closeConnection)
    {
        if (_isClosed)
            return;

        _isClosed = true;
        Exception? failure = null;
        try
        {
            if (_cursor is not null)
                await _cursor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _readerConnection?.ReaderClosed(this);
            if (closeConnection
                && (_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
            {
                try
                {
                    if (_connection is not null)
                        await _connection.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (failure is not null)
                {
                    _ = exception;
                }
            }
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
