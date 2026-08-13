using AwesomeAssertions;
using ManagedSqlite = Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ExplainQueryPlanTests
{
    [Test]
    public void ReportsStableCompiledAndFallbackRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var compiled = ReadPlan(connection, "EXPLAIN QUERY PLAN SELECT 1;");
        compiled.Columns.Should().Equal("id", "parent", "notused", "detail");
        compiled.Rows.Should().ContainSingle()
            .Which.Should().Equal(
                SqlValue.Integer(0),
                SqlValue.Integer(0),
                SqlValue.Integer(0),
                SqlValue.Text("MANAGED COMPILED VDBE"));

        var fallback = ReadPlan(connection, "EXPLAIN QUERY PLAN SELECT DISTINCT 1;");
        fallback.Rows.Should().ContainSingle()
            .Which[3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void RebindingParametersReportsTheLateBoundRuntimeRouteWithoutRenderingValues()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN SELECT ?1 + 1;");

        statement.ParameterCount.Should().Be(1);
        statement.GetParameterName(1).Should().Be("?1");
        statement.Bind(1, SqlValue.Integer(3));
        ReadDetail(statement).Should().Be("MANAGED COMPILED VDBE");

        statement.Reset();
        statement.Bind(1, SqlValue.Text("3"));
        ReadDetail(statement).Should().Be("MANAGED COMPILED VDBE");

        using var unbound = connection.Prepare("EXPLAIN QUERY PLAN SELECT ?1 + 1;");
        ReadDetail(unbound).Should().Be("MANAGED COMPILED VDBE");
    }

    [Test]
    public void PlanningDmlDoesNotExecuteTheInnerStatement()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        ReadPlan(connection, "EXPLAIN QUERY PLAN INSERT INTO t VALUES (2);")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadScalar(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(1));

        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN INSERT INTO t VALUES (2) RETURNING abs(value), value + ?1;",
                SqlValue.Integer(4))
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadScalar(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(1));

        ReadPlan(connection, "EXPLAIN QUERY PLAN INSERT OR IGNORE INTO t VALUES (3);")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadScalar(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void ConstraintOwnedDmlReportsTheRuntimeEvaluatorRoute()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE constrained(value INTEGER UNIQUE ON CONFLICT IGNORE);");

        ReadPlan(connection, "EXPLAIN QUERY PLAN INSERT INTO constrained VALUES (1);")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadPlan(connection, "EXPLAIN QUERY PLAN UPDATE constrained SET value = 2;")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));

        using var insertExplain = connection.Prepare("EXPLAIN INSERT INTO constrained VALUES (1);");
        Assert.Throws<EmbeddedSqlException>(() => insertExplain.Step());
        using var updateExplain = connection.Prepare("EXPLAIN UPDATE constrained SET value = 2;");
        Assert.Throws<EmbeddedSqlException>(() => updateExplain.Step());
        ReadScalar(connection, "SELECT count(*) FROM constrained;").Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void FallbackPlanningDoesNotInvokeUserFunctions()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var calls = 0;
        connection.RegisterScalarFunction(
            "observe",
            1,
            values =>
            {
                calls++;
                return values[0];
            });

        using var statement = connection.Prepare("EXPLAIN QUERY PLAN SELECT observe(7);");
        ReadDetail(statement).Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);

        using var recursive = connection.Prepare(
            """
            EXPLAIN QUERY PLAN
            WITH RECURSIVE c(value) AS (
                SELECT observe(1)
                UNION ALL
                SELECT value + 1 FROM c WHERE value < 2
            )
            SELECT * FROM c;
            """);
        ReadDetail(recursive).Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);

        using var explain = connection.Prepare("EXPLAIN SELECT observe(7);");
        Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
            .Message.Should().Be(
                "EXPLAIN is only supported for statements lowered to the bytecode compiler.");
        calls.Should().Be(0);

        Execute(connection, "CREATE TABLE t(value INTEGER);");
        using var dmlPlan = connection.Prepare(
            "EXPLAIN QUERY PLAN INSERT INTO t VALUES (7) RETURNING observe(value);");
        ReadDetail(dmlPlan).Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);
        ReadScalar(connection, "SELECT count(*) FROM t;").Should().Be(SqlValue.Integer(0));

        ReadScalar(connection, "SELECT observe(7);").Should().Be(SqlValue.Integer(7));
        calls.Should().Be(1);
    }

    [Test]
    public void CancellationStopsBeforePlanningAndLeavesTheStatementReusable()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        using var statement = connection.Prepare(
            "EXPLAIN QUERY PLAN UPDATE t SET value = value + 1 RETURNING value;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        ReadDetail(statement).Should().Be("MANAGED COMPILED VDBE");
        ReadScalar(connection, "SELECT value FROM t;").Should().Be(SqlValue.Integer(1));

        using var cancelable = new CancellationTokenSource();
        using var cancelablePlan = connection.Prepare(
            "EXPLAIN QUERY PLAN UPDATE t SET value = value + 1 RETURNING value;");
        cancelablePlan.Step(cancelable.Token).Should().Be(StatementStepResult.Row);
        cancelablePlan.GetValue(3).Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        cancelablePlan.Step(cancelable.Token).Should().Be(StatementStepResult.Done);
        ReadScalar(connection, "SELECT value FROM t;").Should().Be(SqlValue.Integer(1));

        using var cancelableSelect = connection.Prepare("EXPLAIN QUERY PLAN SELECT value FROM t;");
        cancelableSelect.Step(cancelable.Token).Should().Be(StatementStepResult.Row);
        cancelableSelect.GetValue(3).Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        cancelableSelect.Step(cancelable.Token).Should().Be(StatementStepResult.Done);

        using var cancelableCompound = connection.Prepare(
            "EXPLAIN QUERY PLAN SELECT value FROM t UNION ALL SELECT value + 1 FROM t;");
        cancelableCompound.Step(cancelable.Token).Should().Be(StatementStepResult.Row);
        cancelableCompound.GetValue(3).Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        cancelableCompound.Step(cancelable.Token).Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void StreamingEvaluatorSelectsAreNeverDescribedAsCompiled()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        ReadPlan(connection, "EXPLAIN QUERY PLAN SELECT value FROM t;")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void StreamingCallbackProjectionKeepsItsFailureAtTheLaterRead()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        var calls = 0;
        connection.RegisterScalarFunction(
            "fail_on_two",
            1,
            values =>
            {
                calls++;
                return values[0].AsInteger() == 2
                    ? throw new EmbeddedSqlException("later row")
                    : values[0];
            });

        ReadPlan(connection, "EXPLAIN QUERY PLAN SELECT fail_on_two(value) FROM t;")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        calls.Should().Be(0);

        using var statement = connection.Prepare("SELECT fail_on_two(value) FROM t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("later row");
        calls.Should().Be(2);
    }

    [Test]
    public void RejectsStatementsWithoutAQueryPlanInsteadOfExecutingThem()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("EXPLAIN QUERY PLAN CREATE TABLE should_not_exist(value);");

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be(
                "EXPLAIN QUERY PLAN is only supported for queries and INSERT, UPDATE, or DELETE statements.");
        using var missingTable = connection.Prepare("SELECT * FROM should_not_exist;");
        Assert.Throws<EmbeddedSqlException>(() => missingTable.GetColumnCount())!
            .Message.Should().Be("no such table: should_not_exist");
    }

    [Test]
    public void ClassifiesJoinCompoundWindowAndBitwiseIndexRoutesTruthfully()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE left_items(id INTEGER PRIMARY KEY,flags INTEGER);");
        Execute(connection, "CREATE TABLE right_items(id INTEGER PRIMARY KEY,value TEXT);");
        Execute(connection, "INSERT INTO left_items VALUES (1,1),(2,2);");
        Execute(connection, "INSERT INTO right_items VALUES (1,'one'),(3,'three');");
        Execute(
            connection,
            "CREATE INDEX left_bits ON left_items((flags << 2) | id) WHERE (flags & 1) = 1;");

        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT l.id,r.value FROM left_items l JOIN right_items r ON r.id=l.id;")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT id FROM left_items UNION ALL SELECT id FROM right_items;")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT row_number() OVER (ORDER BY id) FROM left_items;")
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT row_number() OVER (ORDER BY l.id)
                FROM left_items l JOIN right_items r ON r.id=l.id
                """)
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT l.id FROM left_items l JOIN right_items r ON r.id=l.id
                UNION ALL SELECT id FROM left_items
                """)
            .Rows[0][3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM left_items
                WHERE (flags & 1) = 1 AND ((flags << 2) | id) = 5
                """)
            .Rows[0][3].Should().Be(SqlValue.Text("SEARCH left_items USING INDEX left_bits"));
    }

    [Test]
    public void SelectsPartialExpressionIndexesOnlyForIdenticalImpliedPredicates()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT, active INTEGER);");
        Execute(
            connection,
            "CREATE INDEX t_normalized ON t(lower(value) COLLATE NOCASE DESC) WHERE active = 1;");
        Execute(
            connection,
            """
            INSERT INTO t VALUES
                (1, 'Alpha', 1),
                (2, 'alpha', 0),
                (3, 'Beta', 1);
            """);

        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM t
                WHERE active = 1 AND lower(value) COLLATE NOCASE = 'alpha';
                """)
            .Rows.Should().ContainSingle()
            .Which[3].Should().Be(SqlValue.Text("SEARCH t USING INDEX t_normalized"));
        ReadScalar(
                connection,
                """
                SELECT id FROM t
                WHERE active = 1 AND lower(value) COLLATE NOCASE = 'alpha';
                """)
            .Should().Be(SqlValue.Integer(1));
        using (var explain = connection.Prepare(
                   """
                   EXPLAIN SELECT id FROM t
                   WHERE active = 1 AND lower(value) COLLATE NOCASE = 'alpha';
                   """))
        {
            var openReadTargets = new List<string>();
            while (explain.Step() == StatementStepResult.Row)
            {
                if (explain.GetValue(1).AsText() == "OpenReadCursor")
                    openReadTargets.Add(explain.GetValue(5).AsText());
            }

            openReadTargets.Should().ContainSingle()
                .Which.Should().Be("t USING INDEX t_normalized");
        }

        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT id FROM t WHERE lower(value) COLLATE NOCASE = 'alpha';")
            .Rows[0][3].AsText().Should().NotContain("USING INDEX t_normalized");
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM t
                WHERE active = 1
                ORDER BY lower(value) COLLATE NOCASE DESC;
                """)
            .Rows.Should().ContainSingle()
            .Which[3].Should().Be(SqlValue.Text("SCAN t USING INDEX t_normalized"));
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM t
                WHERE active = 1
                ORDER BY lower(value) COLLATE NOCASE ASC;
                """)
            .Rows[0][3].AsText().Should().NotContain("USING INDEX t_normalized");
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM t
                WHERE active = 1 AND upper(value) COLLATE NOCASE = 'ALPHA';
                """)
            .Rows[0][3].AsText().Should().NotContain("USING INDEX t_normalized");
        ReadPlan(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM t
                WHERE active >= 1 AND lower(value) COLLATE NOCASE = 'alpha';
                """)
            .Rows[0][3].AsText().Should().NotContain("USING INDEX t_normalized");
    }

    [Test]
    public void RendersTursoStylePartialIndexSearchAndScanPlans()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, status TEXT);");
        Execute(connection, "CREATE INDEX active_sku ON products(sku) WHERE status = 'active';");
        Execute(
            connection,
            "INSERT INTO products VALUES (1, 'X', 'active'), (2, 'X', 'inactive'), (3, 'Y', 'active');");

        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT id FROM products WHERE status = 'active' AND sku = 'X';")
            .Rows.Should().ContainSingle()
            .Which.Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(0),
                SqlValue.Integer(0),
                SqlValue.Text("SEARCH products USING INDEX active_sku (sku=?)"));
        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT count(*) FROM products WHERE status = 'active';")
            .Rows[0][3].Should().Be(SqlValue.Text("SCAN products USING INDEX active_sku"));
        ReadScalar(connection, "SELECT count(*) FROM products WHERE status = 'active';")
            .Should().Be(SqlValue.Integer(2));
        ReadPlan(
                connection,
                "EXPLAIN QUERY PLAN SELECT id FROM products WHERE status = 'inactive' AND sku = 'X';")
            .Rows[0][3].Should().Be(SqlValue.Text("SCAN products"));
    }

    private static string ReadDetail(EmbeddedStatement statement)
    {
        statement.Step().Should().Be(StatementStepResult.Row);
        var detail = statement.GetValue(3).AsText();
        statement.Step().Should().Be(StatementStepResult.Done);
        return detail;
    }

    private static (string[] Columns, List<SqlValue[]> Rows) ReadPlan(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

        var columns = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(statement.GetValue)
                    .ToArray());
        }

        return (columns, rows);
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}

