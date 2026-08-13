using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Sqlite.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using AhtolaSqliteConnectionStringBuilder = Ahtola.Data.Sqlite.SqliteConnectionStringBuilder;
using AhtolaSqliteOpenMode = Ahtola.Data.Sqlite.SqliteOpenMode;

namespace Ahtola.EntityFrameworkCore.Sqlite.Storage.Internal;

public class AhtolaSqliteRelationalConnection : SqliteRelationalConnection
{
    private readonly IRawSqlCommandBuilder _rawSqlCommandBuilder;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Infrastructure> _logger;
    private readonly int? _commandTimeout;

    public AhtolaSqliteRelationalConnection(
        RelationalConnectionDependencies dependencies,
        IRawSqlCommandBuilder rawSqlCommandBuilder,
        IDiagnosticsLogger<DbLoggerCategory.Infrastructure> logger)
        : base(dependencies, rawSqlCommandBuilder, logger)
    {
        _rawSqlCommandBuilder = rawSqlCommandBuilder;
        _logger = logger;

        var relationalOptions = RelationalOptionsExtension.Extract(dependencies.ContextOptions);
        _commandTimeout = relationalOptions.CommandTimeout;
        if (relationalOptions.Connection is AhtolaSqliteConnection connection)
            InitializeAhtolaConnection(connection);
    }

    protected override DbConnection CreateDbConnection()
    {
        var connection = new AhtolaSqliteConnection(GetValidatedConnectionString());
        InitializeAhtolaConnection(connection);
        return connection;
    }

    public override ISqliteRelationalConnection CreateReadOnlyConnection()
    {
        var connectionStringBuilder = new AhtolaSqliteConnectionStringBuilder(GetValidatedConnectionString())
        {
            Mode = AhtolaSqliteOpenMode.ReadOnly,
            Pooling = false
        };

        var contextOptions = new DbContextOptionsBuilder()
            .UseAhtola(connectionStringBuilder.ToString())
            .Options;

        return new AhtolaSqliteRelationalConnection(
            Dependencies with { ContextOptions = contextOptions },
            _rawSqlCommandBuilder,
            _logger);
    }

    private void InitializeAhtolaConnection(AhtolaSqliteConnection connection)
    {
        if (_commandTimeout.HasValue)
            connection.DefaultTimeout = _commandTimeout.Value;

        connection.CreateFunction<string, string, bool?>(
            "regexp",
            (pattern, input) => input is null || pattern is null
                ? null
                : Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(1000)),
            isDeterministic: true);

        connection.CreateFunction<string, string, long?>(
            "instr",
            (input, value) => input is null || value is null
                ? null
                : input.IndexOf(value, StringComparison.Ordinal) + 1,
            isDeterministic: true);

        connection.CreateFunction(
            "ef_mod",
            (decimal? dividend, decimal? divisor) => divisor == 0m ? null : dividend % divisor,
            isDeterministic: true);

        connection.CreateFunction(
            "ef_add",
            (decimal? left, decimal? right) => left + right,
            isDeterministic: true);

        connection.CreateFunction(
            "ef_divide",
            (decimal? dividend, decimal? divisor) => divisor == 0m ? null : dividend / divisor,
            isDeterministic: true);

        connection.CreateFunction(
            "ef_compare",
            (decimal? left, decimal? right) => left.HasValue && right.HasValue
                ? decimal.Compare(left.Value, right.Value)
                : default(int?),
            isDeterministic: true);

        connection.CreateFunction(
            "ef_multiply",
            (decimal? left, decimal? right) => left * right,
            isDeterministic: true);

        connection.CreateFunction(
            "ef_negate",
            (decimal? value) => -value,
            isDeterministic: true);

        RegisterDecimalAggregates(connection);

