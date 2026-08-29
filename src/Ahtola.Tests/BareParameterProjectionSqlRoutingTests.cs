using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class BareParameterProjectionSqlRoutingTests
{
    [Test]
    public void BareParameterProjectionMatchesSqliteValuesTypesAndColumnOrder()
    {
        const string query =
            "SELECT ?2 AS text_value, ?1 AS integer_value, ?3 AS payload, ?4 AS ratio, ?5 AS null_value, ?2 AS repeated_text;";
        SqlValue[] parameters =
        [
            SqlValue.Integer(7),
            SqlValue.Text("seven"),
            SqlValue.Blob([0x01, 0xFE]),
            SqlValue.Real(1.5),
            SqlValue.Null,
        ];

        var managed = RunManaged(query, parameters);
        var sqlite = RunSqlite(query, parameters);

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().ContainSingle();
        managed.Rows[0].Should().HaveCount(sqlite.Rows[0].Length);
        for (var column = 0; column < sqlite.Rows[0].Length; column++)
            CellShouldMatch(managed.Rows[0][column], sqlite.Rows[0][column]);
    }

    [Test]
    public void BareParameterProjectionRebindsInProjectionOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        const string query = "SELECT ?2 AS second, 9 AS fixed, ?1 AS first, ?2 AS repeated;";

        using var statement = connection.Prepare(query);
        statement.Bind(1, SqlValue.Integer(3));
        statement.Bind(2, SqlValue.Text("three"));
        Drain(statement).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("three"), SqlValue.Integer(9), SqlValue.Integer(3), SqlValue.Text("three"));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(8));
        statement.Bind(2, SqlValue.Text("eight"));
        Drain(statement).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("eight"), SqlValue.Integer(9), SqlValue.Integer(8), SqlValue.Text("eight"));
    }

    [Test]
    public void ComputedProjectionValidationResolvesFunctionsBeforeEvaluation()
    {
        using var connection = new EmbeddedDatabase().Connect();
        const string query = "SELECT abs(-9223372036854775808), no_such_function();";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query))!
            .Message.Should().Contain("EXPLAIN is only supported");

        using var statement = connection.Prepare(query);
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("no such function: NO_SUCH_FUNCTION");
    }

    private static (string[] Columns, List<SqlValue[]> Rows) RunManaged(
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(query);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);

        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        return (columns, Drain(statement));
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        for (var index = 0; index < parameters.Count; index++)
            command.Parameters.AddWithValue($"?{index + 1}", ToSqliteValue(parameters[index]));

        using var reader = command.ExecuteReader();
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

    private static List<SqlValue[]> ReadRows(
        EmbeddedConnection connection,
        string query,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(query);
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
}