public sealed class ExplainQueryPlanDifferentialTests
{
    [Test]
    public void ManagedFacadeMatchesSqlitePublicShapeAndParameterContract()
    {
        var managed = ReadManagedPlan();
        var sqlite = ReadSqlitePlan();

        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Columns.Should().Equal("id", "parent", "notused", "detail");
        managed.Rows.Should().ContainSingle();
        sqlite.Rows.Should().ContainSingle();
        AssertPublicRowShape(managed.Rows[0]);
        AssertPublicRowShape(sqlite.Rows[0]);
        // Managed commands execute with a cancellation-capable timeout token.
        managed.Rows[0][3].Should().Be("MANAGED EVALUATOR FALLBACK");
        ((string)sqlite.Rows[0][3]).Should().NotBeNullOrWhiteSpace();
    }

    private static (string[] Columns, List<object[]> Rows) ReadManagedPlan()
    {
        using var connection = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT ?1 + 1;";
        command.Parameters.AddWithValue("?1", 4);
        using var reader = command.ExecuteReader();
        return ReadRows(reader);
    }

    private static (string[] Columns, List<object[]> Rows) ReadSqlitePlan()
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT ?1 + 1;";
        command.Parameters.AddWithValue("?1", 4);
        using var reader = command.ExecuteReader();
        return ReadRows(reader);
    }

    private static (string[] Columns, List<object[]> Rows) ReadRows(System.Data.Common.DbDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            rows.Add(
                Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetValue)
                    .ToArray());
        }

        return (columns, rows);
    }

    private static void AssertPublicRowShape(object[] row)
    {
        row.Should().HaveCount(4);
        row[0].Should().BeOfType<long>();
        row[1].Should().BeOfType<long>();
        row[2].Should().BeOfType<long>();
        row[3].Should().BeOfType<string>();
    }
}
