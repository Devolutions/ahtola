using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Explicit checkpoint latency after deterministic update churn. Connections
/// stay open between workload generation and checkpoint so sidecars remain live.
/// Returned bytes make post-checkpoint storage growth observable.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoCheckpointBenchmarks
{
    private string _root = string.Empty;
    private string _sqlitePath = string.Empty;
    private string _walPath = string.Empty;
    private string _mvccPath = string.Empty;
    private MicrosoftSqlite.SqliteConnection? _sqlite;
    private AhtolaConnection? _wal;
    private AhtolaConnection? _mvcc;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-checkpoint-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sqlitePath = Path.Combine(_root, "sqlite.db");
        _walPath = Path.Combine(_root, "ahtola-wal.db");
        _mvccPath = Path.Combine(_root, "ahtola-mvcc.db");
    }

    [IterationSetup(Target = nameof(SqliteWalCheckpoint))]
    public void SetupSqlite()
    {
        _sqlite = OpenSqlite(_sqlitePath);
        GenerateChurn(_sqlite);
    }

    [IterationSetup(Target = nameof(AhtolaWalCheckpoint))]
    public void SetupAhtolaWal()
    {
        _wal = OpenAhtola(_walPath, "wal");
        GenerateChurn(_wal);
    }

    [IterationSetup(Target = nameof(AhtolaMvccCheckpoint))]
    public void SetupAhtolaMvcc()
    {
        _mvcc = OpenAhtola(_mvccPath, "mvcc");
        GenerateChurn(_mvcc, concurrent: true);
    }

    [IterationCleanup]
    public void IterationCleanup() => DisposeConnections();

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        DisposeConnections();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [BenchmarkCategory("checkpoint-shared-sql")]
    [Benchmark(Baseline = true, Description = "SQLite WAL truncate checkpoint")]
    public long SqliteWalCheckpoint() => Checkpoint(_sqlite!, _sqlitePath);

    [BenchmarkCategory("checkpoint-shared-sql")]
    [Benchmark(Description = "Ahtola WAL truncate checkpoint")]
    public long AhtolaWalCheckpoint() => Checkpoint(_wal!, _walPath);

    [BenchmarkCategory("ahtola-only", "checkpoint-mvcc")]
    [Benchmark(Description = "Ahtola MVCC truncate checkpoint")]
    public long AhtolaMvccCheckpoint() => Checkpoint(_mvcc!, _mvccPath);

    private static long Checkpoint(DbConnection connection, string path)
    {
        TursoMemoryBenchmarkSupport.Consume(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        return TursoMemoryBenchmarkSupport.DatabaseFamilySize(path);
    }

    private static void GenerateChurn(DbConnection connection, bool concurrent = false)
    {
        for (var transactionNumber = 0; transactionNumber < 32; transactionNumber++)
        {
            TursoMemoryBenchmarkSupport.Execute(connection, concurrent ? "BEGIN CONCURRENT;" : "BEGIN;");
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE checkpoint_rows SET value = value + 1 WHERE id = $id;";
            var id = TursoMemoryBenchmarkSupport.AddParameter(update, "$id");
            for (var item = 0; item < 16; item++)
            {
                id.Value = 1 + ((transactionNumber * 16 + item) % 512);
                update.ExecuteNonQuery();
            }
            TursoMemoryBenchmarkSupport.Execute(connection, "COMMIT;");
        }
    }

    private static MicrosoftSqlite.SqliteConnection OpenSqlite(string path)
    {
        TursoMemoryBenchmarkSupport.DeleteDatabaseFamily(path);
        var connection = new MicrosoftSqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Initialize(connection, "wal");
        return connection;
    }

    private static AhtolaConnection OpenAhtola(string path, string journalMode)
    {
        TursoMemoryBenchmarkSupport.DeleteDatabaseFamily(path);
        var connection = new AhtolaConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Initialize(connection, journalMode);
        return connection;
    }

    private static void Initialize(DbConnection connection, string journalMode)
    {
        TursoMemoryBenchmarkSupport.Execute(connection, $"PRAGMA journal_mode={journalMode};");
        TursoMemoryBenchmarkSupport.Execute(
            connection, "CREATE TABLE checkpoint_rows(id INTEGER PRIMARY KEY, value INTEGER NOT NULL);");
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO checkpoint_rows(id, value) VALUES ($id, 0);";
        var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
        for (var i = 1; i <= 512; i++)
        {
            id.Value = i;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        TursoMemoryBenchmarkSupport.Consume(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private void DisposeConnections()
    {
        _sqlite?.Dispose();
        _sqlite = null;
        _wal?.Dispose();
        _wal = null;
        _mvcc?.Dispose();
        _mvcc = null;
    }
}
