using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for a window function combined with GROUP BY or with a plain
/// aggregate. SQLite aggregates first and runs the window pass over the surviving grouped
/// rows, so a frame here spans groups rather than base rows and a window argument may read
/// an aggregate result. Every case is asserted against Microsoft.Data.Sqlite rather than
/// against hand-written expectations.
/// </summary>
public sealed class WindowWithGroupBySemanticsTests
{
    // Rows are inserted so that first-encounter group order ('c','a','b') differs from
    // sorted key order, which keeps an accidental reliance on grouping order visible.
    private static readonly string[] Setup =
    [
        "CREATE TABLE t(id INTEGER, grp TEXT, label TEXT, value INTEGER);",
        "INSERT INTO t VALUES "
            + "(1, 'c', 'gamma', 7), "
            + "(2, 'a', 'alpha', 10), "
            + "(3, 'b', 'beta', 3), "
            + "(4, 'a', 'ALPHA', 20), "
            + "(5, 'b', NULL, NULL), "
            + "(6, 'c', 'Gamma', 5), "
            + "(7, 'c', 'gamma', 1), "
            + "(8, 'd', 'delta', NULL);",
    ];

    [TestCase("SELECT grp, sum(value), count(*) OVER () FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase("SELECT grp, sum(value), row_number() OVER (ORDER BY grp) FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase("SELECT grp, sum(value), sum(sum(value)) OVER () FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase(
        "SELECT grp, count(*) AS n, row_number() OVER (ORDER BY count(*) DESC, grp) "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase(
        "SELECT grp, sum(value), sum(sum(value)) OVER (ORDER BY grp "
        + "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase(
        "SELECT grp, count(*), sum(count(*)) OVER (PARTITION BY count(*)) "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase(
        "SELECT grp, sum(value), rank() OVER (ORDER BY sum(value)), "
        + "dense_rank() OVER (ORDER BY sum(value)), "
        + "percent_rank() OVER (ORDER BY sum(value)), "
        + "cume_dist() OVER (ORDER BY sum(value)), "
        + "ntile(2) OVER (ORDER BY sum(value), grp) "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase(
        "SELECT grp, sum(value), lag(sum(value)) OVER (ORDER BY grp), "
        + "lead(sum(value), 1, -1) OVER (ORDER BY grp), "
        + "first_value(grp) OVER (ORDER BY grp), "
        + "last_value(grp) OVER (ORDER BY grp) "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    // A bare column that is not in GROUP BY resolves against the group's representative row,
    // and the window then aggregates those representative values.
    [TestCase("SELECT grp, sum(value) OVER () FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase("SELECT grp, group_concat(label) OVER (ORDER BY grp) FROM t GROUP BY grp ORDER BY grp;")]
    // HAVING removes groups before the window pass, so the frame never sees them.
    [TestCase(
        "SELECT grp, sum(value), count(*) OVER (), row_number() OVER (ORDER BY grp) "
        + "FROM t GROUP BY grp HAVING sum(value) > 5 ORDER BY grp;")]
    [TestCase(
        "SELECT grp, sum(value), count(*) OVER () FROM t GROUP BY grp "
        + "HAVING sum(value) > 100000 ORDER BY grp;")]
    // An aggregate anywhere inside the window call makes this an aggregate query with no
    // GROUP BY, which collapses to exactly one row.
    [TestCase("SELECT sum(sum(value)) OVER () FROM t;")]
    [TestCase("SELECT count(*), row_number() OVER (PARTITION BY sum(value)) FROM t;")]
    [TestCase("SELECT max(sum(value)) OVER (ORDER BY grp) FROM t;")]
    [TestCase("SELECT max(sum(value)) OVER (ORDER BY grp) FROM t GROUP BY value;")]
    [TestCase("SELECT sum(count(*)) OVER (ORDER BY count(*)) FROM t WHERE value IS NULL;")]
    // GROUP BY over an expression, and a grouped window inside a larger expression.
    [TestCase(
        "SELECT value IS NULL, count(*), 1 + sum(count(*)) OVER (ORDER BY value IS NULL) "
        + "FROM t GROUP BY value IS NULL ORDER BY 1;")]
    // Reshaping clauses compose on top of the window pass.
    [TestCase("SELECT DISTINCT count(*) OVER () FROM t GROUP BY grp;")]
    [TestCase(
        "SELECT grp, row_number() OVER (ORDER BY grp) FROM t GROUP BY grp "
        + "ORDER BY grp LIMIT 2 OFFSET 1;")]
    [TestCase(
        "SELECT grp, sum(value), row_number() OVER (ORDER BY sum(value) DESC, grp) "
        + "FROM t GROUP BY grp ORDER BY 3;")]
    // A named window definition referencing an aggregate.
    [TestCase(
        "SELECT grp, sum(value) AS s, rank() OVER w "
        + "FROM t GROUP BY grp WINDOW w AS (ORDER BY sum(value)) ORDER BY grp;")]
    // FILTER over grouped rows, including an aggregate predicate.
    [TestCase(
        "SELECT grp, count(*) AS n, sum(count(*)) FILTER (WHERE grp = 'c') OVER () "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    [TestCase(
        "SELECT grp, sum(value), sum(sum(value)) FILTER (WHERE count(*) > 1) OVER (ORDER BY grp) "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    // Several windows with different specs over the same grouped rows.
    [TestCase(
        "SELECT grp, sum(value), count(*) OVER (PARTITION BY count(*)), "
        + "sum(sum(value)) OVER (ORDER BY grp DESC) "
        + "FROM t GROUP BY grp ORDER BY grp;")]
    // Grouping over an empty result still produces one row without GROUP BY and none with it.
    [TestCase("SELECT grp, sum(value), count(*) OVER () FROM t WHERE 0 GROUP BY grp ORDER BY grp;")]
    [TestCase("SELECT sum(count(*)) OVER () FROM t WHERE 0;")]
    public void GroupedWindowResultsMatchSqlite(string query)
        => AssertMatchesSqlite(Setup, query);

    // The window pass runs after aggregation, so a window call handed to an aggregate - or
    // placed in a clause evaluated before the window pass - has no pass left to run in.
    [TestCase(
        "SELECT sum(row_number() OVER ()) FROM t GROUP BY grp;",
        "misuse of window function")]
    [TestCase(
        "SELECT sum(max(value) OVER (ORDER BY grp)) FROM t GROUP BY grp;",
        "misuse of window function")]
    [TestCase(
        "SELECT sum(sum(value) OVER (PARTITION BY grp)) OVER (PARTITION BY value) FROM t;",
        "misuse of window function")]
    [TestCase(
        "SELECT sum(value) OVER (PARTITION BY count(*) OVER ()) FROM t;",
        "misuse of window function")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY count(*) OVER ()) FROM t;",
        "misuse of window function")]
    [TestCase(
        "SELECT sum(value) FILTER (WHERE row_number() OVER (ORDER BY id) = 1) OVER () FROM t;",
        "misuse of window function")]
    [TestCase(
        "SELECT grp FROM t GROUP BY grp HAVING sum(value) OVER (PARTITION BY grp) > 4;",
        "misuse of window function")]
    [TestCase(
        "SELECT grp FROM t GROUP BY sum(value) OVER (PARTITION BY grp);",
        "misuse of window function")]
    [TestCase(
        "SELECT grp FROM t WHERE row_number() OVER () = 1 GROUP BY grp;",
        "misuse of window function")]
    public void GroupedWindowMisusesAreRejectedLikeSqlite(string query, string message)
    {
        var managed = () => RunManaged(Setup, query);
        managed.Should().Throw<EmbeddedSqlException>().WithMessage($"*{message}*");

        var sqlite = () => RunSqlite(Setup, query);
        sqlite.Should().Throw<MsData.SqliteException>().WithMessage($"*{message}*");
    }

    [Test]
    public void GroupedWindowNamesTheNestedCallSqliteNames()
    {
        var managed = () => RunManaged(Setup, "SELECT sum(row_number() OVER ()) FROM t GROUP BY grp;");
        managed.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*misuse of window function row_number()*");
    }

    // The window pass observes exactly the group order the engine's plain GROUP BY produces:
    // ascending key order, matching the order SQLite's aggregation sorter emits groups in.
    // This pins the two managed paths together so an unordered window over groups can never
    // disagree with the unordered grouped projection beside it.
    [Test]
    public void GroupedWindowObservesTheSameGroupOrderAsThePlainGroupedPath()
    {
        using var connection = OpenManaged(Setup);
        var plain = ReadRows(connection, "SELECT grp FROM t GROUP BY grp;")
            .Select(row => row[0].AsText())
            .ToList();
        var windowed = ReadRows(connection, "SELECT grp, row_number() OVER () FROM t GROUP BY grp;");

        plain.Should().Equal("a", "b", "c", "d");
        windowed.Select(row => row[0].AsText()).Should().Equal(plain);
        windowed.Select(row => row[1].AsInteger()).Should().Equal(1L, 2L, 3L, 4L);
    }

    // The compiled window route only knows how to window over scanned rows, so a grouped
    // window statement must stay on the evaluator instead of being lowered.
    [Test]
    public void GroupedWindowStaysOnTheEvaluator()
    {
        using var connection = OpenManaged(Setup);
        var explain = () => ReadRows(connection, "EXPLAIN SELECT grp, sum(sum(value)) OVER () FROM t GROUP BY grp;");
        explain.Should().Throw<EmbeddedSqlException>();

        var explainImplicit = () => ReadRows(connection, "EXPLAIN SELECT sum(sum(value)) OVER () FROM t;");
        explainImplicit.Should().Throw<EmbeddedSqlException>();
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);

        managed.Should().HaveCount(sqlite.Count);
        for (var row = 0; row < sqlite.Count; row++)
        {
            managed[row].Should().HaveCount(sqlite[row].Length);
            for (var column = 0; column < sqlite[row].Length; column++)
                CellsShouldMatch(managed[row][column], sqlite[row][column]);
        }
    }

    private static List<SqlValue[]> RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var connection = OpenManaged(setup);
        return ReadRows(connection, query);
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
        {
            using var prepared = connection.Prepare(statement);
            prepared.Step().Should().Be(StatementStepResult.Done);
        }

        return connection;
    }

    private static List<object?[]> RunSqlite(IReadOnlyList<string> setup, string query)
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

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
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
