using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Short-transaction read throughput adapted from Turso's throughput harness.
/// Begin/commit, parallel dispatch, and row materialization are measured;
/// schema, seeding, and worker-connection creation are not.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoConcurrencyBenchmarks
{
    private const int ReaderCount = 4;
    private const int ReadsPerReader = 32;
    private const int TotalReads = ReaderCount * ReadsPerReader;
    private string _root = string.Empty;
    private string _sqlitePath = string.Empty;
    private string _walPath = string.Empty;
    private string _mvccPath = string.Empty;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-concurrency-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sqlitePath = Path.Combine(_root, "sqlite.db");
        _walPath = Path.Combine(_root, "ahtola-wal.db");
        _mvccPath = Path.Combine(_root, "ahtola-mvcc.db");
    }

    [IterationSetup(Target = nameof(SqliteWalReaders))]
    public void SetupSqlite() => CreateSqlite(_sqlitePath);

    [IterationSetup(Target = nameof(AhtolaWalReaders))]
    public void SetupAhtolaWal() => CreateAhtola(_walPath, mvcc: false);

    [IterationSetup(Target = nameof(AhtolaMvccReaders))]
    public void SetupAhtolaMvcc() => CreateAhtola(_mvccPath, mvcc: true);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        MicrosoftSqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [BenchmarkCategory("concurrency-shared-sql")]
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalReads, Description = "SQLite WAL concurrent readers")]
    public Task<long> SqliteWalReaders() => RunReaders(
        _ => OpenSqlite(_sqlitePath),
        "BEGIN;",
        ReaderCount,
        ReadsPerReader);

    [BenchmarkCategory("concurrency-shared-sql")]
    [Benchmark(OperationsPerInvoke = TotalReads, Description = "Ahtola WAL concurrent readers")]
    public Task<long> AhtolaWalReaders() => RunReaders(
        _ => OpenAhtola(_walPath),
        "BEGIN;",
        ReaderCount,
        ReadsPerReader);

    [BenchmarkCategory("ahtola-only", "concurrency-mvcc")]
    [Benchmark(OperationsPerInvoke = TotalReads, Description = "Ahtola BEGIN CONCURRENT readers")]
    public Task<long> AhtolaMvccReaders() => RunReaders(
        _ => OpenAhtola(_mvccPath),
        "BEGIN CONCURRENT;",
        ReaderCount,
        ReadsPerReader);

    private static async Task<long> RunReaders(
        Func<int, DbConnection> connectionFactory,
        string beginSql,
        int readers,
        int readsPerReader)
    {
        var connections = Enumerable.Range(0, readers).Select(connectionFactory).ToArray();
        using var ready = new Barrier(readers);
        try
        {
            var tasks = Enumerable.Range(0, readers).Select(worker => Task.Run(() =>
            {
                var connection = connections[worker];
                ready.SignalAndWait();
                TursoMemoryBenchmarkSupport.Execute(connection, beginSql);
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT payload FROM writes WHERE id = $id;";
                var id = TursoMemoryBenchmarkSupport.AddParameter(select, "$id");
                long checksum = 0;
                for (var i = 0; i < readsPerReader; i++)
                {
                    id.Value = 1 + (worker * readsPerReader) + i;
                    checksum += Convert.ToString(select.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)?.Length ?? 0;
                }
                TursoMemoryBenchmarkSupport.Execute(connection, "COMMIT;");
                return checksum;
            })).ToArray();
            return (await Task.WhenAll(tasks)).Sum();
        }
        finally
        {
            foreach (var connection in connections)
                connection.Dispose();
        }
    }

    private static void CreateSqlite(string path)
    {
        TursoMemoryBenchmarkSupport.DeleteDatabaseFamily(path);
        using var connection = OpenSqlite(path);
        TursoMemoryBenchmarkSupport.Execute(connection, "PRAGMA journal_mode=wal;");
        TursoMemoryBenchmarkSupport.Execute(
            connection, "CREATE TABLE writes(id INTEGER PRIMARY KEY, worker INTEGER, payload TEXT);");
        Seed(connection);
    }

    private static void CreateAhtola(string path, bool mvcc)
    {
        TursoMemoryBenchmarkSupport.DeleteDatabaseFamily(path);
        using var connection = OpenAhtola(path);
        TursoMemoryBenchmarkSupport.Execute(connection, $"PRAGMA journal_mode={(mvcc ? "mvcc" : "wal")};");
        if (mvcc)
            TursoMemoryBenchmarkSupport.Execute(connection, "PRAGMA mvcc_checkpoint_threshold=-1;");
        TursoMemoryBenchmarkSupport.Execute(
            connection, "CREATE TABLE writes(id INTEGER PRIMARY KEY, worker INTEGER, payload TEXT);");
        Seed(connection);
    }

    private static void Seed(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO writes(id, worker, payload) VALUES ($id, $worker, $payload);";
        var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
        var worker = TursoMemoryBenchmarkSupport.AddParameter(insert, "$worker");
        var payload = TursoMemoryBenchmarkSupport.AddParameter(insert, "$payload");
        for (var i = 1; i <= TotalReads; i++)
        {
            id.Value = i;
            worker.Value = (i - 1) / ReadsPerReader;
            payload.Value = $"seed-item-{i:D4}";
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static MicrosoftSqlite.SqliteConnection OpenSqlite(string path)
    {
        var connection = new MicrosoftSqlite.SqliteConnection(
            $"Data Source={path};Pooling=False;Default Timeout=30");
        connection.Open();
        return connection;
    }

    private static AhtolaConnection OpenAhtola(string path)
    {
        var connection = new AhtolaConnection(
            $"Data Source={path};Pooling=False;Default Timeout=30");
        connection.Open();
        return connection;
    }
}
