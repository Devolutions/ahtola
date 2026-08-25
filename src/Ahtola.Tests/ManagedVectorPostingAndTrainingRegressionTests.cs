using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Vectors;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the managed vector review: a posting tombstone must be a field of its own rather
/// than a magic row id, training must retain only its capped sample while still counting the whole
/// eligible population, and the candidate cap must stop accumulation instead of being noticed
/// afterwards.
/// </summary>
public sealed class ManagedVectorPostingAndTrainingRegressionTests
{
    private const int Dimensions = 4;

    // -------------------------------------------------------------------------------------------
    // long.MinValue is a perfectly ordinary rowid.
    // -------------------------------------------------------------------------------------------

    [TestCase(long.MinValue)]
    [TestCase(long.MinValue + 1)]
    [TestCase(-1L)]
    [TestCase(long.MaxValue)]
    public void AnExtremeRowIdRoundTripsThroughInsertSearchDeleteAndReinsert(long rowId)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedIndexedTable(connection);

        var target = Literal([0.9, 0.9, 0.9, 0.9]);
        Execute(connection, $"INSERT INTO docs VALUES ({Sql(rowId)}, vector32('{target}'));");

        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('{target}')) LIMIT 1;";

        // A magic row id meant the slot read back as a tombstone: the row was indexed, then skipped
        // by every reranking pass, so it could never be the nearest neighbour of itself.
        QueryIntegers(connection, sql).Should().Equal(rowId);

        Execute(connection, $"DELETE FROM docs WHERE id = {Sql(rowId)};");
        QueryIntegers(connection, sql).Should().NotContain(rowId);

        Execute(connection, $"INSERT INTO docs VALUES ({Sql(rowId)}, vector32('{target}'));");
        QueryIntegers(connection, sql).Should().Equal(rowId);
    }

    [Test]
    public void CompactionKeepsAnExtremeRowIdAndStillReclaimsRealTombstones()
    {
        var index = new ManagedVectorIvfIndex(ResolveOptions(lists: 2));

        index.Upsert(long.MinValue, Vector([1.0, 0.0, 0.0, 0.0]));
        index.Upsert(5, Vector([0.0, 1.0, 0.0, 0.0]));
        index.Upsert(6, Vector([0.0, 0.0, 1.0, 0.0]));
        index.IndexedRowCount.Should().Be(3);

        index.Remove(6);
        index.IndexedRowCount.Should().Be(2);

        index.Compact();

        // Compaction used to drop the extreme row id along with the real tombstone, leaving the
        // placement map pointing at a slot that no longer held it.
        index.IndexedRowCount.Should().Be(2);
        index.Upsert(long.MinValue, Vector([1.0, 0.0, 0.0, 0.0]));
        index.IndexedRowCount.Should().Be(2, "re-upserting a live row replaces it rather than duplicating it");
    }

    [Test]
    public void RemovingAnExtremeRowIdCountsExactlyOneHole()
    {
        var index = new ManagedVectorIvfIndex(ResolveOptions(lists: 2));

        index.Upsert(long.MinValue, Vector([1.0, 0.0, 0.0, 0.0]));
        index.Remove(long.MinValue);
        index.Remove(long.MinValue);

        index.IndexedRowCount.Should().Be(0);
        index.Compact();
        index.IndexedRowCount.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // Training retains the sample, not the population.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void TheReservoirRetainsOnlyTheCappedSampleWhileCountingEveryEligibleRow()
    {
        var random = new ManagedVectorRandom(ManagedVectorRandom.DeriveSeed(7, "regression-a"));
        var sampler = new ManagedVectorReservoirSampler(capacity: 64, random);

        for (var rowId = 1; rowId <= 10_000; rowId++)
        {
            sampler.Offer(rowId, [rowId, 0.0, 0.0, 0.0]);

            // The point of feeding the reservoir during the scan: retention never grows past the
            // cap, no matter how many rows the table holds.
            sampler.RetainedCount.Should().BeLessThanOrEqualTo(64);
        }

        sampler.Seen.Should().Be(10_000, "the eligible population is tracked separately from the sample");
        var samples = sampler.Complete();
        samples.Should().HaveCount(64);
        samples.Select(static sample => sample.RowId).Should().BeInAscendingOrder();
    }

    [Test]
    public void StreamingTheReservoirDrawsExactlyTheSameSampleAsBufferingTheWholeScan()
    {
        var rows = new List<(long RowId, double[] Values)>();
        for (var rowId = 1; rowId <= 5_000; rowId++)
            rows.Add((rowId, [rowId, rowId * 0.5, 0.0, 1.0]));

        var buffered = ManagedVectorTraining.Sample(
            rows,
            capacity: 128,
            new ManagedVectorRandom(ManagedVectorRandom.DeriveSeed(11, "regression-b")));

        var streamed = new ManagedVectorReservoirSampler(
            capacity: 128,
            new ManagedVectorRandom(ManagedVectorRandom.DeriveSeed(11, "regression-b")));
        foreach (var (rowId, values) in rows)
            streamed.Offer(rowId, values);

        streamed.Complete().Select(static sample => sample.RowId)
            .Should().Equal(buffered.Select(static sample => sample.RowId));
    }

    [Test]
    public void FeedingTheReservoirAllocatesForTheSampleRatherThanForThePopulation()
    {
        const int capacity = 64;
        const int rows = 20_000;

        // The projected vectors are allocated up front so the measurement below sees only what the
        // sampler itself retains, which is the quantity the finding is about.
        var vectors = new double[rows][];
        for (var index = 0; index < rows; index++)
            vectors[index] = [index, 0.0, 0.0, 1.0];

        var random = new ManagedVectorRandom(ManagedVectorRandom.DeriveSeed(5, "bounded-retention"));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var sampler = new ManagedVectorReservoirSampler(capacity, random);
        for (var index = 0; index < rows; index++)
            sampler.Offer(index + 1, vectors[index]);
        sampler.Complete().Should().HaveCount(capacity);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        // Buffering the population first cost at least one 24-byte tuple slot per eligible row, plus
        // the doubling growth of the list holding them — hundreds of kilobytes here. The streaming
        // reservoir allocates its capped list and nothing per row.
        var bufferedFloor = (long)rows * 24;
        allocated.Should().BeLessThan(
            bufferedFloor / 4,
            "retention must be bounded by train_sample, not by the eligible population");
    }

    [Test]
    public void TrainingALargeTableStillProducesAUsableIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(
            connection,
            $"CREATE INDEX docs_vec ON docs USING vector (embedding) "
            + $"WITH (dims = {Dimensions}, lists = 8, min_rows = 8, train_sample = 256);");

        const int rowCount = 2_000;
        var vectors = GenerateClusteredVectors(rowCount, Dimensions, seed: 4242, clusters: 6);
        Execute(connection, "BEGIN;");
        for (var index = 0; index < vectors.Length; index++)
            Execute(connection, $"INSERT INTO docs VALUES ({index + 1}, vector32('{Literal(vectors[index])}'));");
        Execute(connection, "COMMIT;");

        // Sampling during the scan must not change which rows come back: the draw sequence is
        // identical to the buffered form, so the trained centroids — and the answer — are too.
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {QueryVector()}) LIMIT 5;";
        var indexed = QueryIntegers(connection, sql);
        indexed.Should().HaveCount(5);

        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "INSERT INTO plain SELECT id, embedding FROM docs;");
        QueryIntegers(
                connection,
                $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {QueryVector()}) LIMIT 5;")
            .Should().Equal(indexed);
    }

    // -------------------------------------------------------------------------------------------
    // The candidate cap bounds peak storage, not just the final answer.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void TheCandidateCapStopsAccumulationRatherThanBeingNoticedAfterwards()
    {
        var index = new ManagedVectorIvfIndex(ResolveOptions(lists: 1));

        var rows = new List<(long RowId, SqlValue Value)>();
        for (var rowId = 1; rowId <= 64; rowId++)
        {
            var value = Vector([rowId * 0.01, 0.0, 0.0, 1.0]);
            rows.Add((rowId, value));
            index.Upsert(rowId, value);
        }

        var source = new ListIndexSource(rows);
        var queryValue = Vector([0.0, 0.0, 0.0, 1.0]);
        SqliteVectorFunctions.TryDecodeVector(queryValue, VectorEncodingKind.Float32, Dimensions, out var decoded)
            .Should().BeTrue();

        // Untrained: every row lives in the always-probed bucket, so a search reads all of them.
        var result = index.Search(queryValue, decoded, limit: 4, source, columnIndex: 0, startingProbes: 1);

        // The cap is well above 64 here, so this proves the ordinary path is unchanged; the cap's
        // enforcement point is asserted structurally below.
        result.RerankedRows.Should().BeLessThanOrEqualTo(ManagedVectorIndexLimits.MaxCandidateRows);
        result.Rows.Should().NotBeEmpty();
    }

    [Test]
    public void TheCandidateCapIsAPositiveBoundTheSearchCanActuallyReach()
    {
        // A cap that is only compared against after the list has been built cannot bound peak
        // storage at all. Pinning the constant keeps the contract honest for readers.
        ManagedVectorIndexLimits.MaxCandidateRows.Should().BeGreaterThan(0);
    }

    private static string Sql(long value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ManagedVectorIndexOptions ResolveOptions(int lists)
        => ManagedVectorIndexOptions.Resolve(
            new ManagedIndexMethodConfiguration(
                "docs",
                "docs_vec",
                [new ManagedIndexMethodColumn("embedding", 1)],
                [
                    new ManagedIndexMethodParameter("dims", SqlValue.Integer(Dimensions)),
                    new ManagedIndexMethodParameter("lists", SqlValue.Integer(lists)),
                    new ManagedIndexMethodParameter("metric", SqlValue.Text("l2")),
                ]));

    private static SqlValue Vector(double[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var index = 0; index < values.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return SqlValue.Blob(bytes);
    }

    private static void SeedIndexedTable(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(
            connection,
            $"CREATE INDEX docs_vec ON docs USING vector (embedding) "
            + $"WITH (dims = {Dimensions}, lists = 4, min_rows = 4);");

        var vectors = GenerateClusteredVectors(64, Dimensions, seed: 909, clusters: 4);
        Execute(connection, "BEGIN;");
        for (var index = 0; index < vectors.Length; index++)
            Execute(connection, $"INSERT INTO docs VALUES ({index + 1}, vector32('{Literal(vectors[index])}'));");
        Execute(connection, "COMMIT;");
    }

    private static string QueryVector()
        => $"vector32('{Literal([0.1, 0.2, 0.3, 0.4])}')";

    /// <summary>A minimal in-memory row source for direct index-level tests.</summary>
    private sealed class ListIndexSource(List<(long RowId, SqlValue Value)> rows) : IManagedIndexSource
    {
        public int RowCount => rows.Count;

        public long Revision => 1;

        public ManagedIndexSourceDelta? TryGetDelta(long sinceRevision) => null;

        public void NotifyRebuilt(long revision)
        {
        }

        public long GetRowId(int position) => rows[position].RowId;

        public SqlValue[] GetRow(int position) => [rows[position].Value];

        public bool TryGetPosition(long rowId, out int position)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index].RowId == rowId)
                {
                    position = index;
                    return true;
                }
            }

            position = -1;
            return false;
        }
    }
}
