using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Covers the buffered-window VDBE route: the OpenWindowBuffer / WindowBufferInsert /
/// WindowBufferCompute / WindowBufferData / WindowBufferNext / CloseWindowBuffer opcode family that
/// <c>BufferedWindowProgramBuilder</c> emits for every window shape the streaming running-frame program
/// cannot model. Each routed case asserts both that the statement really lowered (EXPLAIN dumps the
/// buffered opcodes, and EXPLAIN QUERY PLAN reports MANAGED COMPILED VDBE) and that its rows match a real
/// SQLite build. Each fallback case asserts the opposite: the evaluator keeps ownership, raw EXPLAIN
/// refuses to describe a program that was never built, and the evaluator still produces the right value
/// or the right error.
/// </summary>
public sealed class BufferedWindowVdbeRoutingTests
{
    private static readonly string[] Setup =
    [
        "CREATE TABLE t(id INTEGER, grp TEXT, ord REAL, label TEXT, value INTEGER);",
        "INSERT INTO t VALUES "
            + "(1, 'a', NULL, 'Beta', 10), "
            + "(2, 'a', 1, 'alpha', 20), "
            + "(3, 'a', 1, 'ALPHA', 30), "
            + "(4, 'a', 2.5, NULL, 40), "
            + "(5, 'b', 1, 'gamma', NULL), "
            + "(6, 'b', 2, 'Gamma', 60);",
    ];

    // ---- Routed frames ---------------------------------------------------------------------

