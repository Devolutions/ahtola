using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ahtola.Core;
using Ahtola;

namespace Ahtola.Data.Sqlite;

public class SqliteParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private string _sourceColumn = string.Empty;
    private object? _value;
    private int? _size;
    private bool _hasValue;
    private DbType _dbType = DbType.String;
    private SqliteType? _sqliteType;
    // True when _sqliteType was derived from DbType rather than set explicitly: Microsoft.Data.Sqlite
    // lets the value pick the storage class in that case, so an incompatible value falls back to it.
    private bool _sqliteTypeFromDbType;

    public SqliteParameter()
    {
    }

    public SqliteParameter(string? name, object? value)
    {
        ParameterName = name;
        Value = value;
    }

    public SqliteParameter(string? name, SqliteType type)
    {
        ParameterName = name;
        SqliteType = type;
    }

    public SqliteParameter(string? name, SqliteType type, int size)
        : this(name, type)
    {
        Size = size;
    }

    public SqliteParameter(string? name, SqliteType type, int size, string? sourceColumn)
        : this(name, type, size)
    {
        SourceColumn = sourceColumn;
    }

    public override DbType DbType
    {
        get => _dbType;
        set
        {
            _dbType = value;
            _sqliteTypeFromDbType = true;
            _sqliteType = value switch
            {
                DbType.Binary or DbType.Guid => SqliteType.Blob,
                DbType.Byte or DbType.Boolean or DbType.Int16 or DbType.Int32 or DbType.Int64 or DbType.SByte
                    or DbType.UInt16 or DbType.UInt32 or DbType.UInt64 => SqliteType.Integer,
                DbType.Double or DbType.Single => SqliteType.Real,
                _ => SqliteType.Text,
            };
        }
    }

    public SqliteType SqliteType
    {
        get => _sqliteType ?? InferSqliteType(Value);
        set
        {
            _sqliteType = value;
            _sqliteTypeFromDbType = false;
            _dbType = value switch
            {
                SqliteType.Integer => DbType.Int64,
                SqliteType.Real => DbType.Double,
                SqliteType.Blob => DbType.Binary,
                _ => DbType.String,
            };
        }
    }

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
                throw new ArgumentException(Properties.Resources.InvalidParameterDirection(value));
        }
    }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = value ?? string.Empty;
    }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    public override object? Value
    {
        get => _value;
        set
        {
            _value = value;
            _hasValue = true;
        }
    }

    public override bool SourceColumnNullMapping { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="DataRowVersion"/> read when the parameter is filled from
    /// a <see cref="DataRow"/>. The base implementation discards the value, which would make
    /// <see cref="Ahtola.AhtolaCommandBuilder"/>'s optimistic-concurrency predicates compare
    /// current instead of original values.
    /// </summary>
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    public override int Size
    {
        get => _size
            ?? (Value is string stringValue
                ? stringValue.Length
                : Value is byte[] bytes
                    ? bytes.Length
                    : 0);
        set
        {
            if (value < -1)
                throw new ArgumentOutOfRangeException(nameof(value), value, message: null);

            _size = value;
        }
    }

    public override void ResetDbType()
        => ResetSqliteType();

    public virtual void ResetSqliteType()
    {
        _sqliteType = null;
        _sqliteTypeFromDbType = false;
        _dbType = DbType.String;
    }

    /// <summary>
    /// The storage class and value this parameter binds. GUIDs follow the connection's
    /// <c>BinaryGUID</c> setting when nothing pins their storage class: the .NET byte layout as
    /// BLOB (true, the System.Data.SQLite default) or uppercase TEXT (false, as
    /// Microsoft.Data.Sqlite binds them). A <see cref="DbType"/> only picks the storage class
    /// when the value can be stored that way, so <see cref="DbType.Guid"/> accepts GUID strings.
    /// </summary>
    private (SqliteType Type, object Value) ResolveBinding(bool binaryGuid)
    {
        var value = Value!;
        if (_sqliteType is { } pinned && !_sqliteTypeFromDbType)
            return (pinned, value);

        if (_sqliteTypeFromDbType && _dbType == DbType.Guid)
        {
            if (value is string text && Guid.TryParse(text, out var parsed))
                value = parsed;
            if (value is Guid)
                return (binaryGuid ? SqliteType.Blob : SqliteType.Text, value);
            return (InferSqliteType(value), value);
        }

        if (_sqliteType is { } fromDbType)
            return (fromDbType, value);

        return value is Guid
            ? (binaryGuid ? SqliteType.Blob : SqliteType.Text, value)
            : (InferSqliteType(value), value);
    }

    internal bool HasValue => _hasValue;

    internal bool HasSize => _size.HasValue;

    internal AhtolaValue ToNativeValue()
    {
        if (Value is null || Value == DBNull.Value)
            return AhtolaValue.Null();

        return SqliteType switch
        {
            SqliteType.Integer => AhtolaValue.Int(ToInt64(Value)),
            SqliteType.Real => AhtolaValue.Real(ToDouble(Value)),
            SqliteType.Blob => AhtolaValue.Blob(ToBytes(Value)),
            SqliteType.Text => AhtolaValue.String(ApplySize(ToInvariantString(Value))),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    internal SqlValue ToSqlValue(bool binaryGuid = true)
    {
        if (Value is null or DBNull)
            return SqlValue.Null;

        var (type, value) = ResolveBinding(binaryGuid);
        return type switch
        {
            SqliteType.Integer => SqlValue.Integer(ToInt64(value)),
            SqliteType.Real => SqlValue.Real(ToDouble(value)),
            SqliteType.Blob => SqlValue.Blob(ToBytes(value)),
            SqliteType.Text => SqlValue.Text(ApplySize(ToInvariantString(value))),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Produces the SQLite storage-class value used by the remote ADO.NET bridge.
    /// This deliberately applies <see cref="SqliteType"/> and <see cref="Size"/> before
    /// handing the value to <c>AhtolaParameter</c>, whose general-purpose inference differs
    /// from Microsoft.Data.Sqlite's parameter contract.
    /// </summary>
    internal object? GetResolvedStorageValue()
    {
        if (Value is null || Value == DBNull.Value)
            return DBNull.Value;

        return SqliteType switch
        {
            SqliteType.Integer => ToInt64(Value),
            SqliteType.Real => ToDouble(Value),
            SqliteType.Blob => ToBytes(Value),
            SqliteType.Text => ApplySize(ToInvariantString(Value)),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static SqliteType InferSqliteType(object? value)
    {
        return value switch
        {
            null or DBNull => SqliteType.Text,
            byte[] or Memory<byte> or ReadOnlyMemory<byte> or Guid => SqliteType.Blob,
            Enum => SqliteType.Integer,
            float or double => SqliteType.Real,
            bool or byte or sbyte or short or ushort or int or uint or long or ulong => SqliteType.Integer,
            _ => SqliteType.Text,
        };
    }

    private string ToInvariantString(object value)
    {
        return value switch
        {
            object when value.GetType() == typeof(object) => throw new InvalidOperationException(Properties.Resources.UnknownDataType(value.GetType())),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.Ticks % TimeSpan.TicksPerSecond == 0
                ? timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            decimal decimalValue => decimal.Truncate(decimalValue) == decimalValue
                ? decimalValue.ToString("0.0", CultureInfo.InvariantCulture)
                : decimalValue.ToString(CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private byte[] ToBytes(object value)
    {
        return value switch
        {
            byte[] bytes => ApplySize(bytes),
            Memory<byte> memory => ApplySize(memory.ToArray()),
            ReadOnlyMemory<byte> memory => ApplySize(memory.ToArray()),
            Guid guid => guid.ToByteArray(),
            _ => throw new InvalidOperationException(Properties.Resources.UnknownDataType(value.GetType())),
        };
    }

    private byte[] ApplySize(byte[] bytes)
    {
        return _size is > -1 && _size < bytes.Length
            ? bytes[.._size.Value]
            : bytes;
    }

    private string ApplySize(string value)
    {
        return _size is > -1 && _size < value.Length
            ? value[.._size.Value]
            : value;
    }

    private static long ToInt64(object value)
    {
        // Microsoft.Data.Sqlite binds ulong with an unchecked (long) cast, so values above
        // long.MaxValue round-trip through SQLite INTEGER as two's complement.
        return value switch
        {
            ulong u64 => unchecked((long)u64),
            Enum enumValue => Type.GetTypeCode(Enum.GetUnderlyingType(enumValue.GetType())) switch
            {
                TypeCode.UInt64 => unchecked((long)Convert.ToUInt64(enumValue, CultureInfo.InvariantCulture)),
                _ => Convert.ToInt64(enumValue, CultureInfo.InvariantCulture),
            },
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        };
    }

    private static double ToDouble(object value)
    {
        var result = value switch
        {
            DateTime dateTime => dateTime.ToOADate() + 2415018.5,
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToOADate() + 2415018.5,
            DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue).ToOADate() + 2415018.5,
            TimeOnly timeOnly => timeOnly.ToTimeSpan().TotalDays,
            TimeSpan timeSpan => timeSpan.TotalDays,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };

        if (double.IsNaN(result))
            throw new InvalidOperationException(Properties.Resources.CannotStoreNaN);

        return result;
    }
}
