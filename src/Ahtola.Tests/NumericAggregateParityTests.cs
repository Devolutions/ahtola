using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for SQLite's numeric aggregate accumulation, its absence of NaN, and the
/// integer semantics of <c>char()</c>.
/// </summary>
public sealed class NumericAggregateParityTests
{
    // sum()/total()/avg() accumulate integers exactly in a 64-bit accumulator and only switch to
    // compensated floating point once a non-integer input arrives, so a large-magnitude pair that
    // cancels keeps every bit of precision that naive double accumulation would lose.
    [TestCase("SELECT avg(x) FROM (SELECT 9007199254740994 AS x UNION ALL SELECT -9007199254740993)")]
    [TestCase("SELECT avg(x) FROM (SELECT 9007199254740994 AS x UNION ALL SELECT NULL UNION ALL SELECT -9007199254740993)")]
    [TestCase("SELECT avg(x) FROM (SELECT 9223372036854775807 AS x UNION ALL SELECT -9223372036854775806)")]
    [TestCase("SELECT avg(x) FROM (SELECT -9223372036854775808 AS x UNION ALL SELECT 9223372036854775807)")]
    [TestCase("SELECT avg(x) FROM (SELECT 9007199254740994 AS x UNION ALL SELECT -9007199254740993.0)")]
    [TestCase("SELECT typeof(sum(x)), sum(x) FROM (SELECT 9007199254740994 AS x UNION ALL SELECT -9007199254740993)")]
    [TestCase("WITH t(x) AS (VALUES(1),(2),(3)) SELECT typeof(avg(x)), avg(x) FROM t")]
    [TestCase("WITH t(x) AS (VALUES(3),(4)) SELECT typeof(avg(x)), avg(x) FROM t")]
    [TestCase("WITH t(x) AS (VALUES(1),(2)) SELECT typeof(total(x)), total(x) FROM t")]
    public void ExactIntegerAccumulationMatchesSqlite(string sql) => AssertMatchesSqlite(sql);

    // Real accumulation is Kahan-Babuska-Neumaier compensated, so the classic 0.1+0.2+0.3 case sums
    // to exactly 0.6 rather than to the naive 0.6000000000000001.
    [TestCase("WITH t(x) AS (VALUES(0.1),(0.2),(0.3)) SELECT sum(x)=0.6 FROM t")]
    [TestCase("WITH t(x) AS (VALUES(0.1),(0.2),(0.3)) SELECT total(x)=0.6 FROM t")]
    [TestCase("WITH t(x) AS (VALUES(1e308),(1e308),(-1e308)) SELECT typeof(sum(x)), sum(x) FROM t")]
    public void CompensatedRealAccumulationMatchesSqlite(string sql) => AssertMatchesSqlite(sql);

    // SQLite has no NaN: sqlite3VdbeMemSetDouble stores NULL instead, and serialGet reads a stored
    // NaN back as NULL, so cancelling infinities and 0.0/0.0 both produce NULL rather than a real.
    [TestCase("SELECT typeof(1e999 - 1e999)")]
    [TestCase("SELECT typeof(0.0/0.0)")]
    [TestCase("SELECT 1e999 - 1e999")]
    [TestCase("SELECT typeof(1e999), 1e999")]
    [TestCase("SELECT typeof(sum(x)), sum(x) FROM (SELECT '1e999' AS x UNION ALL SELECT '-1e999')")]
    [TestCase("SELECT typeof(total(x)), total(x) FROM (SELECT '1e999' AS x UNION ALL SELECT '-1e999')")]
    [TestCase("SELECT typeof(avg(x)), avg(x) FROM (SELECT '1e999' AS x UNION ALL SELECT '-1e999')")]
    [TestCase("WITH t(x) AS (VALUES(1.0),(-9e+999),(2.0),(+9e+999),(3.0)) SELECT typeof(sum(x)), sum(x) FROM t")]
    [TestCase("SELECT typeof(sum(x)), sum(x) FROM (SELECT 1e999 AS x UNION ALL SELECT -1e999 UNION ALL SELECT 5)")]
    public void NotANumberBecomesNullLikeSqlite(string sql) => AssertMatchesSqlite(sql);

