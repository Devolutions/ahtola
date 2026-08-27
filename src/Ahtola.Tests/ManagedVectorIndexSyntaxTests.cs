using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// The <c>CREATE INDEX … USING vector</c> surface: which declarations are accepted, which are
/// rejected, and the proof that every accepted <c>WITH</c> key changes something observable rather
/// than being accepted and ignored.
/// </summary>
public sealed class ManagedVectorIndexSyntaxTests
{
    private static EmbeddedConnection Connect(EmbeddedDatabase database)
    {
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB, other BLOB, label TEXT);");
        return connection;
    }

    [Test]
    public void TheMethodIsRegisteredWithoutReflection()
    {
        Core.Indexing.ManagedIndexMethodRegistry.Names.Should().Contain("vector");
        Core.Indexing.ManagedIndexMethodRegistry.Resolve("vector")
            .Should().BeSameAs(Core.Vectors.ManagedVectorIndexMethod.Instance);
    }

    [Test]
    public void AMinimalDeclarationIsAcceptedAndRoundTrips()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4);");

        // The catalog hands back the declaration with the state envelope stripped, exactly as it
        // does for the FTS method; the envelope bytes themselves are asserted on the file.
        var sql = Query(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_knn';")[0][0].AsText();
        sql.Should().Contain("USING vector").And.Contain("dims = 4").And.NotContain("ahtola-index-method");
    }

    [Test]
    public void DimensionsAreRequiredRatherThanInferred()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);

        // Inferring the dimensionality from the first row would silently change the index's meaning
        // the moment that row is deleted.
        ShouldThrow(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding);")
            .Message.Should().Contain("requires the vector index parameter 'dims'");
    }

    [Test]
    public void ExactlyOneColumnMayBeIndexed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);
        ShouldThrow(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding, other) WITH (dims = 4);")
            .Message.Should().Contain("must name exactly one vector column");
    }

    [Test]
    public void UnknownAndDuplicateParametersAreRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);
        ShouldThrow(connection, "CREATE INDEX a ON docs USING vector (embedding) WITH (dims = 4, nlist = 8);")
            .Message.Should().Be("unknown vector index parameter: nlist");
        ShouldThrow(connection, "CREATE INDEX b ON docs USING vector (embedding) WITH (dims = 4, dims = 8);")
            .Message.Should().Contain("Duplicate index method parameter 'dims'");
    }

    [TestCase("dims = 0", "'dims' must be between 1 and 2048")]
    [TestCase("dims = 4096", "'dims' must be between 1 and 2048")]
    [TestCase("dims = 4, lists = 0", "'lists' must be between 1 and 4096")]
    [TestCase("dims = 4, lists = 8192", "'lists' must be between 1 and 4096")]
    [TestCase("dims = 4, lists = 4, probes = 5", "'probes' must not exceed 'lists'")]
    [TestCase("dims = 4, probes = 0", "'probes' must be at least 1")]
    [TestCase("dims = 4, iters = 0", "'iters' must be between 1 and 16")]
    [TestCase("dims = 4, iters = 99", "'iters' must be between 1 and 16")]
    [TestCase("dims = 4, train_sample = 8", "'train_sample' must be between 256 and 65536")]
    [TestCase("dims = 4, train_sample = 1000000", "'train_sample' must be between 256 and 65536")]
    [TestCase("dims = 4, min_rows = -1", "'min_rows' must not be negative")]
    [TestCase("dims = 4, exact = 0", "'exact' must be 1; approximate mode is not implemented")]
    [TestCase("dims = 4, metric = 'euclidean'", "unknown vector index metric: euclidean")]
    [TestCase("dims = 4, encoding = 'bfloat16'", "unknown vector index encoding: bfloat16")]
    [TestCase("dims = 4, metric = 'jaccard'", "requires encoding = 'float32_sparse'")]
    [TestCase("dims = 4, encoding = 'float32_sparse'", "requires metric = 'jaccard'")]
    [TestCase("dims = 4, encoding = 'float32_sparse', metric = 'jaccard', lists = 8", "'lists' is not supported for float32_sparse")]
    [TestCase("dims = 4, encoding = 'float1bit', metric = 'l2'", "L2 distance is not supported for float1bit")]
    [TestCase("dims = '4'", "'dims' requires an integer literal")]
    [TestCase("dims = 4, metric = 2", "'metric' requires a text literal")]
    public void InvalidOptionsAreRejected(string options, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);
        ShouldThrow(connection, $"CREATE INDEX docs_knn ON docs USING vector (embedding) WITH ({options});")
            .Message.Should().Contain(expected);
    }

    [Test]
    public void ExactSparseJaccardDeclarationIsAcceptedAndRoundTrips()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);

        Execute(
            connection,
            "CREATE INDEX docs_sparse ON docs USING vector (embedding) "
            + "WITH (dims = 4, encoding = 'float32_sparse', metric = 'jaccard', exact = 1, min_rows = 0);");

        var sql = Query(connection, "SELECT sql FROM sqlite_schema WHERE name = 'docs_sparse';")[0][0].AsText();
        sql.Should().Contain("float32_sparse").And.Contain("jaccard").And.Contain("exact = 1");
    }

    [Test]
    public void AStateEnvelopeLargerThanTheCapIsRejectedBeforeItIsAllocated()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);

        // 4096 lists of 2048 float32 components is 32 MiB of centroids; the declaration is refused
        // rather than accepted and then discovered to be unloadable.
        ShouldThrow(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 2048, lists = 4096);")
            .Message.Should().Contain("would exceed 4194304 bytes");
    }

    [Test]
    public void InheritedMethodIndexRejectionsStillApply()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Connect(database);
        Execute(connection, "CREATE TABLE keyed(id TEXT PRIMARY KEY, embedding BLOB) WITHOUT ROWID;");
        Execute(connection, "CREATE VIEW docs_view AS SELECT * FROM docs;");

        ShouldThrow(connection, "CREATE UNIQUE INDEX a ON docs USING vector (embedding) WITH (dims = 4);")
            .Message.Should().Contain("UNIQUE is not supported with an index method");
        ShouldThrow(connection, "CREATE INDEX b ON docs USING vector (embedding) WITH (dims = 4) WHERE id > 3;")
            .Message.Should().Contain("partial WHERE clause is not supported with an index method");
        ShouldThrow(connection, "CREATE INDEX c ON docs USING vector (embedding DESC) WITH (dims = 4);")
            .Message.Should().Contain("DESC is not supported on an index method column");
        ShouldThrow(connection, "CREATE INDEX d ON docs USING vector (label COLLATE NOCASE) WITH (dims = 4);")
            .Message.Should().Contain("COLLATE is not supported on an index method column");
        ShouldThrow(connection, "CREATE INDEX e ON keyed USING vector (embedding) WITH (dims = 4);")
            .Message.Should().Contain("WITHOUT ROWID");
        ShouldThrow(connection, "CREATE INDEX f ON docs_view USING vector (embedding) WITH (dims = 4);");
        ShouldThrow(connection, "CREATE INDEX g_ahtola_idxm_x ON docs USING vector (embedding) WITH (dims = 4);")
            .Message.Should().Contain("reserved for internal use");
        ShouldThrow(connection, "CREATE INDEX h ON docs USING vector (lower(label)) WITH (dims = 4);");
    }

    [Test]
    public void EveryAcceptedOptionChangesSomethingObservable()
    {
        // Each key is proven to be consumed, not merely tolerated: metric and encoding change which
        // SQL call the index binds to, dims/lists change the persisted state size, seed changes the
        // trained centroids, probes/iters/train_sample change the persisted configuration, and
        // min_rows changes whether the plan is taken at all.
        StateFor("dims = 4, lists = 8").Should().NotBe(StateFor("dims = 4, lists = 16"));
        StateFor("dims = 4, lists = 8").Should().NotBe(StateFor("dims = 8, lists = 8"));
        StateFor("dims = 4, lists = 8, seed = 1").Should().NotBe(StateFor("dims = 4, lists = 8, seed = 2"));
        StateFor("dims = 4, lists = 8, iters = 1").Should().NotBe(StateFor("dims = 4, lists = 8, iters = 16"));
        StateFor("dims = 4, lists = 8, probes = 1").Should().NotBe(StateFor("dims = 4, lists = 8, probes = 8"));
        StateFor("dims = 4, lists = 8, train_sample = 256")
            .Should().NotBe(StateFor("dims = 4, lists = 8, train_sample = 512"));
        StateFor("dims = 4, lists = 8, metric = 'l2'")
            .Should().NotBe(StateFor("dims = 4, lists = 8, metric = 'cosine'"));
        StateFor("dims = 4, lists = 8, encoding = 'float32'")
            .Should().NotBe(StateFor("dims = 4, lists = 8, encoding = 'float64'"));
    }

    [Test]
    public void MinimumRowsDecidesWhetherThePlanIsTakenAtAll()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var vectors = GenerateClusteredVectors(600, 8, seed: 5150);
        SeedCorpus(connection, vectors, VectorTestEncoding.Float32, VectorTestMetric.L2, 8, minimumRows: 8);
        Execute(
            connection,
            "CREATE INDEX docs_knn_big ON docs USING vector (embedding) WITH (dims = 8, lists = 64, min_rows = 100000);");

        const string sql = "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[1,1,1,1,1,1,1,1]')) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("docs_knn").And.NotContain("docs_knn_big");

        Execute(connection, "DROP INDEX docs_knn;");
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");
    }

    /// <summary>The persisted state envelope produced for one set of options over a fixed corpus.</summary>
    private static string StateFor(string options)
    {
        var path = CreateDatabasePath("managed-vector-index-syntax");
        try
        {
            var dimensions = options.Contains("dims = 8", StringComparison.Ordinal) ? 8 : 4;
            var encoding = options.Contains("encoding = 'float64'", StringComparison.Ordinal) ? "vector64" : "vector32";
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
                Execute(connection, $"CREATE INDEX docs_knn ON docs USING vector (embedding) WITH ({options});");
                Execute(connection, "BEGIN;");
                var vectors = GenerateClusteredVectors(200, dimensions, seed: 31337);
                for (var index = 0; index < vectors.Length; index++)
                    Execute(connection, $"INSERT INTO docs VALUES ({index + 1}, {encoding}('{Literal(vectors[index])}'));");

                Execute(connection, "COMMIT;");
                Execute(connection, "REINDEX docs_knn;");
            }

            return ReadStoredEnvelope(path);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }
}
