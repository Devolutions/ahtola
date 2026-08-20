using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public partial class SqliteConnection
{
    private readonly Dictionary<FunctionSignature, AggregateFunctionRegistration> _aggregateFunctions = new(FunctionSignatureComparer.Instance);

    private void RegisterAggregateFunction(string name, int argc, bool isDeterministic, object? seed, Func<object?, object?[], object?>? step, Func<object?, object?> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Capabilities.SupportsUserDefinedAggregates)
            throw new NotSupportedException("User-defined aggregates are supported only for local database connections.");
            // Shared-memory catalogs are process-wide for the named database. Aggregate
            // registrations are therefore catalog-scoped (visible to every lease), which is
            // what EF Core needs when multiple connections share Mode=Memory;Cache=Shared.
            if (step is null)
        {
            RemoveFunctionRegistrations(_aggregateFunctions, name);
            if (IsManagedConnection)
                ManagedConnection.UnregisterAggregateFunctions(name);
            else if (_database is not null)
                SqliteNativeProvider.Current.UnregisterFunctions(NativeDatabase, name);
            return;
        }

        var registration = new AggregateFunctionRegistration(name, argc, isDeterministic, seed, step, resultSelector);
        _aggregateFunctions[new FunctionSignature(name, argc)] = registration;
        if (IsManagedConnection)
        {
            ManagedConnection.UnregisterAggregateFunctions(name);
            foreach (var registeredFunction in _aggregateFunctions.Where(
                         pair => string.Equals(pair.Key.Name, name, StringComparison.OrdinalIgnoreCase))
                     .Select(static pair => pair.Value))
            {
                registeredFunction.RegisterManaged(ManagedConnection);
            }
        }
        else if (_database is not null)
            registration.RegisterNative(NativeDatabase);
    }

    private void RegisterAggregateFunctions()
    {
        if (IsManagedConnection)
        {
            foreach (var registration in _aggregateFunctions.Values)
                registration.RegisterManaged(ManagedConnection);
            return;
        }

        foreach (var registration in _aggregateFunctions
                     .GroupBy(static pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(static group => group.Last().Value))
        {
            registration.RegisterNative(NativeDatabase);
        }
    }

    private static object? InvokeNullableAggregateStep<TAccumulate>(Func<TAccumulate?, TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate?>(accumulator));

    private static object? InvokeNullableAggregateStep<T1, TAccumulate>(string name, Func<TAccumulate?, T1, TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate?>(accumulator), ConvertArgument<T1>(name, args[0], 0));

    private static object? InvokeNullableAggregateStep<TAccumulate>(Func<TAccumulate?, object?[], TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate?>(accumulator), args);

    private static object? InvokeSeededAggregateStep<TAccumulate>(Func<TAccumulate, TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate>(accumulator));

    private static object? InvokeSeededAggregateStep<T1, TAccumulate>(string name, Func<TAccumulate, T1, TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate>(accumulator), ConvertArgument<T1>(name, args[0], 0));

    private static object? InvokeSeededAggregateStep<T1, T2, TAccumulate>(string name, Func<TAccumulate, T1, T2, TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate>(accumulator), ConvertArgument<T1>(name, args[0], 0), ConvertArgument<T2>(name, args[1], 1));

    private static object? InvokeSeededAggregateStep<TAccumulate>(Func<TAccumulate, object?[], TAccumulate> function, object? accumulator, object?[] args)
        => function(CoerceAccumulator<TAccumulate>(accumulator), args);

    private static object? InvokeResultSelector<TAccumulate, TResult>(Func<TAccumulate, TResult> resultSelector, object? accumulator)
        => resultSelector(CoerceAccumulator<TAccumulate>(accumulator));

    private sealed class AggregateFunctionRegistration(
        string name,
        int argc,
        bool isDeterministic,
        object? seed,
        Func<object?, object?[], object?> step,
        Func<object?, object?> resultSelector)
    {
        public void RegisterManaged(IManagedConnectionAdapter connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            connection.RegisterAggregateFunction(
                name,
                argc,
                ToManagedSqlValue(seed),
                InvokeManagedStep,
                InvokeManagedFinal);
        }

        public void RegisterNative(AhtolaNativeDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);
            SqliteNativeProvider.Current.RegisterAggregateFunction(
                database,
                name,
                argc,
                isDeterministic,
                seed,
                step,
                resultSelector);
        }

        private SqlValue InvokeManagedStep(SqlValue accumulator, IReadOnlyList<SqlValue> arguments)
        {
            try
            {
                return ToManagedSqlValue(step(ToManagedObject(accumulator), ToManagedObjects(arguments)));
            }
            catch (Exception ex)
            {
                throw ToManagedCallbackException(ex);
            }
        }

        private SqlValue InvokeManagedFinal(SqlValue accumulator)
        {
            try
            {
                return ToManagedSqlValue(resultSelector(ToManagedObject(accumulator)));
            }
            catch (Exception ex)
            {
                throw ToManagedCallbackException(ex);
            }
        }
    }

}
