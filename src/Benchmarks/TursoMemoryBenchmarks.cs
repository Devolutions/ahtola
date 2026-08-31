using System.Data.Common;
using System.Globalization;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqlite = Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Allocation-sensitive scan and recursive-queue workloads adapted from
/// Turso's memory profiles. All rows are fully materialized in the timed region.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoMemoryBenchmarks
{
    private AhtolaConnection? _ahtola;
    private MicrosoftSqlite.SqliteConnection? _sqlite;

    [Params(4_096)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new MicrosoftSqlite.SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        BuildSeries(_ahtola, RowCount);
        BuildSeries(_sqlite, RowCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtola?.Dispose();
        _sqlite?.Dispose();
    }

    [BenchmarkCategory("memory-full-scan")]
    [Benchmark(Baseline = true, Description = "SQLite materialized series scan")]
    public long SqliteScan() => TursoMemoryBenchmarkSupport.Consume(
        _sqlite!, "SELECT id, reading, payload FROM series ORDER BY id;");

    [BenchmarkCategory("memory-full-scan")]
    [Benchmark(Description = "Ahtola materialized series scan")]
    public long AhtolaScan() => TursoMemoryBenchmarkSupport.Consume(
        _ahtola!, "SELECT id, reading, payload FROM series ORDER BY id;");

    [BenchmarkCategory("memory-recursive-cte")]
    [Benchmark(Baseline = true, Description = "SQLite recursive CTE queue")]
    public long SqliteRecursiveCte() => RecursiveCte(_sqlite!);

    [BenchmarkCategory("memory-recursive-cte")]
    [Benchmark(Description = "Ahtola recursive CTE queue")]
    public long AhtolaRecursiveCte() => RecursiveCte(_ahtola!);

    private long RecursiveCte(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM seq WHERE x < $rows) "
            + "SELECT x, x * x FROM seq;";
        TursoMemoryBenchmarkSupport.AddParameter(command, "$rows").Value = RowCount;
        return TursoMemoryBenchmarkSupport.Consume(command);
    }

    private static void BuildSeries(DbConnection connection, int count)
    {
        TursoMemoryBenchmarkSupport.Execute(
            connection,
            "CREATE TABLE series(id INTEGER PRIMARY KEY, reading REAL NOT NULL, payload BLOB NOT NULL);");
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO series(id, reading, payload) VALUES ($id, $reading, $payload);";
        var id = TursoMemoryBenchmarkSupport.AddParameter(insert, "$id");
        var reading = TursoMemoryBenchmarkSupport.AddParameter(insert, "$reading");
        var payload = TursoMemoryBenchmarkSupport.AddParameter(insert, "$payload");
        var random = new Random(20260831);
        for (var i = 1; i <= count; i++)
        {
            var bytes = new byte[96 + (i % 5) * 32];
            random.NextBytes(bytes);
            id.Value = i;
            reading.Value = Math.Sin(i * 0.01);
            payload.Value = bytes;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}

internal static class TursoMemoryBenchmarkSupport
{
    internal static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static DbParameter AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    internal static long Consume(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Consume(command);
    }

    internal static long Consume(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            checksum++;
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                if (reader.IsDBNull(ordinal))
                    continue;
                checksum = unchecked((checksum * 397) ^ ValueHash(reader.GetValue(ordinal)));
            }
        }
        return checksum;
    }

    internal static long DatabaseFamilySize(string path)
    {
        long bytes = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", "-log" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                bytes += new FileInfo(candidate).Length;
        }
        return bytes;
    }

    internal static void DeleteDatabaseFamily(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", "-log" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private static long ValueHash(object value) => value switch
    {
        byte[] bytes => bytes.Length == 0 ? 0 : (bytes.Length * 31L) + bytes[0] + bytes[^1],
        string text => text.Length,
        long number => number,
        int number => number,
        double number => BitConverter.DoubleToInt64Bits(number),
        float number => BitConverter.SingleToInt32Bits(number),
        decimal number => decimal.ToInt64(decimal.Truncate(number)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 0,
    };
}
