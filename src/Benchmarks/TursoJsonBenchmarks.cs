using System.Data.Common;
using System.Text;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>Public SQL JSONB parsing benchmarks over representative payload sizes.</summary>
/// <remarks>Source provenance: <c>turso-src/core/benches/json_benchmark.rs</c>.</remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoJsonParseBenchmarks
{
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;
    private DbCommand _ahtolaCommand = null!;
    private DbCommand _sqliteCommand = null!;

    [Params("Small", "Medium", "Large")]
    public string PayloadSize { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        var payload = PayloadSize switch
        {
            "Small" => """{"id":1,"name":"Test"}""",
            "Medium" => """{"id":1,"name":"Test","attributes":{"color":"blue","size":"medium","tags":["tag1","tag2","tag3"]}}""",
            "Large" => TursoJsonSupport.BuildLargePayload(),
            _ => throw new InvalidOperationException(PayloadSize),
        };
        _ahtolaCommand = TursoJsonSupport.Prepared(_ahtola, "SELECT length(jsonb(@json))", payload);
        _sqliteCommand = TursoJsonSupport.Prepared(_sqlite, "SELECT length(jsonb(@json))", payload);
    }

    [Benchmark, BenchmarkCategory("JsonParse")]
    public object? AhtolaJsonb() => _ahtolaCommand.ExecuteScalar();

    [Benchmark(Baseline = true), BenchmarkCategory("JsonParse")]
    public object? MicrosoftDataSqliteJsonb() => _sqliteCommand.ExecuteScalar();

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtolaCommand.Dispose();
        _sqliteCommand.Dispose();
        _ahtola.Dispose();
        _sqlite.Dispose();
    }
}

/// <summary>JSON patch and sequential-conversion cases from the upstream JSON suite.</summary>
/// <remarks>Source provenance: <c>turso-src/core/benches/json_benchmark.rs</c>.</remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TursoJsonOperationBenchmarks
{
    private AhtolaConnection _ahtola = null!;
    private SqliteConnection _sqlite = null!;
    private DbCommand _ahtolaSequential = null!;
    private DbCommand _sqliteSequential = null!;
    private DbCommand _ahtolaPatch = null!;
    private DbCommand _sqlitePatch = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:");
        _sqlite = new SqliteConnection("Data Source=:memory:");
        _ahtola.Open();
        _sqlite.Open();
        const string sequential = "SELECT length(jsonb('{\"a\":1}')) + length(jsonb('[1,2,3]')) + length(jsonb('{\"nested\":{\"ok\":true}}')) + length(jsonb('\"text\"'))";
        const string patch = "SELECT json_patch('{\"a\":1,\"nested\":{\"x\":1}}', '{\"b\":2,\"nested\":{\"y\":3}}')";
        _ahtolaSequential = TursoJsonSupport.Prepared(_ahtola, sequential);
        _sqliteSequential = TursoJsonSupport.Prepared(_sqlite, sequential);
        _ahtolaPatch = TursoJsonSupport.Prepared(_ahtola, patch);
        _sqlitePatch = TursoJsonSupport.Prepared(_sqlite, patch);
    }

    [Benchmark, BenchmarkCategory("JsonSequential")]
    public object? AhtolaSequentialJsonb() => _ahtolaSequential.ExecuteScalar();

    [Benchmark(Baseline = true), BenchmarkCategory("JsonSequential")]
    public object? MicrosoftDataSqliteSequentialJsonb() => _sqliteSequential.ExecuteScalar();

    [Benchmark, BenchmarkCategory("JsonPatch")]
    public object? AhtolaJsonPatch() => _ahtolaPatch.ExecuteScalar();

    [Benchmark(Baseline = true), BenchmarkCategory("JsonPatch")]
    public object? MicrosoftDataSqliteJsonPatch() => _sqlitePatch.ExecuteScalar();

    [GlobalCleanup]
    public void Cleanup()
    {
        _ahtolaSequential.Dispose();
        _sqliteSequential.Dispose();
        _ahtolaPatch.Dispose();
        _sqlitePatch.Dispose();
        _ahtola.Dispose();
        _sqlite.Dispose();
    }
}

internal static class TursoJsonSupport
{
    public static DbCommand Prepared(DbConnection connection, string sql, string? payload = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (payload is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@json";
            parameter.Value = payload;
            command.Parameters.Add(parameter);
        }
        command.Prepare();
        return command;
    }

    public static string BuildLargePayload()
    {
        var json = new StringBuilder("""{"metadata":{"version":"1.0","unicode":"你好，世界！😀"},"items":[""");
        for (var i = 0; i < 100; i++)
        {
            if (i > 0)
                json.Append(',');
            json.Append("{\"id\":").Append(i)
                .Append(",\"name\":\"item-").Append(i)
                .Append("\",\"values\":[0,1,2,3,4,5,6,7,8,9]}");
        }
        return json.Append("]}").ToString();
    }
}
