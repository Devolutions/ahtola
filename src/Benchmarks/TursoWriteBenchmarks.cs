using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Benchmarks;

/// <summary>
/// Ports the core workloads from Turso
/// <c>core/benches/write_perf_benchmark.rs</c> at the repository-pinned revision.
/// Setup and database reset are excluded from every measured invocation.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoWriteBenchmarks : TursoWriteBenchmarkSupport
{
    private long[] _sequentialKeys = [];

    [Params(0, 1, 3)]
    public int IndexCount { get; set; }

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=FULL");
        Execute(connection, "CREATE TABLE test(id INTEGER PRIMARY KEY, data TEXT, val INTEGER)");
        if (IndexCount >= 1)
            Execute(connection, "CREATE INDEX idx_val ON test(val)");
        if (IndexCount >= 2)
            Execute(connection, "CREATE INDEX idx_data ON test(data)");
        if (IndexCount >= 3)
            Execute(connection, "CREATE INDEX idx_val_data ON test(val, data)");

        _sequentialKeys = Enumerable.Range(0, 1_000).Select(static value => (long)value).ToArray();
    }

    [BenchmarkCategory("index-impact")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: insert 1,000 rows")]
    public int NativeIndexImpact() => InsertBatch(Native, _sequentialKeys);

    [BenchmarkCategory("index-impact")]
    [Benchmark(Description = "Ahtola: insert 1,000 rows")]
    public int ManagedIndexImpact() => InsertBatch(Managed, _sequentialKeys);

}

/// <summary>
/// Sequential-versus-random key cases derived from Turso
/// <c>core/benches/write_perf_benchmark.rs</c>.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoWriteKeyPatternBenchmarks : TursoWriteBenchmarkSupport
{
    private long[] _sequentialKeys = [];
    private long[] _randomKeys = [];

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=FULL");
        Execute(connection, "CREATE TABLE test(id INTEGER PRIMARY KEY, data TEXT, val INTEGER)");
        _sequentialKeys = Enumerable.Range(0, 1_000).Select(static value => (long)value).ToArray();
        _randomKeys = _sequentialKeys.ToArray();
        new Random(0x5EED).Shuffle(_randomKeys);
    }

    [BenchmarkCategory("sequential-keys")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: sequential integer keys")]
    public int NativeSequentialKeys() => InsertBatch(Native, _sequentialKeys);

    [BenchmarkCategory("sequential-keys")]
    [Benchmark(Description = "Ahtola: sequential integer keys")]
    public int ManagedSequentialKeys() => InsertBatch(Managed, _sequentialKeys);

    [BenchmarkCategory("random-keys")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: deterministic random keys")]
    public int NativeRandomKeys() => InsertBatch(Native, _randomKeys);

    [BenchmarkCategory("random-keys")]
    [Benchmark(Description = "Ahtola: deterministic random keys")]
    public int ManagedRandomKeys() => InsertBatch(Managed, _randomKeys);
}

/// <summary>
/// Transaction-size cases derived from Turso
/// <c>core/benches/write_perf_benchmark.rs</c>.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoWriteMutationBenchmarks : TursoWriteBenchmarkSupport
{
    [Params(10, 100, 1_000)]
    public int TransactionRows { get; set; }

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=FULL");
        Execute(connection, "CREATE TABLE test(id INTEGER PRIMARY KEY, data TEXT, val INTEGER)");
    }

    [BenchmarkCategory("transaction-size")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: explicit transaction")]
    public int NativeTransactionSize() => AppendRows(Native);

    [BenchmarkCategory("transaction-size")]
    [Benchmark(Description = "Ahtola: explicit transaction")]
    public int ManagedTransactionSize() => AppendRows(Managed);

    private int AppendRows(DbConnection connection)
        => InsertRows(connection, TransactionRows, row => 10_000L + row);
}

/// <summary>
/// UPDATE and DELETE range workloads from Turso
/// <c>core/benches/write_perf_benchmark.rs</c>.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoWriteUpdateDeleteBenchmarks : TursoWriteBenchmarkSupport
{
    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA synchronous=FULL");
        Execute(connection, "CREATE TABLE test(id INTEGER PRIMARY KEY, data TEXT, val INTEGER)");
        InsertRows(connection, 2_000);
    }

    [BenchmarkCategory("updates")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: update middle range")]
    public int NativeUpdates() => Execute(Native, "UPDATE test SET data='updated', val=val+1 WHERE id>=950 AND id<1050");

    [BenchmarkCategory("updates")]
    [Benchmark(Description = "Ahtola: update middle range")]
    public int ManagedUpdates() => Execute(Managed, "UPDATE test SET data='updated', val=val+1 WHERE id>=950 AND id<1050");

    [BenchmarkCategory("deletes")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: delete middle range")]
    public int NativeDeletes() => Execute(Native, "DELETE FROM test WHERE id>=950 AND id<1050");

    [BenchmarkCategory("deletes")]
    [Benchmark(Description = "Ahtola: delete middle range")]
    public int ManagedDeletes() => Execute(Managed, "DELETE FROM test WHERE id>=950 AND id<1050");
}

/// <summary>
/// Large dirty-page commits and FULL/OFF synchronization derived from Turso
/// <c>core/benches/write_perf_benchmark.rs</c>.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoWriteCommitBenchmarks : TursoWriteBenchmarkSupport
{
    private readonly long[] _keys = Enumerable.Range(0, 5_000).Select(static value => (long)value).ToArray();

    [Params("FULL", "OFF")]
    public string Synchronous { get; set; } = "FULL";

    protected override void Configure(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, $"PRAGMA synchronous={Synchronous}");
        Execute(connection, "CREATE TABLE test(id INTEGER PRIMARY KEY, data TEXT, val INTEGER)");
    }

    [BenchmarkCategory("large-commit")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: commit 5,000 wide rows")]
    public int NativeLargeCommit() => CommitLargeTransaction(Native, _keys);

    [BenchmarkCategory("large-commit")]
    [Benchmark(Description = "Ahtola: commit 5,000 wide rows")]
    public int ManagedLargeCommit() => CommitLargeTransaction(Managed, _keys);

    private static int CommitLargeTransaction(DbConnection connection, IReadOnlyList<long> keys)
        => InsertBatch(connection, keys, payloadLength: 100);
}
