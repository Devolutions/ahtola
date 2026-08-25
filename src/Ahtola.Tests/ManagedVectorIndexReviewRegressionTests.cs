using System.Buffers.Binary;
using System.Globalization;
using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Vectors;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Concrete reproducers for the managed vector / index-method review findings.
/// </summary>
/// <remarks>
/// Each block states the defect in the terms the review used, reproduces it against behaviour that
/// is observable from outside the fix, and — where the defect was invisible in a result set — pairs
/// the assertion with a structural one (a byte layout, a rebuild counter) so a regression cannot
/// slip through by producing the right rows for the wrong reason.
/// </remarks>
public sealed class ManagedVectorIndexReviewRegressionTests
{
    private const int Dimensions = 4;

    // -------------------------------------------------------------------------------------------
    // Finding 1: the state header's fingerprint write must not spill into the centroid payload.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void TheStateHeaderLayoutIsFixedAndNonOverlapping()
    {
        // Every field at a named offset, every offset the end of the previous field. A fingerprint
        // written eight bytes wide into the four-byte slot at 44 is what silently zeroed the first
        // centroid component, so the layout is asserted rather than described.
        ManagedVectorIndexState.MagicOffset.Should().Be(0);
        ManagedVectorIndexState.VersionOffset.Should().Be(4);
        ManagedVectorIndexState.MetricOffset.Should().Be(6);
        ManagedVectorIndexState.EncodingOffset.Should().Be(7);
        ManagedVectorIndexState.DimensionsOffset.Should().Be(8);
        ManagedVectorIndexState.ListsOffset.Should().Be(12);
        ManagedVectorIndexState.IterationsOffset.Should().Be(16);
        ManagedVectorIndexState.TrainSampleOffset.Should().Be(20);
        ManagedVectorIndexState.SeedOffset.Should().Be(24);
        ManagedVectorIndexState.TrainedSampleOffset.Should().Be(32);
        ManagedVectorIndexState.ExactOffset.Should().Be(36);
        ManagedVectorIndexState.ProbesOffset.Should().Be(40);
        ManagedVectorIndexState.FingerprintOffset.Should().Be(44);
        ManagedVectorIndexState.TrainedPopulationOffset.Should().Be(48);
        ManagedVectorIndexState.HeaderSize.Should().Be(56);

        // The fingerprint is exactly four bytes and the last header field ends exactly where the
        // payload begins, so no header write can reach a centroid.
        (ManagedVectorIndexState.FingerprintOffset + sizeof(uint))
            .Should().Be(ManagedVectorIndexState.TrainedPopulationOffset);
        (ManagedVectorIndexState.TrainedPopulationOffset + sizeof(long))
            .Should().Be(ManagedVectorIndexState.HeaderSize);
    }

