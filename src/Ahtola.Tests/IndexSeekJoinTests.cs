using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class IndexSeekJoinTests
{
    [Test]
    public void AnalyzedSecondaryIndexJoinPerformsBoundedOuterRowSeeks()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER, wanted TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (3, 'p3'), (197, 'p197'), (499, 'p499'), (NULL, 'none');");
        for (var value = 1; value <= 500; value++)
            Execute(connection, $"INSERT INTO inner_items VALUES ({value}, 'p{value}');");
        Execute(connection, "ANALYZE;");

        var explain = ReadRows(
            connection,
            """
            EXPLAIN SELECT outer_items.k, inner_items.payload
            FROM outer_items JOIN inner_items INDEXED BY inner_items_k
            ON outer_items.k = inner_items.k AND outer_items.wanted = inner_items.payload;
            """);
        explain.Single(row => row[1].AsText() == "OpenJoinCursor")[5].AsText()
            .Should().Contain("index-seek inner_items USING INDEX inner_items_k (k=?)");

        ReadRows(
                connection,
                """
                EXPLAIN QUERY PLAN SELECT outer_items.k, inner_items.payload
                FROM outer_items JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k;
                """)
            .Should().ContainSingle()
            .Which[3].AsText().Should().Be("SEARCH inner_items USING INDEX inner_items_k (k=?)");
        database.JoinIndexSeekMetrics.PlansCreated.Should().Be(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        database.ResetJoinOrderDiagnostics();
        var rows = ReadRows(
            connection,
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items JOIN inner_items INDEXED BY inner_items_k
            ON outer_items.k = inner_items.k AND outer_items.wanted = inner_items.payload
            ORDER BY outer_items.k;
            """);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(3), SqlValue.Text("p3"));
        rows[2].Should().Equal(SqlValue.Integer(499), SqlValue.Text("p499"));
        database.JoinIndexSeekMetrics.SeeksAttempted.Should().Be(4);
        database.JoinIndexSeekMetrics.CandidateRowsVisited.Should().Be(3);
        database.JoinIndexSeekMetrics.KeyComparisons.Should().BeLessThan(60);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(500);
    }

    [Test]
    public void CompositeCollatedPrefixWithDuplicatesAndResidualMatchesSqlite()
    {
        var innerValues = string.Join(
            ", ",
            Enumerable.Range(1, 600).Select(value => $"('noise{value}', 'z', 'n{value}')"));
        string[] setup =
        [
            "CREATE TABLE outer_items(a TEXT COLLATE NOCASE, b TEXT COLLATE RTRIM, wanted TEXT);",
            "CREATE TABLE inner_items(a TEXT COLLATE NOCASE, b TEXT COLLATE RTRIM, payload TEXT);",
            "CREATE INDEX inner_items_ab ON inner_items(a COLLATE NOCASE, b COLLATE RTRIM);",
            "INSERT INTO outer_items VALUES ('ALPHA', 'x  ', 'keep'), ('beta', 'y', 'second'), (NULL, 'x', 'none');",
            $"INSERT INTO inner_items VALUES {innerValues};",
            "INSERT INTO inner_items VALUES ('alpha', 'x', 'keep'), ('Alpha', 'x ', 'drop'), ('BETA', 'y   ', 'second');",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT outer_items.a, outer_items.b, inner_items.payload
            FROM outer_items JOIN inner_items INDEXED BY inner_items_ab
            ON outer_items.a = inner_items.a
               AND outer_items.b = inner_items.b
               AND outer_items.wanted = inner_items.payload
            ORDER BY inner_items.payload;
            """;

        AssertMatchesSqlite(setup, sql);
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var detail = ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText();
        detail.Should().Be("SEARCH inner_items USING INDEX inner_items_ab (a=?, b=?)");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Should().HaveCount(2);
        database.JoinIndexSeekMetrics.SeeksAttempted.Should().Be(3);
        database.JoinIndexSeekMetrics.CandidateRowsVisited.Should().Be(3);
    }

    [Test]
    public void NumericOuterKeySafelyReceivesIndexedIntegerAffinity()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(k TEXT);",
            "CREATE TABLE inner_items(k INTEGER, payload TEXT);",
            "CREATE INDEX inner_items_k ON inner_items(k);",
            "INSERT INTO outer_items VALUES ('01'), ('2'), (NULL);",
            "INSERT INTO inner_items VALUES "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 300).Select(value => $"({value}, 'p{value}')"))
                + ";",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items JOIN inner_items INDEXED BY inner_items_k
            ON outer_items.k = inner_items.k
            ORDER BY inner_items.k;
            """;

        AssertMatchesSqlite(setup, sql);
        using var connection = OpenManaged(setup);
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Contain("SEARCH inner_items USING INDEX inner_items_k");
    }

    [TestCase(false, "CREATE INDEX inner_items_a ON inner_items(a);", "outer_items.a = inner_items.a")]
    [TestCase(true, "CREATE INDEX inner_items_ab ON inner_items(a, b);", "outer_items.b = inner_items.b")]
    [TestCase(true, "CREATE INDEX inner_items_a ON inner_items(a) WHERE b > 0;", "outer_items.a = inner_items.a")]
    [TestCase(true, "CREATE INDEX inner_items_expr ON inner_items(lower(a));", "outer_items.a = inner_items.a")]
    [TestCase(true, "CREATE INDEX inner_items_a ON inner_items(a COLLATE NOCASE);", "outer_items.a = inner_items.a COLLATE BINARY")]
    public void UnsupportedIndexShapesFallBackWithoutAdvertisingSearch(
        bool analyze,
        string createIndex,
        string condition)
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE outer_items(a TEXT, b INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(a TEXT, b INTEGER);");
        Execute(connection, createIndex);
        Execute(connection, "INSERT INTO outer_items VALUES ('x', 1);");
        Execute(connection, "INSERT INTO inner_items VALUES ('x', 1), ('y', 2), ('z', 3);");
        if (analyze)
            Execute(connection, "ANALYZE;");

        var detail = ReadRows(
            connection,
            $"EXPLAIN QUERY PLAN SELECT outer_items.a FROM outer_items JOIN inner_items ON {condition};");
        detail.Select(row => row[3].AsText()).Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(
                connection,
                $"SELECT outer_items.a FROM outer_items JOIN inner_items ON {condition};")
            .Should().ContainSingle();
    }

    [Test]
    public void NotIndexedAndUnsafeInnerAffinityRetainFallbackPlans()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (1), (2);");
        Execute(connection, "INSERT INTO inner_items VALUES ('1', 'one'), ('2', 'two'), ('3', 'three');");
        Execute(connection, "ANALYZE;");

        var unsafeAffinity = ReadRows(
            connection,
            """
            EXPLAIN QUERY PLAN SELECT inner_items.payload
            FROM outer_items JOIN inner_items ON outer_items.k = inner_items.k;
            """);
        unsafeAffinity.Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));

        var notIndexed = ReadRows(
            connection,
            """
            EXPLAIN QUERY PLAN SELECT inner_items.payload
            FROM outer_items JOIN inner_items NOT INDEXED ON outer_items.k = inner_items.k;
            """);
        notIndexed.Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
    }

    [Test]
    public void IndexedByAndDescendingIndexOrderDriveTheChosenSeek()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k_desc ON inner_items(k DESC);");
        Execute(connection, "CREATE INDEX inner_items_payload ON inner_items(payload);");
        Execute(connection, "INSERT INTO outer_items VALUES (2), (250), (499);");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES "
            + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"({value}, 'p{value}')"))
            + ";");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k_desc ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH inner_items USING INDEX inner_items_k_desc (k=?)");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("p2", "p250", "p499");
        database.JoinIndexSeekMetrics.CandidateRowsVisited.Should().Be(3);
    }

    [Test]
    public void IndexedByDeclinesCompiledJoinWhenTheNamedIndexCannotDriveTheEquality()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_payload ON inner_items(payload);");
        Execute(connection, "INSERT INTO outer_items VALUES (2);");
        Execute(connection, "INSERT INTO inner_items VALUES (1, 'one'), (2, 'two');");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_payload ON outer_items.k = inner_items.k;
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("two");
        database.JoinIndexSeekMetrics.PlansCreated.Should().Be(0);
    }

    [Test]
    public void DurableReopenRetainsJoinSearchAndRows()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("index-seek-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "INSERT INTO outer_items VALUES (2), (40);");
            Execute(
                connection,
                "INSERT INTO inner_items VALUES "
                + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"({value}, 'p{value}')"))
                + ";");
            Execute(connection, "ANALYZE;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("index-seek-join.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            const string sql =
                """
                SELECT inner_items.payload
                FROM outer_items JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k
                ORDER BY outer_items.k;
                """;
            ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
                .Should().Be("SEARCH inner_items USING INDEX inner_items_k (k=?)");
            ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("p2", "p40");
        }
    }

    [Test]
    public void IndexSeekOperatorPreservesLeftNullExtension()
    {
        var metrics = new VdbeJoinIndexSeekMetrics();
        var right = new VdbeJoinIndexScanPlan(
            "r",
            "r_k",
            "SEARCH r USING INDEX r_k (k=?)",
            columnCount: 1,
            new VdbeCursorSource([[SqlValue.Integer(2)]]),
            [[SqlValue.Integer(2)]],
            outer => outer.Values[0].Kind == SqlValueKind.Null ? null : [outer.Values[0]],
            (stored, seek) => stored[0].AsInteger().CompareTo(seek[0].AsInteger()),
            metrics);
        var plan = new VdbeJoinOperatorPlan(
            new VdbeJoinScanPlan(
                "l",
                1,
                new VdbeCursorSource(
                [
                    [SqlValue.Integer(1)],
                    [SqlValue.Integer(2)],
                    [SqlValue.Null],
                ])),
            right,
            VdbeJoinKind.Left,
            (left, candidate, _) => left.Values[0] == candidate.Values[0]);

        var rows = plan.Materialize(maximumRows: null);
        rows.Should().HaveCount(3);
        rows[0].Values.Should().Equal(SqlValue.Integer(1), SqlValue.Null);
        rows[1].Values.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));
        rows[2].Values.Should().Equal(SqlValue.Null, SqlValue.Null);
        metrics.SeeksAttempted.Should().Be(3);
        metrics.CandidateRowsVisited.Should().Be(1);
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string sql)
    {
        using var managed = OpenManaged(setup);
        var managedRows = ReadRows(managed, sql);
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var query = sqlite.CreateCommand();
        query.CommandText = sql;
        using var reader = query.ExecuteReader();
        var sqliteRows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            sqliteRows.Add(row);
        }

        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column], row, column);
        }
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return connection;
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite, int row, int column)
    {
        switch (sqlite)
        {
            case null:
                managed.Should().Be(SqlValue.Null, $"at row {row}, column {column}");
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer), $"at row {row}, column {column}");
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real), $"at row {row}, column {column}");
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text), $"at row {row}, column {column}");
                break;
            case byte[] blob:
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new InvalidOperationException($"Unsupported SQLite value type {sqlite.GetType()}.");
        }
    }

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
}
