using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ManagedPercentileAggregateRuntimeSliceTests
{
    [Test]
    public void PercentileAggregatesConvertNumericValuesAndApplyTheirDistributionRules()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(value);");
        Execute(connection, """
            INSERT INTO valueset VALUES
                (10.5), (1), ('2'), (-5), (NULL), ('not a number'), (x'00');
            """);

        var rows = ReadRows(
            connection,
            "SELECT median(value), percentile(value, 50), percentile_cont(value, 0.25), percentile_disc(value, 0.75) FROM valueset;");

        rows.Should().ContainSingle();
        rows[0].Should().Equal(
            SqlValue.Real(1.5),
            SqlValue.Real(1.5),
            SqlValue.Real(-0.5),
            SqlValue.Real(2));
    }

    [Test]
    public void OrderedSetAggregatesRewriteWithinGroupArgumentsAndModeBreaksTiesBySmallestValue()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(value);");
        Execute(connection, "INSERT INTO valueset VALUES (3), (1), (3), (1), (2), (NULL);");

        ReadRows(
            connection,
            """
            SELECT
                mode() WITHIN GROUP (ORDER BY value),
                percentile_cont(0.5) WITHIN GROUP (ORDER BY value),
                percentile_disc(0.5) WITHIN GROUP (ORDER BY value)
            FROM valueset;
            """)[0].Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Real(2),
                SqlValue.Integer(2));
    }

    [Test]
    public void OrderedSetDiscretePercentilePreservesTypeAndUsesCumulativeRank()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(value);");
        Execute(connection, "INSERT INTO valueset VALUES (1), (2), (3), (4);");

        ReadRows(
            connection,
            "SELECT percentile_disc(0.6) WITHIN GROUP (ORDER BY value) FROM valueset;")[0]
            .Should().Equal(SqlValue.Integer(3));

        Execute(connection, "DELETE FROM valueset;");
        Execute(connection, "INSERT INTO valueset VALUES ('z'), ('a'), ('m');");
        ReadRows(
            connection,
            "SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY value) FROM valueset;")[0]
            .Should().Equal(SqlValue.Text("m"));
    }

    [Test]
    public void OrderedSetPercentileValidatesDirectArgumentBeforeScanningRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(value);");

        AssertError(
            connection,
            "SELECT percentile_cont(2) WITHIN GROUP (ORDER BY value) FROM valueset;",
            "Percentile value must be between 0.0 and 1.0 inclusive");
        AssertParseError(
            connection,
            "SELECT percentile_cont(ALL 0.5) WITHIN GROUP (ORDER BY value) FROM valueset;",
            "DISTINCT is not supported for ordered-set aggregate percentile_cont()");
    }

    [Test]
    public void OrderedSetAggregatesEnforceTursoSyntaxRestrictions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(value);");

        AssertParseError(
            connection,
            "SELECT mode() FROM valueset;",
            "mode() requires a WITHIN GROUP (ORDER BY ...) clause");
        AssertParseError(
            connection,
            "SELECT mode() WITHIN GROUP (ORDER BY value DESC) FROM valueset;",
            "DESC and NULLS ordering inside WITHIN GROUP are not supported yet");
        AssertParseError(
            connection,
            "SELECT percentile_cont() WITHIN GROUP (ORDER BY value) FROM valueset;",
            "wrong number of arguments to function percentile_cont()");
        AssertParseError(
            connection,
            "SELECT sum(value) WITHIN GROUP (ORDER BY value) FROM valueset;",
            "WITHIN GROUP is not supported for function sum()");
    }

    [Test]
    public void TursoVersionReportsThePinnedCompatibilityVersion()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadRows(connection, "SELECT turso_version();")[0]
            .Should().Equal(SqlValue.Text(EmbeddedDatabase.TursoCompatibilityVersion));
    }

    [Test]
    public void PercentileAggregatesKeepIndependentGroupedStateAndReturnNullWithoutNumericInput()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(group_id TEXT, value, fraction);");
        Execute(connection, """
            INSERT INTO valueset VALUES
                ('a', 1, 0.5), ('a', 9, 0.5), ('a', NULL, 0.5),
                ('b', 10, 0.5), ('b', 20, 0.5), ('b', 40, 0.5),
                ('ignored', 'not a number', 0.5), ('ignored', x'00', 0.5);
            """);

        var grouped = ReadRows(
            connection,
            "SELECT group_id, median(value), percentile_cont(value, fraction), percentile_disc(value, fraction) FROM valueset GROUP BY group_id;");

        grouped.Should().HaveCount(3);
        grouped[0].Should().Equal(
            SqlValue.Text("a"), SqlValue.Real(5), SqlValue.Real(5), SqlValue.Real(1));
        grouped[1].Should().Equal(
            SqlValue.Text("b"), SqlValue.Real(20), SqlValue.Real(20), SqlValue.Real(20));
        grouped[2].Should().Equal(
            SqlValue.Text("ignored"), SqlValue.Null, SqlValue.Null, SqlValue.Null);

        Execute(connection, "CREATE TABLE incomplete(value, fraction);");
        Execute(connection, "INSERT INTO incomplete VALUES (1, NULL), (2, 'not a number'), (3, 0.5);");
        ReadRows(connection, "SELECT percentile_cont(value, fraction) FROM incomplete;")[0]
            .Should().Equal(SqlValue.Real(3));

        Execute(connection, "CREATE TABLE empty_values(value);");
        ReadRows(
            connection,
            "SELECT median(value), percentile(value, 50), percentile_cont(value, 0.5), percentile_disc(value, 0.5) FROM empty_values;")[0]
            .Should().Equal(SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null);
    }

    [Test]
    public void PercentileAggregatesValidateArityBoundsAndConsistentPercentiles()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE valueset(value, percentile);");
        Execute(connection, "INSERT INTO valueset VALUES (1, 0.5), (2, 0.5);");

        AssertError(connection, "SELECT median(value, 0) FROM valueset;", "wrong number of arguments to function median()");
        AssertError(connection, "SELECT percentile(value) FROM valueset;", "wrong number of arguments to function percentile()");
        AssertError(connection, "SELECT percentile_cont(value) FROM valueset;", "wrong number of arguments to function percentile_cont()");
        AssertError(connection, "SELECT percentile_disc(value) FROM valueset;", "wrong number of arguments to function percentile_disc()");
        AssertError(connection, "SELECT percentile(value, 101) FROM valueset;", "Invalid percentile value");
        AssertError(
            connection,
            "SELECT percentile_cont(value, 1.01) FROM valueset;",
            "Percentile value must be between 0.0 and 1.0 inclusive");
        AssertError(
            connection,
            "SELECT percentile_disc(value, -0.01) FROM valueset;",
            "Percentile value must be between 0.0 and 1.0 inclusive");

        Execute(connection, "UPDATE valueset SET percentile = 0.75 WHERE value = 2;");
        AssertError(
            connection,
            "SELECT percentile_cont(value, percentile) FROM valueset;",
            "Inconsistent percentile values across rows");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);

            rows.Add(row);
        }

        return rows;
    }

    private static void AssertError(EmbeddedConnection connection, string sql, string message)
    {
        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql));
        error!.Message.Should().Be(message);
    }

    private static void AssertParseError(EmbeddedConnection connection, string sql, string message)
    {
        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql));
        error!.Message.Should().StartWith(message);
    }
}
