using AwesomeAssertions;
using ManagedSqlite = Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class CteMaterializationHintTests
{
    [Test]
    public void HintsMatchSqliteAcrossQueryShapes()
    {
        string[] setup =
        [
            "CREATE TABLE left_items(id INTEGER, label TEXT)",
            "INSERT INTO left_items VALUES (1, 'one'), (2, 'two'), (3, 'three')",
            "CREATE TABLE right_items(id INTEGER, score INTEGER)",
            "INSERT INTO right_items VALUES (1, 10), (2, 20), (4, 40)",
        ];
        string[] queries =
        [
            """
            WITH joined(id, label, score) AS MATERIALIZED (
                SELECT l.id, l.label, r.score
                FROM left_items AS l JOIN right_items AS r ON r.id = l.id
            )
            SELECT * FROM joined ORDER BY id;
            """,
            """
            WITH ranked AS NOT MATERIALIZED (
                SELECT id, row_number() OVER (ORDER BY id) AS ordinal
                FROM left_items
            )
            SELECT * FROM ranked ORDER BY id;
            """,
            """
            WITH combined(value) AS MATERIALIZED (
                SELECT id FROM left_items
                UNION ALL
                SELECT id + 10 FROM right_items
            )
            SELECT * FROM combined ORDER BY value;
            """,
            """
            WITH first(value) AS NOT MATERIALIZED (SELECT id FROM left_items),
                 second(value) AS MATERIALIZED (SELECT value + 10 FROM first)
            SELECT * FROM second ORDER BY value;
            """,
            """
            WITH outer_cte AS NOT MATERIALIZED (
                WITH inner_cte(id) AS MATERIALIZED (SELECT id FROM left_items)
                SELECT id + 1 AS id FROM inner_cte
            )
            SELECT * FROM outer_cte ORDER BY id;
            """,
            """
            WITH RECURSIVE sequence(value) AS MATERIALIZED (
                VALUES (1)
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < 4
            )
            SELECT * FROM sequence;
            """,
            """
            WITH RECURSIVE sequence(value) AS NOT MATERIALIZED (
                VALUES (1)
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < 4
            )
            SELECT * FROM sequence;
            """,
        ];

        foreach (var query in queries)
            AssertMatchesSqlite(setup, query);
    }

    [Test]
    public void HintsMatchSqliteForCtasAndTemporaryTables()
    {
        string[] setup =
        [
            "CREATE TABLE source(id INTEGER, label TEXT)",
            "INSERT INTO source VALUES (1, 'one'), (2, 'two'), (3, 'three')",
            """
            CREATE TABLE copied AS
            WITH selected AS MATERIALIZED (
                SELECT id, label FROM source WHERE id <= 2
            )
            SELECT * FROM selected
            """,
            """
            CREATE TEMP TABLE temp_copy AS
            WITH selected AS NOT MATERIALIZED (
                SELECT id, label FROM source WHERE id >= 2
            )
            SELECT * FROM selected
            """,
        ];

        AssertMatchesSqlite(setup, "SELECT id, label FROM copied ORDER BY id;");
        AssertMatchesSqlite(setup, "SELECT id, label FROM temp_copy ORDER BY id;");
    }

    [Test]
    public void HintedCteDmlReturningMatchesSqliteAndPersistsState()
    {
        string[] setup =
        [
            "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER)",
            "INSERT INTO target VALUES (1, 10), (2, 20), (3, 30)",
        ];

        AssertDmlMatchesSqlite(
            setup,
            """
            WITH source(id, value) AS NOT MATERIALIZED (VALUES (4, 40))
            INSERT INTO target SELECT id, value FROM source
            RETURNING id, value;
            """,
            "SELECT id, value FROM target ORDER BY id;");
        AssertDmlMatchesSqlite(
            setup,
            """
            WITH selected(id) AS MATERIALIZED (SELECT id FROM target WHERE id = 2)
            UPDATE target
            SET value = value + 5
            WHERE id IN (SELECT id FROM selected)
            RETURNING id, value;
            """,
            "SELECT id, value FROM target ORDER BY id;");
        AssertDmlMatchesSqlite(
            setup,
            """
            WITH doomed(id) AS NOT MATERIALIZED (SELECT id FROM target WHERE id = 3)
            DELETE FROM target
            WHERE id IN (SELECT id FROM doomed)
            RETURNING id, value;
            """,
            "SELECT id, value FROM target ORDER BY id;");
    }

    [Test]
    public void CtePrefixedReplaceMatchesSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER)",
            "INSERT INTO target VALUES (1, 10), (2, 20)",
        ];

        AssertDmlMatchesSqlite(
            setup,
            """
            WITH source(id, value) AS (VALUES (2, 200), (3, 30))
            REPLACE INTO target SELECT id, value FROM source
            RETURNING id, value;
            """,
            "SELECT id, value FROM target ORDER BY id;");
    }

    [Test]
    public void UnreferencedCtesDeferColumnValidationForQueriesAndDml()
    {
        string[] setup =
        [
            "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER)",
            "INSERT INTO target VALUES (1, 10)",
        ];

        AssertMatchesSqlite(
            setup,
            "WITH unused(value) AS (VALUES (2, 4)) SELECT 42;");
        AssertDmlMatchesSqlite(
            setup,
            """
            WITH unused(value) AS (VALUES (2, 4))
            INSERT INTO target VALUES (2, 20)
            RETURNING id, value;
            """,
            "SELECT id, value FROM target ORDER BY id;");
    }

    [Test]
    public void ManagedSqliteFacadeClassifiesHintedCteDmlAndReturningAsWrites()
    {
        using var connection = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE target(id INTEGER PRIMARY KEY, value INTEGER);"
                + "INSERT INTO target VALUES (1, 10);";
            setup.ExecuteNonQuery().Should().Be(1);
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                "WITH selected(id) AS NOT MATERIALIZED (SELECT 1) "
                + "UPDATE target SET value = value + 5 "
                + "WHERE id IN (SELECT id FROM selected) RETURNING id, value;";
            update.ExecuteNonQuery().Should().Be(1);
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM target WHERE id = 1;";
        read.ExecuteScalar().Should().Be(15L);
    }

    [Test]
    public void ManagedHintsKeepOneShotCallbackOrderForUnsafeInliningShapes()
    {
        var calls = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe", 1, values =>
        {
            var value = values[0].AsInteger();
            calls.Add(value);
            return values[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE input(value INTEGER);");
        Execute(connection, "INSERT INTO input VALUES (-1), (1), (2);");

        foreach (var hint in new[] { string.Empty, "MATERIALIZED ", "NOT MATERIALIZED " })
        {
            calls.Clear();
            ReadManaged(
                    connection,
                    $"""
                    WITH observed(value) AS {hint}(SELECT observe(value) FROM input)
                    SELECT value FROM observed WHERE value > 0 ORDER BY value;
                    """)
                .Rows.Select(row => row[0])
                .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
            calls.Should().Equal(-1, 1, 2);
        }

        calls.Clear();
        ReadManaged(
                connection,
                """
                WITH observed(value) AS NOT MATERIALIZED (SELECT observe(value) FROM input)
                SELECT * FROM observed;
                """)
            .Rows.Select(row => row[0])
            .Should().Equal(SqlValue.Integer(-1), SqlValue.Integer(1), SqlValue.Integer(2));
        calls.Should().Equal(-1, 1, 2);

        calls.Clear();
        ReadManaged(
                connection,
                """
                WITH observed(value) AS NOT MATERIALIZED (SELECT observe(value) FROM input),
                     copied(value) AS MATERIALIZED (SELECT value FROM observed)
                SELECT a.value, b.value
                FROM copied AS a JOIN copied AS b ON a.value = b.value
                ORDER BY a.value;
                """)
            .Rows.Should().HaveCount(3);
        calls.Should().Equal(-1, 1, 2);

        calls.Clear();
        ReadManaged(
                connection,
                """
                WITH first(value) AS NOT MATERIALIZED (SELECT observe(10)),
                     second(value) AS MATERIALIZED (SELECT observe(20))
                SELECT first.value, second.value FROM first JOIN second;
                """)
            .Rows.Should().ContainSingle();
        calls.Should().Equal(10, 20);

        ReadManaged(
                connection,
                """
                WITH generated(value) AS NOT MATERIALIZED (SELECT uuid4_str())
                SELECT a.value = b.value FROM generated AS a JOIN generated AS b;
                """)
            .Rows.Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
    }

    [TestCase("MATERIALIZED")]
    [TestCase("NOT MATERIALIZED")]
    public void HintedParametersResetAndRebind(string hint)
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(
            $"WITH selected(value) AS {hint} (SELECT ?1) SELECT * FROM selected;");

        statement.Bind(1, SqlValue.Integer(3));
        ReadRows(statement).Select(row => row[0]).Should().Equal(SqlValue.Integer(3));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(7));
        ReadRows(statement).Select(row => row[0]).Should().Equal(SqlValue.Integer(7));
    }

    [Test]
    public void HintedCallbackErrorsKeepOrderAndStatementReusability()
    {
        var calls = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("fail_if_negative", 1, values =>
        {
            var value = values[0].AsInteger();
            calls.Add(value);
            if (value < 0)
                throw new EmbeddedSqlException("cte callback failure");
            return values[0];
        });
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            """
            WITH first(value) AS NOT MATERIALIZED (SELECT fail_if_negative(?1)),
                 second(value) AS MATERIALIZED (SELECT fail_if_negative(?2))
            SELECT first.value, second.value FROM first JOIN second;
            """);
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(-2));

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("cte callback failure");
        calls.Should().Equal(1, -2);

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(3));
        statement.Bind(2, SqlValue.Integer(4));
        ReadRows(statement).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
        calls.Should().Equal(1, -2, 3, 4);
    }

    [Test]
    public void CancellationStopsHintedCteAndLeavesStatementReusable()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe", 1, values =>
        {
            calls++;
            return values[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE input(value INTEGER);");
        Execute(connection, "INSERT INTO input VALUES (1), (2), (3);");
        using var statement = connection.Prepare(
            """
            WITH observed(value) AS NOT MATERIALIZED (
                SELECT observe(value) FROM input
            )
            SELECT * FROM observed;
            """);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        calls.Should().Be(0);

        statement.Reset();
        ReadRows(statement).Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3));
        calls.Should().Be(3);
    }

    [Test]
    public void ExplainReportsOnlyProvenNotMaterializedPassThroughPrograms()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe", 1, values =>
        {
            calls++;
            return values[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE left_items(id INTEGER);");
        Execute(connection, "CREATE TABLE right_items(id INTEGER);");
        Execute(connection, "INSERT INTO left_items VALUES (1), (2);");
        Execute(connection, "INSERT INTO right_items VALUES (1), (3);");

        const string values =
            "WITH c(value) AS NOT MATERIALIZED (VALUES (1), (2)) SELECT * FROM c;";
        ReadPlanDetail(connection, values).Should().Be("MANAGED COMPILED VDBE");
        ReadOpcodes(connection, values).Should().Contain("LoadConstant");

        const string compound =
            "WITH c(value) AS NOT MATERIALIZED "
            + "(SELECT 1 UNION ALL VALUES (2)) SELECT * FROM c;";
        ReadPlanDetail(connection, compound).Should().Be("MANAGED COMPILED VDBE");
        ReadOpcodes(connection, compound).Should().Contain("ResultRow");

        const string join =
            "WITH c AS NOT MATERIALIZED "
            + "(SELECT l.id FROM left_items AS l JOIN right_items AS r ON r.id = l.id) "
            + "SELECT * FROM c;";
        ReadPlanDetail(connection, join).Should().Be("MANAGED COMPILED VDBE");
        ReadOpcodes(connection, join).Should().Contain("OpenReadCursor");

        const string window =
            "WITH c AS NOT MATERIALIZED "
            + "(SELECT id, sum(id) OVER "
            + "(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running "
            + "FROM left_items ORDER BY id) SELECT * FROM c;";
        ReadPlanDetail(connection, window).Should().Be("MANAGED COMPILED VDBE");
        ReadOpcodes(connection, window).Should().Contain("AggStep");

        const string materialized =
            "WITH c(value) AS MATERIALIZED (VALUES (1), (2)) SELECT * FROM c;";
        ReadPlanDetail(connection, materialized).Should().Be("MANAGED EVALUATOR FALLBACK");
        Assert.Throws<EmbeddedSqlException>(() => ReadOpcodes(connection, materialized));

        const string unspecified =
            "WITH c(value) AS (VALUES (1), (2)) SELECT * FROM c;";
        ReadPlanDetail(connection, unspecified).Should().Be("MANAGED EVALUATOR FALLBACK");

        const string multipleReferences =
            "WITH c(value) AS NOT MATERIALIZED (SELECT observe(1)) "
            + "SELECT a.value, b.value FROM c AS a JOIN c AS b;";
        ReadPlanDetail(connection, multipleReferences).Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);

        const string callback =
            "WITH c(value) AS NOT MATERIALIZED (SELECT observe(1)) SELECT * FROM c;";
        ReadPlanDetail(connection, callback).Should().Be("MANAGED EVALUATOR FALLBACK");
        Assert.Throws<EmbeddedSqlException>(() => ReadOpcodes(connection, callback));
        calls.Should().Be(0);

        foreach (var hint in new[] { "MATERIALIZED", "NOT MATERIALIZED" })
        {
            var recursive =
                $"WITH RECURSIVE c(value) AS {hint} "
                + "(VALUES (1) UNION ALL SELECT value + 1 FROM c WHERE value < 3) "
                + "SELECT * FROM c;";
            ReadPlanDetail(connection, recursive).Should().Be("MANAGED COMPILED VDBE");
            ReadOpcodes(connection, recursive).Should().Contain("OpenWorkTable");
        }

        using var cancellable = new CancellationTokenSource();
        ReadPlanDetail(connection, values, cancellable.Token)
            .Should().Be("MANAGED EVALUATOR FALLBACK");
    }

    [Test]
    public void ParserRejectsIncompleteNotMaterializedHint()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(
                () => connection.Prepare("WITH c AS NOT (SELECT 1) SELECT * FROM c;"))!
            .Message.Should().Contain("Expected keyword MATERIALIZED");
    }

    [Test]
    public void NotMaterializedPassThroughKeepsOuterNamedWindowValidation()
    {
        const string query =
            "WITH c AS NOT MATERIALIZED (SELECT 1 AS x) "
            + "SELECT * FROM c "
            + "WINDOW p AS (PARTITION BY x), q AS (p PARTITION BY x);";

        using (var connection = new EmbeddedDatabase().Connect())
        {
            using var statement = connection.Prepare(query);
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("cannot override PARTITION clause of window: p");
        }

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText = query;
        Assert.Throws<MsData.SqliteException>(() => command.ExecuteReader())!
            .Message.Should().Contain("cannot override PARTITION clause of window: p");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        AssertEquivalent(managed, sqlite);
    }

    private static void AssertDmlMatchesSqlite(
        IReadOnlyList<string> setup,
        string dml,
        string stateQuery)
    {
        var managed = RunManagedDml(setup, dml, stateQuery);
        var sqlite = RunSqliteDml(setup, dml, stateQuery);
        AssertEquivalent(managed.Returning, sqlite.Returning);
        AssertEquivalent(managed.State, sqlite.State);
    }

    private static QueryOutput RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return ReadManaged(connection, query);
    }

    private static DmlOutput RunManagedDml(
        IReadOnlyList<string> setup,
        string dml,
        string stateQuery)
    {
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return new DmlOutput(ReadManaged(connection, dml), ReadManaged(connection, stateQuery));
    }

    private static QueryOutput RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = OpenSqlite(setup);
        return ReadSqlite(connection, query);
    }

    private static DmlOutput RunSqliteDml(
        IReadOnlyList<string> setup,
        string dml,
        string stateQuery)
    {
        using var connection = OpenSqlite(setup);
        return new DmlOutput(ReadSqlite(connection, dml), ReadSqlite(connection, stateQuery));
    }

    private static MsData.SqliteConnection OpenSqlite(IReadOnlyList<string> setup)
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private static QueryOutput ReadManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        return new QueryOutput(columns, ReadRows(statement));
    }

    private static QueryOutput ReadSqlite(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<SqlValue[]>();
        while (reader.Read())
        {
            rows.Add(
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index)
                        ? SqlValue.Null
                        : ToSqlValue(reader.GetValue(index)))
                    .ToArray());
        }
        return new QueryOutput(columns, rows);
    }

    private static List<SqlValue[]> ReadRows(
        EmbeddedStatement statement,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step(cancellationToken) == StatementStepResult.Row)
        {
            rows.Add(
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(statement.GetValue)
                    .ToArray());
        }
        return rows;
    }

    private static IReadOnlyList<string> ReadOpcodes(EmbeddedConnection connection, string query)
    {
        using var statement = connection.Prepare("EXPLAIN " + query);
        return ReadRows(statement).Select(row => row[1].AsText()).ToArray();
    }

    private static string ReadPlanDetail(
        EmbeddedConnection connection,
        string query,
        CancellationToken cancellationToken = default)
    {
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN " + query);
        statement.Step(cancellationToken).Should().Be(StatementStepResult.Row);
        var detail = statement.GetValue(3).AsText();
        statement.Step(cancellationToken).Should().Be(StatementStepResult.Done);
        return detail;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void AssertEquivalent(QueryOutput managed, QueryOutput sqlite)
    {
        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var index = 0; index < managed.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index]);
    }

    private static SqlValue ToSqlValue(object value)
    {
        return value switch
        {
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            byte[] blob => SqlValue.Blob(blob),
            _ => throw new InvalidOperationException(
                $"Unsupported SQLite result type {value.GetType().FullName}."),
        };
    }

    private sealed record QueryOutput(string[] Columns, List<SqlValue[]> Rows);

    private sealed record DmlOutput(QueryOutput Returning, QueryOutput State);
}
