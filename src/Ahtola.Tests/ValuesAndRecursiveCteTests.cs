using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Differential coverage for the managed engine's VALUES table/query expressions and
// WITH [RECURSIVE] common table expressions. Supported behaviour is cross-checked
// byte-for-byte against a real SQLite build (Microsoft.Data.Sqlite); the rejection
// cases pin the exact boundary of the bounded subset and reproduce SQLite's own
// diagnostics so unsupported recursive constructs fail loudly rather than silently
// diverging.
public class ValuesAndRecursiveCteTests
{
    [Test]
    public void TopLevelValuesProducesColumnNamedRowSet()
    {
        AssertMatchesSqlite([], "VALUES (1, 2)");
    }

    [Test]
    public void TopLevelValuesEvaluatesExpressions()
    {
        AssertMatchesSqlite([], "VALUES (1 + 1, 2 * 3)");
    }

    [Test]
    public void MultiRowValuesKeepsRowOrderAndArity()
    {
        AssertMatchesSqlite([], "VALUES (1, 'a'), (2, 'b'), (3, 'c')");
    }

    [Test]
    public void ValuesAsDerivedTableExposesGeneratedColumnNames()
    {
        AssertMatchesSqlite([], "SELECT * FROM (VALUES (3, 4, 5), (5, 6, 7), (8, 9, 10))");
    }

    [Test]
    public void ValuesProjectedByGeneratedColumnName()
    {
        AssertMatchesSqlite([], "SELECT column1, column2 FROM (VALUES (1, 10), (2, 20))");
    }

    [Test]
    public void ValuesParticipatesInCrossJoin()
    {
        // Column names are intentionally duplicated (column1/column2 on both sides), so
        // only the row payload is compared here.
        AssertMatchesSqlite([], "SELECT * FROM (VALUES (1, 2)) JOIN (VALUES (3, 4), (5, 6))", compareColumnNames: false);
    }

    [Test]
    public void ValuesAsTrailingCompoundTermTakesLeftColumnNames()
    {
        AssertMatchesSqlite([], "SELECT 1 AS x UNION ALL VALUES (2)");
    }

    [Test]
    public void ValuesAsLeadingCompoundTermAllowsTrailingOrderBy()
    {
        AssertMatchesSqlite([], "VALUES (3), (1) UNION ALL SELECT 2 ORDER BY 1");
    }

