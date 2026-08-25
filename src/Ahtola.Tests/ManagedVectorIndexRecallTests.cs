using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// The correctness baseline for the managed vector index: for every supported encoding and metric,
/// an indexed KNN query must return exactly what the same query returns without an index.
/// </summary>
/// <remarks>
/// <para>
/// Two independent oracles are used. The first is a sibling table holding the same rows with no
/// index at all, queried with the same SQL: it exercises the engine's ordinary scan and scalar
/// evaluator, so agreement proves the returned rowids, their order and the emitted distances are
/// bit identical to the unindexed answer.
/// </para>
/// <para>
/// The second is <see cref="ManagedVectorTestOracle"/>, a brute-force search written in the test
/// against the documented blob layouts and distance definitions, sharing no code with the engine.
/// It is compared on rowid set and order; its float8 cosine and dot arithmetic groups terms
/// differently from the engine's integer-accumulated form, so agreeing with it on last-bit distance
/// values would be an assertion about rounding rather than about recall.
/// </para>
/// </remarks>
public sealed class ManagedVectorIndexRecallTests
{
    private const int CorpusSize = 600;
    private const int Dimensions = 8;
    private const int QueryCount = 25;

    private static IEnumerable<TestCaseData> SupportedCombinations()
    {
        foreach (var encoding in new[]
                 {
                     VectorTestEncoding.Float32,
                     VectorTestEncoding.Float64,
                     VectorTestEncoding.Float8,
                     VectorTestEncoding.Float1Bit,
                 })
        {
            foreach (var metric in new[] { VectorTestMetric.L2, VectorTestMetric.Cosine, VectorTestMetric.Dot })
            {
                // The scalar evaluator has no L2 distance for float1bit vectors, so the pair is
                // rejected at CREATE INDEX rather than tested here.
                if (encoding == VectorTestEncoding.Float1Bit && metric == VectorTestMetric.L2)
                    continue;

                yield return new TestCaseData(encoding, metric)
                    .SetName($"IndexedKnnMatchesBothOraclesFor_{encoding}_{metric}");
            }
        }
    }

    [TestCaseSource(nameof(SupportedCombinations))]
    public void IndexedKnnMatchesBothOracles(VectorTestEncoding encoding, VectorTestMetric metric)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var binary = encoding == VectorTestEncoding.Float1Bit;
        var vectors = GenerateClusteredVectors(CorpusSize, Dimensions, seed: 20260824, binary: binary);
        SeedCorpus(connection, vectors, encoding, metric, Dimensions);

        var corpus = ReadBlobs(connection, "docs");
        var distance = DistanceFunction(metric);
        var constructor = Constructor(encoding);
        var queries = GenerateClusteredVectors(QueryCount, Dimensions, seed: 99117, binary: binary);
        var executedBefore = EmbeddedDatabase.MethodIndexScansExecuted;

        foreach (var query in queries)
        {
            var literal = $"{constructor}('{Literal(query)}')";
            var indexed = Query(
                connection,
                $"SELECT id, {distance}(embedding, {literal}) FROM docs ORDER BY {distance}(embedding, {literal}) LIMIT 10;");
            var scanned = Query(
                connection,
                $"SELECT id, {distance}(embedding, {literal}) FROM plain ORDER BY {distance}(embedding, {literal}) LIMIT 10;");

            indexed.Select(static row => row[0].AsInteger())
                .Should().Equal(scanned.Select(static row => row[0].AsInteger()));

            // Bit-for-bit, not approximately: the index reranks through the same scalar function.
            indexed.Select(static row => BitConverter.DoubleToInt64Bits(row[1].AsReal()))
                .Should().Equal(scanned.Select(static row => BitConverter.DoubleToInt64Bits(row[1].AsReal())));

            var queryBlob = Query(connection, $"SELECT {literal};")[0][0].AsBlob().ToArray();
            indexed.Select(static row => row[0].AsInteger())
                .Should().Equal(ManagedVectorTestOracle.TopK(corpus, queryBlob, metric, 10));
        }

