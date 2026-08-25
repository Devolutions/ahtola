using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Vectors;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Maintenance for the vector index: incremental DML, reused rowids, REINDEX, OPTIMIZE, DROP, and
/// the rule that drift may cost speed but never recall.
/// </summary>
public sealed class ManagedVectorIndexMaintenanceTests
{
    private const int Dimensions = 8;
    private const string Query = "vector32('[2,-3,4,1,-1,0,3,-2]')";

    private static EmbeddedConnection Seed(EmbeddedDatabase database, int rows = 600)
    {
        var connection = database.Connect();
        SeedCorpus(
            connection,
            GenerateClusteredVectors(rows, Dimensions, seed: 909090),
            VectorTestEncoding.Float32,
            VectorTestMetric.L2,
            Dimensions);
        return connection;
    }

    private static void AssertAgreesWithScan(EmbeddedConnection connection, int limit = 10)
    {
        QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT {limit};")
            .Should().Equal(QueryIntegers(
                connection,
                $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT {limit};"));
    }

    [Test]
    public void IncrementalDmlKeepsTheAnswerExact()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        AssertAgreesWithScan(connection);

        foreach (var sql in new[]
                 {
                     "INSERT INTO {0} VALUES (900001, vector32('[2,-3,4,1,-1,0,3,-2]'));",
                     "UPDATE {0} SET embedding = vector32('[9,9,9,9,9,9,9,9]') WHERE id = 5;",
                     "DELETE FROM {0} WHERE id = 12;",
                     "INSERT OR REPLACE INTO {0} VALUES (7, vector32('[2,-3,4,1,-1,0,3,-1.9]'));",
                     "INSERT INTO {0} VALUES (8, vector32('[1,1,1,1,1,1,1,1]')) ON CONFLICT(id) DO UPDATE SET embedding = excluded.embedding;",
                     "REPLACE INTO {0} VALUES (900001, vector32('[2,-3,4,1,-1,0,3,-2.01]'));",
                 })
        {
            Execute(connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, "docs"));
            Execute(connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, "plain"));
            AssertAgreesWithScan(connection);
        }
    }

    [Test]
    public void AReusedRowIdCannotResurrectAnOldAssignment()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);

        // The rowid-to-placement map is the authority for membership, so deleting a row removes its
        // posting outright rather than leaving a tombstone a later row with the same rowid could
        // inherit.
        for (var round = 0; round < 5; round++)
        {
            Execute(connection, "DELETE FROM docs WHERE id = 42;");
            Execute(connection, "DELETE FROM plain WHERE id = 42;");
            AssertAgreesWithScan(connection);

            var replacement = $"vector32('[{round},{round},{round},{round},{round},{round},{round},{round}]')";
            Execute(connection, $"INSERT INTO docs VALUES (42, {replacement});");
            Execute(connection, $"INSERT INTO plain VALUES (42, {replacement});");
            AssertAgreesWithScan(connection);
        }
    }

    [Test]
    public void TriggersAndForeignKeyCascadesAreSeenByTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database, rows: 300);
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE owners(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE links(id INTEGER PRIMARY KEY, owner INTEGER REFERENCES owners(id) ON DELETE CASCADE);");
        Execute(connection, "INSERT INTO owners VALUES (1);");
        Execute(connection, "INSERT INTO links VALUES (1, 1);");
        Execute(
            connection,
            """
            CREATE TRIGGER link_removed AFTER DELETE ON links BEGIN
              DELETE FROM docs WHERE id = 3;
              DELETE FROM plain WHERE id = 3;
            END;
            """);

        Execute(connection, "DELETE FROM owners WHERE id = 1;");
        QueryIntegers(connection, "SELECT count(*) FROM docs WHERE id = 3;").Should().Equal(0);
        AssertAgreesWithScan(connection);
    }

    [Test]
    public void ReindexRetrainsAndKeepsTheAnswerExact()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);

        // Move the whole corpus somewhere else so the trained centroids no longer describe it, then
        // prove the answer is still exact both before and after the retrain.
        Execute(connection, "UPDATE docs SET embedding = vector32('[50,50,50,50,50,50,50,50]') WHERE id % 3 = 0;");
        Execute(connection, "UPDATE plain SET embedding = vector32('[50,50,50,50,50,50,50,50]') WHERE id % 3 = 0;");
        AssertAgreesWithScan(connection);

        Execute(connection, "REINDEX docs_knn;");
        AssertAgreesWithScan(connection);

        Execute(connection, "REINDEX;");
        AssertAgreesWithScan(connection);
    }

    [Test]
    public void OptimizeCompactsWithoutChangingTheAnswer()
    {
        // Optimize is reachable only through the method-index opcode, never from SQL and never
        // inline in DML, so it is driven at the cursor level the way the bytecode path drives it.
        var attachment = (ManagedVectorIndexAttachment)ManagedIndexMethodRegistry.Resolve("vector").Attach(
            new ManagedIndexMethodConfiguration(
                "points",
                "points_knn",
                [new ManagedIndexMethodColumn("embedding", 0)],
                [
                    new ManagedIndexMethodParameter("dims", SqlValue.Integer(4)),
                    new ManagedIndexMethodParameter("lists", SqlValue.Integer(8)),
                ]));

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var vectors = GenerateClusteredVectors(200, 4, seed: 1717);
        var source = new ArrayManagedIndexSource();
        for (var index = 0; index < vectors.Length; index++)
            source.Upsert(index + 1, Query(connection, $"SELECT vector32('{Literal(vectors[index])}');")[0][0]);

        using (var cursor = attachment.Open(source))
            cursor.OpenRead();

        for (var id = 1; id <= 150; id++)
            source.Remove(id);

        using (var cursor = attachment.Open(source))
            cursor.OpenRead();

        var before = attachment.Index.IndexedRowCount;
        var radiusBefore = attachment.Index.MaximumRadius;
        before.Should().Be(50);

        using (var cursor = attachment.Open(source))
            cursor.Optimize();

        // Compaction reclaims posting slots without changing membership, and the recomputed radii
        // are exact maxima over the live members, so a bound queries rely on can only ever tighten.
        attachment.Index.IndexedRowCount.Should().Be(before);
        attachment.Index.NeedsCompaction.Should().BeFalse();
        attachment.Index.MaximumRadius.Should().BeLessThanOrEqualTo(radiusBefore);
    }

    [Test]
    public void DroppingTheIndexDestroysItsStateAndRestoresTheScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");

        Execute(connection, "DROP INDEX docs_knn;");
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");
        QueryIntegers(connection, sql).Should().Equal(QueryIntegers(
            connection,
            sql.Replace("FROM docs", "FROM plain", StringComparison.Ordinal)));

        // Recreating under the same name with different options must not resurrect the old ones.
        Execute(
            connection,
            "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 8, lists = 32, metric = 'cosine', min_rows = 8);");
        ExplainDetail(
                connection,
                $"SELECT id FROM docs ORDER BY vector_distance_cos(embedding, {Query}) LIMIT 5;")
            .Should().Contain("metric=cosine").And.Contain("lists=32");
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void GrowingTheTableRetrainsWithoutLosingRecall()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database, rows: 200);
        AssertAgreesWithScan(connection);

        var extra = GenerateClusteredVectors(1400, Dimensions, seed: 5252);
        Execute(connection, "BEGIN;");
        for (var index = 0; index < extra.Length; index++)
        {
            var literal = $"vector32('{Literal(extra[index])}')";
            Execute(connection, $"INSERT INTO docs VALUES ({10000 + index}, {literal});");
            Execute(connection, $"INSERT INTO plain VALUES ({10000 + index}, {literal});");
        }

        Execute(connection, "COMMIT;");
        AssertAgreesWithScan(connection);
        AssertAgreesWithScan(connection, limit: 50);
    }

    [Test]
    public void AFullRebuildAndAnIncrementalDeltaAgree()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        Execute(connection, "UPDATE docs SET embedding = vector32('[7,7,7,7,7,7,7,7]') WHERE id IN (1, 2, 3);");
        Execute(connection, "UPDATE plain SET embedding = vector32('[7,7,7,7,7,7,7,7]') WHERE id IN (1, 2, 3);");
        var incremental = QueryIntegers(
            connection,
            $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 20;");

        Execute(connection, "REINDEX docs_knn;");
        QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 20;")
            .Should().Equal(incremental);
    }
}
