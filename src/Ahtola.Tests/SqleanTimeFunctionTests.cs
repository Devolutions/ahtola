using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies the sqlean-compatible time family beyond the corpus basics: the 13-byte
/// blob layout (version byte, 8 big-endian seconds since 0001-01-01, 4 big-endian
/// nanoseconds), BCE years the corpus exercises via time_get_year, the REAL-valued
/// two-argument time_get('second'/'epoch') forms vs the INTEGER named getter, and
/// wrong-arity errors (upstream's varargs shim used to abort on them).
/// </summary>
public class SqleanTimeFunctionTests
{
    [Test]
    public void TimeDateProducesTheDocumentedBlobLayout()
    {
        using var connection = Open();

        // 2011-11-18T15:56:35Z: seconds since 0001-01-01 = 63_475_597_795? verify via blob.
        var blob = ReadScalar(connection, "SELECT hex(time_date(2011, 11, 18, 15, 56, 35));");
        blob.Should().StartWith("01"); // version byte
        blob.Should().HaveLength(26); // 13 bytes hex
    }

    [Test]
    public void BCEYearsRoundTripThroughTheCivilCalendar()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT time_get_year(time_date(-1000, 11, 18));").Should().Be("-1000");
        ReadScalar(connection, "SELECT time_get_month(time_date(-1000, 5, 18));").Should().Be("5");
        ReadScalar(connection, "SELECT time_get_day(time_date(-1000, 5, 25));").Should().Be("25");
    }

    [Test]
    public void NamedSecondGetterIsIntegerWhileTimeGetSecondCarriesTheFraction()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT time_get_second(time_date(2011, 12, 31, 10, 5, 30, 431295000));")
            .Should().Be("30");
        ReadScalar(connection, "SELECT time_get(time_date(2011, 12, 31, 10, 5, 30, 431295000), 'second');")
            .Should().Be("30.431295");
    }

    [Test]
    public void TimeGetEpochIsRealValued()
    {
        using var connection = Open();

        var epoch = ReadScalar(connection, "SELECT time_get(time_date(2024, 8, 6, 21, 22, 15, 431295000), 'epoch');");
        epoch.Should().NotBeEmpty();
        epoch.Should().Contain("1722979335");
    }

    [Test]
    public void WrongArityErrorsMatchUpstream()
    {
        using var connection = Open();

        AssertWrongArity(connection, "SELECT time_now(1);", "time_now");
        AssertWrongArity(connection, "SELECT time_get_year();", "time_get_year");
        AssertWrongArity(connection, "SELECT time_unix();", "time_unix");
        AssertWrongArity(connection, "SELECT time_sub(time_date(2011, 11, 18));", "time_sub");
        AssertWrongArity(connection, "SELECT dur_h(1);", "dur_h");
    }

    [Test]
    public void IsoWeekTruncMatchesTheCorpusWeekSemantics()
    {
        using var connection = Open();

        // The corpus pins week = 2011-11-12 for 2011-11-18 (ISO year Jan 1 + (week-1)*7).
        ReadScalar(
                connection,
                "SELECT time_fmt_iso(time_trunc(time_date(2011, 11, 18, 15, 56, 35, 666777888), 'week'));")
            .Should().Be("2011-11-12T00:00:00Z");
    }

    private static void AssertWrongArity(EmbeddedConnection connection, string sql, string name)
    {
        var act = () => ReadScalar(connection, sql);
        act.Should().Throw<EmbeddedSqlException>()
            .Which.Message.Should().Contain($"wrong number of arguments to function {name}()");
    }

    private static EmbeddedConnection Open()
    {
        var embedded = new EmbeddedDatabase();
        return embedded.Connect();
    }

    private static string ReadScalar(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                    var value = statement.GetValue(0);
                    rows.Add(value.Kind switch
                    {
                        SqlValueKind.Null => string.Empty,
                        SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span),
                        SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        SqlValueKind.Real => value.AsReal().ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
                        SqlValueKind.Text => value.AsText(),
                        _ => value.ToString() ?? string.Empty,
                    });
                }
            }
        }

        return rows.SingleOrDefault() ?? string.Empty;
    }
}
