using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class InstrSqlRoutingTests
{
    [Test]
    public void ParameterizedInstrRoutesThroughFunctionAndMatchesSqlite()
    {
        foreach (var (haystack, needle) in new[]
                 {
                     (SqlValue.Text("αβγβ"), SqlValue.Text("β")),
                     (SqlValue.Blob([0x01, 0x02, 0x03, 0x02, 0x03]), SqlValue.Blob([0x02, 0x03])),
                     (SqlValue.Text("abc"), SqlValue.Text("z")),
                     (SqlValue.Null, SqlValue.Text("x")),
                 })
        {
            using var connection = new EmbeddedDatabase().Connect();
            ReadSingle(connection, "SELECT instr(?1, ?2);", haystack, needle)
                .Should()
                .Be(ReadSqliteSingle("SELECT instr(?1, ?2);", haystack, needle));
        }

        using var explainConnection = new EmbeddedDatabase().Connect();
        Opcodes(ReadRows(explainConnection, "EXPLAIN SELECT instr(?1, ?2);", SqlValue.Null, SqlValue.Null))
            .Should()
            .Equal("LoadParameter", "LoadParameter", "Function", "ResultRow", "Halt");
    }

    [Test]
    public void ScanInstrPreservesOrderValuesAndTypes()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, haystack TEXT COLLATE NOCASE, needle);",
            "INSERT INTO t VALUES (1, 'αβγβ', 'β'), (2, x'0102030203', x'0203'), (3, NULL, 'x'), (4, 'abc', 'z'), (5, 'A', 'a');",
        ];
        const string sql = "SELECT id, instr(haystack, needle) FROM t;";

        using var managed = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(managed, statement);

        RowsShouldMatch(ReadRows(managed, sql), ReadSqliteRows(setup, sql));
        Opcodes(ReadRows(managed, "EXPLAIN " + sql)).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "Column", "Column", "Function", "ResultRow", "Next",
            "CloseCursor", "Halt");
    }

    [Test]
    public void ShadowedInstrFallsBackWhileNestedBuiltinCallsRoute()
    {
        using var connection = new EmbeddedDatabase().Connect();
        connection.RegisterScalarFunction("instr", 2, _ => SqlValue.Text("shadowed"));

        ReadSingle(connection, "SELECT instr(?1, ?2);", SqlValue.Text("abc"), SqlValue.Text("b"))
            .Should()
            .Be(SqlValue.Text("shadowed"));
        ExplainIsRefused(connection, "EXPLAIN SELECT instr(?1, ?2);", SqlValue.Text("abc"), SqlValue.Text("b"));

        using var nestedConnection = new EmbeddedDatabase().Connect();
        ReadSingle(nestedConnection, "SELECT instr(lower(?1), ?2);", SqlValue.Text("ABC"), SqlValue.Text("b"))
            .Should()
            .Be(SqlValue.Integer(2));
        Opcodes(ReadRows(
                nestedConnection,
                "EXPLAIN SELECT instr(lower(?1), ?2);",
                SqlValue.Text("ABC"),
                SqlValue.Text("b")))
            .Should()
            .Equal(
                "LoadParameter", "Function", "LoadParameter", "Function",
                "ResultRow", "Halt");

        using var errorConnection = new EmbeddedDatabase().Connect();
        using var statement = errorConnection.Prepare("SELECT instr(?1);");
        statement.Bind(1, SqlValue.Text("abc"));
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("wrong number of arguments to function instr()");
    }

    private static SqlValue ReadSingle(EmbeddedConnection connection, string sql, params SqlValue[] parameters)
    {
        var rows = ReadRows(connection, sql, parameters);
        rows.Should().ContainSingle().Which.Should().ContainSingle();
        return rows[0][0];
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

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadSqliteSingle(string sql, params SqlValue[] parameters)
    {
        var rows = ReadSqliteRows([], sql, parameters);
        rows.Should().ContainSingle();
        rows[0].Should().ContainSingle();
        return rows[0][0];
    }

    private static List<SqlValue[]> ReadSqliteRows(
        IReadOnlyList<string> setup,
        string sql,
        params SqlValue[] parameters)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText = sql;
        for (var index = 0; index < parameters.Length; index++)
            query.Parameters.AddWithValue($"?{index + 1}", ToSqliteValue(parameters[index]));

        using var reader = query.ExecuteReader();
        var rows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var column = 0; column < row.Length; column++)
                row[column] = reader.IsDBNull(column) ? SqlValue.Null : ToSqlValue(reader.GetValue(column));

            rows.Add(row);
        }

        return rows;
    }

    private static void ExplainIsRefused(EmbeddedConnection connection, string sql, params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static void RowsShouldMatch(
        IReadOnlyList<SqlValue[]> actual,
        IReadOnlyList<SqlValue[]> expected)
    {
        actual.Should().HaveCount(expected.Count);
        for (var row = 0; row < expected.Count; row++)
            actual[row].Should().Equal(expected[row]);
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static object ToSqliteValue(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => DBNull.Value,
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        SqlValueKind.Text => value.AsText(),
        SqlValueKind.Blob => value.AsBlob().ToArray(),
        _ => throw new InvalidOperationException($"Unexpected value kind {value.Kind}."),
    };

    private static SqlValue ToSqlValue(object value) => value switch
    {
        long integer => SqlValue.Integer(integer),
        double real => SqlValue.Real(real),
        string text => SqlValue.Text(text),
        byte[] blob => SqlValue.Blob(blob),
        _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().Name}."),
    };
}
