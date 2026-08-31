using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>Shared file-backed fixture lifecycle for the Turso-derived write benchmarks.</summary>
public class TursoWriteBenchmarkSupport
{
    private string _root = string.Empty;
    private string _managedPath = string.Empty;
    private string _nativePath = string.Empty;

    protected DbConnection Managed { get; private set; } = null!;
    protected DbConnection Native { get; private set; } = null!;

    [GlobalSetup]
    public void CreateScratchDirectory()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-write-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _managedPath = Path.Combine(_root, "managed.db");
        _nativePath = Path.Combine(_root, "native.db");
    }

    [IterationSetup]
    public void ResetFixtures()
    {
        DisposeConnections();
        DeleteDatabase(_managedPath);
        DeleteDatabase(_nativePath);

        Managed = new AhtolaConnection($"Data Source={_managedPath}");
        Native = new SqliteConnection($"Data Source={_nativePath};Pooling=False");
        Managed.Open();
        Native.Open();
        Configure(Managed);
        Configure(Native);
    }

    [IterationCleanup]
    public void CloseFixtures() => DisposeConnections();

    [GlobalCleanup]
    public void DeleteScratchDirectory()
    {
        DisposeConnections();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    protected virtual void Configure(DbConnection connection)
    {
    }

    protected static int Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    protected static long Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    protected static int InsertRows(
        DbConnection connection,
        int count,
        Func<int, long>? keyFactory = null,
        string table = "test")
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}(id, data, val) VALUES ($id, $data, $val)";
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);
        var data = command.CreateParameter();
        data.ParameterName = "$data";
        command.Parameters.Add(data);
        var value = command.CreateParameter();
        value.ParameterName = "$val";
        command.Parameters.Add(value);

        var affected = 0;
        for (var row = 0; row < count; row++)
        {
            id.Value = keyFactory?.Invoke(row) ?? row;
            data.Value = "payload-" + row.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
            value.Value = row * 10L;
            affected += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected;
    }

    protected static int InsertBatch(DbConnection connection, IReadOnlyList<long> keys, int payloadLength = 24)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO test(id, data, val) VALUES ($id, $data, $val)";
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);
        var data = command.CreateParameter();
        data.ParameterName = "$data";
        command.Parameters.Add(data);
        var value = command.CreateParameter();
        value.ParameterName = "$val";
        command.Parameters.Add(value);
        var payload = new string('x', payloadLength);

        var affected = 0;
        for (var row = 0; row < keys.Count; row++)
        {
            id.Value = keys[row];
            data.Value = payload;
            value.Value = row * 10L;
            affected += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected;
    }

    private void DisposeConnections()
    {
        Managed?.Dispose();
        Native?.Dispose();
        Managed = null!;
        Native = null!;
        SqliteConnection.ClearAllPools();
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", "-log" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