        connection.CreateCollation(
            "EF_DECIMAL",
            (left, right) => decimal.Compare(
                decimal.Parse(left, NumberStyles.Number, CultureInfo.InvariantCulture),
                decimal.Parse(right, NumberStyles.Number, CultureInfo.InvariantCulture)));
    }

    private static decimal? ToDecimal(object? value)
        => value switch
        {
            null => null,
            decimal decimalValue => decimalValue,
            string text => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
            // Format doubles through "G15": .NET 11 changed Convert.ToDecimal(double) to keep
            // the exact binary expansion, while SQLite REAL-to-decimal semantics round to 15
            // significant digits.
            double doubleValue => decimal.Parse(doubleValue.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };

    private static void RegisterDecimalAggregates(AhtolaSqliteConnection connection)
    {
        var connectionOptions = new AhtolaSqliteConnectionStringBuilder(connection.ConnectionString);
        if (connectionOptions.IsLocalProviderConfigured
            && connectionOptions.LocalProvider != Ahtola.AhtolaLocalProvider.Managed)
        {
            RegisterNativeDecimalAggregates(connection);
            return;
        }

        connection.CreateAggregate(
            "ef_avg",
            seed: "0|0",
            AddToAverage,
            GetAverage,
            isDeterministic: true);

        connection.CreateAggregate<string?>(
            "ef_max",
            seed: null,
            GetMaximum,
            isDeterministic: true);

        connection.CreateAggregate<string?>(
            "ef_min",
            seed: null,
            GetMinimum,
            isDeterministic: true);

        connection.CreateAggregate(
            "ef_sum",
            seed: "0",
            AddToSum,
            isDeterministic: true);
    }

    private static void RegisterNativeDecimalAggregates(AhtolaSqliteConnection connection)
    {
        connection.CreateAggregate(
            "ef_avg",
            seed: (0m, 0ul),
            ((decimal Sum, ulong Count) accumulator, decimal? value) => value is null
                ? accumulator
                : (accumulator.Sum + value.Value, accumulator.Count + 1),
            ((decimal Sum, ulong Count) accumulator) => accumulator.Count == 0
                ? default(decimal?)
                : accumulator.Sum / accumulator.Count,
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
            "ef_sum",
            seed: null,
            (decimal? sum, decimal? value) => value is null
                ? sum
                : sum is null
                    ? value
                    : sum.Value + value.Value,
            isDeterministic: true);
    }

    private static string AddToSum(string accumulator, object?[] values)
        => ToDecimal(values[0]) is not { } value
            ? accumulator
            : FormatDecimal(ParseDecimal(accumulator) + value);

    private static string AddToAverage(string accumulator, object?[] values)
    {
        if (ToDecimal(values[0]) is not { } value)
            return accumulator;

        var separator = accumulator.IndexOf('|');
        var sum = ParseDecimal(accumulator[..separator]) + value;
        var count = ulong.Parse(accumulator[(separator + 1)..], CultureInfo.InvariantCulture) + 1;
        return $"{FormatDecimal(sum)}|{count.ToString(CultureInfo.InvariantCulture)}";
    }

    private static decimal? GetAverage(string accumulator)
    {
        var separator = accumulator.IndexOf('|');
        var count = ulong.Parse(accumulator[(separator + 1)..], CultureInfo.InvariantCulture);
        return count == 0
            ? null
            : ParseDecimal(accumulator[..separator]) / count;
    }

    private static string? GetMaximum(string? accumulator, object?[] values)
        => SelectBound(accumulator, values[0], decimal.Max);

    private static string? GetMinimum(string? accumulator, object?[] values)
        => SelectBound(accumulator, values[0], decimal.Min);

    private static string? SelectBound(string? accumulator, object? value, Func<decimal, decimal, decimal> selector)
    {
        if (ToDecimal(value) is not { } decimalValue)
            return accumulator;

        return accumulator is null
            ? FormatDecimal(decimalValue)
            : FormatDecimal(selector(ParseDecimal(accumulator), decimalValue));
    }

    private static decimal ParseDecimal(string value)
        => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string FormatDecimal(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);
}
