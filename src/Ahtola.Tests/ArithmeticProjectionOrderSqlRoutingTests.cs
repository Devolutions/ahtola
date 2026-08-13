using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ArithmeticProjectionOrderSqlRoutingTests
{
    private static readonly string[] Setup =
    [
        "CREATE TABLE t(id INTEGER, left_value, right_value, label TEXT COLLATE NOCASE, payload BLOB);",
        "INSERT INTO t VALUES (1, 7, 3, 'Alpha', x'01'), (2, 7.5, 2, 'beta', x'02FF'), (3, NULL, 4, NULL, NULL);",
    ];

    [Test]
    public void LeadingArithmeticProjectionMatchesSqliteAndRoutesThroughArithmetic()
    {
        const string query = "SELECT left_value + right_value AS total, label, id, payload FROM t;";

        AssertMatchesSqlite(Setup, query);

        using var connection = OpenManaged(Setup);
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "Column", "NumericAffinity", "NumericAffinity",
            "Arithmetic", "Column", "Column", "Column", "ResultRow", "Next", "CloseCursor", "Halt");
    }

    [Test]
    public void MiddleArithmeticProjectionPreservesOutputOrderAndTypes()
    {
        const string query = "SELECT label, left_value * right_value AS total, id FROM t;";

        AssertMatchesSqlite(Setup, query);

        using var connection = OpenManaged(Setup);
        var rows = ReadRows(connection, query);
        rows[0].Should().Equal(SqlValue.Text("Alpha"), SqlValue.Integer(21), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Text("beta"), SqlValue.Real(15), SqlValue.Integer(2));
        rows[2].Should().Equal(SqlValue.Null, SqlValue.Null, SqlValue.Integer(3));
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("Arithmetic");
    }

    [Test]
    public void ParameterRoutesWhileCollatedOrderRemainsOnEvaluator()
    {
        const string parameterQuery = "SELECT left_value + ?1 AS total FROM t WHERE id = 1;";
        const string orderedQuery = "SELECT left_value + right_value AS total, label FROM t ORDER BY label COLLATE NOCASE;";

        AssertMatchesSqlite(Setup, parameterQuery, SqlValue.Integer(2));

        using var connection = OpenManaged(Setup);
        ReadRows(connection, orderedQuery).Select(row => row[1]).Should().Equal(
            SqlValue.Null,
            SqlValue.Text("Alpha"),
            SqlValue.Text("beta"));
        Opcodes(ReadRows(connection, "EXPLAIN " + parameterQuery, SqlValue.Integer(2)))
            .Should().Contain("LoadParameter").And.Contain("Arithmetic");
        ExplainRefused(connection, "EXPLAIN " + orderedQuery);
    }

    [Test]
    public void TextArithmeticOperandsRouteThroughNumericAffinity()
    {
        const string query = "SELECT id, left_value + right_value AS total FROM t;";
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, left_value, right_value);",
            "INSERT INTO t VALUES (1, '10', 2), (2, 3, '4');",
        ];

        AssertMatchesSqlite(setup, query);

        using var connection = OpenManaged(setup);
        Opcodes(ReadRows(connection, "EXPLAIN " + query))
            .Should().Contain("NumericAffinity").And.Contain("Arithmetic");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query, params SqlValue[] parameters)
    {
        var managed = RunManaged(setup, query, parameters);
        var sqlite = RunSqlite(setup, query, parameters);

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
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var connection = OpenManaged(setup);
        using var statement = connection.Prepare(query);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);

        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        return (columns, Drain(statement));
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(
        IReadOnlyList<string> setup,
        string query,
        IReadOnlyList<SqlValue> parameters)
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
        for (var index = 0; index < parameters.Count; index++)
            queryCommand.Parameters.AddWithValue($"?{index + 1}", ToSqliteValue(parameters[index]));

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

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        return connection;
    }

    private static object ToSqliteValue(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => DBNull.Value,
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        SqlValueKind.Text => value.AsText(),
        SqlValueKind.Blob => value.AsBlob().ToArray(),
        _ => throw new InvalidOperationException($"Unsupported SQLite parameter type {value.Kind}."),
    };

    private static void CellShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer));
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real));
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
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

    private static void ExplainRefused(EmbeddedConnection connection, string sql, params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
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
            for (var column = 0; column < row.Length; column++)
                row[column] = statement.GetValue(column);

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());
}
