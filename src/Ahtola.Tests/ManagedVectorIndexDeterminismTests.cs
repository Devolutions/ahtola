using Ahtola.Core;
using Ahtola.Core.Vectors;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Determinism guarantees for the vector index: the trained centroids are persisted in the catalog,
/// so identical inputs have to produce identical bytes on every platform and in every process.
/// </summary>
[NonParallelizable]
public sealed class ManagedVectorIndexDeterminismTests
{
    [Test]
    public void TheGeneratorIsSeededNotAmbient()
    {
        var first = Draw(new ManagedVectorRandom(0));
        var second = Draw(new ManagedVectorRandom(0));
        first.Should().Equal(second);

        Draw(new ManagedVectorRandom(1)).Should().NotEqual(first);
        Draw(new ManagedVectorRandom(long.MinValue)).Should().NotEqual(first);

        // A zero seed must not degenerate: xoshiro is undefined for an all-zero state and the
        // SplitMix64 expansion is what keeps it out of that hole.
        first.Distinct().Should().HaveCountGreaterThan(1);
    }

    [Test]
    public void DerivedSeedsDependOnlyOnTheirInputs()
    {
        ManagedVectorRandom.DeriveSeed(7, "docs_knn|L2|Float32|8|64|10|32768")
            .Should().Be(ManagedVectorRandom.DeriveSeed(7, "docs_knn|L2|Float32|8|64|10|32768"));
        ManagedVectorRandom.DeriveSeed(7, "docs_knn|L2|Float32|8|64|10|32768")
            .Should().NotBe(ManagedVectorRandom.DeriveSeed(8, "docs_knn|L2|Float32|8|64|10|32768"));
        ManagedVectorRandom.DeriveSeed(7, "docs_knn|L2|Float32|8|64|10|32768")
            .Should().NotBe(ManagedVectorRandom.DeriveSeed(7, "docs_knn|COS|Float32|8|64|10|32768"));
    }

    [Test]
    public void TwoDatabasesBuiltTheSameWayCarryIdenticalState()
    {
        var vectors = GenerateClusteredVectors(400, 8, seed: 8888);
        BuildEnvelope(vectors, permuteInsertOrder: false)
            .Should().Be(BuildEnvelope(vectors, permuteInsertOrder: false));
    }

    [Test]
    public void InsertionOrderDoesNotChangeTheTrainedCentroids()
    {
        // Training walks rowids, not storage positions, so the same rows inserted in a different
        // order have to train to the same centroids.
        var vectors = GenerateClusteredVectors(400, 8, seed: 4321);
        BuildEnvelope(vectors, permuteInsertOrder: true)
            .Should().Be(BuildEnvelope(vectors, permuteInsertOrder: false));
    }

    [Test]
    public void DifferentSeedsTrainDifferentlyButAnswerIdentically()
    {
        var vectors = GenerateClusteredVectors(600, 8, seed: 202);
        BuildEnvelope(vectors, permuteInsertOrder: false, seed: 1)
            .Should().NotBe(BuildEnvelope(vectors, permuteInsertOrder: false, seed: 2));

        // Exactness does not depend on how well the clustering happened to come out.
        foreach (var seed in new[] { 0, 1, 99 })
        {
            using var database = new EmbeddedDatabase();
            using var connection = database.Connect();
            SeedCorpus(
                connection,
                vectors,
                VectorTestEncoding.Float32,
                VectorTestMetric.L2,
                8,
                extraOptions: $"seed = {seed}");

            const string literal = "vector32('[3,-4,5,1,-2,0,2,-1]')";
            QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {literal}) LIMIT 10;")
                .Should().Equal(QueryIntegers(
                    connection,
                    $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {literal}) LIMIT 10;"));
        }
    }

    [Test]
    public void ReindexIsIdempotentAtTheByteLevel()
    {
        var path = CreateDatabasePath("managed-vector-index-determinism");
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();
            Populate(connection, GenerateClusteredVectors(400, 8, seed: 606), permuteInsertOrder: false);

            Execute(connection, "REINDEX docs_knn;");
            var first = ReadStoredEnvelope(path);
            Execute(connection, "REINDEX docs_knn;");
            ReadStoredEnvelope(path).Should().Be(first);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static ulong[] Draw(ManagedVectorRandom random)
    {
        var values = new ulong[16];
        for (var index = 0; index < values.Length; index++)
            values[index] = random.NextUInt64();

        return values;
    }

    private static string BuildEnvelope(double[][] vectors, bool permuteInsertOrder, int seed = 0)
    {
        var path = CreateDatabasePath("managed-vector-index-determinism");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Populate(connection, vectors, permuteInsertOrder, seed);
                Execute(connection, "REINDEX docs_knn;");
            }

            return ReadStoredEnvelope(path);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void Populate(
        EmbeddedConnection connection,
        double[][] vectors,
        bool permuteInsertOrder,
        int seed = 0)
    {
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(
            connection,
            $"CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 8, lists = 32, seed = {seed}, min_rows = 8);");

        var order = Enumerable.Range(0, vectors.Length).ToArray();
        if (permuteInsertOrder)
        {
            // A fixed, non-identity permutation: the point is that the rowids are assigned in a
            // different physical order, not that the order is random.
            Array.Reverse(order);
            for (var index = 0; index + 1 < order.Length; index += 2)
                (order[index], order[index + 1]) = (order[index + 1], order[index]);
        }

        Execute(connection, "BEGIN;");
        foreach (var index in order)
            Execute(connection, $"INSERT INTO docs VALUES ({index + 1}, vector32('{Literal(vectors[index])}'));");

        Execute(connection, "COMMIT;");
    }
}
