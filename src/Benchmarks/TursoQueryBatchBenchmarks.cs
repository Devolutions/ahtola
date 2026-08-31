using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// Small-batch row materialization adapted from
/// <c>turso-src/perf/query-batch/benches/query_batch.rs</c>.
/// </summary>
/// <remarks>
/// Both lanes execute the same parameterized query and materialize every field
/// into the same managed record shape, making provider read and materialization
/// costs directly comparable.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[BenchmarkCategory("query-batch-materialization")]
public class TursoQueryBatchBenchmarks
{
    private const string Query =
        "SELECT id, name, value, is_active FROM query_batch_rows WHERE is_active = $active;";

    private AhtolaConnection _ahtola = null!;
    private MicrosoftSqliteConnection _sqlite = null!;

    [Params(10, 100, 1_000)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ahtola = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        _ahtola.Open();
        Seed(_ahtola, RowCount);

        _sqlite = new MicrosoftSqliteConnection("Data Source=:memory:");
        _sqlite.Open();
        Seed(_sqlite, RowCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ahtola.Dispose();
        _sqlite.Dispose();
        AhtolaConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
    }

    [Benchmark]
    public List<MaterializedRow> AhtolaMaterialize()
        => Materialize(_ahtola, RowCount);

    [Benchmark(Baseline = true)]
    public List<MaterializedRow> SqliteMaterialize()
        => Materialize(_sqlite, RowCount);

    private static void Seed(DbConnection connection, int rowCount)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE query_batch_rows(
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    value INTEGER NOT NULL,
                    is_active INTEGER NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO query_batch_rows(id, name, value, is_active) VALUES ($id, $name, $value, 1);";
        var id = AddParameter(insert, "$id");
        var name = AddParameter(insert, "$name");
        var value = AddParameter(insert, "$value");
        for (var row = 0; row < rowCount; row++)
        {
            id.Value = (long)row;
            name.Value = $"name_{row:D4}";
            value.Value = (long)(row * 17);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static List<MaterializedRow> Materialize(DbConnection connection, int expectedCount)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Query;
        var active = AddParameter(command, "$active");
        active.Value = 1L;

        using var reader = command.ExecuteReader();
        var rows = new List<MaterializedRow>(expectedCount);
        while (reader.Read())
        {
            rows.Add(
                new MaterializedRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3) != 0));
        }

        return rows;
    }

    private static DbParameter AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    public readonly record struct MaterializedRow(long Id, string Name, long Value, bool IsActive);
}
