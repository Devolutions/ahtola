using System.Globalization;
using System.Text;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests.Sqltest;

internal sealed record SqltestOracleOutcome(IReadOnlyList<string> Rows, string? Error);

/// <summary>
/// In-process SQLite oracle for matrix expansions, mirroring
/// <c>testing/sqltest/src/matrix_oracle.rs</c>: each expansion runs its setups and
/// statement against bundled SQLite through Microsoft.Data.Sqlite, and the managed
/// engine must agree — same rows in the same order, or both rejecting the statement
/// (error messages are not compared).
/// </summary>
internal static class SqltestMatrixOracle
{
    public static SqltestOracleOutcome Run(IReadOnlyList<string> setups, string sql)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        try
        {
            foreach (var setup in setups)
                Execute(connection, setup);

            var rows = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    var values = new string[reader.FieldCount];
                    for (var column = 0; column < values.Length; column++)
                        values[column] = FormatValue(reader, column);
                    rows.Add(string.Join('|', values));
                }
            } while (reader.NextResult());

            return new SqltestOracleOutcome(rows, null);
        }
        catch (MsData.SqliteException exception)
        {
            return new SqltestOracleOutcome([], exception.Message);
        }
        catch (Exception exception)
        {
            return new SqltestOracleOutcome([], exception.Message);
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Mirrors <c>matrix_oracle.rs::format_value</c>, which itself mirrors
    /// <c>backends/rust.rs::value_to_string</c>, so comparing the resulting strings
    /// compares typed values and not incidental text formatting. Text and blob bytes are
    /// read as UTF-8 like the Rust <c>String::from_utf8_lossy</c>.
    /// </summary>
    private static string FormatValue(MsData.SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return string.Empty;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            double real => FormatReal(real),
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string FormatReal(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsInfinity(value))
            return double.IsPositive(value) ? "Inf" : "-Inf";

        var magnitude = Math.Abs(value);
        if (magnitude != 0.0 && (magnitude < 1e-4 || magnitude >= 1e15))
            return FormatExponential(value);

        if (value % 1 == 0)
            return $"{(long)value}.0";

        return FormatSignificantDigits(value, 15);
    }

    private static string FormatExponential(double value)
    {
        var formatted = value.ToString("E14", CultureInfo.InvariantCulture);
        var exponentIndex = formatted.IndexOf('E');
        var mantissa = formatted[..exponentIndex].TrimEnd('0');
        if (mantissa.EndsWith('.'))
            mantissa += "0";

        var exponent = int.Parse(formatted[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        return $"{mantissa}e{(exponent >= 0 ? "+" : string.Empty)}{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatSignificantDigits(double value, int significantDigits)
    {
        if (value == 0.0)
            return "0.0";

        var magnitude = Math.Abs(value);
        var digitsBeforeDecimal = magnitude >= 1.0 ? (int)Math.Floor(Math.Log10(magnitude)) + 1 : 0;
        var decimalPlaces = Math.Max(0, significantDigits - digitsBeforeDecimal);
        var formatted = value.ToString("F" + decimalPlaces.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (!formatted.Contains('.'))
            return formatted;

        var trimmed = formatted.TrimEnd('0');
        return trimmed.EndsWith('.') ? trimmed + "0" : trimmed;
    }
}
