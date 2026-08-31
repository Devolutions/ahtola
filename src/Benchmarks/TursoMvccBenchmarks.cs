using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Benchmarks;

/// <summary>
/// Public-ADO.NET adaptation of Turso <c>core/benches/mvcc_benchmark.rs</c>
/// at the repository-pinned revision. Ahtola uses its MVCC journal while the
/// Microsoft.Data.Sqlite comparison uses WAL.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[InvocationCount(64)]
public class TursoMvccBenchmarks : TursoWriteBenchmarkSupport
{
    protected override void Configure(DbConnection connection)
    {
        Execute(
            connection,
            connection is AhtolaConnection ? "PRAGMA journal_mode=mvcc" : "PRAGMA journal_mode=WAL");
        if (connection is AhtolaConnection)
            Execute(connection, "PRAGMA mvcc_checkpoint_threshold=-1");
        Execute(connection, "PRAGMA synchronous=OFF");
        Execute(connection, "CREATE TABLE test(id INTEGER PRIMARY KEY, data TEXT, val INTEGER)");
        InsertRows(connection, 1_000);
    }

    [BenchmarkCategory("begin-rollback")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: begin + rollback")]
    public int NativeBeginRollback() => BeginRollback(Native);

    [BenchmarkCategory("begin-rollback")]
    [Benchmark(Description = "Ahtola MVCC: begin + rollback")]
    public int ManagedBeginRollback() => BeginRollback(Managed);

    [BenchmarkCategory("read-commit")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: begin + point read + commit")]
    public long NativeReadCommit() => ReadCommit(Native);

    [BenchmarkCategory("read-commit")]
    [Benchmark(Description = "Ahtola MVCC: begin + point read + commit")]
    public long ManagedReadCommit() => ReadCommit(Managed);

    [BenchmarkCategory("update-commit")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: begin + update + commit")]
    public int NativeUpdateCommit() => UpdateCommit(Native);

    [BenchmarkCategory("update-commit")]
    [Benchmark(Description = "Ahtola MVCC: begin + update + commit")]
    public int ManagedUpdateCommit() => UpdateCommit(Managed);

    private static int BeginRollback(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        transaction.Rollback();
        return transaction.GetHashCode();
    }

    private static long ReadCommit(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT val FROM test WHERE id=500";
        var result = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        transaction.Commit();
        return result;
    }

    private static int UpdateCommit(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE test SET data='changed', val=val+1 WHERE id=500";
        var affected = command.ExecuteNonQuery();
        transaction.Commit();
        return affected;
    }
}

/// <summary>
/// Ports the shrunken "huge multi-write" shape from Turso
/// <c>core/benches/mvcc_benchmark.rs</c>: a wide batch with both rowid and
/// unique-index probes. The 32-row default preserves the upstream practical scale.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoMvccMultiWriteBenchmarks : TursoWriteBenchmarkSupport
{
    protected override void Configure(DbConnection connection)
    {
        Execute(
            connection,
            connection is AhtolaConnection ? "PRAGMA journal_mode=mvcc" : "PRAGMA journal_mode=WAL");
        if (connection is AhtolaConnection)
            Execute(connection, "PRAGMA mvcc_checkpoint_threshold=-1");
        Execute(connection, "PRAGMA synchronous=OFF");
        Execute(
            connection,
            """
            CREATE TABLE core(
                rowid_pk INTEGER PRIMARY KEY,
                seq INTEGER,
                c0 TEXT, c1 TEXT, c2 TEXT, c3 TEXT,
                c4 TEXT, c5 TEXT, c6 TEXT, c7 TEXT,
                rank INTEGER, row_number INTEGER,
                created_ts INTEGER, modified_ts INTEGER)
            """);
        Execute(connection, "CREATE UNIQUE INDEX idx_core_seq ON core(seq)");
    }

    [BenchmarkCategory("wide-unique-batch")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: 32-row wide unique batch")]
    public int NativeWideBatch() => InsertWideBatch(Native);

    [BenchmarkCategory("wide-unique-batch")]
    [Benchmark(Description = "Ahtola MVCC: 32-row wide unique batch")]
    public int ManagedWideBatch() => InsertWideBatch(Managed);

    private static int InsertWideBatch(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO core(
                rowid_pk, seq, c0, c1, c2, c3, c4, c5, c6, c7,
                rank, row_number, created_ts, modified_ts)
            VALUES(
                $id, $id, 'v0', 'v1', 'v2', 'v3', 'v4', 'v5', 'v6', 'v7',
                0, $id, 1700000000, 1700000000)
            """;
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);
        var affected = 0;
        for (var row = 0; row < 32; row++)
        {
            id.Value = 1_000_000L + row;
            affected += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected;
    }
}