        // The suite has to prove the index was actually exercised, not that a scan happened to agree
        // with itself. The counter is incremented by the executor, not by the planner, so it reports
        // the plan that ran rather than the plan that was advertised.
        EmbeddedDatabase.MethodIndexScansExecuted.Should().BeGreaterThan(executedBefore);
    }

    [Test]
    public void RecallIsExactAtKOneAcrossSeededDatasets()
    {
        foreach (var seed in new[] { 11, 2027, 65535, 777777 })
        {
            using var database = new EmbeddedDatabase();
            using var connection = database.Connect();
            var vectors = GenerateClusteredVectors(CorpusSize, Dimensions, seed);
            SeedCorpus(connection, vectors, VectorTestEncoding.Float32, VectorTestMetric.L2, Dimensions);

            var corpus = ReadBlobs(connection, "docs");
            foreach (var query in GenerateClusteredVectors(20, Dimensions, seed + 1))
            {
                var literal = $"vector32('{Literal(query)}')";
                var nearest = QueryIntegers(
                    connection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {literal}) LIMIT 1;");
                var queryBlob = Query(connection, $"SELECT {literal};")[0][0].AsBlob().ToArray();

                nearest.Should().Equal(ManagedVectorTestOracle.TopK(corpus, queryBlob, VectorTestMetric.L2, 1));
            }
        }
    }

    [Test]
    public void TiesAtTheLimitBoundaryKeepEveryRowTheScanKeeps()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // Every row is one of four distinct points repeated many times, so the tenth-best distance is
        // shared by a large block of rows and a truncating index has to agree with the scan about
        // which of them the LIMIT keeps.
        var vectors = new double[600][];
        for (var index = 0; index < vectors.Length; index++)
        {
            var bucket = index % 4;
            vectors[index] = [bucket, bucket, bucket, bucket, bucket, bucket, bucket, bucket];
        }

        SeedCorpus(connection, vectors, VectorTestEncoding.Float32, VectorTestMetric.L2, Dimensions);

        const string literal = "vector32('[1,1,1,1,1,1,1,1]')";
        for (var limit = 1; limit <= 40; limit++)
        {
            QueryIntegers(
                    connection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {literal}) LIMIT {limit};")
                .Should().Equal(QueryIntegers(
                    connection,
                    $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {literal}) LIMIT {limit};"));
        }
    }

    [Test]
    public void AdversarialClustersStillReturnTheExactAnswer()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // A shell: every row sits at the same distance from the origin, so no list can be pruned and
        // the certificate has to read every one of them. The answer must still be exact.
        var vectors = new double[600][];
        for (var index = 0; index < vectors.Length; index++)
        {
            var angle = 2.0 * Math.PI * index / vectors.Length;
            vectors[index] =
            [
                Math.Cos(angle), Math.Sin(angle), Math.Cos(2 * angle), Math.Sin(2 * angle),
                Math.Cos(3 * angle), Math.Sin(3 * angle), Math.Cos(4 * angle), Math.Sin(4 * angle),
            ];
        }

        SeedCorpus(connection, vectors, VectorTestEncoding.Float32, VectorTestMetric.L2, Dimensions);

        const string literal = "vector32('[0,0,0,0,0,0,0,0]')";
        QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {literal}) LIMIT 12;")
            .Should().Equal(QueryIntegers(
                connection,
                $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {literal}) LIMIT 12;"));

        // Having been forced to read everything, the measured probe count re-prices the plan out.
        // Drift and adversarial data cost speed, never recall.
        ExplainDetail(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {literal}) LIMIT 12;")
            .Should().NotContain("INDEX METHOD vector");
    }

    [Test]
    public void LargeLimitsAndOffsetsAgreeWithTheScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var vectors = GenerateClusteredVectors(CorpusSize, Dimensions, seed: 4242);
        SeedCorpus(connection, vectors, VectorTestEncoding.Float32, VectorTestMetric.Cosine, Dimensions);

        const string literal = "vector32('[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8]')";
        foreach (var (limit, offset) in new[] { (1, 0), (5, 0), (5, 7), (50, 25), (600, 0) })
        {
            var suffix = offset == 0 ? $"LIMIT {limit}" : $"LIMIT {limit} OFFSET {offset}";
            QueryIntegers(
                    connection,
                    $"SELECT id FROM docs ORDER BY vector_distance_cos(embedding, {literal}) {suffix};")
                .Should().Equal(QueryIntegers(
                    connection,
                    $"SELECT id FROM plain ORDER BY vector_distance_cos(embedding, {literal}) {suffix};"));
        }
    }
}
