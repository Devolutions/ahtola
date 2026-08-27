using System.Buffers.Binary;
using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Vectors;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSparseVectorIndexTests
{
    [Test]
    public void IndexedJaccardMatchesScalarScanWithDeterministicTies()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        const string indexed = "SELECT id FROM docs ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[1,0,0,0,0,0,0,0]')) LIMIT 7;";
        const string scalar = "SELECT id FROM plain ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[1,0,0,0,0,0,0,0]')) LIMIT 7;";

        ExplainDetail(connection, indexed).Should().Contain("INDEX METHOD vector INDEX docs_sparse").And.Contain("exact=1");
        QueryIntegers(connection, indexed).Should().Equal(QueryIntegers(connection, scalar));

        const string reversed = "SELECT id FROM docs ORDER BY vector_distance_jaccard(vector32_sparse('[1,0,0,0,0,0,0,0]'), embedding) LIMIT 7;";
        ExplainDetail(connection, reversed).Should().Contain("INDEX METHOD vector INDEX docs_sparse");
        QueryIntegers(connection, reversed).Should().Equal(QueryIntegers(connection, scalar));
    }

    [Test]
    public void OutOfOrderEqualDistanceLimitUsesAscendingRowIdTies()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE INDEX docs_sparse ON docs USING vector (embedding) WITH (dims=4, encoding='float32_sparse', metric='jaccard', min_rows=0);");
        foreach (var rowId in new[] { 30, 10, 20 })
            Execute(connection, $"INSERT INTO docs VALUES({rowId}, vector32_sparse('[1,0,0,0]'));");

        const string sql =
            "SELECT id FROM docs ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[1,0,0,0]')) LIMIT 1;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");
        QueryIntegers(connection, sql).Should().Equal(10);

        var source = new ArrayManagedIndexSource();
        var value = Query(connection, "SELECT vector32_sparse('[1,0,0,0]');")[0][0];
        foreach (var rowId in new long[] { 30, 10, 20 })
            source.Upsert(rowId, value);
        var index = new ManagedSparseVectorIndex(dimensions: 4);
        foreach (var rowId in new long[] { 30, 10, 20 })
            index.Upsert(rowId, value);
        SqliteVectorFunctions.TryDecodeSparseVector(value, 4, out var query).Should().BeTrue();

        index.Search(value, query, limit: 1, source: source, columnIndex: 0)
            .Rows.Select(static row => row.RowId).Should().Equal(10, 20, 30);
    }

    [Test]
    public void CandidateCapCoversPostingsAlwaysRerankAndAllRowExpansion()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var componentZero = Query(connection, "SELECT vector32_sparse('[1,0,0,0]');")[0][0];
        var componentOne = Query(connection, "SELECT vector32_sparse('[0,1,0,0]');")[0][0];
        var negative = Query(connection, "SELECT vector32_sparse('[-1,0,0,0]');")[0][0];
        SqliteVectorFunctions.TryDecodeSparseVector(componentZero, 4, out var query).Should().BeTrue();

        AssertCapFallback(
            query,
            componentZero,
            [(30, componentZero), (10, componentZero), (20, componentZero)],
            limit: 1);
        AssertCapFallback(
            query,
            componentZero,
            [(30, negative), (10, negative), (20, negative)],
            limit: 1);
        AssertCapFallback(
            query,
            componentZero,
            [(30, componentZero), (10, componentOne), (20, componentOne)],
            limit: 2);
    }

    [Test]
    public void DmlTriggersCascadesAndReusedRowidsMaintainPostings()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);
        Execute(connection, "CREATE TABLE parents(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO parents VALUES (1);");
        Execute(connection, "CREATE TRIGGER move_vector AFTER UPDATE OF id ON parents BEGIN UPDATE docs SET embedding = vector32_sparse('[1,0,0,0,0,0,0,0]') WHERE id = 40; END;");
        Execute(connection, "UPDATE parents SET id = 2 WHERE id = 1;");
        Execute(connection, "DELETE FROM docs WHERE id = 1;");
        Execute(connection, "INSERT INTO docs(id, embedding) VALUES (1, vector32_sparse('[0,0,0,0,0,0,0,3]'));");

        const string query = "SELECT id FROM docs ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[1,0,0,0,0,0,0,0]')) LIMIT 4;";
        QueryIntegers(connection, query).Should().Contain(40).And.NotContain(1);

        Execute(connection, "CREATE TABLE owners(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE children(id INTEGER PRIMARY KEY REFERENCES owners(id) ON DELETE CASCADE, embedding BLOB);");
        Execute(connection, "CREATE INDEX child_sparse ON children USING vector (embedding) WITH (dims=8, encoding='float32_sparse', metric='jaccard', min_rows=0);");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "INSERT INTO owners VALUES(1);");
        Execute(connection, "INSERT INTO children VALUES(1, vector32_sparse('[1,0,0,0,0,0,0,0]'));");
        Execute(connection, "DELETE FROM owners WHERE id=1;");
        QueryIntegers(connection, "SELECT count(*) FROM children;").Should().Equal(0);
    }

    [Test]
    public void NegativeQueryFallsBackExactlyAndRepricesItselfOut()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);
        const string indexed = "SELECT id FROM docs ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[-1,0,0,0,0,0,0,0]')) LIMIT 5;";
        const string scalar = "SELECT id FROM plain ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[-1,0,0,0,0,0,0,0]')) LIMIT 5;";

        ExplainDetail(connection, indexed).Should().Contain("INDEX METHOD vector");
        QueryIntegers(connection, indexed).Should().Equal(QueryIntegers(connection, scalar));
        ExplainDetail(connection, indexed).Should().NotContain("INDEX METHOD vector");
    }

    [Test]
    public void RollbackSavepointReindexAndOptimizePreserveExactResults()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);
        const string query = "SELECT id FROM docs ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[1,0,0,0,0,0,0,0]')) LIMIT 5;";
        var expected = QueryIntegers(connection, query);

        Execute(connection, "BEGIN;");
        Execute(connection, "UPDATE docs SET embedding=vector32_sparse('[1,0,0,0,0,0,0,0]') WHERE id=63;");
        QueryIntegers(connection, query).Should().Contain(63);
        Execute(connection, "SAVEPOINT s;");
        Execute(connection, "DELETE FROM docs WHERE id=9;");
        QueryIntegers(connection, query).Should().NotContain(9);
        Execute(connection, "ROLLBACK TO s;");
        QueryIntegers(connection, query).Should().Contain(9).And.Contain(63);
        Execute(connection, "ROLLBACK;");
        QueryIntegers(connection, query).Should().Equal(expected);
        Execute(connection, "REINDEX docs_sparse;");
        QueryIntegers(connection, query).Should().Equal(expected);
    }

    [Test]
    public void OptimizeRebuildsCompactDeterministicPostings()
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "docs",
            "docs_sparse",
            [new ManagedIndexMethodColumn("embedding", 0)],
            [
                new ManagedIndexMethodParameter("dims", SqlValue.Integer(4)),
                new ManagedIndexMethodParameter("encoding", SqlValue.Text("float32_sparse")),
                new ManagedIndexMethodParameter("metric", SqlValue.Text("jaccard")),
                new ManagedIndexMethodParameter("min_rows", SqlValue.Integer(0)),
            ]);
        var attachment = (ManagedSparseVectorIndexAttachment)ManagedIndexMethodRegistry.Resolve("vector").Attach(configuration);
        var source = new ArrayManagedIndexSource();
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        for (var id = 1; id <= 20; id++)
        {
            var component = id % 4;
            var literal = component switch
            {
                0 => "[1,0,0,0]",
                1 => "[0,1,0,0]",
                2 => "[0,0,1,0]",
                _ => "[0,0,0,1]",
            };
            source.Upsert(id, Query(connection, $"SELECT vector32_sparse('{literal}');")[0][0]);
        }
        using (var cursor = attachment.Open(source))
            cursor.OpenRead();
        for (var id = 1; id <= 10; id++)
            source.Remove(id);
        using (var cursor = attachment.Open(source))
            cursor.OpenRead();

        var rows = attachment.Index.IndexedRows;
        var components = attachment.Index.ComponentCount;
        using (var cursor = attachment.Open(source))
            cursor.Optimize();

        attachment.Index.IndexedRows.Should().Be(rows);
        attachment.Index.ComponentCount.Should().Be(components);
    }

    [Test]
    public void ReopenWalRecoveryAndVacuumIntoRetainSparseIndex()
    {
        var path = CreateDatabasePath("managed-sparse-vector");
        var copy = CreateDatabasePath("managed-sparse-vector-copy");
        try
        {
            IReadOnlyList<long> expected;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "PRAGMA journal_mode=WAL;");
                Seed(connection);
                expected = QueryIntegers(connection, QuerySql);
                Execute(connection, $"VACUUM INTO '{copy.Replace("'", "''", StringComparison.Ordinal)}';");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                QueryIntegers(connection, QuerySql).Should().Equal(expected);
            using (var database = EmbeddedDatabase.OpenFile(copy))
            using (var connection = database.Connect())
                QueryIntegers(connection, QuerySql).Should().Equal(expected);
        }
        finally
        {
            DeleteDatabase(path);
            DeleteDatabase(copy);
        }
    }

    [Test]
    public void SparseDecoderAndStateFailClosedBeforeUnboundedAllocation()
    {
        var bytes = new byte[(5 * 8) + 5];
        for (var index = 0; index < 5; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), 1.0f);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (index * 4)), (uint)index);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 4);
        bytes[^1] = 9;
        SqliteVectorFunctions.TryDecodeSparseVector(SqlValue.Blob(bytes), 4, out _).Should().BeFalse();

        var configuration = new ManagedIndexMethodConfiguration(
            "docs",
            "docs_sparse",
            [new ManagedIndexMethodColumn("embedding", 1)],
            [
                new ManagedIndexMethodParameter("dims", SqlValue.Integer(4)),
                new ManagedIndexMethodParameter("encoding", SqlValue.Text("float32_sparse")),
                new ManagedIndexMethodParameter("metric", SqlValue.Text("jaccard")),
            ]);
        var options = ManagedVectorIndexOptions.Resolve(configuration);
        var state = ManagedSparseVectorIndexState.Encode(options);
        state[10] ^= 0x40;
        var action = () => ManagedSparseVectorIndexState.Validate(state, options);
        action.Should().Throw<EmbeddedSqlException>().WithMessage("*corrupt*");
    }

    private const string QuerySql =
        "SELECT id FROM docs ORDER BY vector_distance_jaccard(embedding, vector32_sparse('[1,0,0,0,0,0,0,0]')) LIMIT 5;";

    private static void Seed(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE INDEX docs_sparse ON docs USING vector (embedding) WITH (dims=8, encoding='float32_sparse', metric='jaccard', exact=1, min_rows=0);");
        Execute(connection, "BEGIN;");
        for (var id = 1; id <= 64; id++)
        {
            var component = (id - 1) % 8;
            var values = new int[8];
            values[component] = 1 + (id % 3);
            var literal = "[" + string.Join(",", values) + "]";
            Execute(connection, $"INSERT INTO docs VALUES({id}, vector32_sparse('{literal}'));");
            Execute(connection, $"INSERT INTO plain VALUES({id}, vector32_sparse('{literal}'));");
        }
        Execute(connection, "COMMIT;");
    }

    private static void AssertCapFallback(
        DecodedSparseVector query,
        SqlValue queryValue,
        (long RowId, SqlValue Value)[] rows,
        int limit)
    {
        var source = new ArrayManagedIndexSource();
        var index = new ManagedSparseVectorIndex(dimensions: 4, candidateLimit: 2);
        foreach (var (rowId, value) in rows)
        {
            source.Upsert(rowId, value);
            index.Upsert(rowId, value);
        }

        var result = index.Search(queryValue, query, limit, source, columnIndex: 0);
        result.Exhaustive.Should().BeTrue();
        result.RerankedRows.Should().Be(rows.Length);
        result.Rows.Select(static row => row.RowId).Should().Equal(10, 20, 30);
    }
}