    // Empty inputs, and inputs that are not well-formed numbers, still contribute a count.
    [TestCase("SELECT typeof(sum(x)), sum(x) FROM (SELECT 1 AS x WHERE 0)")]
    [TestCase("SELECT typeof(total(x)), total(x) FROM (SELECT 1 AS x WHERE 0)")]
    [TestCase("SELECT typeof(avg(x)), avg(x) FROM (SELECT 1 AS x WHERE 0)")]
    [TestCase("WITH t(x) AS (VALUES('abc'),(1)) SELECT typeof(sum(x)), sum(x) FROM t")]
    public void EmptyAndNonNumericInputsMatchSqlite(string sql) => AssertMatchesSqlite(sql);

    // total() and avg() promote to a compensated real accumulator after integer overflow.
    [TestCase("WITH t(x) AS (VALUES(9223372036854775807),(1)) SELECT typeof(total(x)), total(x) FROM t")]
    [TestCase("WITH t(x) AS (VALUES(9223372036854775807),(1)) SELECT typeof(avg(x)), avg(x) FROM t")]
    public void TotalAndAveragePromoteAfterIntegerOverflowLikeSqlite(string sql) => AssertMatchesSqlite(sql);

    // Pinned turso-src/core/vdbe/execute.rs's Sum/Total AggStep (the Value::Numeric(Numeric::Float)
    // match arm) explicitly clears any earlier integer-overflow flag once the accumulator has
    // already switched to Float and a further float input arrives: "A float input clears any
    // earlier integer-overflow flag: the result is a float approximation regardless, so there's
    // nothing to report (func.c:1852)." So MAX+1 (which overflows and switches the accumulator to
    // Float with ovrfl=true) followed by a REAL forgives the overflow exactly like SQLite's own
    // deferred-flag semantics, matching bundled SQLite byte-for-byte here. This test previously
    // asserted the opposite (an immediate, unforgivable error) since the initial commit, predating
    // the pin's sumStep/sumInverse tracing that closed window/frame_sum_overflow.sqltest's
    // sum-overflow-forgiven-by-later-float case with the identical shape (int, int overflow, real).
    [Test]
    public void SumForgivesIntegerOverflowWhenALaterRealArrivesLikeTurso()
    {
        const string sql =
            "WITH t(x) AS (VALUES(9223372036854775807),(1),(-1.0)) "
            + "SELECT typeof(sum(x)), sum(x) FROM t";

        RunManaged(sql).Should().StartWith("real|");
        AssertMatchesSqlite(sql);
    }

    // Pinned turso-src/core/vdbe/execute.rs's handle_text_sum runs the *same* checked_add overflow
    // path as the Numeric::Integer arm for a text value that parses as a full (non-partial)
    // integer: on overflow it sets sum_state.ovrfl = true exactly like the numeric path, with no
    // special forgiveness for the text conversion route. With no later float to clear the flag,
    // Finalize's `if approx && ovrfl` check reports the same "integer overflow" error as the
    // all-integer case, matching bundled SQLite byte-for-byte here. This test previously asserted
    // an unconditional real-typed forgiveness since the initial commit; that predates tracing
    // handle_text_sum against the current pin.
    [Test]
    public void TextIntegerOverflowReportsTheSameErrorAsNumericOverflowLikeTurso()
    {
        const string sql =
            "WITH t(x) AS (VALUES('9223372036854775807'),('1')) "
            + "SELECT typeof(sum(x)), sum(x) FROM t";

        RunManaged(sql).Should().StartWith("ERR:").And.Contain("integer overflow");
        RunSqlite(sql).Should().StartWith("ERR:").And.Contain("integer overflow");
    }

