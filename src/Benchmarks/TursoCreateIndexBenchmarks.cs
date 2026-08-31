using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Benchmarks;

/// <summary>
/// Ports Turso <c>core/benches/create_index_benchmark.rs</c> at the pinned
/// revision. The deterministic table is populated during iteration setup;
/// only index creation (and, for the explicit case, its transaction) is timed.
/// </summary>
[BenchmarkCategory("Write")]
[BenchmarkCategory("Large")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoCreateIndexBenchmarks : TursoWriteBenchmarkSupport
{
    [Params(1_000, 10_000)]
    public int RowCount { get; set; }

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=FULL");
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, val INTEGER, payload TEXT)");

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO t VALUES ($id, $val, $payload)";
        var id = Add(command, "$id");
        var value = Add(command, "$val");
        var payload = Add(command, "$payload");
        var effectiveRowCount = BenchmarkRunContext.ScaleForSmoke(RowCount, 250);
        for (var row = 0; row < effectiveRowCount; row++)
        {
            id.Value = row;
            value.Value = unchecked((long)((uint)row * 2_654_435_761U)) & 0x7fff_ffffL;
            payload.Value = "payload-" + row;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [BenchmarkCategory("create-index")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: CREATE INDEX on populated table")]
    public int NativeCreateIndex() => Execute(Native, "CREATE INDEX idx_val ON t(val)");

    [BenchmarkCategory("create-index")]
    [Benchmark(Description = "Ahtola: CREATE INDEX on populated table")]
    public int ManagedCreateIndex() => Execute(Managed, "CREATE INDEX idx_val ON t(val)");

    [BenchmarkCategory("create-index-transaction")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: BEGIN + CREATE INDEX + COMMIT")]
    public int NativeCreateIndexTransaction() => CreateIndexInTransaction(Native);

    [BenchmarkCategory("create-index-transaction")]
    [Benchmark(Description = "Ahtola: BEGIN + CREATE INDEX + COMMIT")]
    public int ManagedCreateIndexTransaction() => CreateIndexInTransaction(Managed);

    private static DbParameter Add(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static int CreateIndexInTransaction(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE INDEX idx_val ON t(val)";
        var result = command.ExecuteNonQuery();
        transaction.Commit();
        return result;
    }
}
