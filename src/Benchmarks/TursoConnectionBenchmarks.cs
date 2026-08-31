using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// Connection lifecycle measurements adapted from
/// <c>turso-src/perf/connection/limbo/src/main.rs</c>, and
/// <c>turso-src/perf/connection/rusqlite/src/main.rs</c>.
/// </summary>
/// <remarks>
/// The original tools time open plus prepare across generated schema sizes.
/// These cases make cold non-pooled open, warm pooled open, and open plus a
/// prepared point read explicit while using one deterministic on-disk image.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoConnectionBenchmarks
{
    private const string ColdOpenCategory = "connection-cold-open";
    private const string WarmOpenCategory = "connection-warm-open";
    private const string OpenPrepareReadCategory = "connection-open-prepare-read";
    private const int SchemaTableCount = 128;

    private string _root = null!;
    private string _databasePath = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-connection-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "schema.db");
        BuildFixture(_databasePath);

        PrimePool(new AhtolaConnection(AhtolaConnectionString(pooling: true)));
        PrimePool(new MicrosoftSqliteConnection(SqliteConnectionString(pooling: true)));
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        AhtolaConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [BenchmarkCategory(ColdOpenCategory)]
    [Benchmark]
    public int AhtolaColdNonPooledOpen()
        => OpenAndReturnState(new AhtolaConnection(AhtolaConnectionString(pooling: false)));

    [BenchmarkCategory(ColdOpenCategory)]
    [Benchmark(Baseline = true)]
    public int SqliteColdNonPooledOpen()
        => OpenAndReturnState(new MicrosoftSqliteConnection(SqliteConnectionString(pooling: false)));

    [BenchmarkCategory(WarmOpenCategory)]
    [Benchmark]
    public int AhtolaWarmPooledOpen()
        => OpenAndReturnState(new AhtolaConnection(AhtolaConnectionString(pooling: true)));

    [BenchmarkCategory(WarmOpenCategory)]
    [Benchmark(Baseline = true)]
    public int SqliteWarmPooledOpen()
        => OpenAndReturnState(new MicrosoftSqliteConnection(SqliteConnectionString(pooling: true)));

    [BenchmarkCategory(OpenPrepareReadCategory)]
    [Benchmark]
    public string AhtolaOpenPrepareAndRead()
        => OpenPrepareAndRead(new AhtolaConnection(AhtolaConnectionString(pooling: false)));

    [BenchmarkCategory(OpenPrepareReadCategory)]
    [Benchmark(Baseline = true)]
    public string SqliteOpenPrepareAndRead()
        => OpenPrepareAndRead(new MicrosoftSqliteConnection(SqliteConnectionString(pooling: false)));

    private string AhtolaConnectionString(bool pooling)
        => $"Data Source={_databasePath};Mode=ReadOnly;Pooling={pooling};Local Provider=Managed";

    private string SqliteConnectionString(bool pooling)
        => $"Data Source={_databasePath};Mode=ReadOnly;Pooling={pooling}";

    private static int OpenAndReturnState(DbConnection connection)
    {
        using (connection)
        {
            connection.Open();
            return (int)connection.State;
        }
    }

    private static string OpenPrepareAndRead(DbConnection connection)
    {
        using (connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM table_0 WHERE id = $id;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = 1L;
            command.Parameters.Add(parameter);
            command.Prepare();
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }
    }

    private static void PrimePool(DbConnection connection)
    {
        using (connection)
            connection.Open();
    }

    private static void BuildFixture(string path)
    {
        using var connection = new MicrosoftSqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        for (var table = 0; table < SchemaTableCount; table++)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 CREATE TABLE table_{table}(
                     id INTEGER PRIMARY KEY,
                     name TEXT NOT NULL,
                     value INTEGER NOT NULL,
                     created_at TEXT NOT NULL);
                 INSERT INTO table_{table}(id, name, value, created_at)
                 VALUES (1, 'fixture-{table:D3}', {table}, '1970-01-01T00:00:00.0000000Z');
                 """;
            command.ExecuteNonQuery();
        }
    }
}