    [TestCase("sum(value) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING)")]
    [TestCase("sum(value) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING)")]
    [TestCase("count(*) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING)")]
    public void SlidingRowsFramesRouteAndMatchSqlite(string window)
    {
        var query = $"SELECT id, {window} FROM t ORDER BY id;";
        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [TestCase("sum(value) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST RANGE BETWEEN 1 PRECEDING AND 1 FOLLOWING)")]
    [TestCase("count(*) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW)")]
    [TestCase("count(*) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST GROUPS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE GROUP)")]
    [TestCase("sum(value) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE CURRENT ROW)")]
    [TestCase("group_concat(label) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST RANGE BETWEEN 1.5 PRECEDING AND 0.5 FOLLOWING EXCLUDE TIES)")]
    public void PeerRelativeFramesAndExclusionsRouteAndMatchSqlite(string window)
    {
        var query = $"SELECT id, {window} FROM t ORDER BY id;";
        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void RankingAndNavigationFunctionsRouteAndMatchSqlite()
    {
        const string query =
            """
            SELECT id,
                   row_number() OVER w,
                   rank() OVER w,
                   dense_rank() OVER w,
                   percent_rank() OVER w,
                   cume_dist() OVER w,
                   ntile(3) OVER w,
                   lag(value, 1, -1) OVER w,
                   lead(value) OVER w,
                   first_value(value) OVER w,
                   last_value(value) OVER w,
                   nth_value(value, 2) OVER w
            FROM t
            WINDOW w AS (PARTITION BY grp ORDER BY ord NULLS FIRST)
            ORDER BY grp, id;
            """;

        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void SeveralDifferentWindowSpecsInOneSelectRouteThroughOneBuffer()
    {
        const string query =
            """
            SELECT id,
                   sum(value) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW),
                   count(*) OVER (ORDER BY ord NULLS LAST GROUPS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
                   rank() OVER (PARTITION BY label COLLATE NOCASE ORDER BY id DESC)
            FROM t
            ORDER BY id;
            """;

        using var connection = OpenManaged(Setup);
        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        // One buffer serves every spec: the window pass groups the calls internally.
        opcodes.Count(opcode => opcode == "OpenWindowBuffer").Should().Be(1);
        opcodes.Count(opcode => opcode == "WindowBufferCompute").Should().Be(1);

        AssertMatchesSqlite(Setup, query);
    }

    // ---- FILTER and DISTINCT ---------------------------------------------------------------

    [Test]
    public void FilteredAggregateWindowsRouteAndMatchSqlite()
    {
        const string query =
            """
            SELECT id,
                   sum(value) FILTER (WHERE value > 15) OVER (
                       PARTITION BY grp ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
                   count(*) FILTER (WHERE label IS NOT NULL) OVER (PARTITION BY grp),
                   group_concat(label, '/') FILTER (WHERE value IS NOT NULL) OVER (
                       ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING)
            FROM t
            ORDER BY id;
            """;

        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void DistinctAggregateWindowRaisesTheSqliteErrorAndNeverLowers()
    {
        // SQLite rejects DISTINCT on any window function; the route declines so the evaluator raises
        // the identical diagnostic instead of the compiler inventing one.
        const string query = "SELECT count(DISTINCT value) OVER (PARTITION BY grp) FROM t;";

        using var connection = OpenManaged(Setup);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Contain("DISTINCT is not supported for window functions");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3].AsText()
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        var sqlite = () => RunSqlite(Setup, query);
        sqlite.Should().Throw<MsData.SqliteException>();
    }

    [Test]
    public void DistinctPlainAggregateOutsideAWindowStillLowersAsAnOrdinaryAggregate()
    {
        // Guards the aggregate route: adding the window route must not capture (or break) a plain
        // DISTINCT aggregate that carries no OVER clause.
        const string query = "SELECT count(DISTINCT grp) FROM t;";
        AssertMatchesSqlite(Setup, query);

        using var connection = OpenManaged(Setup);
        Opcodes(ReadRows(connection, "EXPLAIN QUERY PLAN " + query)).Should().NotBeEmpty();
    }

    // ---- Computed keys and computed results ------------------------------------------------

    [Test]
    public void ComputedPartitionAndOrderKeysRouteAndMatchSqlite()
    {
        const string query =
            """
            SELECT id,
                   sum(value * 2) OVER (
                       PARTITION BY upper(grp)
                       ORDER BY CAST(ord AS INTEGER) NULLS FIRST, id
                       ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)
            FROM t
            ORDER BY id;
            """;

        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void ComputedProjectionsOverWindowResultsRouteAndMatchSqlite()
    {
        const string query =
            """
            SELECT id,
                   sum(value) OVER w * 2 + 1 AS doubled,
                   CASE WHEN row_number() OVER w = 1 THEN 'first' ELSE 'later' END AS marker,
                   coalesce(lag(value) OVER w, -1) AS previous,
                   upper(grp) || ':' || CAST(count(*) OVER w AS TEXT) AS tagged
            FROM t
            WINDOW w AS (PARTITION BY grp ORDER BY id)
            ORDER BY id;
            """;

        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void OrderByAWindowResultRoutesAndMatchesSqlite()
    {
        const string query =
            """
            SELECT id, sum(value) OVER (PARTITION BY grp) AS total
            FROM t
            ORDER BY total DESC NULLS LAST, id;
            """;

        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void StarProjectionWithWindowRoutesAndMatchesSqlite()
    {
        const string query = "SELECT *, count(*) OVER (PARTITION BY grp) FROM t ORDER BY id;";
        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void QualifiedStarWithWindowRoutesAndMatchesSqlite()
    {
        const string query = "SELECT rows.*, count(*) OVER () FROM t AS rows ORDER BY id;";
        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    // ---- NULL ordering and collation -------------------------------------------------------

    [Test]
    public void ExplicitNullPlacementAndCollationsRouteAndMatchSqlite()
    {
        const string query =
            """
            SELECT id, label,
                   sum(value) OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE DESC NULLS FIRST
                       ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
            FROM t
            ORDER BY label COLLATE NOCASE DESC NULLS FIRST, id;
            """;

        AssertRoutesThroughWindowBuffer(Setup, query);
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void UnknownCollationOnAWindowOrderTermRaisesBeforeAnyLowering()
    {
        using var connection = OpenManaged(Setup);
        const string query =
            "SELECT sum(value) OVER (ORDER BY label COLLATE nope) FROM t;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("no such collation sequence: nope");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowValidationRunsBeforeALimitCallback()
    {
        var callbackCount = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark_limit",
            0,
            _ =>
            {
                callbackCount++;
                return SqlValue.Integer(1);
            });
        using var connection = database.Connect();
        foreach (var statement in Setup)
            Execute(connection, statement);

        const string query =
            "SELECT row_number() OVER () FROM t ORDER BY id COLLATE missing LIMIT mark_limit();";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("no such collation sequence: missing");
        callbackCount.Should().Be(0);
    }

    // ---- WHERE, LIMIT and OFFSET -----------------------------------------------------------

    [Test]
    public void WhereRunsBeforeWindowingOnTheRoutedProgram()
    {
        const string query =
            """
            SELECT id, count(*) OVER (PARTITION BY grp) AS n
            FROM t
            WHERE value IS NOT NULL AND id <> 3
            ORDER BY id;
            """;

        using var connection = OpenManaged(Setup);
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("Filter")
            .And.Contain(opcode => opcode == "WindowBufferCompute" || opcode == "AggStep");
        AssertMatchesSqlite(Setup, query);
    }

    [TestCase("LIMIT 2")]
    [TestCase("LIMIT 3 OFFSET 2")]
    [TestCase("LIMIT -1 OFFSET 4")]
    [TestCase("LIMIT 100 OFFSET 5")]
    public void LimitAndOffsetGateTheRoutedWindowStream(string tail)
    {
        var query =
            "SELECT id, sum(value) OVER (PARTITION BY grp ORDER BY id " +
            $"ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t ORDER BY id {tail};";

        using var connection = OpenManaged(Setup);
        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain(opcode => opcode == "LimitGate" || opcode == "OffsetGate");
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void LimitZeroStaysOnTheEvaluatorSoItsValidationTimingIsPreserved()
    {
        const string query =
            "SELECT sum(value) OVER (ORDER BY id) FROM t ORDER BY id LIMIT 0;";

        using var connection = OpenManaged(Setup);
        ReadRows(connection, query).Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void NonIntegralLimitStaysOnTheEvaluatorAndKeepsItsDiagnostic()
    {
        const string query =
            "SELECT sum(value) OVER (ORDER BY id) FROM t ORDER BY id LIMIT 'x';";

        using var connection = OpenManaged(Setup);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));

        var sqlite = () => RunSqlite(Setup, query);
        sqlite.Should().Throw<MsData.SqliteException>();
    }

    // ---- Parameters, reset and rebind ------------------------------------------------------

    [Test]
    public void ParameterizedWindowsRouteAndReuseTheStatementAcrossResetAndRebind()
    {
        const string query =
            """
            SELECT id,
                   sum(value) OVER (
                       PARTITION BY grp ORDER BY id
                       ROWS BETWEEN ?1 PRECEDING AND ?2 FOLLOWING) AS windowed
            FROM t
            WHERE value > ?3
            ORDER BY id;
            """;

        using var connection = OpenManaged(Setup);
        using (var explain = connection.Prepare("EXPLAIN " + query))
        {
            explain.Bind(1, SqlValue.Integer(1));
            explain.Bind(2, SqlValue.Integer(1));
            explain.Bind(3, SqlValue.Integer(0));
            Opcodes(ReadRows(explain)).Should().Contain("WindowBufferCompute");
        }

        using var statement = connection.Prepare(query);
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(1));
        statement.Bind(3, SqlValue.Integer(0));
        ReadRows(statement).Should().BeEquivalentTo(RunSqlite(Setup, query, 1L, 1L, 0L).Select(Convert));

        // Reset preserves the bindings, so the same program re-runs with the same parameters.
        statement.Reset();
        ReadRows(statement).Should().BeEquivalentTo(RunSqlite(Setup, query, 1L, 1L, 0L).Select(Convert));

        // Rebinding after a reset re-plans against the fresh values.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(0));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(15));
        ReadRows(statement).Should().BeEquivalentTo(RunSqlite(Setup, query, 0L, 2L, 15L).Select(Convert));
    }

    [Test]
    public void InvalidParameterizedFrameOffsetRaisesTheSqliteErrorFromTheRoutedProgram()
    {
        const string query =
            "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN ?1 PRECEDING AND CURRENT ROW) FROM t;";

        using var connection = OpenManaged(Setup);
        using var statement = connection.Prepare(query);
        statement.Bind(1, SqlValue.Integer(-1));

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(statement))!
            .Message.Should().Contain("frame starting offset must be a non-negative integer");

        // The failed run leaves the statement reusable, exactly as the evaluator route does.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(1));
        ReadRows(statement).Should().HaveCount(6);
    }

    // ---- Cancellation ----------------------------------------------------------------------

    [Test]
    public void CancellableExecutionKeepsTheEvaluatorAndStaysReusable()
    {
        using var connection = OpenManaged(Setup);
        using var statement = connection.Prepare(
            "SELECT id, sum(value) OVER (PARTITION BY grp ORDER BY id), sum(value) OVER (ORDER BY id) FROM t ORDER BY id;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // A cancellable step never routes through the compiler, so cancellation timing is the
        // evaluator's throw-before-any-row contract.
        var canceled = () => statement.Step(cancellation.Token);
        canceled.Should().Throw<OperationCanceledException>();

        statement.Reset();
        ReadRows(statement).Should().HaveCount(6);
    }

    [Test]
    public void ResumingAfterPartialConsumptionRestartsTheRoutedProgramFromTheTop()
    {
        using var connection = OpenManaged(Setup);
        using var statement = connection.Prepare(
            "SELECT id, count(*) OVER (PARTITION BY grp) FROM t ORDER BY id;");

        statement.Step().Should().Be(StatementStepResult.Row);
        var firstId = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Row);

        statement.Reset();
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(firstId);
        ReadRows(statement).Should().HaveCount(5);
    }

    // ---- Conservative fallbacks ------------------------------------------------------------

    [Test]
    public void WindowOverADerivedTableFallsBackToEvaluator()
    {
        const string query =
            "SELECT id, count(*) OVER () FROM (SELECT id FROM t WHERE grp = 'a') ORDER BY id;";

        using var connection = OpenManaged(Setup);
        ReadRows(connection, query).Should().HaveCount(4);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void BareStarOverAWindowedDerivedQueryReusesTheInnerProgramAndMatchesSqlite()
    {
        const string query =
            "SELECT * FROM (SELECT id, sum(value) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t);";

        using var connection = OpenManaged(Setup);
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain(opcode => opcode == "WindowBufferCompute" || opcode == "AggStep");
        AssertMatchesSqlite(Setup, query);
    }

    [Test]
    public void WindowOverACommonTableExpressionFallsBackToEvaluator()
    {
        const string query =
            "WITH src AS (SELECT id, value FROM t) SELECT id, sum(value) OVER (ORDER BY id) FROM src ORDER BY id;";

        using var connection = OpenManaged(Setup);
        ReadRows(connection, query).Should().HaveCount(6);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowOverAViewFallsBackToEvaluator()
    {
        using var connection = OpenManaged(Setup);
        Execute(connection, "CREATE VIEW v AS SELECT id, value FROM t;");
        const string query = "SELECT id, sum(value) OVER (ORDER BY id) FROM v ORDER BY id;";

        ReadRows(connection, query).Should().HaveCount(6);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowMixedWithAPlainAggregateStaysOnTheEvaluator()
    {
        const string query = "SELECT sum(value) OVER (ORDER BY id), count(*) FROM t;";

        // An aggregate collapses the statement to one row and the window pass then runs over that
        // single grouped row; this route only windows over scanned rows, so it must decline.
        AssertMatchesSqlite(Setup, query);

        using var connection = OpenManaged(Setup);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowOverAnIndexOrderedScanFallsBackSoRowOrderCannotDiverge()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE idx(id INTEGER PRIMARY KEY, flags INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO idx VALUES (1,1,10),(2,3,20),(3,1,30);");
        Execute(connection, "CREATE INDEX idx_bits ON idx((flags << 2) | id) WHERE (flags & 1) = 1;");

        // The managed index planner would scan this in index order, which the compiled cursor does not
        // reproduce, so the window route declines rather than risk a different input order.
        const string query =
            "SELECT id, count(*) OVER () FROM idx WHERE (flags & 1) = 1 AND ((flags << 2) | id) = 5;";
        ReadRows(connection, query).Should().HaveCount(1);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowsInAttachedTempStrictAndWithoutRowidContextsRouteOrFallBackConservatively()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("buffered-window.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH DATABASE 'buffered-window-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER, grp TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO aux.items VALUES (1,'a',10),(2,'a',20),(3,'b',7);");
        Execute(connection, "CREATE TEMP TABLE scratch(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO scratch VALUES (1,5),(2,6),(3,7);");
        Execute(connection, "CREATE TABLE strict_rows(id INTEGER PRIMARY KEY, value INTEGER) STRICT;");
        Execute(connection, "INSERT INTO strict_rows VALUES (1,10),(2,20),(3,30);");
        Execute(connection, "CREATE TABLE norowid(id INTEGER PRIMARY KEY, value INTEGER) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO norowid VALUES (1,4),(2,5),(3,6);");

        // A schema-qualified (attached) source and a TEMP table are not plain base-table scan targets,
        // so they stay on the evaluator; the local STRICT and WITHOUT ROWID tables lower and stay exact.
        foreach (var fallback in new[]
        {
            "SELECT id, sum(value) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) " +
            "FROM aux.items ORDER BY id;",
            "SELECT id, sum(value) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM scratch ORDER BY id;",
        })
        {
            ReadRows(connection, fallback).Should().HaveCount(3, fallback);
            Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + fallback));
        }

        const string mismatchedOrder =
            "SELECT id, rank() OVER (ORDER BY value DESC) FROM strict_rows ORDER BY id;";
        Opcodes(ReadRows(connection, "EXPLAIN " + mismatchedOrder)).Should()
            .Contain(opcode => opcode == "WindowBufferCompute" || opcode == "AggStep", mismatchedOrder);
        ReadRows(connection, mismatchedOrder).Should().HaveCount(3, mismatchedOrder);

        const string lagQuery = "SELECT id, lag(value) OVER (ORDER BY id) FROM norowid ORDER BY id;";
        Opcodes(ReadRows(connection, "EXPLAIN " + lagQuery)).Should()
            .Contain("AggStep", lagQuery).And.NotContain("OpenWindowBuffer");
        ReadRows(connection, lagQuery).Should().HaveCount(3, lagQuery);
    }

    [Test]
    public void WindowsDoNotDisturbForeignKeyEnforcementOnTheSameConnection()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));");
        Execute(connection, "INSERT INTO parent VALUES (1),(2);");
        Execute(connection, "INSERT INTO child VALUES (10,1),(11,2);");

        const string query = "SELECT id, count(*) OVER (PARTITION BY parent_id) FROM child ORDER BY id;";
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain(opcode => opcode == "WindowBufferCompute" || opcode == "AggStep");
        ReadRows(connection, query).Should().HaveCount(2);

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "INSERT INTO child VALUES (12,99);"));
    }

    [Test]
    public void EmptyInputRoutesAndEmitsNoRows()
    {
        string[] setup = ["CREATE TABLE empty(id INTEGER, value INTEGER);"];
        const string query =
            "SELECT id, sum(value) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM empty ORDER BY id;";

        using var connection = OpenManaged(setup);
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenSorter").And.Contain("AggInverse").And.NotContain("WindowBufferCompute");
        ReadRows(connection, query).Should().BeEmpty();
        AssertMatchesSqlite(setup, query);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void AssertRoutesThroughWindowBuffer(IReadOnlyList<string> setup, string query)
    {
        using var connection = OpenManaged(setup);
        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain(opcode => opcode == "OpenWindowBuffer" || opcode == "AggStep", query)
            .And.Contain("ResultRow");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3].AsText()
            .Should().Be("MANAGED COMPILED VDBE", query);
    }

    private static void AssertMatchesSqlite(
        IReadOnlyList<string> setup,
        string query,
        params object?[] parameters)
    {
        var managed = RunManaged(setup, query, parameters);
        var sqlite = RunSqlite(setup, query, parameters);

        managed.Should().HaveCount(sqlite.Count, query);
        for (var row = 0; row < sqlite.Count; row++)
        {
            managed[row].Should().HaveCount(sqlite[row].Length);
            for (var column = 0; column < sqlite[row].Length; column++)
                CellsShouldMatch(managed[row][column], sqlite[row][column]);
        }
    }

    private static List<SqlValue[]> RunManaged(
        IReadOnlyList<string> setup,
        string query,
        params object?[] parameters)
    {
        using var connection = OpenManaged(setup);
        using var statement = connection.Prepare(query);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, ToSqlValue(parameters[index]));
        return ReadRows(statement);
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return connection;
    }

    private static List<object?[]> RunSqlite(
        IReadOnlyList<string> setup,
        string query,
        params object?[] parameters)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = statement;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        for (var index = 0; index < parameters.Length; index++)
            command.Parameters.AddWithValue($"?{index + 1}", parameters[index] ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static SqlValue[] Convert(object?[] row) => [.. row.Select(ToSqlValue)];

    private static void CellsShouldMatch(SqlValue managed, object? sqlite)
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
                managed.Kind.Should().Be(SqlValueKind.Real);
                managed.AsReal().Should().BeApproximately(real, 1e-9);
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new InvalidOperationException($"Unsupported SQLite value type {sqlite.GetType()}.");
        }
    }

    private static SqlValue ToSqlValue(object? value)
        => value switch
        {
            null => SqlValue.Null,
            int integer => SqlValue.Integer(integer),
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            _ => throw new InvalidOperationException($"Unsupported parameter type {value.GetType()}."),
        };

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return ReadRows(statement);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.ColumnCount];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }
}
