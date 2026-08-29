using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class PlannerAccessPathDepthTests
{
    [Test]
    public void SelectiveAndTermsUseDistinctIndexesAndIntersectBoundedCandidateSets()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX items_a ON items(a);");
        Execute(connection, "CREATE INDEX items_b ON items(b);");
        Execute(
            connection,
            "INSERT INTO items VALUES "
            + string.Join(
                ", ",
                Enumerable.Range(1, 1_000).Select(value =>
                    $"({value}, {value % 100}, {value % 125}, 'p{value}')"))
            + ";");

        var noStatisticsPlan = PlanDetail(
            connection,
            "SELECT id, payload FROM items WHERE a = 7 AND b = 7;");
        PlanDetail(connection, "SELECT id, payload FROM items WHERE a = 7 AND b = 7;")
            .Should().Be(noStatisticsPlan);
        Execute(connection, "ANALYZE;");

        const string sql = "SELECT id, payload FROM items WHERE a = 7 AND b = 7;";
        PlanDetail(connection, sql).Should().Be("MULTI-INDEX AND items (items_a, items_b)");
        ExplainOpenTarget(connection, sql).Should().Contain("USING MULTI-INDEX AND items_a&items_b");

        database.ResetJoinOrderDiagnostics();
        var rows = ReadRows(connection, sql);
        rows.Select(row => row[0].AsInteger()).Should().BeEquivalentTo([7L, 507L]);
        database.PlannerAccessPathMetrics.IntersectionsExecuted.Should().Be(1);
        database.PlannerAccessPathMetrics.IntersectionIndexProbes.Should().Be(2);
        database.PlannerAccessPathMetrics.IntersectionCandidateRows.Should().BeLessThan(30);
    }

    [Test]
    public void IntersectionCostFallsBackForSmallInputsAndCompositePrefixes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE tiny(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER);");
        Execute(connection, "CREATE INDEX tiny_a ON tiny(a);");
        Execute(connection, "CREATE INDEX tiny_b ON tiny(b);");
        Execute(connection, "INSERT INTO tiny VALUES (1,1,1),(2,1,2),(3,2,1),(4,2,2);");
        Execute(connection, "ANALYZE;");

        PlanDetail(connection, "SELECT id FROM tiny WHERE a=1 AND b=1;")
            .Should().NotStartWith("MULTI-INDEX AND");

        Execute(connection, "CREATE INDEX tiny_ab ON tiny(a,b);");
        Execute(connection, "ANALYZE;");
        var composite = PlanDetail(connection, "SELECT id FROM tiny WHERE a=1 AND b=1;");
        composite.Should().NotStartWith("MULTI-INDEX AND");
        composite.Should().Contain("tiny_ab");
    }

    [Test]
    public void ReopenedIntersectionSeeksBothDurableIndexes()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("planner-depth-intersection.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER);");
            Execute(connection, "CREATE INDEX items_a ON items(a);");
            Execute(connection, "CREATE INDEX items_b ON items(b);");
            Execute(
                connection,
                "INSERT INTO items VALUES "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 1_000).Select(value =>
                        $"({value}, {value % 100}, {value % 125})"))
                + ";");
            Execute(connection, "ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("planner-depth-intersection.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        const string sql = "SELECT id FROM items WHERE a=7 AND b=7;";
        PlanDetail(reopenedConnection, sql).Should().Be("MULTI-INDEX AND items (items_a, items_b)");
        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsInteger())
            .Should().BeEquivalentTo([7L, 507L]);
        reopened.PlannerAccessPathMetrics.IntersectionIndexPagesRead.Should().BeGreaterThan(0);
        reopened.PlannerAccessPathMetrics.IntersectionCandidateRows.Should().BeLessThan(30);
    }

    [Test]
    public void Stat4CapturesSkewAndInvalidOrStaleSamplesFallBack()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE skewed(id INTEGER PRIMARY KEY, bucket INTEGER);");
        Execute(connection, "CREATE INDEX skewed_bucket ON skewed(bucket);");
        Execute(
            connection,
            "INSERT INTO skewed VALUES "
            + string.Join(
                ", ",
                Enumerable.Range(1, 1_000).Select(value =>
                    value <= 900
                        ? $"({value}, 0)"
                        : $"({value}, {value - 900})"))
            + ";");
        Execute(connection, "ANALYZE;");

        ReadScalar(connection, "SELECT count(*) FROM sqlite_stat4 WHERE idx='skewed_bucket';")
            .AsInteger().Should().BeGreaterThan(1);

        database.ResetJoinOrderDiagnostics();
        PlanDetail(connection, "SELECT id FROM skewed WHERE bucket=0;")
            .Should().Contain("skewed_bucket");
        var commonEstimate = database.PlannerAccessPathMetrics.LastStat4EstimatedRows;
        commonEstimate.Should().BeGreaterThan(800);

        database.ResetJoinOrderDiagnostics();
        PlanDetail(connection, "SELECT id FROM skewed WHERE bucket=50;")
            .Should().Contain("skewed_bucket");
        database.PlannerAccessPathMetrics.LastStat4EstimatedRows.Should().BeLessThan(commonEstimate);

        database.RegisterCollation("BINARY", string.CompareOrdinal);
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, "SELECT id FROM skewed WHERE bucket=50;").Should().ContainSingle();
        database.PlannerAccessPathMetrics.Stat4EstimatesUsed.Should().Be(0);
        database.UnregisterCollation("BINARY").Should().BeTrue();

        Execute(connection, "INSERT INTO skewed VALUES (1001, 50);");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, "SELECT id FROM skewed WHERE bucket=50;")
            .Select(row => row[0].AsInteger()).Should().BeEquivalentTo([950L, 1001L]);
        database.PlannerAccessPathMetrics.Stat4EstimatesUsed.Should().Be(0);

        Execute(connection, "ANALYZE;");
        Execute(connection, "DROP INDEX skewed_bucket;");
        Execute(connection, "CREATE INDEX skewed_bucket ON skewed(id);");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, "SELECT bucket FROM skewed WHERE id=5;")
            .Single()[0].AsInteger().Should().Be(0);
        database.PlannerAccessPathMetrics.Stat4EstimatesUsed.Should().Be(0);

        Execute(connection, "ANALYZE;");
        Execute(connection, "UPDATE sqlite_stat4 SET neq='broken' WHERE idx='skewed_bucket';");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, "SELECT bucket FROM skewed WHERE id=5;").Should().ContainSingle();
        database.PlannerAccessPathMetrics.Stat4EstimatesUsed.Should().Be(0);
    }

    [Test]
    public void Stat4AnalysisAndPlanningUseBoundedTablePasses()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE measured(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER);");
        Execute(connection, "CREATE INDEX measured_ab ON measured(a, b);");
        Execute(
            connection,
            "INSERT INTO measured "
            + "SELECT value, value % 101, value % 137 FROM generate_series(1, 10000);");

        database.ResetJoinOrderDiagnostics();
        Execute(connection, "ANALYZE;");
        database.PlannerAccessPathMetrics.Stat4AnalysisRowsScanned.Should().Be(20000);

        database.ResetJoinOrderDiagnostics();
        for (var iteration = 0; iteration < 5; iteration++)
            PlanDetail(connection, "SELECT id FROM measured WHERE a=7;").Should().Contain("measured_ab");

        database.PlannerAccessPathMetrics.Stat4RowIdMapBuilds.Should().Be(1);
        database.PlannerAccessPathMetrics.Stat4RowIdsIndexed.Should().Be(10000);
    }

    [Test]
    public void Stat4CompositeVectorsMatchSortedPrefixRuns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE vectors(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER);");
        Execute(connection, "CREATE INDEX vectors_ab ON vectors(a, b);");
        Execute(
            connection,
            "INSERT INTO vectors VALUES (1,1,1),(2,1,1),(3,1,2),(4,2,1);");
        Execute(connection, "ANALYZE;");

        ReadRows(
                connection,
                "SELECT neq, nlt, ndlt FROM sqlite_stat4 "
                + "WHERE idx='vectors_ab' ORDER BY rowid;")
            .Select(row => string.Join("|", row.Select(value => value.AsText())))
            .Should().Equal(
                "3 2|0 0|0 0",
                "3 2|0 0|0 0",
                "3 1|0 2|0 1",
                "1 1|3 3|1 2");
    }

    [Test]
    public void ProfitableInnerJoinBuildsOneAutomaticCoveringIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(
            connection,
            "INSERT INTO outer_items VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'o{value}')"))
            + ";");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'i{value}')"))
            + ";");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT outer_items.k, inner_items.payload
            FROM outer_items JOIN inner_items ON outer_items.k=inner_items.k
            ORDER BY outer_items.k;
            """;
        PlanDetail(connection, sql).Should().Be(
            "SEARCH inner_items USING AUTOMATIC COVERING INDEX (k=?)");

        database.ResetJoinOrderDiagnostics();
        var rows = ReadRows(connection, sql);
        rows.Should().HaveCount(100);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("i1"));
        rows[^1].Should().Equal(SqlValue.Integer(100), SqlValue.Text("i100"));
        database.JoinIndexSeekMetrics.AutomaticIndexesBuilt.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(100);
        database.JoinIndexSeekMetrics.CandidateRowsVisited.Should().Be(100);
    }

    [Test]
    public void AutomaticIndexesDoNotCrossOuterNaturalUsingOrNotIndexedBarriers()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE TABLE r(k INTEGER, payload TEXT);");
        Execute(
            connection,
            "INSERT INTO l VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'l{value}')"))
            + ";");
        Execute(
            connection,
            "INSERT INTO r VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'r{value}')"))
            + ";");
        Execute(connection, "ANALYZE;");

        string[] queries =
        [
            "SELECT l.k FROM l LEFT JOIN r ON l.k=r.k ORDER BY l.k;",
            "SELECT k FROM l NATURAL JOIN r ORDER BY k;",
            "SELECT k FROM l JOIN r USING(k) ORDER BY k;",
        ];
        foreach (var query in queries)
            PlanDetails(connection, query).Should().NotContain(detail => detail.Contains("AUTOMATIC", StringComparison.Ordinal));

        PlanDetails(
                connection,
                "SELECT l.k FROM l JOIN r NOT INDEXED ON l.k=r.k ORDER BY l.k;")
            .Should().NotContain(detail =>
                detail.StartsWith("SEARCH r USING AUTOMATIC", StringComparison.Ordinal));
    }

    [Test]
    public void AutomaticCompositeCandidateCanUseOnlyItsCurrentlyBoundPrefix()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE first_keys(k1 INTEGER);");
        Execute(connection, "CREATE TABLE target(k1 INTEGER, k2 INTEGER, payload TEXT);");
        Execute(connection, "CREATE TABLE second_keys(k2 INTEGER);");
        Execute(
            connection,
            "INSERT INTO first_keys VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value})"))
            + ";");
        Execute(
            connection,
            "INSERT INTO target VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, {value}, 'p{value}')"))
            + ";");
        Execute(
            connection,
            "INSERT INTO second_keys VALUES "
            + string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value})"))
            + ";");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT target.payload
            FROM first_keys
            JOIN target ON first_keys.k1=target.k1
            JOIN second_keys ON target.k2=second_keys.k2
            ORDER BY target.k1;
            """;
        PlanDetails(connection, sql).Should().Contain(detail =>
            detail.StartsWith("SEARCH target USING AUTOMATIC COVERING INDEX", StringComparison.Ordinal));
        ReadRows(connection, sql).Select(row => row[0].AsText())
            .Should().Equal(Enumerable.Range(1, 100).Select(value => $"p{value}"));
    }

    [Test]
    public void ReopenedDatabaseUsesPagerIndexCursorAndDeferredRowidFetch()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("planner-depth.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "CREATE INDEX inner_items_k_payload ON inner_items(k,payload);");
            Execute(connection, "INSERT INTO outer_items VALUES (3),(197),(499);");
            Execute(
                connection,
                "INSERT INTO inner_items VALUES "
                + string.Join(", ", Enumerable.Range(1, 1_000).Select(value => $"({value}, 'p{value}')"))
                + ";");
            Execute(connection, "ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("planner-depth.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k=inner_items.k
            ORDER BY outer_items.k;
            """;
        PlanDetail(reopenedConnection, sql).Should().Be(
            "SEARCH inner_items USING INDEX inner_items_k (k=?)");
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText())
            .Should().Equal("p3", "p197", "p499");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
        reopened.JoinIndexSeekMetrics.TableRowsFetched.Should().Be(3);
        reopened.JoinIndexSeekMetrics.IndexPagesRead.Should().BeLessThan(50);
        reopened.JoinIndexSeekMetrics.CandidateRowsVisited.Should().Be(3);

        const string coveringSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k_payload ON outer_items.k=inner_items.k
            ORDER BY outer_items.k;
            """;
        PlanDetail(reopenedConnection, coveringSql).Should().Be(
            "SEARCH inner_items USING COVERING INDEX inner_items_k_payload (k=?)");
        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, coveringSql).Select(row => row[0].AsText())
            .Should().Equal("p3", "p197", "p499");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        reopened.JoinIndexSeekMetrics.TableRowsFetched.Should().Be(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void TransactionLocalRowsUseMaterializedIndexFallback()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("planner-depth-transaction.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
        Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES (2);");
        Execute(connection, "INSERT INTO inner_items VALUES (1,'one'),(2,'two');");
        Execute(connection, "ANALYZE;");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO inner_items VALUES (2,'two-local');");

        database.ResetJoinOrderDiagnostics();
        ReadRows(
                connection,
                """
                SELECT inner_items.payload
                FROM outer_items
                JOIN inner_items INDEXED BY inner_items_k ON outer_items.k=inner_items.k
                ORDER BY inner_items.payload;
                """)
            .Select(row => row[0].AsText())
            .Should().Equal("two", "two-local");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(3);
        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void PagerIndexCursorWalksDuplicatePrefixesAcrossInteriorSeparators()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("planner-depth-duplicates.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE outer_items(k INTEGER);");
            Execute(connection, "CREATE TABLE inner_items(k INTEGER, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "INSERT INTO outer_items VALUES (7);");
            Execute(
                connection,
                "INSERT INTO inner_items VALUES "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 1_000).Select(value =>
                        value <= 700
                            ? $"(7, 'match-{value}')"
                            : $"({value}, 'noise-{value}')"))
                + ";");
            Execute(connection, "ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("planner-depth-duplicates.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.ResetJoinOrderDiagnostics();
        ReadRows(
                reopenedConnection,
                """
                SELECT inner_items.payload
                FROM outer_items
                JOIN inner_items INDEXED BY inner_items_k ON outer_items.k=inner_items.k;
                """)
            .Should().HaveCount(700);
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        reopened.JoinIndexSeekMetrics.CandidateRowsVisited.Should().Be(700);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    private static string ExplainOpenTarget(EmbeddedConnection connection, string sql)
        => ReadRows(connection, "EXPLAIN " + sql)
            .Single(row => row[1].AsText() == "OpenReadCursor")[5]
            .AsText();

    private static string PlanDetail(EmbeddedConnection connection, string sql)
        => PlanDetails(connection, sql).Single();

    private static string[] PlanDetails(EmbeddedConnection connection, string sql)
        => ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .ToArray();

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single()[0];

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
