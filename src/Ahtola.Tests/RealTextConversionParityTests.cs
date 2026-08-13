using System.Globalization;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for the way SQLite converts a REAL to text.
/// </summary>
public sealed class RealTextConversionParityTests
{
    // SQLite never renders a floating point value as a bare integer and never uses the platform's
    // exponent syntax, so a whole-number real keeps a fractional digit and 1e20 is "1.0e+20".
    [TestCase("SELECT CAST(0.0 AS TEXT), CAST(1.5 AS TEXT), CAST(1e300 AS TEXT), CAST(-0.0 AS TEXT)")]
    [TestCase("SELECT 0.0 || '', 1.0 || '', 100.0 || '', 1e20 || '', 1e21 || ''")]
    [TestCase("SELECT CAST(1e15 AS TEXT), CAST(1e16 AS TEXT), CAST(1e17 AS TEXT)")]
    [TestCase("SELECT CAST(1e-4 AS TEXT), CAST(1e-5 AS TEXT), CAST(0.5 AS TEXT), CAST(1.1 AS TEXT)")]
    [TestCase("SELECT CAST(-1.5 AS TEXT), CAST(-1e300 AS TEXT), CAST(-1e-300 AS TEXT)")]
    [TestCase("SELECT CAST(0.1+0.2 AS TEXT), CAST(123.456 AS TEXT), CAST(2.5 AS TEXT)")]
    [TestCase("SELECT group_concat(x) FROM (SELECT 1.0 AS x UNION ALL SELECT 2.50 UNION ALL SELECT 3e30)")]
    [TestCase("WITH t(x) AS (VALUES(0.0),(0.0)) SELECT group_concat(x) FROM t")]
    [TestCase("WITH t(x) AS (VALUES(0.0),('zero')) SELECT group_concat(x) FROM t")]
    [TestCase("SELECT printf('%s', 1e20), printf('%s', 0.0), printf('%s', 1.0)")]
    public void RealToTextMatchesSqlite(string sql)
        => NumericAggregateParityTests.RunManaged(sql).Should()
            .Be(NumericAggregateParityTests.RunSqlite(sql), because: sql);

    // The fixed-notation window is decided by the decimal exponent of the leading significant
    // digit: 1e16 stays fixed and 1e17 does not, while 1e-4 stays fixed and 1e-5 does not.
    [TestCase("1e16", "10000000000000000.0")]
    [TestCase("1e17", "1.0e+17")]
    [TestCase("1e-4", "0.0001")]
    [TestCase("1e-5", "1.0e-05")]
    [TestCase("-0.0", "0.0")]
    [TestCase("0.0", "0.0")]
    [TestCase("1.0", "1.0")]
    [TestCase("100.0", "100.0")]
    [TestCase("1e300", "1.0e+300")]
    [TestCase("-1e-5", "-1.0e-05")]
    public void FixedNotationWindowIsPinned(string literal, string expected)
        => NumericAggregateParityTests
            .RunManaged($"SELECT CAST({literal} AS TEXT)")
            .Should().Be(expected);

    /// <summary>
    /// SQLite derives its digits from <c>sqlite3FpDecode</c>, which is deliberately cheap rather
    /// than correctly rounded, so it sometimes emits a redundant and even incorrect seventeenth
    /// digit. The managed engine emits the shortest text that reads back as the same double, so its
    /// output must always round-trip and must never be longer than SQLite's.
    /// </summary>
    [Test]
    public void EveryRealRoundTripsAndIsNeverMoreVerboseThanSqlite()
    {
        var random = new Random(20240607);
        var values = new List<double>
        {
            0.0, -0.0, 1.0, -1.0, 0.5, 100.0, 1e15, 1e16, 1e17, 1e20, 1e-4, 1e-5,
            1.0 / 3.0, 0.1, 0.1 + 0.2, 2.5, 123.456, 1e100, 1e308, 5e-324, 9.93e-322,
            1234567890123456.0, 12345678901234567.0, double.MaxValue, double.Epsilon,
        };
        for (var index = 0; index < 200; index++)
        {
            values.Add(Math.Round(
                random.NextDouble() * Math.Pow(10, random.Next(-8, 9)),
                random.Next(0, 8)));
        }

        for (var index = 0; index < 100; index++)
        {
            var candidate = BitConverter.Int64BitsToDouble(random.NextInt64());
            if (!double.IsNaN(candidate) && !double.IsInfinity(candidate))
                values.Add(candidate);
        }

        foreach (var value in values)
        {
            var sql = "SELECT CAST(CAST("
                + value.ToString("G17", CultureInfo.InvariantCulture)
                + " AS REAL) AS TEXT)";
            var managed = NumericAggregateParityTests.RunManaged(sql);
            var sqlite = NumericAggregateParityTests.RunSqlite(sql);

            double.Parse(managed, NumberStyles.Float, CultureInfo.InvariantCulture)
                .Should().Be(value, because: $"{sql} produced {managed}");
            SignificantDigits(managed).Should().BeLessThanOrEqualTo(
                SignificantDigits(sqlite),
                because: $"{sql} produced {managed} against SQLite's {sqlite}");
            Shape(managed).Should().Be(Shape(sqlite), because: sql);
        }
    }

    private static int SignificantDigits(string text)
    {
        var mantissa = text.Split('e')[0].Replace("-", string.Empty).Replace(".", string.Empty);
        var trimmed = mantissa.TrimStart('0').TrimEnd('0');
        return trimmed.Length == 0 ? 1 : trimmed.Length;
    }

    /// <summary>The sign, notation, and exponent, which must agree even when the digits do not.</summary>
    private static string Shape(string text)
    {
        var parts = text.Split('e');
        var negative = parts[0].StartsWith('-');
        return parts.Length == 1
            ? $"{negative}/fixed/{parts[0].IndexOf('.')}"
            : $"{negative}/scientific/{parts[1]}";
    }
}