    // The flag is sticky across later integer inputs, so the third case still fails even though the
    // mathematical total fits in 64 bits.
    [TestCase("WITH t(x) AS (VALUES(9223372036854775807),(1)) SELECT typeof(sum(x)), sum(x) FROM t")]
    [TestCase("WITH t(x) AS (VALUES(-9223372036854775808),(-1)) SELECT typeof(sum(x)), sum(x) FROM t")]
    [TestCase("WITH t(x) AS (VALUES(9223372036854775807),(1),(-1)) SELECT typeof(sum(x)), sum(x) FROM t")]
    public void SumReportsIntegerOverflowLikeSqlite(string sql)
    {
        RunManaged(sql).Should().StartWith("ERR:").And.Contain("integer overflow");
        RunSqlite(sql).Should().StartWith("ERR:").And.Contain("integer overflow");
    }

    // char() reads every argument with sqlite3_value_int64, so NULL is code point 0 - a NUL
    // character in the output - rather than an argument that is skipped.
    [TestCase("SELECT hex(char(NULL))")]
    [TestCase("SELECT hex(char(65,NULL,66))")]
    [TestCase("SELECT hex(char(NULL,65,NULL,NULL,66,NULL))")]
    [TestCase("SELECT length(char()), hex(char())")]
    [TestCase("SELECT hex(char(0)), hex(char(-1)), hex(char(1114112))")]
    public void CharTreatsEveryArgumentAsAnIntegerLikeSqlite(string sql) => AssertMatchesSqlite(sql);

    // char() coerces every argument to an integer codepoint exactly like the pinned Turso
    // exec_char (turso-src/core/vdbe/value.rs): text/blob by their numeric prefix (0 if none),
    // REAL by truncation, NULL as 0. The refreshed conformance corpus
    // (char-coerces-arguments-to-integers.sqltest) pins this behavior.
    [TestCase("SELECT hex(char('x'))", "00")]
    [TestCase("SELECT hex(char(2.7)), hex(char('65')), hex(char('abc'))", "02|41|00")]
    [TestCase("SELECT hex(char(65, 1.5, '66', X'43', 67))", "4101420043")]
    public void CharCoercesEveryArgumentToAnIntegerCodePoint(string sql, string expected)
    {
        RunManaged(sql).Should().Be(expected, because: sql);
    }

    // Documented divergence: SQLite emits a lone surrogate as the raw three-byte WTF-8 sequence,
    // which .NET's UTF-8 encoder cannot produce, so the managed engine substitutes U+FFFD.
    [Test]
    public void LoneSurrogateCodePointsUseTheReplacementCharacter()
    {
        RunManaged("SELECT hex(char(55296))").Should().Be("EFBFBD");
        RunSqlite("SELECT hex(char(55296))").Should().Be("EDA080");
    }

    private static void AssertMatchesSqlite(string sql)
        => RunManaged(sql).Should().Be(RunSqlite(sql), because: sql);

    internal static string RunManaged(string sql)
    {
        try
        {
            using var connection = new EmbeddedDatabase().Connect();
            using var statement = connection.Prepare(sql + ";");
            var rows = new List<string>();
            while (statement.Step() == StatementStepResult.Row)
            {
                var columns = new List<string>();
                for (var index = 0; index < statement.ColumnCount; index++)
                    columns.Add(Describe(statement.GetValue(index)));
                rows.Add(string.Join("|", columns));
            }

            return string.Join(" ;; ", rows);
        }
        catch (Exception exception)
        {
            return "ERR:" + exception.Message;
        }
    }

    private static string Describe(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => "<null>",
        SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
        SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span),
        _ => value.AsText(),
    };

    internal static string RunSqlite(string sql)
    {
        try
        {
            using var connection = new MsData.SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql + ";";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var columns = new List<string>();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = reader.GetValue(index);
                    columns.Add(value switch
                    {
                        null or DBNull => "<null>",
                        long integer => integer.ToString(CultureInfo.InvariantCulture),
                        double real => real.ToString("R", CultureInfo.InvariantCulture),
                        byte[] blob => Convert.ToHexString(blob),
                        _ => value.ToString() ?? string.Empty,
                    });
                }

                rows.Add(string.Join("|", columns));
            }

            return string.Join(" ;; ", rows);
        }
        catch (Exception exception)
        {
            return "ERR:" + exception.Message;
        }
    }
}
