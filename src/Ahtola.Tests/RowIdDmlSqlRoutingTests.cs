using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class RowIdDmlSqlRoutingTests
{
    [Test]
    public void UpdateByRowidRoutesThroughTheRowidAwareFilterAndPreservesParameterSlots()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30);");

        Opcodes(ExplainBound(
                connection,
                "EXPLAIN UPDATE t SET value = ?1 WHERE rowid = ?2 RETURNING rowid, value;",
                SqlValue.Integer(99),
                SqlValue.Integer(2)))
            .Should().Equal(
                "OpenWriteCursor", "Rewind", "FilterRowId", "Update", "Next", "OpenReadCursor",
                "Rewind", "RowId", "Column", "ResultRow", "Next", "CloseCursor", "Commit",
                "CloseCursor", "Halt");

        using (var statement = connection.Prepare("UPDATE t SET value = ?1 WHERE rowid = ?2 RETURNING rowid, value;"))
        {
            statement.Bind(1, SqlValue.Integer(99));
            statement.Bind(2, SqlValue.Integer(2));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(2));
            statement.GetValue(1).Should().Be(SqlValue.Integer(99));
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(1);
        }

        ReadRows(connection, "SELECT value FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(99), SqlValue.Integer(30));
    }

    [Test]
    public void QualifiedRowidDeleteRoutesAndReturnsThePreDeleteRow()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30);");

        Opcodes(ReadRows(connection, "EXPLAIN DELETE FROM t WHERE t.rowid = 2 RETURNING rowid, value;"))
            .Should().Contain("FilterRowId");

        using (var statement = connection.Prepare("DELETE FROM t WHERE t.rowid = 2 RETURNING rowid, value;"))
        {
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(2));
            statement.GetValue(1).Should().Be(SqlValue.Integer(20));
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(1);
        }

        ReadRows(connection, "SELECT value FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(30));
    }

    [Test]
    public void RowidUpdateConstraintFailureRollsBackTheBufferedStatement()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        using (var statement = connection.Prepare("UPDATE t SET id = 1 WHERE rowid = 2 RETURNING id;"))
            Assert.Throws<EmbeddedSqlException>(() => statement.Step());

        var rows = ReadRows(connection, "SELECT id, value FROM t;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));
    }

    [Test]
    public void RowidPredicateWithSubqueryRemainsEvaluatorBacked()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30);");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN DELETE FROM t WHERE rowid IN (SELECT 2);"))!
            .Message.Should().Contain("EXPLAIN is only supported");

        using (var statement = connection.Prepare("DELETE FROM t WHERE rowid IN (SELECT 2);"))
        {
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(1);
        }

        ReadRows(connection, "SELECT value FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(30));
    }

    [Test]
    public void WithoutRowidRowidPredicateRetainsEvaluatorDiagnostic()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO t VALUES (1);");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN DELETE FROM t WHERE rowid = 1;"))!
            .Message.Should().Contain("EXPLAIN is only supported");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "DELETE FROM t WHERE rowid = 1;"))!
            .Message.Should().Contain("no such column");
    }

    private static EmbeddedConnection Connect() => new EmbeddedDatabase().Connect();

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> ExplainBound(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(EmbeddedStatement statement)
    {
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

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());
}
