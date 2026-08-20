using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ahtola.Core;

namespace Ahtola;

public class AhtolaParameter : DbParameter
{
    private int _size;
    private static readonly Dictionary<Type, ParameterValueKind> ParameterTypeMapping =
        new()
        {
            { typeof(bool), ParameterValueKind.Integer },
            { typeof(byte), ParameterValueKind.Integer },
            { typeof(byte[]), ParameterValueKind.Blob },
            { typeof(char), ParameterValueKind.Text },
            { typeof(DateTime), ParameterValueKind.Text },
            { typeof(DateTimeOffset), ParameterValueKind.Text },
            { typeof(DateOnly), ParameterValueKind.Text },
            { typeof(TimeOnly), ParameterValueKind.Text },
            { typeof(DBNull), ParameterValueKind.Null },
            { typeof(decimal), ParameterValueKind.Text },
            { typeof(double), ParameterValueKind.Real },
            { typeof(float), ParameterValueKind.Real },
            { typeof(Guid), ParameterValueKind.Text },
            { typeof(int), ParameterValueKind.Integer },
            { typeof(long), ParameterValueKind.Integer },
            { typeof(sbyte), ParameterValueKind.Integer },
            { typeof(short), ParameterValueKind.Integer },
            { typeof(string), ParameterValueKind.Text },
            { typeof(TimeSpan), ParameterValueKind.Text },
            { typeof(uint), ParameterValueKind.Integer },
            { typeof(ulong), ParameterValueKind.Integer },
            { typeof(ushort), ParameterValueKind.Integer }
        };

    public AhtolaParameter()
    {
    }

    public AhtolaParameter(object value)
    {
        Value = value;
    }

    public AhtolaParameter(string parameterName, object value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    public AhtolaParameter(string parameterName, DbType dbType, object value)
    {
        ParameterName = parameterName;
        DbType = dbType;
        Value = value;
    }

    public override void ResetDbType()
    {
        DbType = DbType.String;
    }

    public override DbType DbType { get; set; } = DbType.String;

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new ArgumentException("Only input parameters are supported");
            }
        }
    }
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get; set; } = "";

    /// <summary>
    /// Gets or sets the <see cref="DataRowVersion"/> read when the parameter is filled from
    /// a <see cref="DataRow"/>. The base implementation discards the value, which would make
    /// <see cref="AhtolaCommandBuilder"/>'s optimistic-concurrency predicates compare current
    /// instead of original values.
    /// </summary>
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    [AllowNull]
    public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }

    public AhtolaValue ToValue()
    {
        if (Value is null)
            return new AhtolaValue { ValueType = AhtolaValueType.Null };

        var valueType = Value.GetType();
        if (!TryGetParameterValueKind(valueType, out var parameterValueKind))
        {
            throw new ArgumentException($"Parameter type {valueType} is not supported");
        }

        return GetAhtolaValue(Value, parameterValueKind);
    }

    internal SqlValue ToSqlValue()
    {
        if (Value is null)
            return SqlValue.Null;

        var valueType = Value.GetType();
        if (!TryGetParameterValueKind(valueType, out var parameterValueKind))
            throw new ArgumentException($"Parameter type {valueType} is not supported");

        return parameterValueKind switch
        {
            ParameterValueKind.Null => SqlValue.Null,
            ParameterValueKind.Integer => SqlValue.Integer(ToInt64Unchecked(Value)),
            ParameterValueKind.Real => SqlValue.Real(Convert.ToDouble(Value, CultureInfo.InvariantCulture)),
            ParameterValueKind.Text => SqlValue.Text(ToInvariantString(Value)),
            ParameterValueKind.Blob => SqlValue.Blob((byte[])Value),
            _ => throw new ArgumentOutOfRangeException(nameof(parameterValueKind), parameterValueKind, null)
        };
    }

    public override int Size
    {
        get => _size;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -1);
            _size = value;
        }
    }

    private static bool TryGetParameterValueKind(Type valueType, out ParameterValueKind parameterValueKind)
    {
        if (valueType.IsEnum)
        {
            parameterValueKind = ParameterValueKind.Integer;
            return true;
        }

        return ParameterTypeMapping.TryGetValue(valueType, out parameterValueKind);
    }

    private static long ToInt64Unchecked(object value)
    {
        if (value is ulong u64)
            return unchecked((long)u64);
        if (!value.GetType().IsEnum)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);

        return Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
        {
            TypeCode.SByte => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            TypeCode.Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
            TypeCode.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            TypeCode.UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            TypeCode.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            TypeCode.UInt32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            TypeCode.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            TypeCode.UInt64 => unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException($"Enum type {value.GetType()} has an unsupported underlying type.")
        };
    }

    private static AhtolaValue GetAhtolaValue(object value, ParameterValueKind parameterValueKind)
    {
        return parameterValueKind switch
        {
            ParameterValueKind.Null => new AhtolaValue { ValueType = AhtolaValueType.Null },
            ParameterValueKind.Integer => new AhtolaValue { ValueType = AhtolaValueType.Integer, IntValue = ToInt64Unchecked(value) },
            ParameterValueKind.Real => new AhtolaValue { ValueType = AhtolaValueType.Real, RealValue = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
            ParameterValueKind.Text => new AhtolaValue { ValueType = AhtolaValueType.Text, StringValue = ToInvariantString(value) },
            ParameterValueKind.Blob => new AhtolaValue { ValueType = AhtolaValueType.Blob, BlobValue = (byte[])value },
            _ => throw new ArgumentOutOfRangeException(nameof(parameterValueKind), parameterValueKind, null)
        };
    }

    private enum ParameterValueKind
    {
        Null,
        Integer,
        Real,
        Text,
        Blob,
    }

    private static string ToInvariantString(object value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!
        };
    }
}
