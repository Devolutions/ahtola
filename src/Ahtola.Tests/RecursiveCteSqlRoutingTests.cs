using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes safe single-term recursive CTEs, including base-table joins and
// recursive-term DISTINCT, through the real RecursiveCteProgramBuilder generation-worktable bytecode while
// keeping results
// byte-identical to the tree-walking evaluator. As in the compound and aggregate routing tests, EXPLAIN is
// the ground truth for "was this lowered to bytecode?": a routed recursion whose outer query is a bare
// SELECT * FROM cte dumps the worktable opcode stream, while every deliberate fallback shape throws because
// EXPLAIN only describes lowered programs. Fallback tests also assert the evaluator still produces the
// correct value or its exact error.
public class RecursiveCteSqlRoutingTests
{
    [Test]
    public void SeedOnlyRecursionSurfacesAnchorRowWhenRecursionTerminatesImmediately()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The recursive term never fires (5 < 0 is false), so only the anchor row is admitted -- the seed
        // section alone must surface it.
        Column0(ReadRows(connection, "WITH RECURSIVE c(x) AS (SELECT 5 UNION ALL SELECT x + 1 FROM c WHERE x < 0) SELECT * FROM c;"))
            .Should().Equal(SqlValue.Integer(5));

        Opcodes(ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 5 UNION ALL SELECT x + 1 FROM c WHERE x < 0) SELECT * FROM c;"))
            .Should().Contain("SeedWorkTable").And.Contain("WorkTableStep");
    }

    [Test]
    public void LinearCounterRoutesThroughWorkTableBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 5) SELECT * FROM cnt;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4),
                SqlValue.Integer(5));

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 5) SELECT * FROM cnt;")).ToList();

        // The full canonical worktable program: allocate, seed, then the observable Step/ResultRow/Expand
        // drain loop, then release.
        opcodes.Should().Contain("OpenWorkTable")
            .And.Contain("SeedWorkTable")
            .And.Contain("WorkTableStep")
            .And.Contain("ResultRow")
            .And.Contain("WorkTableExpandGeneration")
            .And.Contain("CloseWorkTable")
            .And.Contain("Halt");
    }

    [Test]
    public void RecursiveKeywordIsOptionalAndStillRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "WITH cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 3) SELECT * FROM cnt;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));

        Assert.DoesNotThrow(() =>
            ReadRows(connection, "EXPLAIN WITH cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 3) SELECT * FROM cnt;"));
    }

    [Test]
    public void MultipleAnchorsWithSingleRecursiveTermSeedTheFrontierBreadthFirst()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // Two anchor rows (1 and 2) seed the frontier; a single linear recursive term expands them one
        // generation at a time, so the emitted order is anchors, then their children, then grandchildren.
        Column0(ReadRows(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT x + 2 FROM c WHERE x < 7) SELECT * FROM c;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4),
                SqlValue.Integer(5),
                SqlValue.Integer(6),
                SqlValue.Integer(7),
                SqlValue.Integer(8));

        Opcodes(ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT x + 2 FROM c WHERE x < 7) SELECT * FROM c;"))
            .Count(opcode => opcode == "SeedWorkTable").Should().Be(2);
    }

    [Test]
    public void UnionDistinctBreaksCycleViaDeduplication()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The term saturates at 3 (3 maps to 3 again); UNION de-duplication drops that repeat and
        // terminates the recursion instead of looping forever.
        Column0(ReadRows(connection, "WITH RECURSIVE c(x) AS (SELECT 0 UNION SELECT CASE WHEN x < 3 THEN x + 1 ELSE 3 END FROM c) SELECT * FROM c;"))
            .Should().Equal(
                SqlValue.Integer(0),
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3));

        Assert.DoesNotThrow(() =>
            ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 0 UNION SELECT CASE WHEN x < 3 THEN x + 1 ELSE 3 END FROM c) SELECT * FROM c;"));
    }

    [Test]
    public void UnionAllExplainReportsAKeepAllWorkTable()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var rows = ReadRows(connection, "EXPLAIN WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 5) SELECT * FROM cnt;");
        var open = rows.Single(row => row[1].AsText() == "OpenWorkTable");

        open[5].AsText().Should().Be("union all");
        open[6].AsText().Should().Be("open work table 0 (1 cols, union all, <=1000000 rows, depth<=1000000)");
    }

    [Test]
    public void UnionDistinctExplainReportsADistinctWorkTable()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var rows = ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 0 UNION SELECT CASE WHEN x < 3 THEN x + 1 ELSE 3 END FROM c) SELECT * FROM c;");
        var open = rows.Single(row => row[1].AsText() == "OpenWorkTable");

        open[5].AsText().Should().Be("distinct");
        open[6].AsText().Should().Be("open work table 0 (1 cols, distinct, <=1000000 rows, depth<=1000000)");
    }

    [Test]
    public void BoundParametersFeedTheAnchorAndRecursiveTermAndReExecuteOnRebind()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(
            "WITH RECURSIVE c(x) AS (SELECT ?1 UNION ALL SELECT x + 1 FROM c WHERE x < ?2) SELECT * FROM c;");

        // ?1 seeds the (baked) anchor, ?2 bounds the recursive term.
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(4));
        Drain(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3),
            SqlValue.Integer(4));

        // Reset drops the materialized result; the recursion is rebuilt from the fresh bindings on the next
        // step, so both the anchor seed and the recursive bound re-read their parameters.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(10));
        statement.Bind(2, SqlValue.Integer(12));
        Drain(statement).Should().Equal(
            SqlValue.Integer(10),
            SqlValue.Integer(11),
            SqlValue.Integer(12));
    }

    [Test]
    public void RunawayUnionAllRecursionHitsTheRowLimitLoudly()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A genuinely non-terminating UNION ALL recursion routes and the worktable's row guard translates
        // to the evaluator's exact overflow diagnostic.
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT * FROM c;"))!;
        error.Message.Should().Contain("exceeded the maximum");

        // It is genuinely routed: EXPLAIN describes the worktable program rather than throwing.
        Assert.DoesNotThrow(() =>
            ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT * FROM c;"));
    }

    [Test]
    public void DeclaredColumnCountMismatchRaisesTheEvaluatorError()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "WITH c(a, b) AS (SELECT 1 UNION ALL SELECT a + 1 FROM c WHERE a < 3) SELECT * FROM c;"))!;
        error.Message.Should().Be("table c has 1 values for 2 columns");

        // The mismatch is rejected before routing, so EXPLAIN cannot describe a program either.
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN WITH c(a, b) AS (SELECT 1 UNION ALL SELECT a + 1 FROM c WHERE a < 3) SELECT * FROM c;"));
    }

    [Test]
    public void EmptyAnchorFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A zero-row anchor cannot seed the worktable (the builder rejects empty seeds), so this stays on
        // the evaluator, which returns an empty set.
        ReadRows(connection, "WITH RECURSIVE c(x) AS (SELECT 1 WHERE 1 = 0 UNION ALL SELECT x + 1 FROM c WHERE x < 3) SELECT * FROM c;")
            .Should().BeEmpty();

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 1 WHERE 1 = 0 UNION ALL SELECT x + 1 FROM c WHERE x < 3) SELECT * FROM c;"));
    }

    [Test]
    public void JoinedRecursiveTermRoutesThroughGenerationExpansion()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE edges(src INTEGER, dst INTEGER);");
        Execute(connection, "INSERT INTO edges VALUES (1, 2), (2, 3), (3, 1), (3, 4);");

        Column0(ReadRows(connection, "WITH RECURSIVE reach(n) AS (SELECT 1 UNION SELECT dst FROM edges JOIN reach ON src = n) SELECT n FROM reach ORDER BY n;"))
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3), SqlValue.Integer(4));

        Opcodes(ReadRows(connection, "EXPLAIN WITH RECURSIVE reach(n) AS (SELECT 1 UNION SELECT dst FROM edges JOIN reach ON src = n) SELECT * FROM reach;"))
            .Should().Contain("WorkTableExpandGeneration");
    }

    [Test]
    public void DistinctRecursiveTermRoutesThroughGenerationExpansion()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Column0(ReadRows(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT DISTINCT x + 1 FROM c WHERE x < 4) SELECT * FROM c;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));

        Opcodes(ReadRows(connection, "EXPLAIN WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT DISTINCT x + 1 FROM c WHERE x < 4) SELECT * FROM c;"))
            .Should().Contain("WorkTableExpandGeneration");
    }

    [Test]
    public void CompoundOrderByOnTheRecursiveBodyFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // An ORDER BY on the CTE's own compound (not the outer query) changes term/order semantics, so the
        // router declines even though the recursive term itself is linear.
        Column0(ReadRows(connection, "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 4 ORDER BY 1) SELECT * FROM cnt;"))
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 4 ORDER BY 1) SELECT * FROM cnt;"));
    }

    [Test]
    public void RoutedRecursionTakesColumnNamesFromTheDeclaredColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ColumnNames(connection, "WITH RECURSIVE cnt(step) AS (SELECT 1 UNION ALL SELECT step + 1 FROM cnt WHERE step < 3) SELECT * FROM cnt;")
            .Should().Equal("step");
    }

    private static List<SqlValue> Drain(EmbeddedStatement statement)
    {
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0));

        return values;
    }

    private static IEnumerable<SqlValue> Column0(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0]);

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
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);

            rows.Add(values);
        }

        return rows;
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var names = new string[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
            names[ordinal] = statement.GetColumnName(ordinal);

        return names;
    }
}
