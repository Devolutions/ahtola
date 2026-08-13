using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ArithmeticScanSqlRoutingTests
{
    [Test]
    public void NumericColumnArithmeticMatchesSqliteValuesAndTypes()
    {
        var setup = new[]
        {
            "CREATE TABLE t(id INTEGER, left_value, right_value)",
            "INSERT INTO t VALUES (1, 7, 3), (2, 7.5, 2), (3, NULL, 4), (4, 5, 0)",
        };

        foreach (var op in new[] { "+", "-", "*", "/", "%" })
            AssertMatchesSqlite(setup, $"SELECT id, left_value {op} right_value AS result FROM t");
    }

    [Test]
    public void NumericColumnArithmeticRoutesThroughArithmeticOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, left_value, right_value)");
        Execute(connection, "INSERT INTO t VALUES (1, 7, 3), (2, 7.5, 2), (3, NULL, 4)");

        var rows = ReadRows(connection, "SELECT id, left_value + right_value AS total FROM t");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(10));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Real(9.5));
        rows[2].Should().Equal(SqlValue.Integer(3), SqlValue.Null);

        var explain = ReadRows(connection, "EXPLAIN SELECT id, left_value + right_value AS total FROM t");
        explain.Select(row => row[1].AsText()).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "Column", "Column", "NumericAffinity",
            "NumericAffinity", "Arithmetic", "ResultRow", "Next", "CloseCursor", "Halt");
        explain.Select(row => row[6].AsText()).Should().Contain(
            "r[2]=c0.col[1]",
            "r[3]=c0.col[2]",
            "r[1]=r[2] + r[3]",
            "output=r[0..1]");
    }

    [Test]
    public void TextColumnsConstantsAndFiltersRouteThroughGenericExpressions()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a, b)");
        Execute(connection, "INSERT INTO t VALUES ('10', 2)");

        ReadRows(connection, "SELECT a + b FROM t")[0].Should().Equal(SqlValue.Integer(12));
        ReadRows(connection, "EXPLAIN SELECT a + b FROM t")
            .Select(row => row[1].AsText()).Should().Contain("NumericAffinity");

        ReadRows(connection, "SELECT a + 1 FROM t")[0].Should().Equal(SqlValue.Integer(11));
        ReadRows(connection, "EXPLAIN SELECT a + 1 FROM t")
            .Select(row => row[1].AsText()).Should().Contain("Arithmetic");

        ReadRows(connection, "SELECT a + b FROM t WHERE b = 2")[0].Should().Equal(SqlValue.Integer(12));
        ReadRows(connection, "EXPLAIN SELECT a + b FROM t WHERE b = 2")
            .Select(row => row[1].AsText()).Should().Contain("JumpIfNotTrue");
    }

    [Test]
    public void ResetRechecksLiveOperandValueKindsBeforeRouting()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a, b)");
        Execute(connection, "INSERT INTO t VALUES (10, 2)");

        using var statement = connection.Prepare("SELECT a + b FROM t");
        Drain(statement)[0].Should().Equal(SqlValue.Integer(12));

        // The same generic bytecode shape handles the newly visible text value through numeric affinity.
        Execute(connection, "INSERT INTO t VALUES ('10', 2)");
        statement.Reset();
        var rows = Drain(statement);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(12));
        rows[1].Should().Equal(SqlValue.Integer(12));
        ReadRows(connection, "EXPLAIN SELECT a + b FROM t")
            .Select(row => row[1].AsText()).Should().Contain("NumericAffinity");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var row = 0; row < sqlite.Rows.Count; row++)
        {
            managed.Rows[row].Should().HaveCount(sqlite.Rows[row].Length);
            for (var column = 0; column < sqlite.Rows[row].Length; column++)
                CellShouldMatch(managed.Rows[row][column], sqlite.Rows[row][column]);
        }
    }

    private static (string[] Columns, List<SqlValue[]> Rows) RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        return (columns, Drain(statement));
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = query;
        using var reader = queryCommand.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var column = 0; column < row.Length; column++)
                row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(row);
        }

        return (columns, rows);
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Kind.Should().Be(SqlValueKind.Integer);
                managed.AsInteger().Should().Be(integer);
                break;
            case double real:
                managed.Kind.Should().Be(SqlValueKind.Real);
                managed.AsReal().Should().Be(real);
                break;
            default:
                Assert.Fail($"Unexpected SQLite value type {sqlite.GetType().Name}.");
                break;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return Drain(statement);
    }

    private static void ExplainRefused(EmbeddedConnection connection, string sql)
    {
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, sql))!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static List<SqlValue[]> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var column = 0; column < row.Length; column++)
                row[column] = statement.GetValue(column);

            rows.Add(row);
        }

        return rows;
    }
}
