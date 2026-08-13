using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public class CompiledCompoundRecursiveDifferentialTests
{
    [TestCase("SELECT 1 + 1 AS x UNION ALL VALUES (3), (4)", "Arithmetic")]
    [TestCase("SELECT 2 AS x INTERSECT VALUES (1 + 1)", "Arithmetic")]
    [TestCase("SELECT * FROM (SELECT 1 AS x UNION SELECT 2) INTERSECT VALUES (2)", "CompoundResultRow")]
    [TestCase("SELECT * FROM (SELECT 1 AS x INTERSECT SELECT 1) EXCEPT VALUES (2)", "GuardedRow")]
    [TestCase("SELECT * FROM (SELECT 1 AS x EXCEPT SELECT 2) UNION ALL VALUES (3)", "CompoundResultRow")]
    [TestCase("VALUES (1), (2), (2) EXCEPT SELECT 2", "RowSetRewind")]
    public void SafeCompoundShapesMatchSqliteAndRoute(string query, string routedOpcode)
    {
        using var connection = new EmbeddedDatabase().Connect();

        AssertMatchesSqlite([], query);
        ExplainOpcodes(connection, query).Should().Contain(routedOpcode);
        QueryPlanDetail(connection, query).Should().Be("MANAGED COMPILED VDBE");
    }

    [Test]
    public void CompoundCollationMetadataAndDeduplicationMatchSqlite()
    {
        const string query = "SELECT 'X' COLLATE NOCASE AS value INTERSECT VALUES ('x')";
        using var connection = new EmbeddedDatabase().Connect();

        var output = AssertMatchesSqlite([], query);

        output.Columns.Should().Equal("value");
        output.Rows.Should().ContainSingle();
        output.Rows[0][0].Should().Be(SqlValue.Text("X"));
        ExplainOpcodes(connection, query).Should().Contain("CompoundResultRow");
    }

    [Test]
    public void CompoundParametersResetAndRebindWithoutChangingRouting()
    {
        const string query =
            "SELECT * FROM (VALUES (?1) EXCEPT VALUES (?2)) UNION ALL VALUES (?3)";
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(query);

        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(2));
        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        AssertMatchesSqlite([], query, SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(2));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(4));
        statement.Bind(2, SqlValue.Integer(4));
        statement.Bind(3, SqlValue.Integer(5));
        Drain(statement).Should().Equal(SqlValue.Integer(5));
        AssertMatchesSqlite([], query, SqlValue.Integer(4), SqlValue.Integer(4), SqlValue.Integer(5));

        using var explain = connection.Prepare("EXPLAIN " + query);
        explain.Bind(1, SqlValue.Null);
        explain.Bind(2, SqlValue.Null);
        explain.Bind(3, SqlValue.Null);
        ReadRows(explain).Select(row => row[1].AsText())
            .Should().Contain("LoadParameter").And.Contain("RowSetRewind");
    }

    [Test]
    public void ErrorCapableSetTermsKeepEvaluatorSourceOrder()
    {
        string[] setup =
        [
            "CREATE TABLE first_error(value)",
            "INSERT INTO first_error VALUES (-9223372036854775808)",
            "CREATE TABLE second_error(value)",
            "INSERT INTO second_error VALUES ('not-json')",
        ];
        const string query =
            "SELECT abs(value) FROM first_error INTERSECT "
            + "SELECT json_extract(value, '$') FROM second_error";

        var managed = CaptureManagedError(setup, query);
        var sqlite = CaptureSqliteError(setup, query);

        managed.RowsBeforeError.Should().Be(sqlite.RowsBeforeError).And.Be(0);
        managed.Message.Should().Contain("integer overflow");
        sqlite.Message.Should().Contain("integer overflow");

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        QueryPlanDetail(connection, query).Should().Be("MANAGED EVALUATOR FALLBACK");
        Assert.Throws<EmbeddedSqlException>(() => ExplainOpcodes(connection, query));
    }

    [Test]
    public void ExplicitNullOrderingMatchesSqliteAndReportsFallback()
    {
        const string query =
            "VALUES (NULL), (2), (1) UNION ALL SELECT NULL ORDER BY 1 NULLS LAST";
        using var connection = new EmbeddedDatabase().Connect();

        var output = AssertMatchesSqlite([], query);

        output.Rows.Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Null,
            SqlValue.Null);
        QueryPlanDetail(connection, query).Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    [Test]
    public void CancellableCompoundExecutionReportsEvaluatorFallback()
    {
        const string query = "SELECT 1 + 1 UNION ALL VALUES (3)";
        using var connection = new EmbeddedDatabase().Connect();
        using var cancellation = new CancellationTokenSource();

        QueryPlanDetail(connection, query).Should().Be("MANAGED COMPILED VDBE");
        QueryPlanDetail(connection, query, cancellation.Token)
            .Should().Be("MANAGED EVALUATOR FALLBACK");

        using var statement = connection.Prepare(query);
        ReadRows(statement, cancellation.Token).Should().HaveCount(2);
    }

    [Test]
    public void JoinedAndDistinctRecursiveTermsMatchSqliteAndRoute()
    {
        string[] setup =
        [
            "CREATE TABLE edges(src INTEGER, dst INTEGER)",
            "INSERT INTO edges VALUES (1, 2), (1, 3), (2, 4), (3, 4), (4, 1)",
        ];
        const string joined =
            "WITH RECURSIVE reach(n) AS ("
            + "VALUES (1) UNION SELECT DISTINCT dst FROM edges JOIN reach ON src = n"
            + ") SELECT * FROM reach";

        var output = AssertMatchesSqlite(setup, joined);
        output.Rows.Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3),
            SqlValue.Integer(4));

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        ExplainOpcodes(connection, joined).Should().Contain("WorkTableExpandGeneration");
        QueryPlanDetail(connection, joined).Should().Be("MANAGED COMPILED VDBE");
    }

    [Test]
    public void RecursiveParametersResetRebindAndTerminateLikeSqlite()
    {
        const string query =
            "WITH RECURSIVE c(x) AS ("
            + "VALUES (?1) UNION SELECT x + 1 FROM c WHERE x < ?2"
            + ") SELECT * FROM c";
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(query);

        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(3));
        Drain(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3));
        AssertMatchesSqlite([], query, SqlValue.Integer(1), SqlValue.Integer(3));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Integer(9));
        Drain(statement).Should().Equal(
            SqlValue.Integer(7),
            SqlValue.Integer(8),
            SqlValue.Integer(9));
        AssertMatchesSqlite([], query, SqlValue.Integer(7), SqlValue.Integer(9));
    }

    [Test]
    public void RecursiveCallbackAndCancellationShapesStayOnEvaluator()
    {
        const string query =
            "WITH RECURSIVE c(x) AS ("
            + "VALUES (1) UNION ALL SELECT cancel_next(x + 1) FROM c WHERE x < 4"
            + ") SELECT * FROM c";
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_next",
            1,
            values =>
            {
                calls++;
                cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();

        QueryPlanDetail(connection, query).Should().Be("MANAGED EVALUATOR FALLBACK");
        Assert.Throws<OperationCanceledException>(
            () => ReadRows(connection.Prepare(query), cancellation.Token));
        calls.Should().Be(1);
    }

    [Test]
    public void CancellableRecursivePlanTruthfullyReportsFallback()
    {
        const string query =
            "WITH RECURSIVE c(x) AS (VALUES (1) UNION SELECT x + 1 FROM c WHERE x < 3) "
            + "SELECT * FROM c";
        using var connection = new EmbeddedDatabase().Connect();
        using var cancellation = new CancellationTokenSource();

        QueryPlanDetail(connection, query).Should().Be("MANAGED COMPILED VDBE");
        QueryPlanDetail(connection, query, cancellation.Token)
            .Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    [Test]
    public void RegisterPredicatesCaseCastsAndBoundedScansMatchSqliteAfterRebinding()
    {
        string[] setup =
        [
            "CREATE TABLE items(value INTEGER)",
            "INSERT INTO items VALUES (NULL), (-1), (0), (2), (3)",
        ];
        const string controlFlowQuery =
            "SELECT CASE WHEN ?1 THEN CAST(?2 AS TEXT) ELSE CAST(?3 AS TEXT) END AS result";
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        using var statement = connection.Prepare(controlFlowQuery);

        statement.Bind(1, SqlValue.Integer(0));
        statement.Bind(2, SqlValue.Null);
        statement.Bind(3, SqlValue.Text("fallback"));
        Drain(statement).Should().Equal(SqlValue.Text("fallback"));
        AssertMatchesSqlite(
            [],
            controlFlowQuery,
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Text("fallback"));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(7));
        statement.Bind(3, SqlValue.Null);
        Drain(statement).Should().Equal(SqlValue.Text("7"));
        AssertMatchesSqlite(
            [],
            controlFlowQuery,
            SqlValue.Integer(1),
            SqlValue.Integer(7),
            SqlValue.Null);

        QueryPlanDetail(
                connection,
                controlFlowQuery,
                [
                    SqlValue.Integer(1),
                    SqlValue.Integer(7),
                    SqlValue.Null,
                ])
            .Should().Be("MANAGED COMPILED VDBE");
        ExplainOpcodes(
                connection,
                controlFlowQuery,
                [
                    SqlValue.Integer(1),
                    SqlValue.Integer(7),
                    SqlValue.Null,
                ])
            .Should()
                .Contain("JumpIfNotTrue").And.Contain("Cast")
            .And.Contain("ResultRow");

        const string predicateQuery = "SELECT value FROM items WHERE value >= ?1";
        AssertMatchesSqlite(setup, predicateQuery, SqlValue.Integer(0)).Rows
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(0), SqlValue.Integer(2), SqlValue.Integer(3));
        QueryPlanDetail(connection, predicateQuery, [SqlValue.Integer(0)])
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        ExplainOpcodes(connection, predicateQuery, [SqlValue.Integer(0)])
            .Should().Contain("Compare").And.Contain("JumpIfNotTrue");

        const string boundedQuery = "SELECT value FROM items WHERE value >= ?1 LIMIT ?2 OFFSET ?3";
        using var bounded = connection.Prepare(boundedQuery);
        bounded.Bind(1, SqlValue.Integer(0));
        bounded.Bind(2, SqlValue.Integer(2));
        bounded.Bind(3, SqlValue.Integer(1));
        Drain(bounded).Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
        AssertMatchesSqlite(
            setup,
            boundedQuery,
            SqlValue.Integer(0),
            SqlValue.Integer(2),
            SqlValue.Integer(1));
        QueryPlanDetail(
                connection,
                boundedQuery,
                [SqlValue.Integer(0), SqlValue.Integer(2), SqlValue.Integer(1)])
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        ExplainOpcodes(
                connection,
                boundedQuery,
                [SqlValue.Integer(0), SqlValue.Integer(2), SqlValue.Integer(1)])
            .Should().Contain("LimitGate").And.Contain("OffsetGate");
    }

    [Test]
    public void EmptyAndNullPredicateScansMatchSqliteAndReportTheirActualRoutes()
    {
        const string predicate = "SELECT value FROM items WHERE value > ?1";
        const string empty = "SELECT value FROM empty_items WHERE value IS NULL LIMIT 1 OFFSET 0";
        string[] setup =
        [
            "CREATE TABLE items(value INTEGER)",
            "INSERT INTO items VALUES (NULL), (1), (2)",
            "CREATE TABLE empty_items(value INTEGER)",
        ];
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

        AssertMatchesSqlite(setup, predicate, SqlValue.Null).Rows.Should().BeEmpty();
        AssertMatchesSqlite(setup, empty).Rows.Should().BeEmpty();
        QueryPlanDetail(connection, predicate, [SqlValue.Null])
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        QueryPlanDetail(connection, empty).Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    [Test]
    public void SafeTypeofUnionAllMatchesSqliteAfterRebinding()
    {
        const string query = "SELECT typeof(?1) AS kind UNION ALL SELECT typeof(?2)";
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(query);

        statement.Bind(1, SqlValue.Null);
        statement.Bind(2, SqlValue.Blob([1, 2]));
        Drain(statement).Should().Equal(SqlValue.Text("null"), SqlValue.Text("blob"));
        AssertMatchesSqlite([], query, SqlValue.Null, SqlValue.Blob([1, 2]));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Real(2.5));
        Drain(statement).Should().Equal(SqlValue.Text("integer"), SqlValue.Text("real"));
        AssertMatchesSqlite([], query, SqlValue.Integer(7), SqlValue.Real(2.5));

        QueryPlanDetail(
                connection,
                query,
                [SqlValue.Integer(7), SqlValue.Real(2.5)])
            .Should().Be("MANAGED COMPILED VDBE");
        ExplainOpcodes(
                connection,
                query,
                [SqlValue.Integer(7), SqlValue.Real(2.5)])
            .Should().Contain("Function").And.Contain("LoadParameter");
    }

    [TestCase("tracking", false)]
    [TestCase("NOCASE", true)]
    public void CustomAndOverriddenCollationsMatchSqliteWhileStayingEvaluatorOwned(
        string collation,
        bool overridesBuiltin)
    {
        Func<string, string, int> comparer = overridesBuiltin
            ? static (string _, string _) => 0
            : static (string left, string right) =>
                string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        var database = new EmbeddedDatabase();
        database.RegisterCollation(collation, comparer);
        using var managed = database.Connect();
        Execute(managed, $"CREATE TABLE names(value TEXT COLLATE {collation});");
        Execute(managed, "INSERT INTO names VALUES ('Ada'), ('ada'), ('Grace');");
        const string query = "SELECT value FROM names WHERE value = ?1";
        using var statement = managed.Prepare(query);
        statement.Bind(1, SqlValue.Text("ADA"));
        var managedRows = ReadRows(statement);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        sqlite.CreateCollation(collation, (left, right) => comparer(left, right));
        using (var setup = sqlite.CreateCommand())
        {
            setup.CommandText = $"CREATE TABLE names(value TEXT COLLATE {collation});"
                + "INSERT INTO names VALUES ('Ada'), ('ada'), ('Grace');";
            setup.ExecuteNonQuery();
        }

        using var command = sqlite.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("?1", "ADA");
        using var reader = command.ExecuteReader();
        var sqliteRows = new List<SqlValue[]>();
        while (reader.Read())
            sqliteRows.Add([FromClrValue(reader.GetValue(0))]);

        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var index = 0; index < managedRows.Count; index++)
            managedRows[index].Should().Equal(sqliteRows[index]);
        QueryPlanDetail(managed, query, [SqlValue.Text("ADA")])
            .Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    [Test]
    public void CallbackAndCancellationCandidatesReportEvaluatorFallbackWithoutEagerExecution()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "observe",
            1,
            values =>
            {
                calls++;
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(value INTEGER);");
        Execute(connection, "INSERT INTO items VALUES (1), (2);");
        const string callbackQuery = "SELECT value FROM items WHERE observe(value) > 0";
        const string sorterQuery = "SELECT value FROM items ORDER BY value DESC LIMIT 1";
        using var cancellation = new CancellationTokenSource();

        QueryPlanDetail(connection, callbackQuery).Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);
        QueryPlanDetail(connection, sorterQuery).Should().Be("MANAGED COMPILED VDBE");
        QueryPlanDetail(connection, sorterQuery, cancellation.Token)
            .Should().Be("MANAGED EVALUATOR FALLBACK");

        cancellation.Cancel();
        using var statement = connection.Prepare(sorterQuery);
        Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        statement.Reset();
        Drain(statement).Should().Equal(SqlValue.Integer(2));
    }

    private static QueryOutput AssertMatchesSqlite(
        IReadOnlyList<string> setup,
        string query,
        params SqlValue[] parameters)
    {
        var managed = RunManaged(setup, query, parameters);
        var sqlite = RunSqlite(setup, query, parameters);

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var index = 0; index < managed.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index]);
        return managed;
    }

    private static QueryOutput RunManaged(
        IReadOnlyList<string> setup,
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        using var statement = connection.Prepare(query);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        return new QueryOutput(columns, ReadRows(statement));
    }

    private static QueryOutput RunSqlite(
        IReadOnlyList<string> setup,
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        for (var index = 0; index < parameters.Count; index++)
            command.Parameters.AddWithValue($"?{index + 1}", ToClrValue(parameters[index]));
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? SqlValue.Null : FromClrValue(reader.GetValue(index));
            rows.Add(row);
        }

        return new QueryOutput(columns, rows);
    }

    private static ErrorOutput CaptureManagedError(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        using var statement = connection.Prepare(query);
        var rows = 0;
        var error = Assert.Throws<EmbeddedSqlException>(() =>
        {
            while (statement.Step() == StatementStepResult.Row)
                rows++;
        });
        return new ErrorOutput(rows, error!.Message);
    }

    private static ErrorOutput CaptureSqliteError(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
            setupCommand.ExecuteNonQuery();
        }

        var rows = 0;
        var error = Assert.Throws<MsData.SqliteException>(() =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows++;
        });
        return new ErrorOutput(rows, error!.Message);
    }

    private static IReadOnlyList<string> ExplainOpcodes(EmbeddedConnection connection, string query)
    {
        using var statement = connection.Prepare("EXPLAIN " + query);
        return ReadRows(statement).Select(row => row[1].AsText()).ToArray();
    }

    private static IReadOnlyList<string> ExplainOpcodes(
        EmbeddedConnection connection,
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var statement = connection.Prepare("EXPLAIN " + query);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);
        return ReadRows(statement).Select(row => row[1].AsText()).ToArray();
    }

    private static string QueryPlanDetail(
        EmbeddedConnection connection,
        string query,
        CancellationToken cancellationToken = default)
    {
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN " + query);
        statement.Step(cancellationToken).Should().Be(StatementStepResult.Row);
        return statement.GetValue(3).AsText();
    }

    private static string QueryPlanDetail(
        EmbeddedConnection connection,
        string query,
        IReadOnlyList<SqlValue> parameters)
    {
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN " + query);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(3).AsText();
    }

    private static List<SqlValue> Drain(EmbeddedStatement statement)
    {
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0));
        return values;
    }

    private static List<SqlValue[]> ReadRows(
        EmbeddedStatement statement,
        CancellationToken cancellationToken = default)
    {
        using (statement)
        {
            var rows = new List<SqlValue[]>();
            while (statement.Step(cancellationToken) == StatementStepResult.Row)
            {
                var row = new SqlValue[statement.GetColumnCount()];
                for (var index = 0; index < row.Length; index++)
                    row[index] = statement.GetValue(index);
                rows.Add(row);
            }

            return rows;
        }
    }

    private static List<SqlValue[]> ReadRows(EmbeddedStatement statement)
        => ReadRows(statement, default);

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static object ToClrValue(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => DBNull.Value,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => value.AsBlob().ToArray(),
            _ => throw new InvalidOperationException($"Unsupported SQL value kind {value.Kind}."),
        };

    private static SqlValue FromClrValue(object value)
        => value switch
        {
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            byte[] blob => SqlValue.Blob(blob),
            _ => throw new InvalidOperationException(
                $"Unsupported Microsoft.Data.Sqlite value type {value.GetType().Name}."),
        };

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<SqlValue[]> Rows);

    private sealed record ErrorOutput(int RowsBeforeError, string Message);
}