    [Test]
    public void CorrelatedValuesScalarSubqueryResolvesOuterColumns()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE users(id INTEGER, name TEXT)",
                "INSERT INTO users VALUES (1, 'Ada'), (2, 'Bob')",
            ],
            "SELECT id, (VALUES (name)) AS name_again FROM users ORDER BY id");
    }

    [Test]
    public void ValuesUsableAsInSubquery()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(id INTEGER)",
                "INSERT INTO t VALUES (1), (2), (3)",
            ],
            "SELECT id FROM t WHERE id IN (VALUES (1), (3)) ORDER BY id");
    }

    [Test]
    public void ValuesBindsPositionalParameters()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("VALUES (?1, ?2), (?2, ?1)");
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Text("z"));

        statement.GetColumnName(0).Should().Be("column1");
        statement.GetColumnName(1).Should().Be("column2");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        statement.GetValue(1).Should().Be(SqlValue.Text("z"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("z"));
        statement.GetValue(1).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void ValuesRejectsMismatchedRowArity()
    {
        CaptureError([], "VALUES (1, 2), (3)")
            .Should().Be("all VALUES must have the same number of terms");
    }

    [Test]
    public void RecursiveLinearCounterMatchesSqlite()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 5) SELECT x FROM cnt");
    }

    [Test]
    public void RecursiveKeywordIsOptional()
    {
        AssertMatchesSqlite(
            [],
            "WITH cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 5) SELECT x FROM cnt");
    }

    [Test]
    public void RecursiveFibonacciWithValuesAnchorMatchesSqlite()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE fib(a, b) AS (VALUES (0, 1) UNION ALL SELECT b, a + b FROM fib WHERE b < 20) SELECT a FROM fib");
    }

    [Test]
    public void RecursiveUnionDeduplicatesAndTerminatesCycle()
    {
        // The graph contains a cycle (1->2->3->1); UNION deduplication both removes
        // repeats and guarantees termination.
        AssertMatchesSqlite(
            [
                "CREATE TABLE edges(src INTEGER, dst INTEGER)",
                "INSERT INTO edges VALUES (1, 2), (2, 3), (3, 1), (3, 4)",
            ],
            "WITH RECURSIVE reach(n) AS (SELECT 1 UNION SELECT dst FROM edges JOIN reach ON src = n) "
            + "SELECT n FROM reach ORDER BY n");
    }

    [Test]
    public void RecursiveUnionTreatsIntegerAndRealValuesAsEqual()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION SELECT CAST(x AS REAL) FROM c) "
            + "SELECT count(*) AS total FROM c");
    }

    [Test]
    public void RecursiveUnionUsesNoCaseCollationForDeduplication()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE c(x) AS (SELECT 'A' COLLATE NOCASE UNION SELECT lower(x) FROM c) "
            + "SELECT count(*) AS total FROM c");
    }

    [Test]
    public void RecursiveUnionUsesRTrimCollationForDeduplication()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE c(x) AS (SELECT 'a ' COLLATE RTRIM UNION SELECT 'a' FROM c) "
            + "SELECT count(*) AS total FROM c");
    }

    [Test]
    public void RecursiveUnionTreatsNullValuesAsEqual()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE c(x) AS (SELECT NULL UNION SELECT NULL FROM c) "
            + "SELECT count(*) AS total FROM c");
    }

    [Test]
    public void RecursiveTreeTraversalMatchesSqlite()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE emp(id INTEGER, name TEXT, manager INTEGER)",
                "INSERT INTO emp VALUES (1, 'a', NULL), (2, 'b', 1), (3, 'c', 1), (4, 'd', 2)",
            ],
            "WITH RECURSIVE chain(id, name, depth) AS ("
            + "SELECT id, name, 0 FROM emp WHERE manager IS NULL "
            + "UNION ALL "
            + "SELECT e.id, e.name, c.depth + 1 FROM emp e JOIN chain c ON e.manager = c.id) "
            + "SELECT name, depth FROM chain");
    }

    [Test]
    public void RecursiveWithMultipleAnchorsMatchesSqlite()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE c(x) AS ("
            + "SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT x + 2 FROM c WHERE x < 7) "
            + "SELECT x FROM c ORDER BY x");
    }

    [Test]
    public void RecursiveResultFeedsOuterAggregate()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 5) "
            + "SELECT sum(x) AS s, count(*) AS c FROM cnt");
    }

    [Test]
    public void RecursiveTermMayUseDistinct()
    {
        AssertMatchesSqlite(
            [],
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT DISTINCT x + 1 FROM c WHERE x < 4) SELECT x FROM c");
    }

    [Test]
    public void RecursiveDeclaredColumnCountMismatchIsRejected()
    {
        CaptureError([], "WITH c(a, b) AS (SELECT 1 UNION ALL SELECT a + 1 FROM c WHERE a < 3) SELECT * FROM c")
            .Should().Be("table c has 1 values for 2 columns");
    }

    [Test]
    public void NonCompoundSelfReferenceIsRejectedAsCircular()
    {
        CaptureError([], "WITH t AS (SELECT * FROM t) SELECT * FROM t")
            .Should().Be("circular reference: t");
    }

    [Test]
    public void RecursiveTermBeforeAnchorIsRejectedAsCircular()
    {
        CaptureError([], "WITH t(x) AS (SELECT x FROM t UNION ALL SELECT 1) SELECT * FROM t")
            .Should().Be("circular reference: t");
    }

    [Test]
    public void SelfReferenceViaExceptIsRejectedAsCircular()
    {
        CaptureError([], "WITH t(x) AS (SELECT 1 AS x EXCEPT SELECT x FROM t) SELECT * FROM t")
            .Should().Be("circular reference: t");
    }

    [Test]
    public void SelfReferenceInsideSubqueryIsRejectedAsCircular()
    {
        CaptureError([], "WITH t(x) AS (SELECT 1 AS x UNION ALL SELECT (SELECT max(x) FROM t) + 1) SELECT * FROM t")
            .Should().Be("circular reference: t");
    }

    [Test]
    public void MultipleSelfReferencesAreRejected()
    {
        CaptureError(
            [],
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT a.x + 1 FROM c a JOIN c b ON a.x = b.x WHERE a.x < 3) SELECT x FROM c")
            .Should().Be("multiple references to recursive table: c");
    }

    [Test]
    public void RecursiveAggregateIsRejected()
    {
        CaptureError([], "WITH t(x) AS (SELECT 1 AS x UNION ALL SELECT count(*) FROM t) SELECT * FROM t")
            .Should().Be("recursive aggregate queries not supported");
    }

    [Test]
    public void RecursiveGroupByIsRejected()
    {
        CaptureError([], "WITH t(x) AS (SELECT 1 AS x UNION ALL SELECT x + 1 FROM t GROUP BY x) SELECT * FROM t")
            .Should().Be("recursive aggregate queries not supported");
    }

    [Test]
    public void RunawayRecursionHitsRowLimit()
    {
        // A genuinely non-terminating UNION ALL recursion (SQLite would loop forever). The
        // managed engine materializes eagerly, so it is bounded and fails loudly instead.
        CaptureError([], "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT x FROM c")
            .Should().Contain("exceeded the maximum");
    }

    private static void AssertMatchesSqlite(
        IReadOnlyList<string> setup,
        string query,
        bool compareColumnNames = true)
    {
        var managed = RunManaged(setup, query);
        var reference = RunSqlite(setup, query);

        if (compareColumnNames)
            managed.Columns.Should().Equal(reference.Columns, "column names should match SQLite");

        managed.Rows.Should().HaveCount(reference.Rows.Count);
        for (var row = 0; row < reference.Rows.Count; row++)
        {
            managed.Rows[row].Should().HaveCount(reference.Rows[row].Length, "row {0} width should match SQLite", row);
            for (var column = 0; column < reference.Rows[row].Length; column++)
                CellsShouldMatch(managed.Rows[row][column], reference.Rows[row][column], row, column);
        }
    }

    private static string CaptureError(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var exception = Assert.Throws<EmbeddedSqlException>(() =>
        {
            using var statement = connection.Prepare(query);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        });

        return exception!.Message;
    }

    private static QueryOutput RunManaged(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        using var command = connection.Prepare(query);
        var columns = new string[command.GetColumnCount()];
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            columns[ordinal] = command.GetColumnName(ordinal);

        var rows = new List<SqlValue[]>();
        while (command.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[command.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = command.GetValue(ordinal);

            rows.Add(values);
        }

        return new QueryOutput(columns, rows);
    }

    private static ReferenceOutput RunSqlite(IReadOnlyList<string> setup, string query)
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
        var columns = new string[reader.FieldCount];
        for (var column = 0; column < columns.Length; column++)
            columns[column] = reader.GetName(column);

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(values);
        }

        return new ReferenceOutput(columns, rows);
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference, int row, int column)
    {
        var because = $"cell ({row},{column}) should match SQLite";
        switch (reference)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null, because);
                break;
            case long integer:
                managed.Kind.Should().Be(SqlValueKind.Integer, because);
                managed.AsInteger().Should().Be(integer, because);
                break;
            case double real:
                managed.Kind.Should().Be(SqlValueKind.Real, because);
                managed.AsReal().Should().BeApproximately(real, 1e-9, because);
                break;
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text, because);
                managed.AsText().Should().Be(text, because);
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob, because);
                managed.AsBlob().ToArray().Should().Equal(blob, because);
                break;
            default:
                managed.ToString().Should().Be(reference.ToString(), because);
                break;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<SqlValue[]> Rows);

    private sealed record ReferenceOutput(string[] Columns, IReadOnlyList<object?[]> Rows);
}
