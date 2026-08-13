using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class CompiledDmlExpressionSqlRoutingTests
{
    [Test]
    public void ComplexInsertUpdateAndDeleteMatchSqlite()
    {
        const string schema = """
            CREATE TABLE t(id INTEGER, name TEXT, amount TEXT);
            INSERT INTO t VALUES (1, 'ada', '10'), (2, 'Grace', '20'), (3, 'linus', '30');
            """;
        var statements = new[]
        {
            """
            INSERT INTO t VALUES (4, 'Edsger', '40')
            RETURNING id + 1, upper(name), length(name) + abs(id - 3), typeof(amount);
            """,
            """
            UPDATE t SET amount = amount + 5
            WHERE abs(id - 2) <= 1 AND length(name) > 0
            RETURNING id, upper(name), amount + 0;
            """,
            """
            DELETE FROM t
            WHERE instr(lower(name), 'a') > 0
            RETURNING id * 10, upper(name);
            """,
        };

        using var managed = new EmbeddedDatabase().Connect();
        ExecuteManagedBatch(managed, schema);
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        ExecuteSqliteBatch(sqlite, schema);

        foreach (var sql in statements)
            AssertRowsEqual(ReadManaged(managed, sql), ReadSqlite(sqlite, sql), sql);
    }

    [Test]
    public void ComplexPredicateAndReturningExpressionExposeTheTwoPhasePlan()
    {
        using var connection = new EmbeddedDatabase().Connect();
        ExecuteManaged(connection, "CREATE TABLE t(id INTEGER, name TEXT, amount TEXT);");

        var rows = Explain(
            connection,
            """
            EXPLAIN UPDATE t SET amount = amount
            WHERE abs(id - ?1) <= 2 AND length(name) > 0
            RETURNING t.*, upper(name), (amount + ?2) * abs(id);
            """,
            SqlValue.Integer(2),
            SqlValue.Integer(5));
        var opcodes = Opcodes(rows).ToArray();

        opcodes.Should().ContainInOrder(
            "OpenWriteCursor", "Filter", "Update", "Next", "OpenReadCursor", "Rewind");
        opcodes.Count(opcode => opcode == "Function").Should().Be(2);
        opcodes.Count(opcode => opcode == "LoadParameter").Should().Be(1);
        opcodes.Count(opcode => opcode == "Arithmetic").Should().Be(2);
        opcodes[^4..].Should().Equal("CloseCursor", "Commit", "CloseCursor", "Halt");
    }

    [Test]
    public void ReturningParametersRemainLateBoundAcrossReset()
    {
        using var connection = new EmbeddedDatabase().Connect();
        ExecuteManaged(connection, "CREATE TABLE t(name TEXT);");
        using var statement = connection.Prepare(
            "INSERT INTO t VALUES ('ada') RETURNING upper(name), ?1 + length(name);");

        statement.Bind(1, SqlValue.Text("10"));
        statement.Step().Should().Be(StatementStepResult.Row);
        ReadCurrentRow(statement).Should().Equal(SqlValue.Text("ADA"), SqlValue.Integer(13));
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(20));
        statement.Step().Should().Be(StatementStepResult.Row);
        ReadCurrentRow(statement).Should().Equal(SqlValue.Text("ADA"), SqlValue.Integer(23));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void ReturningErrorRunsAfterAllPredicateAndAssignmentCallbacksAndBeforeCommit()
    {
        var events = new List<string>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark_predicate",
            1,
            values =>
            {
                events.Add($"predicate:{values[0].AsInteger()}");
                return SqlValue.Integer(1);
            });
        database.RegisterScalarFunction(
            "mark_assignment",
            1,
            values =>
            {
                events.Add($"assignment:{values[0].AsInteger()}");
                return values[0];
            });
        using var connection = database.Connect();
        ExecuteManaged(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        ExecuteManaged(
            connection,
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        Opcodes(ReadManaged(
                connection,
                """
                EXPLAIN UPDATE t SET value = mark_assignment(id)
                WHERE mark_predicate(id)
                RETURNING abs(-9223372036854775808);
                """))
            .Should().Contain("Function");

        using (var statement = connection.Prepare(
                   """
                   UPDATE t SET value = mark_assignment(id)
                   WHERE mark_predicate(id)
                   RETURNING abs(-9223372036854775808);
                   """))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("integer overflow");
        }

        events.Should().Equal(
            "predicate:1", "assignment:1",
            "predicate:2", "assignment:2",
            "predicate:3", "assignment:3");
        AssertRowsEqual(ReadManaged(connection, "SELECT id, value FROM t;"),
        [
            [SqlValue.Integer(1), SqlValue.Integer(10)],
            [SqlValue.Integer(2), SqlValue.Integer(20)],
            [SqlValue.Integer(3), SqlValue.Integer(30)],
        ]);
    }

    [Test]
    public void CancelableDmlRetainsEvaluatorCancellationAndAtomicity()
    {
        using var cancellation = new CancellationTokenSource();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_now",
            1,
            values =>
            {
                cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();
        ExecuteManaged(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        ExecuteManaged(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        using (var statement = connection.Prepare(
                   "UPDATE t SET value = value + 1 WHERE cancel_now(id) RETURNING upper(value);"))
        {
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        }

        AssertRowsEqual(ReadManaged(connection, "SELECT id, value FROM t;"),
        [
            [SqlValue.Integer(1), SqlValue.Integer(10)],
            [SqlValue.Integer(2), SqlValue.Integer(20)],
        ]);
    }

    [Test]
    public void ObservableReturningFamiliesRemainEvaluatorOwnedWhilePureExpressionsCompile()
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("upper", 1, values => values[0]);
        using var connection = database.Connect();
        ExecuteManaged(connection, "CREATE TABLE t(value TEXT);");

        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES ('x') RETURNING upper(value);");
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES ('x') RETURNING random();");
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES ('x') RETURNING (SELECT value);");

        Opcodes(Explain(
                connection,
                "EXPLAIN INSERT INTO t VALUES ('x') RETURNING value || '!', value = 'x', CAST(value AS TEXT);"))
            .Should().Contain(["Function", "Compare", "Cast"]);
    }

    private static void ExplainRefused(EmbeddedConnection connection, string sql)
        => Assert.Throws<EmbeddedSqlException>(() => ReadManaged(connection, sql))!
            .Message.Should().Contain("EXPLAIN is only supported");

    private static List<SqlValue[]> Explain(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);
        return DrainManaged(statement);
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static void ExecuteManagedBatch(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
            ExecuteManaged(connection, statement);
    }

    private static void ExecuteManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return DrainManaged(statement);
    }

    private static List<SqlValue[]> DrainManaged(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(ReadCurrentRow(statement));
        return rows;
    }

    private static SqlValue[] ReadCurrentRow(EmbeddedStatement statement)
    {
        var row = new SqlValue[statement.GetColumnCount()];
        for (var index = 0; index < row.Length; index++)
            row[index] = statement.GetValue(index);
        return row;
    }

    private static void ExecuteSqliteBatch(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<SqlValue[]> ReadSqlite(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = ToSqlValue(reader.GetValue(index));
            rows.Add(row);
        }
        return rows;
    }

    private static SqlValue ToSqlValue(object value)
        => value switch
        {
            DBNull => SqlValue.Null,
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            byte[] blob => SqlValue.Blob(blob),
            _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().Name}."),
        };

    private static void AssertRowsEqual(
        IReadOnlyList<SqlValue[]> actual,
        IReadOnlyList<SqlValue[]> expected,
        string because = "")
    {
        actual.Should().HaveCount(expected.Count, because);
        for (var index = 0; index < actual.Count; index++)
            actual[index].Should().Equal(expected[index], because);
    }
}
