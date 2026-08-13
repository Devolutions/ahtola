using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class RowidScanSqlRoutingTests
{
    [Test]
    public void HiddenRowidScanMatchesSqliteValuesAndTypes()
    {
        var setup = new[]
        {
            "CREATE TABLE t(value, payload, data)",
            "INSERT INTO t(rowid, value, payload, data) VALUES (5, 42, 'text', x'CAFE')",
            "INSERT INTO t(rowid, value, payload, data) VALUES (9, NULL, 3.5, x'00')",
        };

        AssertMatchesSqlite(
            setup,
            "SELECT x.rowid AS rid, value, payload, data FROM t AS x WHERE x.rowid >= 5");
    }

    [Test]
    public void HiddenRowidScanUsesRowIdAndRowIdFilterOpcodes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value TEXT)");
        Execute(connection, "INSERT INTO t(rowid, value) VALUES (4, 'four'), (8, 'eight')");

        var explain = ReadRows(
            connection,
            "EXPLAIN SELECT x.rowid, x.value FROM t AS x WHERE x.rowid >= ?1",
            SqlValue.Integer(5));
        explain.Select(row => row[1].AsText()).Should().Equal(
            "OpenReadCursor", "Rewind", "FilterRowId", "RowId", "Column", "ResultRow", "Next",
            "CloseCursor", "Halt");
        explain[2][6].Should().Be(SqlValue.Text("skip row when WHERE is false, goto 6"));
        explain[3][6].Should().Be(SqlValue.Text("r[0]=c0.rowid"));

        ReadRows(
                connection,
                "SELECT x.rowid, x.value FROM t AS x WHERE x.rowid >= ?1",
                SqlValue.Integer(5))
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(8), SqlValue.Text("eight"));
    }

    [Test]
    public void ExcludedRowidShapesKeepEvaluatorErrorsAndRouting()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER)");
        Execute(connection, "INSERT INTO t VALUES (1)");

        using var invalid = connection.Prepare("SELECT other.rowid FROM t AS x");
        Assert.Throws<EmbeddedSqlException>(() => invalid.Step())!.Message
            .Should()
            .Be("no such column: other.rowid");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT other.rowid FROM t AS x"))!
            .Message
            .Should()
            .Contain("EXPLAIN is only supported");
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

    private static (string[] Columns, List<SqlValue[]> Rows) RunManaged(
        IReadOnlyList<string> setup,
        string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        return (columns, Drain(statement));
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(
        IReadOnlyList<string> setup,
        string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
            setupCommand.ExecuteNonQuery();
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
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text);
                managed.AsText().Should().Be(text);
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
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

    private static List<SqlValue[]> ReadRows(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);
        return Drain(statement);
    }

    private static List<SqlValue[]> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }
}
