using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ThreePartColumnReferenceTests
{
    [Test]
    public void MainSchemaColumnReferenceResolvesInDmlPredicate()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE test(col);");
        Execute(connection, "INSERT INTO test VALUES (1), (2);");
        Execute(connection, "DELETE FROM test WHERE main.test.col = 2;");

        ReadValues(connection, "SELECT col FROM test;")
            .Should()
            .Equal(SqlValue.Integer(1));
    }

    [Test]
    public void UnknownSchemaColumnReferenceDoesNotResolveAgainstMain()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE test(col);");
        Execute(connection, "INSERT INTO test VALUES (1);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "DELETE FROM test WHERE unknown.test.col = 1;"))!;

        error.Message.Should().Be("no such database: unknown");
    }

    [Test]
    public void MainSchemaColumnReferenceIsAllowedInPartialIndexExpressions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(id INTEGER PRIMARY KEY, val TEXT);");

        Execute(connection, "CREATE INDEX idx_t1 ON t1(val) WHERE LENGTH(main.t1.val) > 3;");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'ab'), (2, 'abcdef');");

        ReadValues(connection, "SELECT id FROM t1 ORDER BY id;")
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [TestCase("CREATE TABLE t1(a INTEGER, b AS (main.t1.a));")]
    [TestCase("CREATE TABLE t1(a INTEGER, b AS (ABS(main.t1.a)));")]
    public void MainSchemaColumnReferenceIsRejectedInGeneratedColumns(string sql)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var error = Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql))!;

        error.Message.Should().Be("the \".\" operator prohibited in generated columns");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue> ReadValues(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0));

        return values;
    }
}
