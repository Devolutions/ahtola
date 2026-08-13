using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class GroupByCorrelationScopeTests
{
    [TestCase("t2.d")]
    [TestCase("abs(t2.d)")]
    [TestCase("(SELECT t2.d)")]
    public void GroupByRejectsOuterReferencesIncludingNestedExpressions(string groupBy)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(b, c)");
        Execute(connection, "INSERT INTO t1 VALUES(1, 0)");
        Execute(connection, "CREATE TABLE t2(d)");
        Execute(connection, "INSERT INTO t2 VALUES(2)");

        var act = () => Execute(
            connection,
            $"SELECT d FROM t2 WHERE EXISTS(SELECT 1 FROM t1 GROUP BY {groupBy});");

        act.Should().Throw<EmbeddedSqlException>().WithMessage("no such column: t2.d");
    }

    [Test]
    public void GroupByAllowsNestedSubqueriesUsingTheirOwnAndTheGroupedSource()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(b)");
        Execute(connection, "INSERT INTO t1 VALUES(1), (1)");
        Execute(connection, "CREATE TABLE grouping_config(group_column)");
        Execute(connection, "INSERT INTO grouping_config VALUES('b')");

        var act = () => Execute(
            connection,
            """
            SELECT b
            FROM t1
            GROUP BY (
                SELECT CASE WHEN group_column = 'b' THEN b END
                FROM grouping_config
            );
            """);

        act.Should().NotThrow();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
