using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedCallbackRegistrationParityTests
{
    [Test]
    public void ManagedCallbacksPreserveSignatureSpecificRegistrationsAcrossReopen()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateFunction<long, string>("callback_arity", static value => $"one:{value}");
        connection.CreateFunction<long, long, string>("callback_arity", static (left, right) => $"two:{left + right}");
        connection.CreateFunction("callback_arity", static arguments => $"variadic:{arguments.Length}");
        connection.CreateAggregate<long, long>("callback_total", 0L, static (total, value) => total + value);
        connection.CreateAggregate<long>("callback_total", 0L, static (total, arguments) => total + (arguments.Length * 100));

        connection.Open();
        connection.Close();
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE CallbackValues(Value INTEGER); INSERT INTO CallbackValues VALUES (2), (3);");

        connection.ExecuteScalar<string>("SELECT callback_arity(3);").Should().Be("one:3");
        connection.ExecuteScalar<string>("SELECT callback_arity(3, 4);").Should().Be("two:7");
        connection.ExecuteScalar<string>("SELECT callback_arity(3, 4, 5);").Should().Be("variadic:3");
        connection.ExecuteScalar<long>("SELECT callback_total(Value) FROM CallbackValues;").Should().Be(5);
        connection.ExecuteScalar<long>("SELECT callback_total(Value, Value) FROM CallbackValues;").Should().Be(400);
    }

    [Test]
    public void SeededAggregateWithTupleAccumulatorSurvivesManagedStateRoundTrip()
    {
        // EF Core registers ef_avg with a (decimal, ulong) accumulator; the managed engine
        // round-trips callback state through SqlValue, so the tuple must encode and decode.
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateAggregate<decimal?, (decimal sum, ulong count), decimal?>(
            "ef_avg",
            (0m, 0ul),
            static (acc, value) => value is null ? acc : (acc.sum + value.Value, acc.count + 1),
            static acc => acc.count == 0 ? default(decimal?) : acc.sum / acc.count,
            isDeterministic: true);

        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE Prices(Value TEXT); INSERT INTO Prices VALUES ('9.80'), ('10.20'), (NULL);");

        connection.ExecuteScalar<decimal>("SELECT ef_avg(Value) FROM Prices;").Should().Be(10.00m);
    }

    [Test]
    public void NullableDecimalAggregateAccumulatorsSurviveManagedStateRoundTrip()
    {
        // EF Core registers ef_sum/ef_min/ef_max with a null seed and a decimal?
        // accumulator; the managed engine round-trips that state through SqlValue.Text,
        // so the step invoker must coerce the text back to decimal? instead of failing
        // an InvalidCastException on the second row.
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateAggregate(
            "ef_sum",
            seed: null,
            (decimal? sum, decimal? value) => value is null
                ? sum
                : sum is null
                    ? value
                    : sum.Value + value.Value,
            isDeterministic: true);
        connection.CreateAggregate(
            "ef_min",
            seed: null,
            (decimal? min, decimal? value) => min is null
                ? value
                : value is null
                    ? min
                    : decimal.Min(min.Value, value.Value),
            isDeterministic: true);
        connection.CreateAggregate(
            "ef_max",
            seed: null,
            (decimal? max, decimal? value) => max is null
                ? value
                : value is null
                    ? max
                    : decimal.Max(max.Value, value.Value),
            isDeterministic: true);

        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE Prices(Value TEXT); INSERT INTO Prices VALUES ('9.80'), ('10.20'), (NULL);");

        connection.ExecuteScalar<decimal>("SELECT ef_sum(Value) FROM Prices;").Should().Be(20.00m);
        connection.ExecuteScalar<decimal>("SELECT ef_min(Value) FROM Prices;").Should().Be(9.80m);
        connection.ExecuteScalar<decimal>("SELECT ef_max(Value) FROM Prices;").Should().Be(10.20m);
    }

    [Test]
    public void RealArgumentsToDecimalAggregateKeepSqliteTextPrecision()
    {
        // Native SQLite feeds REAL values to callbacks as %.15g text, so decimal steps see
        // 9.8 exactly. Convert.ChangeType(double, decimal) on .NET 11+ instead expands the
        // double's exact binary value, which leaks noise into decimal accumulators.
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.CreateAggregate(
            "ef_sum",
            seed: null,
            (decimal? sum, decimal? value) => value is null
                ? sum
                : sum is null
                    ? value
                    : sum.Value + value.Value,
            isDeterministic: true);

        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE Prices(Value REAL); INSERT INTO Prices VALUES (9.8), (9.8);");

        connection.ExecuteScalar<decimal>("SELECT ef_sum(Value) FROM Prices;").Should().Be(19.6m);
    }
}
