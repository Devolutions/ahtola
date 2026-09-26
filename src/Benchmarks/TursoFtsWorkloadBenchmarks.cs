using System.Data.Common;
using System.Text;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// A realistic full-text workload: a Zipf-distributed 20,000-term vocabulary over 46-token
/// documents, compared against SQLite FTS5 (external content, trigger maintained). Each category
/// pairs the SQLite baseline with the equivalent Ahtola <c>USING fts</c> statement.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoFtsWorkloadBenchmarks
{
    private const string CommonTerm = "t1";
    private const string MidTerm = "t300";
    private const string MidTerm2 = "t301";
    private const string RareTerm = "t15000";

    private AhtolaConnection? _ahtola;
    private MicrosoftSqlite.SqliteConnection? _sqlite;
    private long _nextId;

    [Params(20_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new MicrosoftSqlite.SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();

        const string table = "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL);";
        TursoMemoryBenchmarkSupport.Execute(_ahtola, table);
        TursoMemoryBenchmarkSupport.Execute(_ahtola, "CREATE INDEX docs_fts ON docs USING fts (title, body);");
        TursoMemoryBenchmarkSupport.Execute(_sqlite, table);
        TursoMemoryBenchmarkSupport.Execute(
            _sqlite, "CREATE VIRTUAL TABLE docs_fts USING fts5(title, body, content='docs', content_rowid='id');");
        TursoMemoryBenchmarkSupport.Execute(
            _sqlite,
            "CREATE TRIGGER docs_ai AFTER INSERT ON docs BEGIN "
            + "INSERT INTO docs_fts(rowid, title, body) VALUES(new.id, new.title, new.body); END;");
        TursoMemoryBenchmarkSupport.Execute(
            _sqlite,
            "CREATE TRIGGER docs_ad AFTER DELETE ON docs BEGIN "
            + "INSERT INTO docs_fts(docs_fts, rowid, title, body) VALUES('delete', old.id, old.title, old.body); END;");

        var corpus = new ZipfCorpus(seed: 42);
        var documents = Enumerable.Range(1, DocumentCount)
            .Select(id => (Id: (long)id, Title: corpus.Words(6), Body: corpus.Body()))
            .ToArray();
        Populate(_ahtola, documents);
        Populate(_sqlite, documents);
        _nextId = DocumentCount + 1;

        // Build the lazily derived Ahtola postings before measuring steady-state queries.
        TursoMemoryBenchmarkSupport.Consume(_ahtola, $"SELECT count(*) FROM docs WHERE fts_match(title, body, '{CommonTerm}');");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtola?.Dispose();
        _sqlite?.Dispose();
    }

    [BenchmarkCategory("fts-workload-rare")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 rare term")]
    public long SqliteRare() => SqliteMatch(RareTerm);

    [BenchmarkCategory("fts-workload-rare")]
    [Benchmark(Description = "Ahtola fts rare term")]
    public long AhtolaRare() => AhtolaMatch(RareTerm);

    [BenchmarkCategory("fts-workload-mid")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 mid-frequency term")]
    public long SqliteMid() => SqliteMatch(MidTerm);

    [BenchmarkCategory("fts-workload-mid")]
    [Benchmark(Description = "Ahtola fts mid-frequency term")]
    public long AhtolaMid() => AhtolaMatch(MidTerm);

    [BenchmarkCategory("fts-workload-common-count")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 count(*) of a term in every document")]
    public long SqliteCommonCount() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!, $"SELECT count(*) FROM docs_fts WHERE docs_fts MATCH '{CommonTerm}';");

    [BenchmarkCategory("fts-workload-common-count")]
    [Benchmark(Description = "Ahtola fts count(*) of a term in every document")]
    public long AhtolaCommonCount() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, $"SELECT count(*) FROM docs WHERE fts_match(title, body, '{CommonTerm}');");

    [BenchmarkCategory("fts-workload-and")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 AND")]
    public long SqliteAnd() => SqliteMatch($"{MidTerm} AND {MidTerm2}");

    [BenchmarkCategory("fts-workload-and")]
    [Benchmark(Description = "Ahtola fts AND")]
    public long AhtolaAnd() => AhtolaMatch($"{MidTerm} AND {MidTerm2}");

    [BenchmarkCategory("fts-workload-phrase")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 phrase")]
    public long SqlitePhrase() => SqliteMatch("\"quantum ledger\"");

    [BenchmarkCategory("fts-workload-phrase")]
    [Benchmark(Description = "Ahtola fts phrase")]
    public long AhtolaPhrase() => AhtolaMatch("\"quantum ledger\"");

    [BenchmarkCategory("fts-workload-prefix")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 prefix (111 terms)")]
    public long SqlitePrefix() => SqliteMatch("t123*");

    [BenchmarkCategory("fts-workload-prefix")]
    [Benchmark(Description = "Ahtola fts prefix (111 terms)")]
    public long AhtolaPrefix() => AhtolaMatch("t123*");

    [BenchmarkCategory("fts-workload-top10")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 top-10 by bm25")]
    public long SqliteTop10() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!, $"SELECT rowid, title FROM docs_fts WHERE docs_fts MATCH '{MidTerm} OR {MidTerm2}' ORDER BY rank LIMIT 10;");

    [BenchmarkCategory("fts-workload-top10")]
    [Benchmark(Description = "Ahtola fts top-10 by fts_score")]
    public long AhtolaTop10() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!,
        $"SELECT id, title FROM docs WHERE fts_match(title, body, '{MidTerm} OR {MidTerm2}') "
        + $"ORDER BY fts_score(title, body, '{MidTerm} OR {MidTerm2}') DESC LIMIT 10;");

    [BenchmarkCategory("fts-workload-write")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 insert, query, delete")]
    public long SqliteWriteCycle() => WriteCycle(_sqlite!, $"SELECT count(*) FROM docs_fts WHERE docs_fts MATCH '{MidTerm}';");

    [BenchmarkCategory("fts-workload-write")]
    [Benchmark(Description = "Ahtola fts insert, query, delete")]
    public long AhtolaWriteCycle() => WriteCycle(_ahtola!, $"SELECT count(*) FROM docs WHERE fts_match(title, body, '{MidTerm}');");

    private long SqliteMatch(string query) => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!, $"SELECT rowid, title FROM docs_fts WHERE docs_fts MATCH '{query}';");

    private long AhtolaMatch(string query) => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, $"SELECT id, title FROM docs WHERE fts_match(title, body, '{query}');");

    private long WriteCycle(DbConnection connection, string query)
    {
        var id = _nextId++;
        TursoMemoryBenchmarkSupport.Execute(
            connection, $"INSERT INTO docs(id, title, body) VALUES ({id}, 'fresh {MidTerm}', 'new {MidTerm} document');");
        var hits = TursoMemoryBenchmarkSupport.Consume(connection, query);
        TursoMemoryBenchmarkSupport.Execute(connection, $"DELETE FROM docs WHERE id = {id};");
        return hits;
    }

    private static void Populate(DbConnection connection, (long Id, string Title, string Body)[] documents)
    {
        using var transaction = connection.BeginTransaction();
        for (var start = 0; start < documents.Length; start += 500)
        {
            var sql = new StringBuilder("INSERT INTO docs(id, title, body) VALUES ");
            for (var index = start; index < Math.Min(start + 500, documents.Length); index++)
            {
                if (index > start)
                    sql.Append(',');
                var (id, title, body) = documents[index];
                sql.Append('(').Append(id).Append(",'").Append(title).Append("','").Append(body).Append("')");
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = sql.ToString();
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Deterministic Zipf(1.07) sampler over <c>t1</c> … <c>t20000</c>.</summary>
    private sealed class ZipfCorpus
    {
        private readonly double[] _cumulative = new double[20_000];
        private readonly Random _random;

        public ZipfCorpus(int seed)
        {
            _random = new Random(seed);
            var sum = 0.0;
            for (var rank = 0; rank < _cumulative.Length; rank++)
                sum += 1.0 / Math.Pow(rank + 1, 1.07);
            var running = 0.0;
            for (var rank = 0; rank < _cumulative.Length; rank++)
                _cumulative[rank] = running += 1.0 / Math.Pow(rank + 1, 1.07) / sum;
        }

        public string Body()
            => _random.Next(100) == 0 ? Words(40) + " quantum ledger" : Words(40);

        public string Words(int count)
        {
            var builder = new StringBuilder(count * 6);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                    builder.Append(' ');
                var rank = Array.BinarySearch(_cumulative, _random.NextDouble());
                builder.Append('t').Append(Math.Min((rank < 0 ? ~rank : rank) + 1, _cumulative.Length));
            }

            return builder.ToString();
        }
    }
}
