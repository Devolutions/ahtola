using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public partial class SqliteConnection
{
    private readonly Dictionary<FunctionSignature, ScalarFunctionRegistration> _scalarFunctions = new(FunctionSignatureComparer.Instance);

    private void RegisterScalarFunction(string name, int argc, bool isDeterministic, Func<object?[], object?>? function)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Capabilities.SupportsUserDefinedFunctions)
            throw new NotSupportedException("User-defined functions are supported only for local database connections.");
            // Shared-memory catalogs are process-wide for the named database. Scalar
            // registrations are therefore catalog-scoped (visible to every lease), which is
            // what EF Core needs when multiple connections share Mode=Memory;Cache=Shared.
            if (function is null)
        {
            RemoveFunctionRegistrations(_scalarFunctions, name);
            if (IsManagedConnection)
                ManagedConnection.UnregisterScalarFunctions(name);
            else if (_database is not null)
                SqliteNativeProvider.Current.UnregisterFunctions(NativeDatabase, name);
            return;
        }

        var registration = new ScalarFunctionRegistration(name, argc, isDeterministic, function);
        _scalarFunctions[new FunctionSignature(name, argc)] = registration;
        if (IsManagedConnection)
        {
            ManagedConnection.UnregisterScalarFunctions(name);
            foreach (var registeredFunction in _scalarFunctions.Where(
                         pair => string.Equals(pair.Key.Name, name, StringComparison.OrdinalIgnoreCase))
                     .Select(static pair => pair.Value))
            {
                registeredFunction.RegisterManaged(ManagedConnection);
            }
        }
        else if (_database is not null)
            registration.RegisterNative(NativeDatabase);
    }

    private void RegisterScalarFunctions()
    {
        if (IsManagedConnection)
        {
            foreach (var registration in _scalarFunctions.Values)
                registration.RegisterManaged(ManagedConnection);
            return;
        }

        foreach (var registration in _scalarFunctions
                     .GroupBy(static pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(static group => group.Last().Value))
        {
            registration.RegisterNative(NativeDatabase);
        }
    }

    private static void RemoveFunctionRegistrations<TRegistration>(
        Dictionary<FunctionSignature, TRegistration> registrations,
        string name)
    {
        var matchingSignatures = registrations.Keys
            .Where(signature => string.Equals(signature.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var signature in matchingSignatures)
            registrations.Remove(signature);
    }

    private static SqlValue ToManagedSqlValue(object? value)
    {
        if (value is null or DBNull)
            return SqlValue.Null;

        return value switch
        {
            bool boolValue => SqlValue.Integer(boolValue ? 1 : 0),
            byte byteValue => SqlValue.Integer(byteValue),
            sbyte sbyteValue => SqlValue.Integer(sbyteValue),
            short shortValue => SqlValue.Integer(shortValue),
            ushort ushortValue => SqlValue.Integer(ushortValue),
            int intValue => SqlValue.Integer(intValue),
            uint uintValue => SqlValue.Integer(uintValue),
            long longValue => SqlValue.Integer(longValue),
            float floatValue => SqlValue.Real(floatValue),
            double doubleValue => SqlValue.Real(doubleValue),
            decimal decimalValue => SqlValue.Text(decimalValue.ToString(CultureInfo.InvariantCulture)),
            byte[] bytes => SqlValue.Blob(bytes),
            ITuple tuple => SqlValue.Text(EncodeTuple(tuple)),
            _ => SqlValue.Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
    }

    // Aggregate accumulators round-trip through SqlValue in the managed engine, unlike the
    // native provider which keeps callback state in memory. Tuples are encoded as tagged text
    // so seeded aggregates such as EF Core's ef_avg ((decimal sum, ulong count)) survive.
    private const string TupleEncodingPrefix = "\u001FT";
    private const char TupleFieldSeparator = '\u001F';

    private static string EncodeTuple(ITuple tuple)
    {
        var builder = new StringBuilder(TupleEncodingPrefix);
        builder.Append(tuple.Length.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < tuple.Length; index++)
        {
            builder.Append(TupleFieldSeparator);
            builder.Append(tuple[index] switch
            {
                null => string.Empty,
                double doubleValue => doubleValue.ToString("R", CultureInfo.InvariantCulture),
                float floatValue => floatValue.ToString("R", CultureInfo.InvariantCulture),
                IConvertible convertible => convertible.ToString(CultureInfo.InvariantCulture),
                var other => other.ToString() ?? string.Empty,
            });
        }

        return builder.ToString();
    }

    private static T CoerceAccumulator<T>(object? value)
    {
        if (value is T typed)
            return typed;
        if (value is string text)
        {
            if (typeof(ITuple).IsAssignableFrom(typeof(T)) && text.StartsWith(TupleEncodingPrefix, StringComparison.Ordinal))
                return (T)DecodeTuple(text, typeof(T));
            // Non-tuple accumulators (EF Core's ef_sum/ef_min/ef_max use decimal?) also
            // round-trip through SqlValue.Text, so coerce the text back to the accumulator
            // type instead of letting the blind cast below fail.
            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (targetType != typeof(string) && typeof(IConvertible).IsAssignableFrom(targetType))
                return (T)Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
        }

        return (T)value!;
    }

    private static object DecodeTuple(string text, Type tupleType)
    {
        var fields = text.Split(TupleFieldSeparator);
        var elementTypes = tupleType.IsGenericType ? tupleType.GetGenericArguments() : Type.EmptyTypes;
        // Layout after splitting "\u001FT{arity}\u001Ff0\u001Ff1...": "", "T{arity}", f0, f1, ...
        if (fields.Length != elementTypes.Length + 2
            || fields[0].Length != 0
            || fields[1].Length < 2
            || fields[1][0] != 'T'
            || !int.TryParse(fields[1].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var arity)
            || arity != elementTypes.Length)
        {
            throw new InvalidCastException($"Cannot decode aggregate accumulator '{text}' as {tupleType}.");
        }

        var elements = new object?[elementTypes.Length];
        for (var index = 0; index < elementTypes.Length; index++)
        {
            var field = fields[index + 2];
            var elementType = Nullable.GetUnderlyingType(elementTypes[index]) ?? elementTypes[index];
            elements[index] = field.Length == 0
                ? elementType == typeof(string) ? string.Empty : Nullable.GetUnderlyingType(elementTypes[index]) is not null ? null : Convert.ChangeType(field, elementType, CultureInfo.InvariantCulture)
                : Convert.ChangeType(field, elementType, CultureInfo.InvariantCulture);
        }

        return Activator.CreateInstance(tupleType, elements)!;
    }

    private static object?[] ToManagedObjects(IReadOnlyList<SqlValue> values)
    {
        var result = new object?[values.Count];
        for (var index = 0; index < values.Count; index++)
            result[index] = ToManagedObject(values[index]);

        return result;
    }

    private static object? ToManagedObject(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => value.AsBlob().ToArray(),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}.")
        };
    }

    private static object? InvokeTypedFunction<T1, TResult>(string name, Func<T1, TResult> function, object?[] args)
        => function(ConvertArgument<T1>(name, args[0], 0));

    private static object? InvokeTypedFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult> function, object?[] args)
        => function(ConvertArgument<T1>(name, args[0], 0), ConvertArgument<T2>(name, args[1], 1));

    private static object? InvokeTypedFunction<TState, TResult>(TState state, Func<TState, TResult> function, object?[] args)
        => function(state);

    private static object? InvokeTypedFunction<TState, T1, TResult>(string name, TState state, Func<TState, T1, TResult> function, object?[] args)
        => function(state, ConvertArgument<T1>(name, args[0], 0));

    private static object? InvokeTypedFunction<TState, T1, T2, TResult>(string name, TState state, Func<TState, T1, T2, TResult> function, object?[] args)
        => function(state, ConvertArgument<T1>(name, args[0], 0), ConvertArgument<T2>(name, args[1], 1));

    private static T ConvertArgument<T>(string functionName, object? value, int ordinal)
    {
        if (value is null or DBNull)
        {
            if (!typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null)
                return default!;

            throw new SqliteException(Properties.Resources.UDFCalledWithNull(functionName, ordinal), 1);
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType == typeof(object))
            return (T)value;
        if (targetType == typeof(byte[]) && value is byte[] bytes)
            return (T)(object)bytes;
        if (targetType == typeof(string))
            return (T)(object)Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (targetType == typeof(bool))
            return (T)(object)Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        // REAL->decimal must go through %.15g text like native SQLite (sqlite3_value_text),
        // not Convert.ChangeType, which on .NET 11+ expands the double's exact binary value.
        if (targetType == typeof(decimal) && value is double realValue)
            return (T)(object)decimal.Parse(realValue.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private sealed class ScalarFunctionRegistration(string name, int argc, bool isDeterministic, Func<object?[], object?> invoke)
    {
        public object? Invoke(object?[] args) => invoke(args);

        public void RegisterManaged(IManagedConnectionAdapter connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            connection.RegisterScalarFunction(name, argc, InvokeManaged);
        }

        public void RegisterNative(AhtolaNativeDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);
            SqliteNativeProvider.Current.RegisterScalarFunction(database, name, argc, isDeterministic, Invoke);
        }

        private SqlValue InvokeManaged(IReadOnlyList<SqlValue> arguments)
        {
            try
            {
                return ToManagedSqlValue(Invoke(ToManagedObjects(arguments)));
            }
            catch (Exception ex)
            {
                throw ToManagedCallbackException(ex);
            }
        }
    }

    private static EmbeddedSqlException ToManagedCallbackException(Exception exception)
        => exception is SqliteException sqliteException
            ? new EmbeddedSqlException($"__ahtola_sqlite_error__:{sqliteException.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}:{sqliteException.Message}")
            : new EmbeddedSqlException(exception.Message);

    private readonly record struct FunctionSignature(string Name, int Arity);

    private sealed class FunctionSignatureComparer : IEqualityComparer<FunctionSignature>
    {
        public static readonly FunctionSignatureComparer Instance = new();

        public bool Equals(FunctionSignature left, FunctionSignature right)
            => left.Arity == right.Arity
                && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(FunctionSignature signature)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(signature.Name), signature.Arity);
    }

}
