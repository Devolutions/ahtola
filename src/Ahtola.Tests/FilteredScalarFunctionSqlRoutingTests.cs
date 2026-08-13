using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class FilteredScalarFunctionSqlRoutingTests
{
    [Test]
    public void FilteredBuiltinFunctionMatchesSqliteAndGatesTheRowBeforeTheFunction()
    {
        string[] setup =
        [
            "CREATE TABLE items(id INTEGER, category TEXT COLLATE NOCASE, label TEXT);",
            "INSERT INTO items VALUES (1, 'keep', 'MiXeD'), (2, 'discard', 'hidden'), (3, 'KEEP', NULL);",
        ];
        const string query =
            "SELECT id, upper(label) AS normalized FROM items WHERE category COLLATE NOCASE = 'keep';";

        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        RowsShouldMatch(managed, sqlite);

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        // A simple column-versus-literal predicate lowers to a register-native comparison and a
        // JumpIfNotTrue gate instead of the evaluator-callback Filter opcode.
        var opcodes = ReadRows(connection, "EXPLAIN " + query).Select(row => row[1].AsText()).ToList();
        opcodes.Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "LoadConstant", "Compare", "JumpIfNotTrue", "Column",
            "Column", "Function", "ResultRow", "Next", "CloseCursor", "Halt");
        opcodes.Should().NotContain("Filter");
        opcodes.IndexOf("JumpIfNotTrue").Should().BeLessThan(opcodes.IndexOf("Function"));
    }

    [Test]
    public void PredicateGateRunsBeforeAFunctionErrorOnAnExcludedRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(include_row INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (0, -9223372036854775808), (1, -5);");

        ReadRows(connection, "SELECT abs(value) FROM t WHERE include_row = 1;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(5));

        var opcodes = ReadRows(connection, "EXPLAIN SELECT abs(value) FROM t WHERE include_row = 1;")
            .Select(row => row[1].AsText())
            .ToList();
        opcodes.Should().Contain("JumpIfNotTrue").And.Contain("Function");
        opcodes.IndexOf("JumpIfNotTrue").Should().BeLessThan(opcodes.IndexOf("Function"));
    }

    [Test]
    public void ArityValidationPrecedesRowEvaluationLikeSqlite()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, x INTEGER, y INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 1, 0), (2, 2, 0);");

        // SQLite resolves function arity while preparing, so the arity diagnostic wins over any
        // row-evaluation error the WHERE clause would otherwise raise first.
        Assert.Throws<EmbeddedSqlException>(() =>
            ReadRows(
                connection,
                "SELECT abs(x, x) FROM t WHERE CASE WHEN id = 2 THEN abs(-9223372036854775808) ELSE 1 END;"))!
            .Message.Should().Be("wrong number of arguments to function abs()");

        // With a well-formed projection the evaluator still surfaces the WHERE clause error.
        Assert.Throws<EmbeddedSqlException>(() =>
            ReadRows(
                connection,
                "SELECT abs(x) FROM t WHERE CASE WHEN id = 2 THEN abs(-9223372036854775808) ELSE 1 END;"))!
            .Message.Should().Be("integer overflow");
    }

    private static List<SqlValue[]> RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        return ReadRows(connection, query);
    }

    private static List<object?[]> RunSqlite(IReadOnlyList<string> setup, string query)
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

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
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
