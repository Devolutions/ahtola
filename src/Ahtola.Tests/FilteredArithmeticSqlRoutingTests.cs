using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class FilteredArithmeticSqlRoutingTests
{
    [Test]
    public void FilteredArithmeticMatchesSqliteAndGatesTheRowBeforeArithmetic()
    {
        string[] setup =
        [
            "CREATE TABLE items(id INTEGER, category TEXT COLLATE NOCASE, left_value, right_value, label TEXT);",
            "INSERT INTO items VALUES (1, 'discard', x'01', 5, 'ignored'), (2, 'keep', 7, 3, 'seven'), (3, 'KEEP', 7.5, 2, 'real'), (4, 'keep', NULL, 4, NULL);",
        ];
        const string query =
            "SELECT id, left_value + right_value AS total, label FROM items WHERE category COLLATE NOCASE = ?1;";

        var managed = RunManaged(setup, query, SqlValue.Text("kEeP"));
        var sqlite = RunSqlite(setup, query, SqlValue.Text("kEeP"));
        RowsShouldMatch(managed, sqlite);

        using var connection = OpenManaged(setup);
        var opcodes = ReadRows(connection, "EXPLAIN " + query, SqlValue.Text("kEeP"))
            .Select(row => row[1].AsText())
            .ToList();
        opcodes.Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "LoadParameter", "Compare", "JumpIfNotTrue", "Column",
            "Column", "Column", "NumericAffinity", "NumericAffinity", "Arithmetic", "Column", "ResultRow",
            "Next", "CloseCursor", "Halt");
        opcodes.Should().NotContain("Filter");
        opcodes.IndexOf("JumpIfNotTrue").Should().BeLessThan(opcodes.IndexOf("Arithmetic"));
    }

    [Test]
    public void ComplexFilterFallsBackAndKeepsTheEvaluatorsFilterValidationErrorFirst()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, x INTEGER, y INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 9223372036854775807, 1), (2, 1, 1);");
        const string query =
            "SELECT x + y FROM t WHERE CASE WHEN id = 2 THEN abs(x, x) ELSE 1 END;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query))!
            .Message.Should().Contain("EXPLAIN is only supported");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("wrong number of arguments to function abs()");
    }

    private static List<SqlValue[]> RunManaged(
        IReadOnlyList<string> setup,
        string query,
        SqlValue parameter)
    {
        using var connection = OpenManaged(setup);
        return ReadRows(connection, query, parameter);
    }

    private static List<object?[]> RunSqlite(
        IReadOnlyList<string> setup,
        string query,
        SqlValue parameter)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = query;
        queryCommand.Parameters.AddWithValue("?1", parameter.AsText());
        using var reader = queryCommand.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var column = 0; column < row.Length; column++)
                row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(row);
        }

        return rows;
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        return connection;
    }

    private static void RowsShouldMatch(IReadOnlyList<SqlValue[]> managed, IReadOnlyList<object?[]> sqlite)
    {
        managed.Should().HaveCount(sqlite.Count);
        for (var row = 0; row < sqlite.Count; row++)
        {
            managed[row].Should().HaveCount(sqlite[row].Length);
            for (var column = 0; column < sqlite[row].Length; column++)
            {
                switch (sqlite[row][column])
                {
                    case null:
                        managed[row][column].Kind.Should().Be(SqlValueKind.Null);
                        break;
                    case long integer:
                        managed[row][column].Should().Be(SqlValue.Integer(integer));
                        break;
                    case double real:
                        managed[row][column].Should().Be(SqlValue.Real(real));
                        break;
                    case string text:
                        managed[row][column].Should().Be(SqlValue.Text(text));
                        break;
                    default:
                        Assert.Fail($"Unexpected SQLite value type {sqlite[row][column]!.GetType().Name}.");
                        break;
                }
            }
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
