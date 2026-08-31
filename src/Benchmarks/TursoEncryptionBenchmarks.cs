using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Benchmarks;

/// <summary>
/// Ahtola-only page-encryption cost. Each measured operation is one durable
/// transaction; database creation and key parsing occur in iteration setup.
/// The returned value is the resulting database-plus-sidecar byte count.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoEncryptionBenchmarks
{
    private const int RowsPerTransaction = 128;
    private const string Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private string _root = string.Empty;
    private string _plainPath = string.Empty;
    private string _encryptedPath = string.Empty;
    private AhtolaConnection? _plain;
    private AhtolaConnection? _encrypted;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-encryption-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _plainPath = Path.Combine(_root, "plain.db");
        _encryptedPath = Path.Combine(_root, "encrypted.db");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        DisposeConnections();
        TursoMemoryBenchmarkSupport.DeleteDatabaseFamily(_plainPath);
        TursoMemoryBenchmarkSupport.DeleteDatabaseFamily(_encryptedPath);
        _plain = Open(_plainPath, encrypted: false);
        _encrypted = Open(_encryptedPath, encrypted: true);
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

    [BenchmarkCategory("ahtola-only", "encryption-write")]
    [Benchmark(Baseline = true, OperationsPerInvoke = RowsPerTransaction, Description = "Ahtola plaintext batch")]
    public long PlaintextWriteBatch() => InsertBatch(_plain!, _plainPath);

    [BenchmarkCategory("ahtola-only", "encryption-write")]
    [Benchmark(OperationsPerInvoke = RowsPerTransaction, Description = "Ahtola AES-256-GCM batch")]
    public long EncryptedWriteBatch() => InsertBatch(_encrypted!, _encryptedPath);

    private static AhtolaConnection Open(string path, bool encrypted)
    {
        var encryption = encrypted
            ? $";Encryption Cipher=Aes256Gcm;Encryption Key={Key}"
            : string.Empty;
        var connection = new AhtolaConnection($"Data Source={path};Pooling=False{encryption}");
        connection.Open();
        TursoMemoryBenchmarkSupport.Execute(
            connection, "CREATE TABLE payloads(id INTEGER PRIMARY KEY, payload BLOB NOT NULL);");
        return connection;
    }

    private static long InsertBatch(DbConnection connection, string path)
    {
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO payloads(id, payload) VALUES ($id, $payload);";
        var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
        var payload = TursoMemoryBenchmarkSupport.AddParameter(insert, "$payload");
        var bytes = new byte[512];
        for (var i = 1; i <= RowsPerTransaction; i++)
        {
            id.Value = i;
            bytes[0] = (byte)i;
            payload.Value = bytes;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return TursoMemoryBenchmarkSupport.DatabaseFamilySize(path);
    }

    private void DisposeConnections()
    {
        _plain?.Dispose();
        _plain = null;
        _encrypted?.Dispose();
        _encrypted = null;
    }
}
