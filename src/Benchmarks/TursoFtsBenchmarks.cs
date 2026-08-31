using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>Deterministic FTS query and index-routing workloads.</summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoFtsBenchmarks
{
    private AhtolaConnection? _ahtola;
    private MicrosoftSqlite.SqliteConnection? _sqlite;

    [Params(5_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new MicrosoftSqlite.SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();

        TursoMemoryBenchmarkSupport.Execute(
            _ahtola, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL);");
        TursoMemoryBenchmarkSupport.Execute(
            _ahtola, "CREATE INDEX docs_fts ON docs USING fts (title, body);");
        TursoMemoryBenchmarkSupport.Execute(
            _sqlite, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL);");
        TursoMemoryBenchmarkSupport.Execute(
            _sqlite, "CREATE VIRTUAL TABLE docs_fts USING fts5(title, body, content='docs', content_rowid='id');");
        TursoMemoryBenchmarkSupport.Execute(
            _sqlite,
            "CREATE TRIGGER docs_ai AFTER INSERT ON docs BEGIN "
            + "INSERT INTO docs_fts(rowid, title, body) VALUES(new.id, new.title, new.body); END;");
        Populate(_ahtola, DocumentCount);
        Populate(_sqlite, DocumentCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtola?.Dispose();
        _sqlite?.Dispose();
    }

    [BenchmarkCategory("fts-common-term")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 common term")]
    public long SqliteCommonTerm() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!, "SELECT rowid, title FROM docs_fts WHERE docs_fts MATCH 'database';");

    [BenchmarkCategory("fts-common-term")]
    [Benchmark(Description = "Ahtola FTS index common term")]
    public long AhtolaCommonTerm() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, "SELECT id, title FROM docs WHERE fts_match(title, body, 'database');");

    [BenchmarkCategory("fts-phrase")]
    [Benchmark(Baseline = true, Description = "SQLite FTS5 phrase")]
    public long SqlitePhrase() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!, "SELECT rowid, title FROM docs_fts WHERE docs_fts MATCH '\"distributed systems\"';");

    [BenchmarkCategory("fts-phrase")]
    [Benchmark(Description = "Ahtola FTS index phrase")]
    public long AhtolaPhrase() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, "SELECT id, title FROM docs WHERE fts_match(title, body, '\"distributed systems\"');");

    [BenchmarkCategory("ahtola-only", "fts-index-method")]
    [Benchmark(Baseline = true, Description = "Ahtola FTS indexed")]
    public long AhtolaIndexedRareTerm() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, "SELECT id FROM docs WHERE fts_match(title, body, 'traceidentifier');");

    [BenchmarkCategory("ahtola-only", "fts-index-method")]
    [Benchmark(Description = "Ahtola FTS scalar scan (NOT INDEXED)")]
    public long AhtolaScannedRareTerm() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, "SELECT id FROM docs NOT INDEXED WHERE fts_match(title, body, 'traceidentifier');");

    private static void Populate(DbConnection connection, int count)
    {
        var topics = new[] { "database", "database", "storage", "networking", "compiler", "security" };
        var domains = new[] { "cloud", "mobile", "backend", "embedded", "distributed" };
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO docs(id, title, body) VALUES ($id, $title, $body);";
        var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
        var title = TursoMemoryBenchmarkSupport.AddParameter(insert, "$title");
        var body = TursoMemoryBenchmarkSupport.AddParameter(insert, "$body");
        for (var i = 1; i <= count; i++)
        {
            var topic = topics[i % topics.Length];
            var domain = domains[(i * 3) % domains.Length];
            id.Value = i;
            title.Value = $"{topic} {domain} field guide {i}";
            body.Value =
                $"A practical guide for {domain} teams covering {topic}, "
                + (i % 20 == 0 ? "distributed systems" : "production services")
                + (i % 1_000 == 0 ? " traceidentifier" : string.Empty);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
