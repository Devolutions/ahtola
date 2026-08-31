using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// Planner and statement preparation cases adapted from the read and schema
/// workloads in <c>turso-src/core/benches/benchmark.rs</c>.
/// </summary>
/// <remarks>
/// Setup creates a deterministic medium-sized catalog. The measured operation
/// constructs and prepares a fresh command against an already-open connection,
/// isolating warm connection planning from connection-open costs.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoPlannerBenchmarks
{
    private const string ConstantCategory = "planner-constant";
    private const string PointReadCategory = "planner-point-read";
    private const string AggregateCategory = "planner-aggregate";
    private const int SchemaTableCount = 128;

    private AhtolaConnection _ahtola = null!;
    private MicrosoftSqliteConnection _sqlite = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        _ahtola.Open();
        Seed(_ahtola);

        _sqlite = new MicrosoftSqliteConnection("Data Source=:memory:");
        _sqlite.Open();
        Seed(_sqlite);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ahtola.Dispose();
        _sqlite.Dispose();
        AhtolaConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
    }

    [BenchmarkCategory(ConstantCategory)]
    [Benchmark]
    public int AhtolaPrepareConstant()
        => Prepare(_ahtola, "SELECT 1;");

    [BenchmarkCategory(ConstantCategory)]
    [Benchmark(Baseline = true)]
    public int SqlitePrepareConstant()
        => Prepare(_sqlite, "SELECT 1;");

    [BenchmarkCategory(PointReadCategory)]
    [Benchmark]
    public int AhtolaPreparePointRead()
        => Prepare(_ahtola, "SELECT name FROM planner_table_0 WHERE id = $id;");

    [BenchmarkCategory(PointReadCategory)]
    [Benchmark(Baseline = true)]
    public int SqlitePreparePointRead()
        => Prepare(_sqlite, "SELECT name FROM planner_table_0 WHERE id = $id;");

    [BenchmarkCategory(AggregateCategory)]
    [Benchmark]
    public int AhtolaPrepareAggregate()
        => Prepare(
            _ahtola,
            "SELECT state, COUNT(*), MAX(name), SUM(value) FROM planner_rows GROUP BY state HAVING COUNT(*) > 1;");

    [BenchmarkCategory(AggregateCategory)]
    [Benchmark(Baseline = true)]
    public int SqlitePrepareAggregate()
        => Prepare(
            _sqlite,
            "SELECT state, COUNT(*), MAX(name), SUM(value) FROM planner_rows GROUP BY state HAVING COUNT(*) > 1;");

    private static int Prepare(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$id", StringComparison.Ordinal))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = 1L;
            command.Parameters.Add(parameter);
        }

        command.Prepare();
        return command.CommandText.Length + command.Parameters.Count;
    }

    private static void Seed(DbConnection connection)
    {
        for (var table = 0; table < SchemaTableCount; table++)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE TABLE planner_table_{table} (id INTEGER PRIMARY KEY, name TEXT, value INTEGER);";
            command.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE planner_rows(id INTEGER PRIMARY KEY, state TEXT, name TEXT, value INTEGER);";
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO planner_rows(id, state, name, value) VALUES ($id, $state, $name, $value);";
        var id = AddParameter(insert, "$id");
        var state = AddParameter(insert, "$state");
        var name = AddParameter(insert, "$name");
        var value = AddParameter(insert, "$value");
        for (var row = 1; row <= 256; row++)
        {
            id.Value = (long)row;
            state.Value = $"state-{row % 8:D2}";
            name.Value = $"name-{row:D4}";
            value.Value = (long)(row * 3);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static DbParameter AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
