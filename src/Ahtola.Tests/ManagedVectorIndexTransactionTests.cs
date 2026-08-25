using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Transaction, savepoint and MVCC behaviour for the vector index. Every durable byte is either the
/// catalog row or the base rows, both of which the engine already keeps snapshot isolated, so the
/// suite's job is to prove no method-visible state survives a rollback.
/// </summary>
public sealed class ManagedVectorIndexTransactionTests
{
    private const int Dimensions = 8;
    private const string Query = "vector32('[2,-3,4,1,-1,0,3,-2]')";

    private static EmbeddedConnection Seed(EmbeddedDatabase database)
    {
        var connection = database.Connect();
        SeedCorpus(
            connection,
            GenerateClusteredVectors(600, Dimensions, seed: 424242),
            VectorTestEncoding.Float32,
            VectorTestMetric.L2,
            Dimensions);
        return connection;
    }

    private static void AssertAgreesWithScan(EmbeddedConnection connection, int limit = 10)
        => QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT {limit};")
            .Should().Equal(QueryIntegers(
                connection,
                $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT {limit};"));

    [Test]
    public void ARolledBackTransactionLeavesNoMethodState()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var baseline = QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs VALUES (700001, vector32('[2,-3,4,1,-1,0,3,-2]'));");
        QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;")
            .Should().Contain(700001);
        Execute(connection, "ROLLBACK;");

        QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;")
            .Should().Equal(baseline);
        AssertAgreesWithScan(connection);
    }

    [Test]
    public void SavepointsRollBackMethodStateWithTheRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var baseline = QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;");

        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT outer_point;");
        Execute(connection, "INSERT INTO docs VALUES (700002, vector32('[2,-3,4,1,-1,0,3,-2]'));");
        Execute(connection, "SAVEPOINT inner_point;");
        Execute(connection, "DELETE FROM docs WHERE id = 1;");
        Execute(connection, "ROLLBACK TO inner_point;");
        QueryIntegers(connection, "SELECT count(*) FROM docs WHERE id = 1;").Should().Equal(1);
        Execute(connection, "ROLLBACK TO outer_point;");
        Execute(connection, "RELEASE outer_point;");
        Execute(connection, "COMMIT;");

        QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;")
            .Should().Equal(baseline);
        AssertAgreesWithScan(connection);
    }

    [Test]
    public void ADroppedIndexIsRestoredByRollingBackTheDdl()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");

        Execute(connection, "BEGIN;");
        Execute(connection, "DROP INDEX docs_knn;");
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");
        Execute(connection, "ROLLBACK;");

        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");
        AssertAgreesWithScan(connection);
    }

    [Test]
    public void ACreatedIndexDisappearsWhenItsDdlRollsBack()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4);");
        Execute(connection, "ROLLBACK;");

        QueryIntegers(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'docs_knn';").Should().Equal(0);
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 8, lists = 4);");
        Query(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_knn';")[0][0].AsText()
            .Should().Contain("dims = 8");
    }

    [Test]
    public void MvccIsDeclaredAsATransactionalBackingStore()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");

        var attachment = Core.Indexing.ManagedIndexMethodRegistry.Resolve("vector").Attach(
            new Core.Indexing.ManagedIndexMethodConfiguration(
                "docs",
                "docs_knn",
                [new Core.Indexing.ManagedIndexMethodColumn("embedding", 1)],
                [new Core.Indexing.ManagedIndexMethodParameter("dims", SqlValue.Integer(4))]));

        attachment.Definition.MvccSupport
            .Should().Be(Core.Indexing.ManagedIndexMethodMvccSupport.TransactionalBackingStore);
        attachment.Definition.BackingBtree.Should().BeTrue();
        attachment.Definition.ResultsMaterialized.Should().BeTrue();
        attachment.Definition.Patterns.Select(static pattern => pattern.Shape)
            .Should().Equal(
                Core.Indexing.ManagedIndexPatternShape.KnnLimit,
                Core.Indexing.ManagedIndexPatternShape.Knn);

        var act = () => Core.Indexing.ManagedIndexMethodMvcc.Ensure(attachment.Definition, mvccEnabled: true, forWrite: true);
        act.Should().NotThrow();
    }

    [Test]
    public void MvccWritesAndReadsStayCorrectThroughTheScalarPath()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        Execute(connection, "PRAGMA journal_mode = mvcc;");
        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO docs VALUES (700003, vector32('[2,-3,4,1,-1,0,3,-2]'));");
        Execute(connection, "INSERT INTO plain VALUES (700003, vector32('[2,-3,4,1,-1,0,3,-2]'));");
        AssertAgreesWithScan(connection);
        Execute(connection, "COMMIT;");

        AssertAgreesWithScan(connection);
    }

    [Test]
    public void AForkedAttachmentStartsWithNoPostings()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);

        var configuration = new Core.Indexing.ManagedIndexMethodConfiguration(
            "docs",
            "docs_knn",
            [new Core.Indexing.ManagedIndexMethodColumn("embedding", 1)],
            [new Core.Indexing.ManagedIndexMethodParameter("dims", SqlValue.Integer(8))]);
        var attachment = (Core.Vectors.ManagedVectorIndexAttachment)
            Core.Indexing.ManagedIndexMethodRegistry.Resolve("vector").Attach(configuration);

        var source = new ArrayManagedIndexSource();
        source.Upsert(1, SqlValue.Null, Query(connection, "SELECT vector32('[1,2,3,4,5,6,7,8]');")[0][0]);
        using (var cursor = attachment.Open(source))
            cursor.OpenRead();

        attachment.Index.IndexedRowCount.Should().Be(1);

        var forked = (Core.Vectors.ManagedVectorIndexAttachment)attachment.Fork();
        forked.Index.IndexedRowCount.Should().Be(0);

        // The fork carries the trained centroids so a rollback does not silently re-cluster, but it
        // carries none of the derived placements.
        forked.Index.IsTrained.Should().Be(attachment.Index.IsTrained);
    }
}
