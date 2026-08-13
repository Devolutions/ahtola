using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class NotBetweenOperandTests
{
    [Test]
    public void NotBetweenAcceptsNotInSubqueryAsLowerBound()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value TEXT);");
        Execute(connection, "INSERT INTO data VALUES ('a'), ('b');");
        Execute(connection, "CREATE TABLE bounds(value TEXT);");

        ReadScalar(connection, """
            SELECT count(*)
            FROM data
            WHERE value NOT BETWEEN
                UPPER('x') NOT IN (
                    SELECT value
                    FROM bounds
                    WHERE QUOTE(NULL)
                    ORDER BY value
                    LIMIT 1
                )
                AND DATE('2024-01-01');
            """)
            .Kind
            .Should()
            .Be(SqlValueKind.Integer);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }
}
