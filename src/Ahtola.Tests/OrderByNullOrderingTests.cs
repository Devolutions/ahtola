using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class OrderByNullOrderingTests
{
    private static readonly string[] ScalarSetup =
    [
        "CREATE TABLE t(value INTEGER);",
        "INSERT INTO t VALUES (2), (NULL), (1), (NULL), (3);",
    ];

    [TestCase("SELECT value FROM t ORDER BY value;")]
    [TestCase("SELECT value FROM t ORDER BY value ASC NULLS LAST;")]
    [TestCase("SELECT value FROM t ORDER BY value DESC;")]
    [TestCase("SELECT value FROM t ORDER BY value DESC NULLS FIRST;")]
    public void CompiledSorterMatchesSqliteDirectionDefaultsAndExplicitPlacement(string query)
    {
        AssertMatchesSqlite(ScalarSetup, query);

        using var connection = OpenManaged(ScalarSetup);
        UsesSorter(connection, query).Should().BeTrue();
    }

    [Test]
    public void CompiledSorterPreservesAliasesOrdinalsCollationAndLimitOffset()
    {
        string[] setup =
        [
            "CREATE TABLE t(rank INTEGER, label TEXT);",
            "INSERT INTO t VALUES (2, 'beta'), (NULL, 'Zulu'), (1, NULL), (3, 'alpha'), (NULL, 'Bravo');",
        ];
        const string aliasAndCollationQuery =
            "SELECT label AS name, rank AS score FROM t " +
            "ORDER BY name COLLATE NOCASE ASC NULLS LAST, score DESC NULLS FIRST LIMIT 4 OFFSET 1;";
        const string ordinalQuery =
            "SELECT label AS name, rank AS score FROM t " +
            "ORDER BY 1 COLLATE NOCASE ASC NULLS LAST, 2 DESC NULLS FIRST LIMIT 4 OFFSET 1;";

        AssertMatchesSqlite(setup, aliasAndCollationQuery);
        AssertMatchesSqlite(setup, ordinalQuery);

        using var connection = OpenManaged(setup);
        foreach (var query in new[] { aliasAndCollationQuery, ordinalQuery })
        {
            UsesSorter(connection, query).Should().BeTrue(query);
            Opcodes(ReadRows(connection, "EXPLAIN " + query))
                .Should().Contain("SorterSort").And.Contain("OffsetGate").And.Contain("LimitGate");
        }
    }

    [Test]
    public void ComputedAndCompoundFallbacksAndCompiledAggregateOrderingMatchSqlite()
    {
        var cases = new[]
        {
            (
                Setup: ScalarSetup,
                Query: "SELECT value + 0 AS computed FROM t ORDER BY computed DESC NULLS FIRST;",
                UsesSorter: false
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE a(value TEXT);",
                    "CREATE TABLE b(value TEXT);",
                    "INSERT INTO a VALUES ('beta'), (NULL), ('Alpha');",
                    "INSERT INTO b VALUES ('charlie'), (NULL), ('Bravo');",
                },
                Query:
                    "SELECT value AS x FROM a UNION ALL SELECT value FROM b " +
                    "ORDER BY x COLLATE NOCASE DESC NULLS FIRST LIMIT 4 OFFSET 1;",
                UsesSorter: false
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE grouped(k INTEGER, v INTEGER);",
                    "INSERT INTO grouped VALUES (NULL, 1), (NULL, NULL), (1, 2), (1, NULL), (2, 3);",
                },
                Query:
                    "SELECT k, count(v) AS c FROM grouped GROUP BY k " +
                    "ORDER BY c ASC NULLS LAST, k DESC NULLS FIRST;",
                UsesSorter: true
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE pairs(left_value TEXT, right_value TEXT);",
                    "INSERT INTO pairs VALUES ('first', 'b'), ('second', 'A'), ('third', NULL);",
                },
                Query:
                    "SELECT * FROM pairs UNION ALL SELECT * FROM pairs WHERE 0 " +
                    "ORDER BY 2 COLLATE NOCASE ASC NULLS LAST;",
                UsesSorter: false
            ),
        };

        foreach (var testCase in cases)
        {
            AssertMatchesSqlite(testCase.Setup, testCase.Query);

            using var connection = OpenManaged(testCase.Setup);
            UsesSorter(connection, testCase.Query).Should().Be(testCase.UsesSorter, testCase.Query);
        }
    }

    [Test]
    public void RedundantOrderBySuffixAfterUniqueRowidMatchesSqlite()
    {
        var cases = new[]
        {
            (
                Setup: new[]
                {
                    "CREATE TABLE primary_key_table(a INTEGER PRIMARY KEY, b TEXT);",
                    "INSERT INTO primary_key_table VALUES (1, 'x'), (2, 'y');",
                },
                Query:
                    "SELECT a FROM primary_key_table " +
                    "ORDER BY a DESC, b NOT IN (SELECT a, b FROM primary_key_table);"
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE rowid_table(a INTEGER, b TEXT);",
                    "INSERT INTO rowid_table VALUES (1, 'x'), (2, 'y');",
                },
                Query:
                    "SELECT a FROM rowid_table " +
                    "ORDER BY rowid DESC, b NOT IN (SELECT a, b FROM rowid_table);"
            ),
        };

        foreach (var testCase in cases)
            AssertMatchesSqlite(testCase.Setup, testCase.Query);
    }

    [Test]
    public void GroupedStarProjectionWithConstantOrderByMatchesSqlite()
    {
        var cases = new[]
        {
            (
                Setup: new[]
                {
                    "CREATE TABLE float_order(c1 INT);",
                    "INSERT INTO float_order VALUES (1), (2);",
                },
                Query: "SELECT * FROM float_order GROUP BY c1 ORDER BY 58.058;"
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE string_order(c1 INT);",
                    "INSERT INTO string_order VALUES (1), (2);",
                },
                Query: "SELECT * FROM string_order GROUP BY c1 ORDER BY 'hello';"
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE null_order(c1 INT);",
                    "INSERT INTO null_order VALUES (1), (2);",
                },
                Query: "SELECT * FROM null_order GROUP BY c1 ORDER BY NULL;"
            ),
            (
                Setup: new[]
                {
                    "CREATE TABLE view_order(c1 INT);",
                    "INSERT INTO view_order VALUES (1), (2);",
                    "CREATE VIEW ordered_view AS SELECT * FROM view_order GROUP BY c1 ORDER BY STRFTIME('test');",
                },
                Query: "SELECT * FROM ordered_view;"
            ),
        };

        foreach (var testCase in cases)
            AssertMatchesSqlite(testCase.Setup, testCase.Query);
    }

    [Test]
    public void CompiledAndFallbackWindowOrderingMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, value INTEGER);",
            "INSERT INTO t VALUES (2, 20), (NULL, 5), (1, 10), (NULL, 7), (3, 30);",
        ];
        const string frame = "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
        var running =
            $"SELECT id, sum(value) OVER (ORDER BY id ASC NULLS LAST {frame}) AS running " +
            "FROM t ORDER BY id ASC NULLS LAST;";
        const string buffered =
            "SELECT id, sum(value) OVER (ORDER BY id DESC NULLS FIRST) AS running " +
            "FROM t ORDER BY id DESC NULLS FIRST;";
        const string fallback =
            "SELECT DISTINCT sum(value) OVER (ORDER BY id DESC NULLS FIRST) AS running " +
            "FROM t ORDER BY 1 DESC NULLS FIRST;";

        AssertMatchesSqlite(setup, running);
        AssertMatchesSqlite(setup, buffered);
        AssertMatchesSqlite(setup, fallback);

        using var connection = OpenManaged(setup);
        Opcodes(ReadRows(connection, "EXPLAIN " + running))
            .Should().Contain("SorterSort").And.Contain("AggFinalize");
        // The default RANGE frame lowers onto the buffered-window family, whose ORDER BY comparer
        // reuses the evaluator's explicit NULL placement.
        Opcodes(ReadRows(connection, "EXPLAIN " + buffered))
            .Should().Contain("WindowBufferCompute").And.Contain("SorterSort");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + fallback));
    }

    [Test]
    public void PersistedViewRetainsExplicitNullPlacementAfterReopen()
    {
        const string query = "SELECT value FROM ordered_values;";
        string[] setup =
        [
            "CREATE TABLE t(value INTEGER);",
            "INSERT INTO t VALUES (2), (NULL), (1);",
            "CREATE VIEW ordered_values AS SELECT value FROM t ORDER BY value DESC NULLS FIRST;",
        ];
        var expected = RunSqlite(setup, query);
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("null-ordering.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("null-ordering.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        AssertRowsMatch(ReadRows(reopenedConnection, query), expected.Rows);
        ReadRows(
                reopenedConnection,
                "SELECT sql FROM sqlite_master WHERE type = 'view' AND name = 'ordered_values';")[0][0]
            .AsText().Should().Contain("NULLS FIRST");
    }

    [Test]
    public void NullOrderingSyntaxAndRuntimeErrorsPreserveSqlitePrecedence()
    {
        using var managed = OpenManaged(ScalarSetup);

        const string invalidSyntax = "SELECT value FROM t ORDER BY value NULLS MIDDLE;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, invalidSyntax))!
            .Message.Should().Contain("Expected FIRST or LAST");
        SqliteError(ScalarSetup, invalidSyntax).Should().Contain("syntax error");

        const string collationBeforeLimit =
            "SELECT value COLLATE missing AS x FROM t ORDER BY x NULLS LAST LIMIT 'bad';";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, collationBeforeLimit))!
            .Message.Should().Be("no such collation sequence: missing");
        SqliteError(ScalarSetup, collationBeforeLimit).Should().Contain("no such collation sequence");

        const string badLimit = "SELECT value FROM t ORDER BY value NULLS LAST LIMIT 'bad';";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, badLimit))!
            .Message.Should().Be("datatype mismatch");
        SqliteError(ScalarSetup, badLimit).Should().Contain("datatype mismatch");

        const string invalidOrdinal = "SELECT value FROM t ORDER BY 2 COLLATE NOCASE NULLS LAST;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, invalidOrdinal))!
            .Message.Should().Contain("out of range");
        SqliteError(ScalarSetup, invalidOrdinal).Should().Contain("out of range");

        const string negativeOrdinal = "SELECT value FROM t ORDER BY (-1) NULLS LAST;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, negativeOrdinal))!
            .Message.Should().Contain("out of range");
        SqliteError(ScalarSetup, negativeOrdinal).Should().Contain("out of range");

        const string compoundCollationBeforeLimit =
            "SELECT value AS x FROM t UNION ALL SELECT value FROM t WHERE 0 " +
            "ORDER BY x COLLATE missing NULLS LAST LIMIT 'bad';";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, compoundCollationBeforeLimit))!
            .Message.Should().Be("no such collation sequence: missing");
        SqliteError(ScalarSetup, compoundCollationBeforeLimit).Should().Contain("no such collation sequence");

        const string windowCollationBeforeLimit =
            "SELECT sum(value) OVER (ORDER BY value COLLATE missing NULLS LAST) FROM t LIMIT 'bad';";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(managed, windowCollationBeforeLimit))!
            .Message.Should().Be("no such collation sequence: missing");
        SqliteError(ScalarSetup, windowCollationBeforeLimit).Should().Contain("no such collation sequence");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        using var managed = OpenManaged(setup);
        using var statement = managed.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        var rows = DrainRows(statement);
        var expected = RunSqlite(setup, query);

        columns.Should().Equal(expected.Columns);
        AssertRowsMatch(rows, expected.Rows);
    }

    private static void AssertRowsMatch(
        IReadOnlyList<SqlValue[]> managed,
        IReadOnlyList<object?[]> expected)
    {
        managed.Should().HaveCount(expected.Count);
        for (var row = 0; row < expected.Count; row++)
        {
            managed[row].Should().HaveCount(expected[row].Length);
            for (var column = 0; column < expected[row].Length; column++)
                CellShouldMatch(managed[row][column], expected[row][column]);
        }
    }

    private static void CellShouldMatch(SqlValue managed, object? expected)
    {
        switch (expected)
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
                throw new InvalidOperationException($"Unsupported SQLite value type {expected.GetType().Name}.");
        }
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return connection;
    }

    private static (string[] Columns, List<object?[]> Rows) RunSqlite(
        IReadOnlyList<string> setup,
        string query)
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
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return (columns, rows);
    }

    private static string SqliteError(IReadOnlyList<string> setup, string query)
    {
        var exception = Assert.Throws<MsData.SqliteException>(() => RunSqlite(setup, query));
        return exception!.Message;
    }

    private static bool UsesSorter(EmbeddedConnection connection, string query)
    {
        try
        {
            return Opcodes(ReadRows(connection, "EXPLAIN " + query)).Contains("SorterSort");
        }
        catch (EmbeddedSqlException)
        {
            return false;
        }
    }

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
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(EmbeddedStatement statement)
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
