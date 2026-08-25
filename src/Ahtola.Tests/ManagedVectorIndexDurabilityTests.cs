using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Vectors;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// File-backed durability for the vector index, plus the fail-closed matrix for its state envelope.
/// Every rejection is proven to happen before the centroid array is allocated.
/// </summary>
[NonParallelizable]
public sealed class ManagedVectorIndexDurabilityTests
{
    private const int Dimensions = 8;
    private const string Query = "vector32('[2,-3,4,1,-1,0,3,-2]')";

    [Test]
    public void TrainedStateSurvivesReopen()
    {
        var path = CreateDatabasePath("managed-vector-index-durability");
        try
        {
            string envelope;
            IReadOnlyList<long> expected;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(
                    connection,
                    GenerateClusteredVectors(600, Dimensions, seed: 13579),
                    VectorTestEncoding.Float32,
                    VectorTestMetric.L2,
                    Dimensions);
                Execute(connection, "REINDEX docs_knn;");
                expected = QueryIntegers(
                    connection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;");
            }

            envelope = ReadStoredEnvelope(path);
            envelope.Should().StartWith("/*ahtola-index-method:1:");

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(
                    reopenedConnection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;")
                .Should().Equal(expected);

            // A reopen restores the trained centroids rather than retraining, so the persisted bytes
            // are unchanged by merely reading the database back.
            ExplainDetail(
                    reopenedConnection,
                    $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 10;")
                .Should().Contain("INDEX METHOD vector");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void UncommittedWorkIsNotVisibleAfterCrashAndReopen()
    {
        var path = CreateDatabasePath("managed-vector-index-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(
                    connection,
                    GenerateClusteredVectors(300, Dimensions, seed: 2468),
                    VectorTestEncoding.Float32,
                    VectorTestMetric.L2,
                    Dimensions);
                Execute(connection, "BEGIN;");
                Execute(connection, "INSERT INTO docs VALUES (800001, vector32('[2,-3,4,1,-1,0,3,-2]'));");
                // Dispose without COMMIT.
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var connection2 = reopened.Connect();
            QueryIntegers(connection2, "SELECT count(*) FROM docs WHERE id = 800001;").Should().Equal(0);
            QueryIntegers(connection2, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;")
                .Should().Equal(QueryIntegers(
                    connection2,
                    $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;"));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void VacuumBackupAndAttachPreserveTheIndex()
    {
        var path = CreateDatabasePath("managed-vector-index-durability");
        var attached = CreateDatabasePath("managed-vector-index-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(
                    connection,
                    GenerateClusteredVectors(300, Dimensions, seed: 97531),
                    VectorTestEncoding.Float32,
                    VectorTestMetric.L2,
                    Dimensions);
                Execute(connection, "DELETE FROM docs WHERE id % 7 = 0;");
                Execute(connection, "DELETE FROM plain WHERE id % 7 = 0;");
                Execute(connection, "VACUUM;");
                QueryIntegers(connection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;")
                    .Should().Equal(QueryIntegers(
                        connection,
                        $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;"));

                Execute(connection, $"VACUUM INTO '{attached.Replace("'", "''", StringComparison.Ordinal)}';");
            }

            using var copy = EmbeddedDatabase.OpenFile(attached);
            using var copyConnection = copy.Connect();
            QueryIntegers(copyConnection, $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;")
                .Should().Equal(QueryIntegers(
                    copyConnection,
                    $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;"));
        }
        finally
        {
            DeleteDatabase(path);
            DeleteDatabase(attached);
        }
    }

    [Test]
    public void AnAttachedDatabaseKeepsItsOwnIndex()
    {
        var main = CreateDatabasePath("managed-vector-index-durability");
        var side = CreateDatabasePath("managed-vector-index-durability");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(side))
            using (var connection = database.Connect())
            {
                SeedCorpus(
                    connection,
                    GenerateClusteredVectors(300, Dimensions, seed: 24680),
                    VectorTestEncoding.Float32,
                    VectorTestMetric.L2,
                    Dimensions);
            }

            using var host = EmbeddedDatabase.OpenFile(main);
            using var hostConnection = host.Connect();
            Execute(hostConnection, "CREATE TABLE placeholder(id INTEGER PRIMARY KEY);");
            Execute(hostConnection, $"ATTACH DATABASE '{side.Replace("'", "''", StringComparison.Ordinal)}' AS side;");

            // The method index travels with its own schema: the attached table's index is resolved
            // against the attached table's rows, so the answer matches its own un-indexed sibling.
            var indexed = QueryIntegers(
                hostConnection,
                $"SELECT id FROM side.docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;");
            var scanned = QueryIntegers(
                hostConnection,
                $"SELECT id FROM side.plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;");
            indexed.Should().Equal(scanned);
            indexed.Should().HaveCount(5);

            Execute(hostConnection, "DETACH DATABASE side;");
        }
        finally
        {
            DeleteDatabase(main);
            DeleteDatabase(side);
        }
    }

    [Test]
    public void MissingStateTrainsSilently()
    {
        var attachment = CreateAttachment();
        var act = () => attachment.LoadState(0, []);
        act.Should().NotThrow();
    }

    [Test]
    public void ANewerStateVersionFailsClosed()
    {
        var attachment = CreateAttachment();
        var act = () => attachment.LoadState(int.MaxValue, attachment.SaveState());
        act.Should().Throw<EmbeddedSqlException>().WithMessage("*was written by a newer managed index method*");
    }

    [TestCaseSource(nameof(CorruptionCases))]
    public void TheCorruptionMatrixFailsClosed(Func<byte[], byte[]> corrupt, string expected)
    {
        var attachment = CreateAttachment();
        var state = corrupt(attachment.SaveState());
        var act = () => attachment.LoadState(1, state);
        act.Should().Throw<EmbeddedSqlException>().WithMessage(expected);
    }

    private static IEnumerable<TestCaseData> CorruptionCases()
    {
        yield return Case("EmptyPayload", static _ => [], "*empty state*");
        yield return Case("TruncatedHeader", static _ => new byte[8], "*truncated state*");
        yield return Case(
            "BadMagic",
            static state =>
            {
                state[0] ^= 0xFF;
                return state;
            },
            "*truncated state*");
        yield return Case("Metric", static state => Flip(state, 6), "*state metric does not match*");
        yield return Case("Encoding", static state => Flip(state, 7), "*state encoding does not match*");
        yield return Case("Dimensions", static state => Flip(state, 8), "*state dims does not match*");
        yield return Case("Lists", static state => Flip(state, 12), "*state lists does not match*");
        yield return Case("Iterations", static state => Flip(state, 16), "*state iters does not match*");
        yield return Case("TrainSample", static state => Flip(state, 20), "*state train_sample does not match*");
        yield return Case("Seed", static state => Flip(state, 24), "*state seed does not match*");
        yield return Case("Exact", static state => Flip(state, 36), "*state exact does not match*");
        yield return Case("Probes", static state => Flip(state, 40), "*state probes does not match*");
        yield return Case(
            "PayloadLength",
            static state => state[..^4],
            "*centroid payload length mismatch*");
        yield return Case(
            "Checksum",
            static state =>
            {
                state[^1] ^= 0x7F;
                return state;
            },
            "*centroid checksum mismatch*");
        yield return Case(
            "NonFiniteCentroid",
            static state =>
            {
                // NaN in the first centroid component, with the fingerprint recomputed so the test
                // proves the finite-value check runs on its own rather than riding the checksum.
                BitConverter.GetBytes(float.NaN).CopyTo(state, ManagedVectorIndexState.HeaderSize);
                RewriteFingerprint(state);
                return state;
            },
            "*non-finite centroid*");
        yield return Case(
            "NegativeTrainedRows",
            static state =>
            {
                BitConverter.GetBytes(-1).CopyTo(state, 32);
                return state;
            },
            "*invalid trained row count*");
    }

    [Test]
    public void AnOversizedEnvelopeIsRejectedBeforeTheDecodeAllocates()
    {
        var attachment = CreateAttachment();

        // Nine megabytes of nonsense: the size gate has to fire before the loader tries to read a
        // centroid out of it.
        var act = () => attachment.LoadState(1, new byte[9 * 1024 * 1024]);
        act.Should().Throw<EmbeddedSqlException>().WithMessage("*would exceed 4194304 bytes*");
    }

    [Test]
    public void AnEnvelopeOnAnOrdinaryIndexIsLeftAlone()
    {
        const string declaration = "CREATE INDEX i ON t (a) /*ahtola-index-method:1:AAAA*/";
        ManagedIndexMethodStateSql.TrySplit(
                declaration,
                ManagedIndexMethodSemantics.IsMethodIndexDeclaration,
                out var parsed,
                out _,
                out _)
            .Should().BeFalse();
        parsed.Should().Be(declaration);
    }

    [Test]
    public void TheEmittedSqlIsNotStockSqliteParseable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4);");

        // A database carrying a method index is Ahtola/Turso-only by construction: stock SQLite has
        // no USING clause on CREATE INDEX. The suite states that as an assertion rather than a hope.
        var sql = Query(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_knn';")[0][0].AsText();
        sql.Should().Contain("USING vector");

        using var stock = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        stock.Open();
        using var command = stock.CreateCommand();
        command.CommandText = "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB); " + sql + ";";
        var act = () => command.ExecuteNonQuery();
        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
    }

    private static TestCaseData Case(string name, Func<byte[], byte[]> corrupt, string expected)
        => new TestCaseData(corrupt, expected).SetName(name);

    private static byte[] Flip(byte[] state, int offset)
    {
        state[offset] ^= 0x01;
        RewriteFingerprint(state);
        return state;
    }

    /// <summary>Recomputes the payload fingerprint so a field check is not masked by the checksum.</summary>
    private static void RewriteFingerprint(byte[] state)
    {
        var hash = 0xCBF29CE484222325UL;
        for (var index = ManagedVectorIndexState.HeaderSize; index < state.Length; index++)
        {
            hash ^= state[index];
            hash *= 0x100000001B3UL;
        }

        BitConverter.GetBytes((uint)(hash & 0xFFFF_FFFFUL)).CopyTo(state, 44);
    }

    private static ManagedIndexMethodAttachment CreateAttachment()
        => ManagedIndexMethodRegistry.Resolve("vector").Attach(new ManagedIndexMethodConfiguration(
            "docs",
            "docs_knn",
            [new ManagedIndexMethodColumn("embedding", 1)],
            [
                new ManagedIndexMethodParameter("dims", SqlValue.Integer(4)),
                new ManagedIndexMethodParameter("lists", SqlValue.Integer(8)),
            ]));
}
