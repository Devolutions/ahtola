using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies that a bare <c>DEFAULT</c> token is only accepted where SQLite/Turso accept it
/// (an INSERT statement's VALUES row list) and rejected everywhere else with a typed
/// <see cref="EmbeddedSqlException"/> rather than escaping as a raw runtime exception
/// (turso-sqltests/insert-default.sqltest's <c>default-in-select-errors</c> and
/// <c>default-in-where-errors</c>, which pin only "some error" via an empty
/// <c>expect error {}</c> pattern and would not themselves catch a diagnostic-type
/// regression).
/// </summary>
public class ManagedDefaultValueExpressionScopeTests
{
    [Test]
    public void DefaultInSelectListThrowsATypedSqlException()
    {
        using var connection = Open();

        var error = Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "SELECT DEFAULT;"));
        error!.Message.Should().Be("DEFAULT is only valid in INSERT VALUES");
    }

    [Test]
    public void DefaultInWhereClauseThrowsATypedSqlException()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER DEFAULT 10);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "SELECT * FROM t WHERE a = DEFAULT;"));
        error!.Message.Should().Be("DEFAULT is only valid in INSERT VALUES");
    }

    [Test]
    public void DefaultInHavingAndOrderByThrowsATypedSqlException()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER DEFAULT 10);");

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "SELECT a FROM t GROUP BY a HAVING a = DEFAULT;"))!
            .Message.Should().Be("DEFAULT is only valid in INSERT VALUES");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "SELECT a FROM t ORDER BY DEFAULT;"))!
            .Message.Should().Be("DEFAULT is only valid in INSERT VALUES");
    }

    [Test]
    public void DefaultInInsertSelectSourceThrowsATypedSqlException()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER DEFAULT 10);");
        Execute(connection, "CREATE TABLE u (id INTEGER PRIMARY KEY, a INTEGER);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO u SELECT id, DEFAULT FROM t;"));
        error!.Message.Should().Be("DEFAULT is only valid in INSERT VALUES");
    }

    [Test]
    public void DefaultInsideAnInsertValuesRowRemainsLegal()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER DEFAULT 10);");
        Execute(connection, "INSERT INTO t (id, a) VALUES (1, DEFAULT);");

        var rows = ReadRows(connection, "SELECT * FROM t;");
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
    }

    private static EmbeddedConnection Open() => new EmbeddedDatabase().Connect();

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        var rows = new List<SqlValue[]>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                    var row = new SqlValue[statement.ColumnCount];
                    for (var column = 0; column < row.Length; column++)
                        row[column] = statement.GetValue(column);

                    rows.Add(row);
                }
            }
        }

        return rows;
    }
}