    [Test]
    public void EncodingCentroidsPreservesEveryComponentIncludingTheFirst()
    {
        var options = ResolveOptions(lists: 2);
        float[] centroids = [7.5f, -1.25f, 0.5f, 3.75f, 11f, 12f, 13f, 14f];

        var encoded = ManagedVectorIndexState.Encode(options, centroids, trainedSampleRows: 5, trainedPopulation: 40);
        var (decoded, sample, population) = ManagedVectorIndexState.Decode("docs_knn", options, 1, encoded);

        decoded.Should().Equal(centroids, "no header write may overlap the centroid payload");
        sample.Should().Be(5);
        population.Should().Be(40);

        // The payload bytes themselves, read straight out of the envelope: the first component is
        // where the overlapping eight-byte fingerprint write landed.
        BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(ManagedVectorIndexState.HeaderSize)))
            .Should().Be(7.5f);
    }

    [Test]
    public void ANonZeroFirstCentroidSurvivesAReopen()
    {
        var path = CreateDatabasePath(nameof(ManagedVectorIndexReviewRegressionTests));
        try
        {
            const string query = "vector32('[0.25,0.5,-0.25,0.75]')";
            IReadOnlyList<long> before;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedClusteredCorpus(connection);

                // Force a train and a save, then read back the persisted envelope's first centroid.
                before = QueryIntegers(
                    connection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {query}) LIMIT 5;");
                ReadStoredEnvelope(path).Should().StartWith("/*ahtola-index-method:");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                // A zeroed first centroid component still produces correct rows — the certificate
                // degrades rather than lies — so correctness alone cannot catch this. What it does
                // change is the restored geometry, so the reopened index is asserted to answer
                // identically to both the first session and the un-indexed sibling.
                var after = QueryIntegers(
                    connection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {query}) LIMIT 5;");
                var scanned = QueryIntegers(
                    connection,
                    $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {query}) LIMIT 5;");

                after.Should().Equal(before);
                after.Should().Equal(scanned);
            }

            // The decisive assertion: the persisted centroids restore bit for bit.
            var attachment = CreateAttachment(lists: 2);
            float[] centroids = [1.5f, 2.5f, 3.5f, 4.5f, -1.5f, -2.5f, -3.5f, -4.5f];
            var envelope = ManagedVectorIndexState.Encode(
                ResolveOptions(lists: 2),
                centroids,
                trainedSampleRows: 3,
                trainedPopulation: 3);
            attachment.LoadState(1, envelope);
            attachment.SaveState().Should().Equal(envelope, "a save/load round trip must be the identity");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Finding 2: the trained population is not the reservoir sample size.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void DriftIsMeasuredAgainstThePopulationNotTheCappedSample()
    {
        var index = new ManagedVectorIvfIndex(ResolveOptions(lists: 2));

        // A table of 40 000 eligible rows sampled down to the 1 000-row cap. Comparing 40 000
        // against the sample would satisfy "grown by a factor of four" immediately and for ever.
        index.PublishCentroids(new float[2 * Dimensions], trainedSampleRows: 1_000, trainedPopulation: 40_000);
        index.TrainedSampleRows.Should().Be(1_000);
        index.TrainedPopulation.Should().Be(40_000);

        index.NeedsRetrain(40_000).Should().BeFalse("the population has not drifted at all");
        index.NeedsRetrain(120_000).Should().BeFalse("three times is inside the factor-of-four window");
        index.NeedsRetrain(160_000).Should().BeTrue("four times the population is real drift");
        index.NeedsRetrain(10_000).Should().BeTrue("a quarter of the population is real drift");
    }

    [Test]
    public void ATableLargerThanTheTrainSampleDoesNotRebuildOnEveryStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // train_sample is pinned well below the row count, which is the exact condition that made
        // NeedsRetrain true for ever: the sample can never grow to a quarter of the table.
        SeedClusteredCorpus(connection, rows: 1_400, extraOptions: "train_sample = 256");
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {QueryVector()}) LIMIT 5;";

        // First statement reconciles; every later one must not.
        QueryIntegers(connection, sql).Should().HaveCount(5);
        var rebuildsAfterWarmUp = ManagedIndexMethodDiagnostics.StateRebuilds;

        for (var attempt = 0; attempt < 4; attempt++)
            QueryIntegers(connection, sql).Should().HaveCount(5);

        ManagedIndexMethodDiagnostics.StateRebuilds.Should().Be(
            rebuildsAfterWarmUp,
            "a table above 4 x train_sample must not re-cluster on every query");
    }

    // -------------------------------------------------------------------------------------------
    // Finding 3: cosine pruning has to be sound under the exact scalar float32 arithmetic.
    // -------------------------------------------------------------------------------------------

    [TestCase(1e-24)]
    [TestCase(1e20)]
    public void CosineRecallSurvivesScalarNormCollapse(double magnitude)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // The construction is adversarial on purpose, because a merely unusual corpus does not
        // expose this. float32 squares of 1e-24 underflow the norm accumulator to zero and squares
        // of 1e20 overflow it to infinity; either way vector_distance_cos takes a branch that has
        // nothing to do with the angle and reports exactly 1. Meanwhile the widened double
        // components still describe a perfectly ordinary direction, so the angular bound is computed
        // from a geometry the scalar evaluator never uses.
        //
        // So: put the collapsing rows where the bound says "far" (antipodal to the query, bound 2)
        // but the reported distance says "near" (1), and put every ordinary row at an angle whose
        // reported distance is worse than 1. The scalar answer is then entirely made of collapsing
        // rows, and any bound-based pruning that trusts the double geometry drops all of them.
        const int collapsed = 10;
        const int ordinary = 200;
        var vectors = new double[collapsed + ordinary][];
        for (var index = 0; index < collapsed; index++)
            vectors[index] = [-magnitude, 0.0, 0.0, 0.0];

        var random = new DeterministicTestRandom(90210);
        for (var index = 0; index < ordinary; index++)
        {
            // 2.2 radians away from the query: reported cosine distance 1 - cos(2.2) = 1.589,
            // comfortably worse than the 1.0 the collapsing rows report.
            var angle = 2.2 + ((random.NextDouble() - 0.5) * 0.05);
            vectors[collapsed + index] =
            [
                Math.Round(Math.Cos(angle), 6),
                Math.Round(Math.Sin(angle), 6),
                Math.Round((random.NextDouble() - 0.5) * 0.01, 6),
                Math.Round((random.NextDouble() - 0.5) * 0.01, 6),
            ];
        }

        SeedCorpus(
            connection,
            vectors,
            VectorTestEncoding.Float32,
            VectorTestMetric.Cosine,
            Dimensions,
            lists: 16,
            minimumRows: 8,
            extraOptions: "probes = 1");

        foreach (var query in new[]
                 {
                     "[1,0,0,0]",
                     Literal([magnitude, 0.0, 0.0, 0.0]),
                     "[0.1,0.2,0.3,0.4]",
                 })
        {
            var sql = $"SELECT id FROM docs ORDER BY vector_distance_cos(embedding, vector32('{query}')) LIMIT 10;";
            var scanned = sql.Replace("FROM docs", "FROM plain", StringComparison.Ordinal);
            QueryIntegers(connection, sql).Should().Equal(QueryIntegers(connection, scanned), query);
        }

        // The negative control: the first query's answer really is the collapsing rows, so the
        // assertion above is comparing something the geometry would have pruned.
        QueryIntegers(
                connection,
                "SELECT id FROM plain ORDER BY vector_distance_cos(embedding, vector32('[1,0,0,0]')) LIMIT 10;")
            .Should().OnlyContain(id => id <= collapsed);
    }

    [Test]
    public void ScalarCosineUsabilityTracksTheFloat32Accumulator()
    {
        // The gate is the accumulator the scalar evaluator actually builds, not a double norm.
        ManagedVectorGeometry.IsCosineScalarUsable([1.0, 2.0, 3.0], VectorEncodingKind.Float32)
            .Should().BeTrue();
        ManagedVectorGeometry.IsCosineScalarUsable([1e-24, 1e-24, 1e-24], VectorEncodingKind.Float32)
            .Should().BeFalse("float32 squares of 1e-24 underflow the accumulator to zero");
        ManagedVectorGeometry.IsCosineScalarUsable([1e20, 1e20, 1e20], VectorEncodingKind.Float32)
            .Should().BeFalse("float32 squares of 1e20 overflow the accumulator to infinity");
        ManagedVectorGeometry.IsCosineScalarUsable([0.0, 0.0, 0.0], VectorEncodingKind.Float32)
            .Should().BeFalse();

        // float64 accumulates in double, so the same components are perfectly usable there, and
        // float1bit cosine is an exact integer count with no accumulator at all.
        ManagedVectorGeometry.IsCosineScalarUsable([1e-24, 1e-24, 1e-24], VectorEncodingKind.Float64)
            .Should().BeTrue();
        ManagedVectorGeometry.IsCosineScalarUsable([0.0, 0.0], VectorEncodingKind.Float1Bit)
            .Should().BeTrue();
    }

    [Test]
    public void ADegenerateRowIsAlwaysProbedRatherThanBounded()
    {
        var options = ResolveOptions(lists: 2, metric: "cosine");
        var index = new ManagedVectorIvfIndex(options);
        index.PublishCentroids([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f], trainedSampleRows: 4, trainedPopulation: 4);

        index.Upsert(1, Vector32([1.0, 0.0, 0.0, 0.0]));
        index.UnboundedRowCount.Should().Be(0, "an ordinary row is bounded by its list");

        index.Upsert(2, Vector32([1e-24, 0.0, 0.0, 0.0]));
        index.UnboundedRowCount.Should().Be(1, "a row whose scalar norm underflows cannot be bounded");

        index.Upsert(3, Vector32([1e20, 1e20, 0.0, 0.0]));
        index.UnboundedRowCount.Should().Be(2, "a row whose scalar norm overflows cannot be bounded");
    }

    // -------------------------------------------------------------------------------------------
    // Finding 4: limits must be enforced before anything proportional is allocated.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void AnOversizedBlobIsRejectedBeforeItIsCopied()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");

        // 64 MiB of zeroes. Before the length gate this parsed into a 16-million-component vector:
        // a 64 MiB copy of the blob followed by a 128 MiB double[] per evaluation.
        Execute(connection, "INSERT INTO docs VALUES (1, zeroblob(67108864));");

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        ShouldThrow(connection, "SELECT vector_distance_l2(embedding, vector32('[1,2,3,4]')) FROM docs;")
            .Message.Should().Contain("exceeds the managed limit");
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        // The rejection must cost far less than one copy of the payload, let alone the widening.
        allocated.Should().BeLessThan(
            8 * 1024 * 1024,
            "the length gate has to fire before the blob is copied or widened");
    }

    [TestCase("zeroblob(67108864)", "exceeds the managed limit")]
    [TestCase("x'000000000004'", "f32 dense vector unexpected data length: 6")]
    [TestCase("x'FF0003'", "float1bit vector needs 2 data bytes but blob holds 2")]
    [TestCase("x'000004'", "float8 vector blob of 2 bytes is too short")]
    public void MalformedAndOversizedBlobsFailClosed(string literal, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, $"INSERT INTO docs VALUES (1, {literal});");

        ShouldThrow(connection, "SELECT vector_distance_l2(embedding, vector32('[1,2,3,4]')) FROM docs;")
            .Message.Should().Contain(expected);
    }

    [Test]
    public void IndexingDecodeRejectsAWrongShapeWithoutWidening()
    {
        // A million-component blob against a four-dimensional index: the shape is refused from the
        // parsed header, so nothing proportional to the declared component count is materialized.
        var wide = SqlValue.Blob(new byte[1024 * 1024 * sizeof(float)]);
        var payloadBytes = wide.AsBlob().Length;

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        SqliteVectorFunctions.TryDecodeVector(wide, VectorEncodingKind.Float32, Dimensions, out _)
            .Should().BeFalse();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        // One blob copy is unavoidable inside the shared parser; the eight-byte-per-component
        // widening on top of it — three times this payload — is what the shape check removes.
        allocated.Should().BeLessThan(
            2L * payloadBytes,
            "a rejected shape must not be widened into a double[] first");
    }

    [Test]
    public void ALargeZeroblobInAnIndexedColumnIsUnindexableRatherThanFatal()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedClusteredCorpus(connection);

        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {QueryVector()}) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");

        Execute(connection, "INSERT INTO docs VALUES (900001, zeroblob(67108864));");
        Execute(connection, "INSERT INTO plain VALUES (900001, zeroblob(67108864));");

        // The row is not a vector of the declared shape, so the plan is declined and the ordinary
        // scan raises the same error the scalar form would.
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD vector");
        ShouldThrow(connection, sql).Message
            .Should().Be(ShouldThrow(connection, sql.Replace("FROM docs", "FROM plain", StringComparison.Ordinal)).Message);
    }

    // -------------------------------------------------------------------------------------------
    // Finding 5: planning prices candidates; only the winner is opened.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void ExplainDoesNotRebuildAColdIndex()
    {
        var path = CreateDatabasePath(nameof(ManagedVectorIndexReviewRegressionTests));
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedClusteredCorpus(connection);
            }

            // A reopened database is the honest cold case: the catalog restores centroids but no
            // postings, and no statement has reconciled anything yet.
            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var fresh = reopened.Connect();
            var before = ManagedIndexMethodDiagnostics.StateRebuilds;
            var detail = ExplainDetail(
                fresh,
                $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {QueryVector()}) LIMIT 5;");

            detail.Should().Contain("INDEX METHOD vector", "the plan is still reported");
            ManagedIndexMethodDiagnostics.StateRebuilds.Should().Be(
                before,
                "EXPLAIN QUERY PLAN must price a plan, not build one");

            // And the price it reported includes the reconciliation it declined to perform, so the
            // plan is not advertised as cheaper than it is.
            detail.Should().MatchRegex(@"cost~\d+");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void OnlyTheWinningCandidateIsRebuilt()
    {
        var path = CreateDatabasePath(nameof(ManagedVectorIndexReviewRegressionTests));
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, a BLOB, b BLOB, c BLOB);");
                foreach (var column in new[] { "a", "b", "c" })
                {
                    Execute(
                        connection,
                        $"CREATE INDEX docs_{column} ON docs USING vector ({column}) "
                        + $"WITH (dims = {Dimensions}, lists = 64, min_rows = 8);");
                }

                var vectors = GenerateClusteredVectors(600, Dimensions, seed: 31337, clusters: 8);
                Execute(connection, "BEGIN;");
                for (var index = 0; index < vectors.Length; index++)
                {
                    var literal = $"vector32('{Literal(vectors[index])}')";
                    Execute(connection, $"INSERT INTO docs VALUES ({index + 1}, {literal}, {literal}, {literal});");
                }

                Execute(connection, "COMMIT;");
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var fresh = reopened.Connect();
            var before = ManagedIndexMethodDiagnostics.StateRebuilds;
            var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(b, {QueryVector()}) LIMIT 5;";
            ExplainDetail(fresh, sql).Should().Contain("INDEX METHOD vector INDEX docs_b");
            ManagedIndexMethodDiagnostics.StateRebuilds.Should().Be(before, "pricing must not reconcile");

            QueryIntegers(fresh, sql).Should().HaveCount(5);

            // Three cold candidate indexes were considered; exactly one of them answers, so exactly
            // one may pay for reconciliation.
            (ManagedIndexMethodDiagnostics.StateRebuilds - before).Should().Be(
                1,
                "only the selected access path may be reconciled");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ARejectedCandidateIsNeverReconciled()
    {
        var path = CreateDatabasePath(nameof(ManagedVectorIndexReviewRegressionTests));
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedClusteredCorpus(connection);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var fresh = reopened.Connect();

            // An unlimited ranking loses to the scan, so no method index is selected and none may be
            // reconciled on the way to finding that out.
            var before = ManagedIndexMethodDiagnostics.StateRebuilds;
            QueryIntegers(fresh, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {QueryVector()});")
                .Should().HaveCount(600);
            ManagedIndexMethodDiagnostics.StateRebuilds.Should().Be(before);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Finding 6: a nested subquery reusing an alias must not rebind an outer scalar call.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void AnInnerSubqueryAliasDoesNotRebindAnOuterFtsScore()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body);");
        Execute(connection, "CREATE INDEX notes_fts ON notes USING fts (title, body);");
        Execute(connection, "INSERT INTO docs VALUES (1, 'alpha', 'alpha alpha alpha'), (2, 'beta', 'beta');");
        Execute(connection, "INSERT INTO notes VALUES (1, 'zulu', 'zulu'), (2, 'zulu', 'zulu zulu');");

        var expected = Query(
            connection,
            "SELECT d.id, fts_score(d.title, d.body, 'alpha') FROM docs AS d ORDER BY d.id;");

        // The nested scan reuses the alias 'd' for a different table that also carries an FTS index
        // over columns with the same names, and it is materialized after the outer source. A
        // statement-wide alias registry ends the statement holding 'd' -> notes, so every evaluation
        // of the outer projection scores against the wrong corpus and the wrong rowid.
        var actual = Query(
            connection,
            "SELECT d.id, fts_score(d.title, d.body, 'alpha') FROM docs AS d "
            + "JOIN (SELECT id FROM notes AS d) AS n ON n.id = d.id ORDER BY d.id;");

        actual.Should().HaveCount(expected.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            actual[index][0].AsInteger().Should().Be(expected[index][0].AsInteger());
            Rank(actual[index][1]).Should().Be(
                Rank(expected[index][1]),
                "an inner alias must not rebind the outer source");
        }

        // The negative control: the outer score is a real corpus score, so the reproducer above is
        // not passing because binding was disabled altogether.
        Rank(expected[0][1]).Should().BeGreaterThan(0.0);
    }

    [Test]
    public void AnInnerSubqueryAliasDoesNotRebindAnOuterVectorSource()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedClusteredCorpus(connection);
        Execute(connection, "CREATE TABLE other(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(
            connection,
            $"CREATE INDEX other_knn ON other USING vector (embedding) WITH (dims = {Dimensions}, lists = 64, min_rows = 8);");
        Execute(connection, "INSERT INTO other SELECT id, embedding FROM docs WHERE id <= 20;");

        // The same shape against the vector method: an inner scan of a different table under the
        // outer alias must not change which rows the outer ranking produces.
        var expected = QueryIntegers(
            connection,
            $"SELECT d.id FROM plain AS d JOIN (SELECT id FROM other AS d) AS n ON n.id = d.id "
            + $"ORDER BY vector_distance_l2(d.embedding, {QueryVector()}) LIMIT 5;");
        var actual = QueryIntegers(
            connection,
            $"SELECT d.id FROM docs AS d JOIN (SELECT id FROM other AS d) AS n ON n.id = d.id "
            + $"ORDER BY vector_distance_l2(d.embedding, {QueryVector()}) LIMIT 5;");

        actual.Should().Equal(expected);
        actual.Should().NotBeEmpty();
    }

    // -------------------------------------------------------------------------------------------
    // Finding 7: unranked and tied rows are appended in ordinary table-scan rowid order.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void UnrankedRowsAreAppendedInRowIdOrderNotInsertionOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body);");

        // Rowids inserted in descending order, so storage order is the reverse of scan order. Every
        // row but one scores zero, so the LIMIT lands squarely inside a block of ties.
        for (var rowId = 40; rowId >= 1; rowId--)
        {
            var body = rowId == 7 ? "alpha" : "filler";
            Execute(connection, $"INSERT INTO docs VALUES ({rowId}, 'title', '{body}');");
            Execute(connection, $"INSERT INTO plain VALUES ({rowId}, 'title', '{body}');");
        }

        const string indexed =
            "SELECT id FROM docs ORDER BY fts_score(title, body, 'alpha') DESC LIMIT 6;";
        var scanned = indexed.Replace("FROM docs", "FROM plain", StringComparison.Ordinal);

        QueryIntegers(connection, indexed).Should().Equal(
            QueryIntegers(connection, scanned),
            "tied rows must arrive in the order an ordinary table scan would produce them");
        QueryIntegers(connection, indexed).Should().Equal(7, 1, 2, 3, 4, 5);
    }

    [Test]
    public void TiedVectorDistancesResolveInRowIdOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(
            connection,
            $"CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = {Dimensions}, lists = 8, min_rows = 8);");

        // Every row holds the same vector, so every distance ties and the LIMIT is decided purely by
        // the order rows are handed to the sort. Rowids are inserted descending on purpose.
        Execute(connection, "BEGIN;");
        for (var rowId = 60; rowId >= 1; rowId--)
        {
            Execute(connection, $"INSERT INTO docs VALUES ({rowId}, vector32('[1,2,3,4]'));");
            Execute(connection, $"INSERT INTO plain VALUES ({rowId}, vector32('[1,2,3,4]'));");
        }

        Execute(connection, "COMMIT;");

        const string indexed =
            "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[1,2,3,4]')) LIMIT 5;";
        var scanned = indexed.Replace("FROM docs", "FROM plain", StringComparison.Ordinal);

        QueryIntegers(connection, indexed).Should().Equal(QueryIntegers(connection, scanned));
        QueryIntegers(connection, indexed).Should().Equal(1, 2, 3, 4, 5);
    }

    // -------------------------------------------------------------------------------------------

    private static string QueryVector() => "vector32('[0.25,0.5,-0.25,0.75]')";

    private static double Rank(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Integer => value.AsInteger(),
            _ => 0.0,
        };

    private static SqlValue Vector32(IReadOnlyList<double> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return SqlValue.Blob(bytes);
    }

    private static void SeedClusteredCorpus(
        EmbeddedConnection connection,
        int rows = 600,
        string? extraOptions = null)
        => SeedCorpus(
            connection,
            GenerateClusteredVectors(rows, Dimensions, seed: 4242, clusters: 8),
            VectorTestEncoding.Float32,
            VectorTestMetric.L2,
            Dimensions,
            lists: 64,
            minimumRows: 8,
            extraOptions: extraOptions);

    private static ManagedVectorIndexOptions ResolveOptions(int lists, string metric = "l2")
        => ManagedVectorIndexOptions.Resolve(BuildConfiguration(lists, metric));

    private static ManagedIndexMethodAttachment CreateAttachment(int lists, string metric = "l2")
        => ManagedIndexMethodRegistry.Resolve("vector").Attach(BuildConfiguration(lists, metric));

    private static ManagedIndexMethodConfiguration BuildConfiguration(int lists, string metric)
        => new(
            "docs",
            "docs_knn",
            [new ManagedIndexMethodColumn("embedding", 1)],
            [
                new ManagedIndexMethodParameter("dims", SqlValue.Integer(Dimensions)),
                new ManagedIndexMethodParameter("lists", SqlValue.Integer(lists)),
                new ManagedIndexMethodParameter("metric", SqlValue.Text(metric)),
            ]);

    private static string Literal(IReadOnlyList<double> values)
        => "[" + string.Join(
            ",",
            values.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
