using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Ahtola;
using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public class SqliteDataReader : DbDataReader, IConnectionOwnedReader
{
    private readonly SqliteCommand _command;
    private readonly SqliteConnection _connection;
    private SqliteStatementAdapter? _statement;
    private DbDataReader? _delegatedReader;
    private bool _skipDelegatedReaderToFirstColumnResult;
    private string _currentSql = string.Empty;
    private readonly List<string> _remainingSql = new();
    private readonly CommandBehavior _behavior;
    private readonly Action _closeCallback;
    private int _recordsAffected;
    private bool _isClosed;
    private bool _hasCurrentRow;
    private bool _hasPrefetchedRow;
    private bool _hadResultSet;
    private bool _hadRecordsAffectedStatement;
    private bool _currentStatementRowsAffectedCounted;

    private enum ReaderValueKind
    {
        Empty,
        Null,
        Integer,
        Real,
        Text,
        Blob,
    }

    private readonly struct ReaderValue
    {
        private readonly AhtolaValue _nativeValue;
        private readonly ManagedResultValue _managedValue;
        private readonly bool _isManaged;

        private ReaderValue(AhtolaValue nativeValue)
        {
            _nativeValue = nativeValue;
        }

        private ReaderValue(ManagedResultValue managedValue)
        {
            _managedValue = managedValue;
            _isManaged = true;
        }

        public ReaderValueKind Kind => _isManaged
            ? _managedValue.Kind switch
            {
                ManagedResultValueKind.Null => ReaderValueKind.Null,
                ManagedResultValueKind.Integer => ReaderValueKind.Integer,
                ManagedResultValueKind.Real => ReaderValueKind.Real,
                ManagedResultValueKind.Text => ReaderValueKind.Text,
                ManagedResultValueKind.Blob => ReaderValueKind.Blob,
                _ => throw new InvalidOperationException($"Unknown managed result value kind {_managedValue.Kind}."),
            }
            : _nativeValue.ValueType switch
            {
                AhtolaValueType.Empty => ReaderValueKind.Empty,
                AhtolaValueType.Null => ReaderValueKind.Null,
                AhtolaValueType.Integer => ReaderValueKind.Integer,
                AhtolaValueType.Real => ReaderValueKind.Real,
                AhtolaValueType.Text => ReaderValueKind.Text,
                AhtolaValueType.Blob => ReaderValueKind.Blob,
                _ => throw new InvalidOperationException($"Unknown native result value type {_nativeValue.ValueType}."),
            };

        public long Integer => _isManaged ? _managedValue.AsInteger() : _nativeValue.IntValue;

        public double Real => _isManaged ? _managedValue.AsReal() : _nativeValue.RealValue;

        public string Text => _isManaged ? _managedValue.AsText() : _nativeValue.StringValue;

        public byte[] Blob => _isManaged ? _managedValue.AsBlob().ToArray() : _nativeValue.BlobValue;

        public static ReaderValue FromNative(AhtolaValue value) => new(value);

        public static ReaderValue FromManaged(ManagedResultValue value) => new(value);
    }

    internal SqliteDataReader(
        SqliteCommand command,
        SqliteStatementAdapter statement,
        string currentSql,
        List<string> remainingSql,
        int recordsAffected,
        CommandBehavior behavior,
        Action closeCallback)
        : this(
            command,
            statement,
            currentSql,
            remainingSql,
            recordsAffected,
            hadRecordsAffectedStatement: false,
            behavior,
            closeCallback)
    {
    }

    internal SqliteDataReader(
        SqliteCommand command,
        SqliteStatementAdapter statement,
        string currentSql,
        List<string> remainingSql,
        int recordsAffected,
        bool hadRecordsAffectedStatement,
        CommandBehavior behavior,
        Action closeCallback)
    {
        _command = command;
        _connection = command.Connection
            ?? throw new InvalidOperationException("A data reader requires an associated connection.");
        _statement = statement;
        _currentSql = currentSql;
        _hadResultSet = true;
        _remainingSql = remainingSql;
        _recordsAffected = recordsAffected;
        _hadRecordsAffectedStatement = hadRecordsAffectedStatement;
        _behavior = behavior;
        _closeCallback = closeCallback;
        ((ILocalReaderConnection)_connection).ReaderOpened(this);
    }

    internal SqliteDataReader(SqliteCommand command, int recordsAffected, CommandBehavior behavior, Action closeCallback)
    {
        _command = command;
        _connection = command.Connection
            ?? throw new InvalidOperationException("A data reader requires an associated connection.");
        _recordsAffected = recordsAffected;
        _behavior = behavior;
        _closeCallback = closeCallback;
        ((ILocalReaderConnection)_connection).ReaderOpened(this);
    }

    internal SqliteDataReader(
        SqliteCommand command,
        DbDataReader delegatedReader,
        CommandBehavior behavior,
        Action closeCallback,
        bool skipToFirstColumnResult)
    {
        _command = command;
        _connection = command.Connection
            ?? throw new InvalidOperationException("A data reader requires an associated connection.");
        _delegatedReader = delegatedReader ?? throw new ArgumentNullException(nameof(delegatedReader));
        _behavior = behavior;
        _closeCallback = closeCallback;
        _skipDelegatedReaderToFirstColumnResult = skipToFirstColumnResult;
        if (skipToFirstColumnResult)
        {
            // A SqliteCommand.CommandText containing multiple semicolon-separated statements
            // (e.g. "INSERT ...; SELECT changes();") is executed as one remote/replica batch.
            // Position the reader on the first result that has columns, exactly like the local
            // statement-stepping path already does in Execute(...)/NextResultCore below: a
            // leading INSERT/UPDATE/DELETE without RETURNING is absorbed rather than exposed as
            // its own reader position, so ExecuteScalar/ExecuteReader callers — including EF
            // Core's own ModificationCommandBatch and SqliteHistoryRepository — land directly on
            // the first row-producing result instead of reading null/throwing off an empty one.
            // This applies only to SqliteCommand's own multi-statement CommandText splitting.
            // SqliteBatch's explicit DbBatchCommands (skipToFirstColumnResult: false) — and
            // AhtolaBatch's own DbBatch.ExecuteReader() used directly — must keep exposing every
            // explicitly-added command 1:1, matching the local/replica SequentialBatchDataReader
            // contract: each NextResult() call advances exactly one explicit command, even one
            // with zero columns (a plain INSERT/UPDATE/DELETE without RETURNING).
            SkipDelegatedReaderToColumns(_delegatedReader);
        }

        ((ILocalReaderConnection)_connection).ReaderOpened(this);
    }

    private static bool SkipDelegatedReaderToColumns(DbDataReader reader)
    {
        while (reader.FieldCount == 0 && reader.NextResult())
        {
        }

        return reader.FieldCount > 0;
    }

    private static async Task<bool> SkipDelegatedReaderToColumnsAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        while (reader.FieldCount == 0
               && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        return reader.FieldCount > 0;
    }

    public override int Depth => _delegatedReader?.Depth ?? 0;

    public override int FieldCount
    {
        get
        {
            if (_delegatedReader is not null)
                return _delegatedReader.FieldCount;
            EnsureOpen();
            if (_statement is null)
                return 0;

            var statement = GetStatement();
            return statement.UsesManagedResults
                ? statement.ManagedResultMetadata.ColumnCount
                : statement.ColumnCount;
        }
    }

    public override bool HasRows
    {
        get
        {
            if (_delegatedReader is not null)
                return _delegatedReader.HasRows;
            EnsureOpen();
            if (_statement is null)
                return false;

            return _command.Connection?.UsesManagedDatabase == true
                ? GetStatement().HasRows()
                : !Regex.IsMatch(_currentSql, @"\bWHERE\b\s+0\s*=\s*1\b", RegexOptions.IgnoreCase);
        }
    }

    public override bool IsClosed => _isClosed
        || (_delegatedReader?.IsClosed ?? false)
        || _command.Connection?.State != ConnectionState.Open
        || (_statement?.IsInvalid ?? false);

    public override int RecordsAffected
    {
        get
        {
            if (_delegatedReader is not null)
                return _delegatedReader.RecordsAffected;
            if (_recordsAffected > 0 || _hadRecordsAffectedStatement)
                return _recordsAffected;

            return _statement is null ? _hadResultSet ? -1 : _recordsAffected : -1;
        }
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool GetBoolean(int ordinal)
    {
        if (_delegatedReader is not null)
        {
            var delegatedValue = GetDelegatedValue(ordinal);
            return delegatedValue is string text && bool.TryParse(text, out var parsed)
                ? parsed
                : Convert.ToInt64(delegatedValue, CultureInfo.InvariantCulture) != 0;
        }
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        if (value.Kind == ReaderValueKind.Text && bool.TryParse(value.Text, out var boolValue))
            return boolValue;

        return GetInt64(ordinal) != 0;
    }

    public override byte GetByte(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetByte(ordinal);
        EnsureOpen();
        return (byte)GetInt64(ordinal);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        EnsureOpen();
        return CopyValue(GetBlobValue(ordinal), dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetChar(ordinal);
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        if (value.Kind == ReaderValueKind.Text && value.Text.Length == 1)
            return value.Text[0];

        // Microsoft.Data.Sqlite parity: anything else coerces through the INTEGER column
        // path — TEXT parses a CAST-style digit prefix ('' or 'abc' -> 0), REAL truncates —
        // and the checked cast surfaces out-of-range values like MDS does.
        var coerced = value.Kind switch
        {
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => (long)value.Real,
            ReaderValueKind.Text => ParseTextAsIntegerPrefix(value.Text),
            ReaderValueKind.Null => throw new InvalidOperationException(
                Properties.Resources.CalledOnNullValue(ordinal)),
            _ => GetInt64(ordinal),
        };

        return checked((char)coerced);
    }

    // CAST-style text-to-integer conversion matching the native column-int64 API:
    // skips leading ASCII whitespace, accepts an optional sign, consumes a digit
    // prefix, yields 0 when no digits are present, and clamps on overflow.
    private static long ParseTextAsIntegerPrefix(string text)
    {
        var index = 0;
        while (index < text.Length && text[index] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
        {
            index++;
        }

        var negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        ulong magnitude = 0;
        var sawDigit = false;
        var overflow = false;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            sawDigit = true;
            if (!overflow)
            {
                var digit = (ulong)(text[index] - '0');
                if (magnitude > (ulong.MaxValue - digit) / 10)
                {
                    overflow = true;
                }
                else
                {
                    magnitude = magnitude * 10 + digit;
                }
            }

            index++;
        }

        if (!sawDigit)
        {
            return 0;
        }

        if (negative)
        {
            return overflow || magnitude > (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude;
        }

        return overflow || magnitude > (ulong)long.MaxValue ? long.MaxValue : (long)magnitude;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        EnsureOpen();
        return CopyValue(GetTextValue(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
    }

    public override string GetDataTypeName(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetDataTypeName(ordinal);
        EnsureOpen();
        ValidateOrdinal(ordinal);
        var declaredType = GetDeclaredTypeName(ordinal);
        if (!string.IsNullOrEmpty(declaredType))
            return declaredType;

        var valueType = CurrentValueKind(ordinal);
        if (HasUndeclaredSelectSourceColumn(ordinal))
        {
            if (valueType is ReaderValueKind.Empty or ReaderValueKind.Null)
                valueType = GetSampleValueType(ordinal);

            return GetDataTypeNameFromValueType(valueType, string.Empty);
        }

        if (valueType is ReaderValueKind.Empty or ReaderValueKind.Null)
            valueType = GetSampleValueType(ordinal);

        return valueType switch
        {
            ReaderValueKind.Null => "BLOB",
            ReaderValueKind.Integer => "INTEGER",
            ReaderValueKind.Real => "REAL",
            ReaderValueKind.Text => "TEXT",
            ReaderValueKind.Blob => "BLOB",
            ReaderValueKind.Empty => InferDataTypeName(GetName(ordinal)),
            _ => throw new InvalidEnumArgumentException()
        };
    }

    public override DateTime GetDateTime(int ordinal)
    {
        if (_delegatedReader is not null)
            return ApplyConfiguredDateTimeKind(ParseDateTime(GetDelegatedTextValue(ordinal)));
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        var dateTime = value.Kind switch
        {
            ReaderValueKind.Text => ParseDateTime(value.Text),
            ReaderValueKind.Real => DateTime.FromOADate(value.Real - 2415018.5),
            ReaderValueKind.Integer => DateTime.FromOADate(value.Integer - 2415018.5),
            _ => DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
        };
        return ApplyConfiguredDateTimeKind(dateTime);
    }

    public virtual DateTimeOffset GetDateTimeOffset(int ordinal)
    {
        if (_delegatedReader is not null)
            return ParseDateTimeOffset(GetDelegatedTextValue(ordinal));
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => ParseDateTimeOffset(value.Text),
            ReaderValueKind.Real => new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(value.Real - 2415018.5), DateTimeKind.Unspecified), TimeSpan.Zero),
            ReaderValueKind.Integer => new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(value.Integer - 2415018.5), DateTimeKind.Unspecified), TimeSpan.Zero),
            _ => ParseDateTimeOffset(GetString(ordinal))
        };
    }

    public override decimal GetDecimal(int ordinal)
    {
        if (_delegatedReader is not null)
        {
            var delegatedValue = GetDelegatedValue(ordinal);
            return delegatedValue is string text
                ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : Convert.ToDecimal(delegatedValue, CultureInfo.InvariantCulture);
        }
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => decimal.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture),
            // Format through "G15" instead of Convert.ToDecimal(double): .NET 11 changed
            // double-to-decimal conversion to keep the exact binary expansion, but SQLite's
            // own REAL-to-decimal path (and Microsoft.Data.Sqlite, which parses the 15-digit
            // text form) rounds to 15 significant digits.
            ReaderValueKind.Real => decimal.Parse(value.Real.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            ReaderValueKind.Integer => value.Integer,
            _ => Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public override double GetDouble(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetDouble(ordinal);
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Text => double.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => value.Real
        };
    }

    public override IEnumerator GetEnumerator() => _delegatedReader?.GetEnumerator() ?? new DbEnumerator(this, closeReader: false);

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetFieldType(ordinal);
        EnsureOpen();
        ValidateOrdinal(ordinal);
        var valueType = CurrentValueKind(ordinal);
        var declaredType = GetDeclaredTypeName(ordinal);
        if (!string.IsNullOrEmpty(declaredType))
        {
            if (IsBlobType(declaredType))
            {
                // BLOB affinity does not constrain SQLite storage classes. DataAdapter fixes a
                // DataColumn's type before it reads rows, so byte[] metadata would reject a later
                // TEXT value from the same column. Use object to preserve mixed BLOB/TEXT values.
                return typeof(object);
            }

            return GetClrTypeFromSqliteType(declaredType, valueType);
        }

        if (HasUndeclaredSelectSourceColumn(ordinal))
        {
            var sampledValueType = valueType is ReaderValueKind.Empty or ReaderValueKind.Null
                ? GetSampleValueType(ordinal)
                : valueType;
            return sampledValueType == ReaderValueKind.Blob
                ? typeof(object)
                : GetClrTypeFromValueType(sampledValueType);
        }

        var dataTypeName = GetDataTypeName(ordinal);
        // Identifier-shaped expressions can materialize a binary GUID as text in GetValue.
        // Keep ordinary BLOB expressions binary, but make those adapter-facing GUID projections
        // object-typed so DataAdapter does not commit to byte[] before reading the value.
        return IsBlobType(dataTypeName) && IsIdentifierColumnName(GetName(ordinal))
            ? typeof(object)
            : GetClrTypeFromSqliteType(dataTypeName, valueType);
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        EnsureOpen();
        if (_delegatedReader is null && typeof(T) == typeof(byte[]))
        {
            var rawValue = GetTypedValue(ordinal);
            if (rawValue.Kind == ReaderValueKind.Blob)
                return (T)(object)rawValue.Blob;
        }

        var value = GetValue(ordinal);
        if (value == DBNull.Value)
        {
            // Microsoft.Data.Sqlite surfaces DBNull.Value for object/DBNull reads so callers
            // (e.g. EF Core's detailed read-error path) can distinguish NULL from cast errors.
            if (typeof(T) == typeof(DBNull) || typeof(T) == typeof(object))
                return (T)value;

            throw new InvalidOperationException(Properties.Resources.CalledOnNullValue(ordinal));
        }

        if (typeof(T) == typeof(DBNull))
            throw new InvalidCastException();

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(DateOnly))
            return (T)(object)DateOnly.FromDateTime(GetDateTime(ordinal));

        if (targetType == typeof(TimeOnly))
            return (T)(object)TimeOnly.FromTimeSpan(GetTimeSpan(ordinal));

        if (targetType == typeof(DateTime))
            return (T)(object)GetDateTime(ordinal);

        if (targetType == typeof(DateTimeOffset))
            return (T)(object)GetDateTimeOffset(ordinal);

        if (targetType == typeof(TimeSpan))
            return (T)(object)GetTimeSpan(ordinal);

        if (targetType == typeof(decimal))
            return (T)(object)GetDecimal(ordinal);

        if (targetType == typeof(Guid))
            return (T)(object)GetGuid(ordinal);

        if (targetType == typeof(Stream))
            return (T)(object)GetStream(ordinal);

        if (targetType == typeof(TextReader))
            return (T)(object)GetTextReader(ordinal);

        if (targetType.IsEnum)
            return (T)Enum.ToObject(targetType, Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture));

        if (targetType != typeof(T) && value.GetType() == targetType)
            return (T)value;

        // Microsoft.Data.Sqlite reads unsigned types with unchecked casts so two's-complement
        // values written through the matching unchecked parameter binds round-trip.
        if (targetType == typeof(ulong))
            return (T)(object)unchecked((ulong)GetInt64(ordinal));

        if (targetType == typeof(uint))
            return (T)(object)unchecked((uint)GetInt64(ordinal));

        if (targetType == typeof(ushort))
            return (T)(object)unchecked((ushort)GetInt64(ordinal));

        if (targetType == typeof(byte))
            return (T)(object)unchecked((byte)GetInt64(ordinal));

        if (targetType == typeof(sbyte))
            return (T)(object)unchecked((sbyte)GetInt64(ordinal));

        // Microsoft.Data.Sqlite routes char reads (including char?) through GetChar,
        // whose TEXT coercion Convert.ChangeType cannot reproduce (e.g. '' -> '\0').
        if (targetType == typeof(char))
            return (T)(object)GetChar(ordinal);

        return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    public override float GetFloat(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetFloat(ordinal);
        EnsureOpen();
        return (float)GetDouble(ordinal);
    }

    public override Guid GetGuid(int ordinal)
    {
        if (_delegatedReader is not null)
        {
            var delegatedValue = GetDelegatedValue(ordinal);
            if (delegatedValue is Guid guid)
                return guid;
            if (delegatedValue is byte[] bytes)
            {
                if (bytes.Length == 16)
                    return new Guid(bytes);
                if (Guid.TryParse(Encoding.UTF8.GetString(bytes), out guid))
                    return guid;
            }
            if (delegatedValue is string text && Guid.TryParse(text, out guid))
                return guid;

            throw new InvalidCastException($"Cannot convert the value at ordinal {ordinal} to Guid.");
        }
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return ToGuid(ordinal, value);
    }

    public override short GetInt16(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetInt16(ordinal);
        EnsureOpen();
        return (short)GetInt64(ordinal);
    }

    public override int GetInt32(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetInt32(ordinal);
        EnsureOpen();
        return (int)GetInt64(ordinal);
    }

    public override long GetInt64(int ordinal)
    {
        if (_delegatedReader is not null) return Convert.ToInt64(GetDelegatedValue(ordinal), CultureInfo.InvariantCulture);
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => (long)value.Real,
            ReaderValueKind.Text => long.Parse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public override string GetName(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetName(ordinal);
        EnsureOpen();
        var statement = GetStatement();
        ValidateOrdinal(ordinal);
        return statement.UsesManagedResults
            ? statement.ManagedResultMetadata.GetColumn(ordinal).Name
            : statement.GetName(ordinal);
    }

    public override Stream GetStream(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetStream(ordinal);
        EnsureOpen();
        return new MemoryStream(GetBlobValue(ordinal).ToArray(), writable: false);
    }

    public override TextReader GetTextReader(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetTextReader(ordinal);
        EnsureOpen();
        return new StringReader(GetTextValue(ordinal));
    }

    public override int GetOrdinal(string name)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetOrdinal(name);
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(name);
        _ = GetStatement();
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.Ordinal))
                return i;
        }

        string? match = null;
        var matchOrdinal = -1;
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                if (match is not null)
                    throw new InvalidOperationException(Properties.Resources.AmbiguousColumnName(name, match, GetName(i)));

                match = GetName(i);
                matchOrdinal = i;
            }
        }

        if (match is not null)
            return matchOrdinal;

        throw new ArgumentOutOfRangeException(nameof(name), name, $"Column {name} was not found.");
    }

    public override DataTable GetSchemaTable()
    {
        if (_delegatedReader is not null) return _delegatedReader.GetSchemaTable() ?? new DataTable("SchemaTable");
        EnsureOpen();
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schema.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        schema.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        schema.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        schema.Columns.Add("BaseServerName", typeof(string));
        schema.Columns.Add("BaseCatalogName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        schema.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));

        var hasSources = TryGetSelectSources(out var sources, out var selections);
        var sourceColumns = hasSources
            ? GetSelectSourceColumns(sources)
            : new Dictionary<string, Dictionary<string, SchemaColumnInfo>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < FieldCount; i++)
        {
            var columnName = GetName(i);
            var selection = i < selections.Count ? selections[i] : columnName;
            var resolvedColumn = hasSources
                ? ResolveSelectColumn(selection, columnName, sources, sourceColumns)
                : null;
            var info = resolvedColumn?.Column;
            var hasBaseColumn = info is not null;
            var valueType = _hasCurrentRow ? ReadValue(i).Kind : ReaderValueKind.Empty;
            if (valueType == ReaderValueKind.Empty
                && !string.IsNullOrEmpty(info?.TypeName)
                && !IsBlobType(info.TypeName))
                valueType = GetValueKindFromTypeName(info.TypeName);
            else if (valueType is ReaderValueKind.Empty or ReaderValueKind.Null)
                valueType = GetSampleValueType(i);
            var dataTypeName = info is not null
                ? StripTypeLength(info.TypeName)
                : GetDataTypeNameFromValueType(valueType, selection);
            var dataType = info is not null
                // SQLite columns without a declared type have BLOB affinity, but their values are
                // dynamically typed. Preserve a sampled runtime type; otherwise use object so
                // DataTable can retain the value instead of rejecting text as byte[].
                ? string.IsNullOrEmpty(info.TypeName)
                    ? valueType == ReaderValueKind.Blob
                        ? typeof(object)
                        : GetClrTypeFromValueType(valueType)
                    : IsBlobType(info.TypeName)
                        ? typeof(object)
                    : GetClrTypeFromSqliteType(info.TypeName, valueType)
                // Identifier-shaped expressions can materialize a binary GUID as text in
                // GetValue, so DataAdapter must not commit their schema to byte[].
                : valueType == ReaderValueKind.Blob && IsIdentifierColumnName(columnName)
                    ? typeof(object)
                    : GetClrTypeFromValueType(valueType);
            var isExpression = info is null;
            var isAliased = info is null
                || !string.Equals(info.Name, columnName, StringComparison.Ordinal);
            var row = schema.NewRow();
            row[SchemaTableColumn.ColumnName] = columnName;
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.ColumnSize] = -1;
            row[SchemaTableColumn.NumericPrecision] = DBNull.Value;
            row[SchemaTableColumn.NumericScale] = DBNull.Value;
            row[SchemaTableColumn.IsUnique] = info is not null ? !isAliased && info.IsUnique : DBNull.Value;
            row[SchemaTableColumn.IsKey] = info is not null ? info.IsKey : DBNull.Value;
            row["BaseServerName"] = "";
            row["BaseCatalogName"] = info is not null ? "main" : DBNull.Value;
            row[SchemaTableColumn.BaseColumnName] = info is not null ? info.Name : DBNull.Value;
            row[SchemaTableColumn.BaseSchemaName] = DBNull.Value;
            row[SchemaTableColumn.BaseTableName] = resolvedColumn?.TableName ?? (object)DBNull.Value;
            row[SchemaTableColumn.DataType] = dataType;
            row["DataTypeName"] = dataTypeName;
            row[SchemaTableColumn.AllowDBNull] = info is not null ? isAliased || info.AllowNull : DBNull.Value;
            row[SchemaTableColumn.IsAliased] = isAliased;
            row[SchemaTableColumn.IsExpression] = isExpression;
            row[SchemaTableOptionalColumn.IsAutoIncrement] = hasBaseColumn ? false : DBNull.Value;
            row[SchemaTableColumn.IsLong] = DBNull.Value;
            row[SchemaTableColumn.ProviderType] = (int)GetSqliteType(valueType);
            schema.Rows.Add(row);
        }

        return schema;
    }

    public override string GetString(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetString(ordinal);
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => value.Text,
            ReaderValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
            ReaderValueKind.Real => value.Real.ToString(CultureInfo.InvariantCulture),
            ReaderValueKind.Blob => Encoding.UTF8.GetString(value.Blob),
            _ => Convert.ToString(GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    public virtual TimeSpan GetTimeSpan(int ordinal)
    {
        if (_delegatedReader is not null)
            return TimeSpan.Parse(GetDelegatedTextValue(ordinal), CultureInfo.InvariantCulture);
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Real => TimeSpan.FromDays(value.Real),
            ReaderValueKind.Integer => TimeSpan.FromDays(value.Integer),
            _ => TimeSpan.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public override object GetValue(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetValue(ordinal);
        EnsureOpen();
        EnsureHasCurrentRow();
        var value = ReadValue(ordinal);
        var declaredType = GetDeclaredTypeName(ordinal);
        if (IsGuidType(declaredType) && value.Kind is ReaderValueKind.Blob or ReaderValueKind.Text)
            return ToGuid(ordinal, value);
        if (ShouldMaterializeTextGuid(ordinal, declaredType, value))
            return ToGuid(ordinal, value).ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant();

        return value.Kind switch
        {
            ReaderValueKind.Null or ReaderValueKind.Empty => DBNull.Value,
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => value.Real,
            ReaderValueKind.Text => value.Text,
            ReaderValueKind.Blob => value.Blob,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private object GetDelegatedValue(int ordinal)
    {
        EnsureOpen();
        var value = _delegatedReader!.GetValue(ordinal);
        if (value == DBNull.Value)
            throw new InvalidOperationException(Properties.Resources.CalledOnNullValue(ordinal));

        return value;
    }

    public override int GetValues(object[] values)
    {
        if (_delegatedReader is not null) return _delegatedReader.GetValues(values);
        EnsureOpen();
        _ = GetStatement();
        EnsureHasCurrentRow();
        ArgumentNullException.ThrowIfNull(values);

        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);

        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        if (_delegatedReader is not null) return _delegatedReader.IsDBNull(ordinal);
        EnsureOpen();
        EnsureHasCurrentRow();
        var valueType = ReadValue(ordinal).Kind;
        return valueType is ReaderValueKind.Null or ReaderValueKind.Empty;
    }

    public override bool NextResult()
    {
        ThrowIfSynchronousBrowserOperation();
        if (_delegatedReader is null)
            return _command.RunOperation(NextResultCore);

        try
        {
            if (!_skipDelegatedReaderToFirstColumnResult)
            {
                // SqliteBatch's explicit DbBatchCommands (and AhtolaBatch's own DbBatch API used
                // directly) must expose every command 1:1: advance exactly one step, whatever
                // its column count, matching SequentialBatchDataReader's local/replica contract.
                return _delegatedReader.NextResult();
            }

            // Mirror NextResultCore below: always move at least one step, then keep skipping
            // forward through any further 0-column (non-RETURNING DML) results.
            return _delegatedReader.NextResult() && SkipDelegatedReaderToColumns(_delegatedReader);
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            throw _command.MapAhtolaException(ex);
        }
    }

    private bool NextResultCore(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_statement is null)
            return false;
        while (GetStatement().Read(cancellationToken))
        {
        }

        CountCurrentStatementRowsAffected();

        _hasCurrentRow = false;
        _hasPrefetchedRow = false;
        _statement.Dispose();
        _statement = null;
        _currentStatementRowsAffectedCounted = false;

        try
        {
            while (_remainingSql.Count > 0)
            {
                var sql = _remainingSql[0];
                _remainingSql.RemoveAt(0);
                if (_command.TryHandleFacadeStatement(sql, out var rewrittenSql))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                var statement = _command.PrepareSingleStatement(rewrittenSql);
                if (cancellationToken.IsCancellationRequested)
                {
                    statement.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (statement.ColumnCount > 0)
                {
                    _statement = statement;
                    _currentSql = rewrittenSql;
                    _hadResultSet = true;
                    _currentStatementRowsAffectedCounted = false;
                    _hasPrefetchedRow = statement.Read(cancellationToken);
                    return true;
                }

                while (statement.Read(cancellationToken))
                {
                }

                if (SqliteCommand.CountsRowsAffected(sql))
                {
                    _hadRecordsAffectedStatement = true;
                    _recordsAffected += statement.RowsAffected;
                }
                _command.MarkTransactionCompletedExternally(sql);
                statement.Dispose();
            }
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            _statement?.Dispose();
            _statement = null;
            _hasPrefetchedRow = false;
            _remainingSql.Clear();
            throw SqliteCommand.ToSqliteException(ex);
        }

        return false;
    }

    private async ValueTask<bool> NextResultCoreAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_statement is null)
            return false;
        while (await GetStatement().ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        CountCurrentStatementRowsAffected();

        _hasCurrentRow = false;
        _hasPrefetchedRow = false;
        await _statement.DisposeAsync().ConfigureAwait(false);
        _statement = null;
        _currentStatementRowsAffectedCounted = false;

        try
        {
            while (_remainingSql.Count > 0)
            {
                var sql = _remainingSql[0];
                _remainingSql.RemoveAt(0);
                if (_command.TryHandleFacadeStatement(sql, out var rewrittenSql))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                var statement = await _command
                    .PrepareSingleStatementAsync(rewrittenSql, cancellationToken)
                    .ConfigureAwait(false);
                if (statement.ColumnCount > 0)
                {
                    _statement = statement;
                    _currentSql = rewrittenSql;
                    _hadResultSet = true;
                    _currentStatementRowsAffectedCounted = false;
                    _hasPrefetchedRow = await statement.ReadAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

                try
                {
                    while (await statement.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                    }

                    if (SqliteCommand.CountsRowsAffected(sql))
                    {
                        _hadRecordsAffectedStatement = true;
                        _recordsAffected += statement.RowsAffected;
                    }
                    _command.MarkTransactionCompletedExternally(sql);
                }
                finally
                {
                    await statement.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            if (_statement is not null)
                await _statement.DisposeAsync().ConfigureAwait(false);
            _statement = null;
            _hasPrefetchedRow = false;
            _remainingSql.Clear();
            throw SqliteCommand.ToSqliteException(ex);
        }

        return false;
    }

    public override bool Read()
    {
        ThrowIfSynchronousBrowserOperation();
        if (_delegatedReader is null)
            return _command.RunOperation(ReadCore);

        try
        {
            return _delegatedReader.Read();
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            throw _command.MapAhtolaException(ex);
        }
    }

    private bool ReadCore(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_statement is null)
            return false;
        if (_hasPrefetchedRow)
        {
            _hasPrefetchedRow = false;
            _hasCurrentRow = true;
            return true;
        }

        try
        {
            _hasCurrentRow = GetStatement().Read(cancellationToken);
            if (!_hasCurrentRow)
                CountCurrentStatementRowsAffected();
            return _hasCurrentRow;
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            throw SqliteCommand.ToSqliteException(ex);
        }
    }

    private async ValueTask<bool> ReadCoreAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_statement is null)
            return false;
        if (_hasPrefetchedRow)
        {
            _hasPrefetchedRow = false;
            _hasCurrentRow = true;
            return true;
        }

        try
        {
            _hasCurrentRow = await GetStatement().ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!_hasCurrentRow)
                CountCurrentStatementRowsAffected();
            return _hasCurrentRow;
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            throw SqliteCommand.ToSqliteException(ex);
        }
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (_delegatedReader is null)
        {
            return _statement?.UsesManagedResults == true
                ? await _command.RunOperationAsync(ReadCoreAsync, cancellationToken).ConfigureAwait(false)
                : await _command.RunOperationAsync(ReadCore, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _delegatedReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            throw _command.MapAhtolaException(ex);
        }
    }

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        if (_delegatedReader is null)
        {
            return _statement?.UsesManagedResults == true
                ? await _command.RunOperationAsync(NextResultCoreAsync, cancellationToken).ConfigureAwait(false)
                : await _command.RunOperationAsync(NextResultCore, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (!_skipDelegatedReaderToFirstColumnResult)
            {
                // SqliteBatch's explicit DbBatchCommands (and AhtolaBatch's own DbBatch API used
                // directly) must expose every command 1:1: advance exactly one step, whatever
                // its column count, matching SequentialBatchDataReader's local/replica contract.
                return await _delegatedReader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            }

            // Mirror NextResult() above: always move at least one step, then keep skipping
            // forward through any further 0-column (non-RETURNING DML) results.
            if (!await _delegatedReader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                return false;

            return await SkipDelegatedReaderToColumnsAsync(_delegatedReader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException or HttpRequestException)
        {
            throw _command.MapAhtolaException(ex);
        }
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => _delegatedReader?.IsDBNullAsync(ordinal, cancellationToken) ?? CompleteAsync(() => IsDBNull(ordinal), cancellationToken);

    private void ThrowIfSynchronousBrowserOperation()
    {
        if (_command.RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                "Synchronous reader iteration is not supported by the browser database source. "
                + "Use ReadAsync or NextResultAsync.");
        }
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => CompleteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetFieldValue<T>(ordinal);
        }, cancellationToken);

    private string GetDelegatedTextValue(int ordinal)
    {
        var value = _delegatedReader!.GetValue(ordinal);
        if (value == DBNull.Value)
            throw new InvalidOperationException(Properties.Resources.CalledOnNullValue(ordinal));

        return value switch
        {
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static Task<T> CompleteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Complete(operation);
    }

    private static Task<T> Complete<T>(Func<T> operation)
    {
        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCore(throwOnError: false);

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_delegatedReader is null && _statement?.UsesManagedResults != true)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await CloseCoreAsync(throwOnError: false).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void Close() => CloseCore();

    void IConnectionOwnedReader.CloseFromConnection()
    {
        if (_isClosed)
            return;

        _delegatedReader?.Dispose();
        _delegatedReader = null;
        _statement?.Dispose();
        _statement = null;
        _remainingSql.Clear();
        _hasPrefetchedRow = false;
        _hasCurrentRow = false;
        FinishClose(closeConnection: false);
    }

    private void CloseCore(bool throwOnError = true)
    {
        if (_isClosed)
            return;

        try
        {
            if (_delegatedReader is not null)
            {
                _recordsAffected = _delegatedReader.RecordsAffected;
                _hadRecordsAffectedStatement = true;
                _delegatedReader.Dispose();
                _delegatedReader = null;
                FinishClose();
                return;
            }

            if (_statement is not null)
            {
                while (GetStatement().Read())
                {
                }

                CountCurrentStatementRowsAffected();

                _statement.Dispose();
                _statement = null;
                _hasPrefetchedRow = false;
                _currentStatementRowsAffectedCounted = false;
            }

            DrainRemainingStatements();
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            _statement?.Dispose();
            _statement = null;
            _remainingSql.Clear();
            FinishClose();
            if (throwOnError)
                throw SqliteCommand.ToSqliteException(ex);
            return;
        }
        catch (SqliteException)
        {
            _statement?.Dispose();
            _statement = null;
            _remainingSql.Clear();
            FinishClose();
            if (throwOnError)
                throw;
            return;
        }

        FinishClose();
    }

    private async ValueTask CloseCoreAsync(bool throwOnError = true)
    {
        if (_isClosed)
            return;

        try
        {
            if (_delegatedReader is not null)
            {
                _recordsAffected = _delegatedReader.RecordsAffected;
                _hadRecordsAffectedStatement = true;
                await _delegatedReader.DisposeAsync().ConfigureAwait(false);
                _delegatedReader = null;
                FinishClose();
                return;
            }

            if (_statement is not null)
            {
                while (await GetStatement().ReadAsync().ConfigureAwait(false))
                {
                }

                CountCurrentStatementRowsAffected();

                await _statement.DisposeAsync().ConfigureAwait(false);
                _statement = null;
                _hasPrefetchedRow = false;
                _currentStatementRowsAffectedCounted = false;
            }

            await DrainRemainingStatementsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            if (_statement is not null)
                await _statement.DisposeAsync().ConfigureAwait(false);
            _statement = null;
            _remainingSql.Clear();
            FinishClose();
            if (throwOnError)
                throw SqliteCommand.ToSqliteException(ex);
            return;
        }
        catch (SqliteException)
        {
            if (_statement is not null)
                await _statement.DisposeAsync().ConfigureAwait(false);
            _statement = null;
            _remainingSql.Clear();
            FinishClose();
            if (throwOnError)
                throw;
            return;
        }

        FinishClose();
    }

    private void FinishClose(bool closeConnection = true)
    {
        if (_isClosed)
            return;

        _closeCallback();
        ((ILocalReaderConnection)_connection).ReaderClosed(this);
        if (closeConnection && (_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
            _connection.Close();

        _isClosed = true;
    }

    private void EnsureOpen([CallerMemberName] string operation = "")
    {
        if (IsClosed)
            throw new InvalidOperationException(Properties.Resources.DataReaderClosed(NormalizeOperationName(operation)));
    }

    private static string NormalizeOperationName(string operation)
        => operation.StartsWith("get_", StringComparison.Ordinal)
            ? operation[4..]
            : operation;

    private SqliteStatementAdapter GetStatement()
    {
        if (_statement is null)
            throw new InvalidOperationException(Properties.Resources.NoData);

        return _statement;
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, null);
    }

    private void EnsureHasCurrentRow()
    {
        if (!_hasCurrentRow)
            throw new InvalidOperationException(Properties.Resources.NoData);
    }

    private void CountCurrentStatementRowsAffected()
    {
        if (_statement is not null
            && !_currentStatementRowsAffectedCounted
            && SqliteCommand.CountsRowsAffected(_currentSql))
        {
            _hadRecordsAffectedStatement = true;
            _recordsAffected += GetStatement().RowsAffected;
            _currentStatementRowsAffectedCounted = true;
        }
    }

    private string GetDeclaredTypeName(int ordinal)
    {
        if (_command.Connection is { RequiresAsyncExecution: true } browserConnection)
        {
            var browserMatch = Regex.Match(
                _command.CommandText,
                @"^\s*SELECT\s+(?<column>[\w\[\]""`]+)\s+FROM\s+(?<table>[\w\[\]""`.]+)",
                RegexOptions.IgnoreCase);
            if (browserMatch.Success)
            {
                var columnName = UnquoteIdentifier(browserMatch.Groups["column"].Value);
                var tableName = browserMatch.Groups["table"].Value
                    .Split('.')
                    .Select(UnquoteIdentifier)
                    .ToArray();
                var qualifiedTable = string.Join('.', tableName);
                var columns = GetManagedTableColumns(
                    browserConnection.ManagedConnection,
                    qualifiedTable);
                if (columns.TryGetValue(columnName, out var schemaColumn))
                    return StripTypeLength(schemaColumn.TypeName);
            }
        }

        if (TryGetSelectSources(out var sources, out var selections))
        {
            var sourceColumns = GetSelectSourceColumns(sources);
            var columnName = GetName(ordinal);
            var selection = ordinal < selections.Count ? selections[ordinal] : columnName;
            var resolvedColumn = ResolveSelectColumn(selection, columnName, sources, sourceColumns);
            if (resolvedColumn is not null)
                return StripTypeLength(resolvedColumn.Column.TypeName);
        }

        var match = Regex.Match(_command.CommandText, @"^\s*SELECT\s+(?<column>[\w\[\]""`]+)\s+FROM\s+(?<table>[\w\[\]""`]+)", RegexOptions.IgnoreCase);
        if (!match.Success || _command.Connection is null)
            return string.Empty;

        var column = UnquoteIdentifier(match.Groups["column"].Value);
        if (!string.Equals(column, GetName(ordinal), StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var table = UnquoteIdentifier(match.Groups["table"].Value);
        using var suspension = _command.Connection.SuspendHooks();
        using var command = _command.Connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return StripTypeLength(reader.GetString(2));
        }

        return string.Empty;
    }

    private bool HasUndeclaredSelectSourceColumn(int ordinal)
    {
        if (!TryGetSelectSources(out var sources, out var selections))
            return false;

        var sourceColumns = GetSelectSourceColumns(sources);
        var columnName = GetName(ordinal);
        var selection = ordinal < selections.Count ? selections[ordinal] : columnName;
        var resolvedColumn = ResolveSelectColumn(selection, columnName, sources, sourceColumns);
        return resolvedColumn is not null && string.IsNullOrEmpty(resolvedColumn.Column.TypeName);
    }

    private static string InferDataTypeName(string expression)
    {
        var trimmed = expression.Trim();
        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return "BLOB";
        if (trimmed.StartsWith("X'", StringComparison.OrdinalIgnoreCase))
            return "BLOB";
        if (trimmed.StartsWith('\''))
            return "TEXT";
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return "INTEGER";
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return "REAL";

        return "BLOB";
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string UnquoteIdentifier(string identifier)
    {
        var trimmed = identifier.Trim();
        if (trimmed.Length < 2)
            return trimmed;

        return (trimmed[0], trimmed[^1]) switch
        {
            ('"', '"') => trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
            ('[', ']') => trimmed[1..^1].Replace("]]", "]", StringComparison.Ordinal),
            ('`', '`') => trimmed[1..^1].Replace("``", "`", StringComparison.Ordinal),
            _ => trimmed
        };
    }

    private static string StripTypeLength(string typeName)
    {
        var index = typeName.IndexOf('(');
        return index < 0 ? typeName : typeName[..index];
    }

    private bool TryGetSelectSources(out List<SelectSource> sources, out List<string> selections)
    {
        sources = new List<SelectSource>();
        selections = new List<string>();
        var match = Regex.Match(
            _currentSql,
            @"^\s*SELECT\s+(?<select>.*?)\s+FROM\s+",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success && TryGetSelectSources(_currentSql, out sources))
        {
            selections = ExpandWildcardSelections(StripSelectModifier(SplitSelectList(match.Groups["select"].Value)), sources);
            return true;
        }

        // DML ... RETURNING <cols>: the result columns come from the target table, so the
        // declared column types resolve via PRAGMA table_info of the source table. Without this,
        // GetSchemaTable/GetDeclaredTypeName fall back to GetSampleValueType, which would
        // RE-EXECUTE the DML once per column (INSERT...RETURNING -> N+1 inserts) and report
        // Byte[]/BLOB for every column before the first Read.
        var returningMatch = Regex.Match(
            _currentSql,
            @"\bRETURNING\s+(?<cols>.+?)\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!returningMatch.Success)
            return false;

        var tableMatch = Regex.Match(
            _currentSql,
            @"(?:INSERT\s+(?:OR\s+\w+\s+)?INTO|UPDATE|DELETE\s+FROM)\s+(?<table>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)",
            RegexOptions.IgnoreCase);
        if (!tableMatch.Success)
            return false;

        var tableName = UnquoteIdentifier(tableMatch.Groups["table"].Value);
        sources.Add(new SelectSource(tableName, tableName));
        selections = SplitSelectList(returningMatch.Groups["cols"].Value);
        if (selections.Count == 1 && selections[0] == "*")
            selections = Enumerable.Range(0, FieldCount).Select(GetName).ToList();

        return true;
    }

    private static List<string> StripSelectModifier(List<string> selections)
    {
        if (selections.Count > 0)
            selections[0] = Regex.Replace(selections[0], @"^\s*(?:DISTINCT|ALL)\s+", "", RegexOptions.IgnoreCase);

        return selections;
    }

    private static bool TryGetSelectSources(string sql, out List<SelectSource> sources)
    {
        sources = new List<SelectSource>();
        var matches = Regex.Matches(
            sql,
            @"\b(?:FROM|JOIN)\s+(?<table>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)(?:\s+(?:AS\s+)?(?<alias>[\w]+))?",
            RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var tableName = UnquoteIdentifier(match.Groups["table"].Value);
            var alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : tableName;
            if (IsJoinKeyword(alias))
                alias = tableName;

            sources.Add(new SelectSource(tableName, alias));
        }

        return sources.Count > 0;
    }

    private List<string> ExpandWildcardSelections(List<string> selections, List<SelectSource> sources)
    {
        var expanded = new List<string>();
        foreach (var selection in selections)
        {
            var match = Regex.Match(
                selection,
                @"^(?:(?<alias>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)\.)?\*$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                expanded.Add(selection);
                continue;
            }

            var alias = match.Groups["alias"].Success ? UnquoteIdentifier(match.Groups["alias"].Value) : null;
            foreach (var source in sources.Where(source => alias is null || string.Equals(source.Alias, alias, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var column in GetTableColumns(source.TableName).Values)
                    expanded.Add($"{source.Alias}.{QuoteIdentifier(column.Name)}");
            }
        }

        return expanded;
    }

    private Dictionary<string, Dictionary<string, SchemaColumnInfo>> GetSelectSourceColumns(List<SelectSource> sources)
    {
        var columns = new Dictionary<string, Dictionary<string, SchemaColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            columns[source.Alias] = GetTableColumns(source.TableName);

        return columns;
    }

    private static ResolvedSchemaColumn? ResolveSelectColumn(
        string selection,
        string columnName,
        List<SelectSource> sources,
        IReadOnlyDictionary<string, Dictionary<string, SchemaColumnInfo>> sourceColumns)
    {
        var withoutAlias = Regex.Replace(selection, @"\s+AS\s+.*$", "", RegexOptions.IgnoreCase).Trim();
        var qualifiedMatch = Regex.Match(
            withoutAlias,
            @"^(?<alias>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)\.(?<column>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)$",
            RegexOptions.IgnoreCase);
        if (qualifiedMatch.Success)
        {
            var alias = UnquoteIdentifier(qualifiedMatch.Groups["alias"].Value);
            var column = UnquoteIdentifier(qualifiedMatch.Groups["column"].Value);
            if (sourceColumns.TryGetValue(alias, out var columns)
                && columns.TryGetValue(column, out var columnInfo))
            {
                var source = sources.First(source => string.Equals(source.Alias, alias, StringComparison.OrdinalIgnoreCase));
                return new ResolvedSchemaColumn(source.TableName, columnInfo);
            }
        }

        var candidate = UnquoteIdentifier(withoutAlias);
        var canUseColumnName = selection.Length == withoutAlias.Length
            && !Regex.IsMatch(selection, @"[+\-*/()]");
        var matches = new List<ResolvedSchemaColumn>();
        foreach (var source in sources)
        {
            if (sourceColumns[source.Alias].TryGetValue(candidate, out var columnInfo)
                || sourceColumns[source.Alias].TryGetValue(columnName, out columnInfo)
                    && canUseColumnName)
            {
                matches.Add(new ResolvedSchemaColumn(source.TableName, columnInfo));
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsJoinKeyword(string value)
        => value.Equals("LEFT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("INNER", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OUTER", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CROSS", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FULL", StringComparison.OrdinalIgnoreCase)
            || value.Equals("WHERE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ORDER", StringComparison.OrdinalIgnoreCase)
            || value.Equals("GROUP", StringComparison.OrdinalIgnoreCase)
            || value.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ON", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitSelectList(string selectList)
    {
        var selections = new List<string>();
        var start = 0;
        var quote = false;
        for (var i = 0; i < selectList.Length; i++)
        {
            if (selectList[i] == '\'')
                quote = !quote;
            else if (!quote && selectList[i] == ',')
            {
                selections.Add(selectList[start..i].Trim());
                start = i + 1;
            }
        }

        selections.Add(selectList[start..].Trim());
        return selections;
    }

    private Dictionary<string, SchemaColumnInfo> GetTableColumns(string tableName)
    {
        var columns = new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase);
        if (_command.Connection is null)
            return columns;
        if (_command.Connection.RequiresAsyncExecution)
            return GetManagedTableColumns(_command.Connection.ManagedConnection, tableName);

        using var suspension = _command.Connection.SuspendHooks();
        using (var command = _command.Connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                columns[name] = new SchemaColumnInfo(
                    name,
                    reader.GetString(2),
                    reader.GetInt64(3) == 0,
                    reader.GetInt64(5) != 0,
                    false);
            }
        }

        using (var indexCommand = _command.Connection.CreateCommand())
        {
            indexCommand.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            using var indexes = indexCommand.ExecuteReader();
            while (indexes.Read())
            {
                if (indexes.GetInt64(2) == 0 || indexes.GetInt64(4) != 0)
                    continue;

                var indexName = indexes.GetString(1);
                using var infoCommand = _command.Connection.CreateCommand();
                infoCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)});";
                using var indexInfo = infoCommand.ExecuteReader();
                var indexedColumns = new List<string>();
                while (indexInfo.Read())
                {
                    if (!indexInfo.IsDBNull(2))
                        indexedColumns.Add(indexInfo.GetString(2));
                }

                if (indexedColumns.Count == 1 && columns.TryGetValue(indexedColumns[0], out var column))
                    columns[indexedColumns[0]] = column with { IsUnique = true };
            }
        }

        return columns;
    }

    private static Dictionary<string, SchemaColumnInfo> GetManagedTableColumns(
        IManagedConnectionAdapter connection,
        string tableName)
    {
        var columns = new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase);
        using (var statement = connection.Prepare(
                   BuildManagedPragma(tableName, "table_info")))
        {
            while (statement.Step() == StatementStepResult.Row)
            {
                var name = statement.GetValue(1).AsText();
                columns[name] = new SchemaColumnInfo(
                    name,
                    statement.GetValue(2).AsText(),
                    statement.GetValue(3).AsInteger() == 0,
                    statement.GetValue(5).AsInteger() != 0,
                    false);
            }
        }

        using var indexes = connection.Prepare(
            BuildManagedPragma(tableName, "index_list"));
        while (indexes.Step() == StatementStepResult.Row)
        {
            if (indexes.GetValue(2).AsInteger() == 0
                || indexes.GetValue(4).AsInteger() != 0)
            {
                continue;
            }

            var indexName = indexes.GetValue(1).AsText();
            using var info = connection.Prepare(
                BuildManagedPragma(
                    QualifyPragmaObject(tableName, indexName),
                    "index_info"));
            string? indexedColumn = null;
            var indexedColumnCount = 0;
            while (info.Step() == StatementStepResult.Row)
            {
                if (info.GetValue(2).Kind != SqlValueKind.Null)
                {
                    indexedColumn = info.GetValue(2).AsText();
                    indexedColumnCount++;
                }
            }

            if (indexedColumnCount == 1
                && indexedColumn is not null
                && columns.TryGetValue(indexedColumn, out var column))
            {
                columns[indexedColumn] = column with { IsUnique = true };
            }
        }

        return columns;
    }

    private static string BuildManagedPragma(string objectName, string pragma)
    {
        var separator = objectName.IndexOf('.');
        return separator < 0
            ? $"PRAGMA {pragma}({QuoteIdentifier(objectName)});"
            : $"PRAGMA {QuoteIdentifier(objectName[..separator])}.{pragma}"
              + $"({QuoteIdentifier(objectName[(separator + 1)..])});";
    }

    private static string QualifyPragmaObject(string tableName, string objectName)
    {
        var separator = tableName.IndexOf('.');
        return separator < 0
            ? objectName
            : tableName[..separator] + "." + objectName;
    }

    private static string GetDataTypeNameFromValueType(ReaderValueKind valueType, string selection)
    {
        if (valueType == ReaderValueKind.Blob && Regex.IsMatch(selection, @"[+\-*/]"))
            return "INTEGER";

        return valueType switch
        {
            ReaderValueKind.Integer => "INTEGER",
            ReaderValueKind.Real => "REAL",
            ReaderValueKind.Text => "TEXT",
            _ => "BLOB"
        };
    }

    private ReaderValueKind GetSampleValueType(int ordinal)
    {
        // Re-executing the statement to sample a value type is only safe for read-only SELECTs.
        // DML (INSERT/UPDATE/DELETE/REPLACE, including ... RETURNING) would mutate the database
        // again on each call — once per column — corrupting rows. Declared types for RETURNING
        // are resolved via TryGetSelectSource; this fallback must stay read-only and otherwise
        // report BLOB, matching Microsoft.Data.Sqlite (which exposes a NULL declared type for
        // non-table expressions).
        if (IsMutableStatement(_currentSql))
            return ReaderValueKind.Blob;

        using var statement = _command.PrepareSingleStatement(_currentSql);
        while (statement.Read())
        {
            var value = statement.UsesManagedResults
                ? ReaderValue.FromManaged(statement.ManagedCurrentRow.GetValue(ordinal))
                : ReaderValue.FromNative(statement.GetNativeValue(ordinal));
            if (value.Kind is not ReaderValueKind.Null and not ReaderValueKind.Empty)
                return value.Kind;
        }

        return ReaderValueKind.Blob;
    }

    private static bool IsMutableStatement(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        return StartsWithKeyword(trimmed, "INSERT")
            || StartsWithKeyword(trimmed, "UPDATE")
            || StartsWithKeyword(trimmed, "DELETE")
            || StartsWithKeyword(trimmed, "REPLACE");
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> text, string keyword)
    {
        if (text.Length < keyword.Length)
            return false;

        if (!text[..keyword.Length].Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        return text.Length == keyword.Length || !char.IsLetterOrDigit(text[keyword.Length]);
    }

    private static ReaderValueKind GetValueKindFromTypeName(string typeName)
    {
        var normalized = StripTypeLength(typeName).ToUpperInvariant();
        if (IsGuidType(normalized))
            return ReaderValueKind.Blob;
        if (normalized.Contains("INT"))
            return ReaderValueKind.Integer;
        if (normalized.Contains("CHAR") || normalized.Contains("CLOB") || normalized.Contains("TEXT"))
            return ReaderValueKind.Text;
        if (normalized.Contains("REAL") || normalized.Contains("FLOA") || normalized.Contains("DOUB"))
            return ReaderValueKind.Real;
        if (normalized.Length == 0 || normalized.Contains("BLOB"))
            return ReaderValueKind.Blob;

        // Unknown declared types have NUMERIC affinity in SQLite. The provider surfaces them
        // as text, matching GetClrTypeFromSqliteType and avoiding conflicting BLOB metadata.
        return ReaderValueKind.Text;
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type GetClrTypeFromSqliteType(string typeName, ReaderValueKind fallback)
    {
        if (IsGuidType(typeName))
            return typeof(Guid);

        var normalized = typeName.ToUpperInvariant();
        if (normalized.Length == 0)
            return GetClrTypeFromValueType(fallback);
        if (normalized.Contains("INT"))
            return typeof(long);
        if (normalized.Contains("CHAR") || normalized.Contains("CLOB") || normalized.Contains("TEXT"))
            return typeof(string);
        if (normalized.Contains("REAL") || normalized.Contains("FLOA") || normalized.Contains("DOUB"))
            return typeof(double);
        if (normalized.Contains("BLOB"))
            return typeof(byte[]);

        return typeof(string);
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type GetClrTypeFromValueType(ReaderValueKind valueType)
        => valueType switch
        {
            ReaderValueKind.Integer => typeof(long),
            ReaderValueKind.Real => typeof(double),
            ReaderValueKind.Text => typeof(string),
            _ => typeof(byte[])
        };

    private static bool IsGuidType(string typeName)
    {
        var normalized = StripTypeLength(typeName).Trim();
        return normalized.Equals("GUID", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldMaterializeTextGuid(int ordinal, string declaredType, ReaderValue value)
    {
        // SQLite's dynamic storage permits a .NET Guid parameter to retain its BLOB storage class
        // even when the source declaration is TEXT, BLOB, or absent. With BinaryGUID enabled,
        // preserve such identifier values through DataAdapter instead of letting DataColumn
        // stringify the byte array as "System.Byte[]". Restrict this compatibility conversion to
        // conventional identifier names so binary payloads remain blobs.
        return _connection.BinaryGuid
            && value.Kind == ReaderValueKind.Blob
            && value.Blob.Length == 16
            && (string.IsNullOrWhiteSpace(declaredType)
                || IsTextType(declaredType)
                || IsBlobType(declaredType))
            && IsIdentifierColumnName(GetName(ordinal));
    }

    private static bool IsTextType(string typeName)
    {
        var normalized = StripTypeLength(typeName).Trim();
        return normalized.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("CLOB", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("TEXT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlobType(string typeName)
        => StripTypeLength(typeName).Contains("BLOB", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifierColumnName(string columnName)
    {
        var separator = columnName.LastIndexOf('.');
        var name = separator >= 0 ? columnName[(separator + 1)..] : columnName;
        return name.Equals("ID", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ID", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SelectSource(string TableName, string Alias);

    private sealed record ResolvedSchemaColumn(string TableName, SchemaColumnInfo Column);

    private sealed record SchemaColumnInfo(string Name, string TypeName, bool AllowNull, bool IsKey, bool IsUnique);

    private ReaderValue GetTypedValue(int ordinal)
    {
        EnsureHasCurrentRow();
        ValidateOrdinal(ordinal);
        var value = ReadValue(ordinal);
        if (value.Kind is ReaderValueKind.Null or ReaderValueKind.Empty)
            throw new InvalidOperationException(Properties.Resources.CalledOnNullValue(ordinal));

        return value;
    }

    private static DateTime ParseDateTime(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTimeOffset)
            && HasOffset(value))
            return dateTimeOffset.UtcDateTime;

        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        if (HasOffset(value))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

        return new DateTimeOffset(DateTime.Parse(value, CultureInfo.InvariantCulture), TimeSpan.Zero);
    }

    private static bool HasOffset(string value)
    {
        var timeSeparator = value.IndexOf(':', StringComparison.Ordinal);
        return timeSeparator >= 0
               && (value.EndsWith('Z')
                   || value.LastIndexOf('+') > timeSeparator
                   || value.LastIndexOf('-') > timeSeparator);
    }

    private static long CopyValue<T>(T[] source, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        if (dataOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(dataOffset), dataOffset, message: null);
        if (buffer is null)
            return source.LongLength;
        if (bufferOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferOffset), bufferOffset, message: null);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, message: null);
        if (bufferOffset > buffer.Length - length)
            throw new ArgumentException(Properties.Resources.InvalidOffsetAndCount);
        if (dataOffset >= source.LongLength)
            return 0;

        var count = (int)Math.Min(length, source.LongLength - dataOffset);
        Array.Copy(source, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    private byte[] GetBlobValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.Kind == ReaderValueKind.Blob
            ? value.Blob
            : throw new InvalidCastException("The requested value is not a BLOB.");
    }

    private string GetTextValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.Kind == ReaderValueKind.Text
            ? value.Text
            : throw new InvalidCastException("The requested value is not TEXT.");
    }

    private Guid ToGuid(int ordinal, ReaderValue value)
    {
        if (value.Kind == ReaderValueKind.Blob)
        {
            if (value.Blob.Length == 16)
            {
                // BinaryGUID=true (default): little-endian .NET Guid blob layout (SDS default).
                // BinaryGUID=false: big-endian RFC 4122 byte order.
                return _connection.BinaryGuid
                    ? new Guid(value.Blob)
                    : new Guid(
                        (value.Blob[0] << 24) | (value.Blob[1] << 16) | (value.Blob[2] << 8) | value.Blob[3],
                        (short)((value.Blob[4] << 8) | value.Blob[5]),
                        (short)((value.Blob[6] << 8) | value.Blob[7]),
                        value.Blob[8],
                        value.Blob[9],
                        value.Blob[10],
                        value.Blob[11],
                        value.Blob[12],
                        value.Blob[13],
                        value.Blob[14],
                        value.Blob[15]);
            }

            if (Guid.TryParse(Encoding.UTF8.GetString(value.Blob), out var blobGuid))
                return blobGuid;

            throw CreateGuidFormatException(ordinal, value);
        }

        if (value.Kind == ReaderValueKind.Text && Guid.TryParse(value.Text, out var textGuid))
            return textGuid;

        throw CreateGuidFormatException(ordinal, value);
    }

    private InvalidOperationException CreateGuidFormatException(int ordinal, ReaderValue value)
    {
        var storageDetails = value.Kind == ReaderValueKind.Blob
            ? $"BLOB ({value.Blob.Length} bytes)"
            : value.Kind.ToString().ToUpperInvariant();
        var declaredType = GetDeclaredTypeName(ordinal);
        return new InvalidOperationException(
            $"Unable to parse GUID for column '{GetName(ordinal)}' (ordinal {ordinal}, declared type '{declaredType}', storage {storageDetails}).");
    }

    private DateTime ApplyConfiguredDateTimeKind(DateTime value)
    {
        // Match Microsoft.Data.Sqlite / SDS: apply connection DateTimeKind via SpecifyKind
        // (no zone conversion) so RDM DateTimeKind=Utc surfaces Kind=Utc on reads.
        return DateTime.SpecifyKind(value, _connection.DateTimeKind);
    }

    private void DrainRemainingStatements()
    {
        try
        {
            foreach (var sql in _remainingSql)
            {
                if (_command.TryHandleFacadeStatement(sql, out var rewrittenSql))
                    continue;

                using var statement = _command.PrepareSingleStatement(rewrittenSql);
                while (statement.Read())
                {
                }

                if (SqliteCommand.CountsRowsAffected(rewrittenSql))
                {
                    _hadRecordsAffectedStatement = true;
                    _recordsAffected += statement.RowsAffected;
                }
                _command.MarkTransactionCompletedExternally(sql);
            }
        }

        finally
        {
            _remainingSql.Clear();
        }
    }

    private async ValueTask DrainRemainingStatementsAsync()
    {
        try
        {
            foreach (var sql in _remainingSql)
            {
                if (_command.TryHandleFacadeStatement(sql, out var rewrittenSql))
                    continue;

                var statement = await _command.PrepareSingleStatementAsync(rewrittenSql).ConfigureAwait(false);
                try
                {
                    while (await statement.ReadAsync().ConfigureAwait(false))
                    {
                    }

                    if (SqliteCommand.CountsRowsAffected(rewrittenSql))
                    {
                        _hadRecordsAffectedStatement = true;
                        _recordsAffected += statement.RowsAffected;
                    }
                    _command.MarkTransactionCompletedExternally(sql);
                }
                finally
                {
                    await statement.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _remainingSql.Clear();
        }
    }

    private static SqliteType GetSqliteType(ReaderValueKind valueType)
    {
        return valueType switch
        {
            ReaderValueKind.Integer => SqliteType.Integer,
            ReaderValueKind.Real => SqliteType.Real,
            ReaderValueKind.Blob => SqliteType.Blob,
            _ => SqliteType.Text,
        };
    }

    private ReaderValue ReadValue(int ordinal)
    {
        var statement = GetStatement();
        return statement.UsesManagedResults
            ? ReaderValue.FromManaged(statement.ManagedCurrentRow.GetValue(ordinal))
            : ReaderValue.FromNative(statement.GetNativeValue(ordinal));
    }

    /// <summary>
    /// Returns the value kind of the current row, or <see cref="ReaderValueKind.Empty"/> when the
    /// reader is not positioned on a row. Type metadata has to stay readable before the first
    /// <c>Read</c> because <see cref="System.Data.Common.DbDataAdapter"/> maps the result schema
    /// before it fetches any rows.
    /// </summary>
    private ReaderValueKind CurrentValueKind(int ordinal)
        => _hasCurrentRow ? ReadValue(ordinal).Kind : ReaderValueKind.Empty;
}
