using System.Data.Common;
using Ahtola;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;

namespace Benchmarks;

/// <summary>
/// Public-ADO.NET startup/recovery counterpart to Turso
/// <c>core/benches/mvcc_recovery_benchmark.rs</c> at the pinned revision.
/// Fixtures are built once and copied outside measurement; each benchmark
/// measures opening the copied image and proving all committed rows are visible.
/// </summary>
[BenchmarkCategory("Write")]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TursoRecoveryBenchmarks
{
    private string _root = string.Empty;
    private string _managedFixture = string.Empty;
    private string _nativeFixture = string.Empty;
    private string _managedWorking = string.Empty;
    private string _nativeWorking = string.Empty;

    [Params(100, 1_000)]
    public int TransactionCount { get; set; }

    [GlobalSetup]
    public void BuildFixtures()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-recovery-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _managedFixture = Path.Combine(_root, "managed-fixture.db");
        _nativeFixture = Path.Combine(_root, "native-fixture.db");
        _managedWorking = Path.Combine(_root, "managed-working.db");
        _nativeWorking = Path.Combine(_root, "native-working.db");
        Build<AhtolaConnection>(Path.Combine(_root, "managed-source.db"), _managedFixture);
        Build<SqliteConnection>(Path.Combine(_root, "native-source.db"), _nativeFixture);
    }

    /// <summary>
    /// Large-frame recovery case from Turso <c>core/benches/mvcc_recovery_benchmark.rs</c>:
    /// eight transactions each commit one large BLOB.
    /// </summary>
    [BenchmarkCategory("Write")]
    [MemoryDiagnoser]
    [CategoriesColumn]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    public class TursoRecoveryLargeFrameBenchmarks : TursoRecoveryScenarioSupport
    {
        [Params(64 * 1024, 1024 * 1024)]
        public int PayloadBytes { get; set; }

        protected override void Populate(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO t VALUES ($id, zeroblob($size))";
            var id = command.CreateParameter();
            id.ParameterName = "$id";
            command.Parameters.Add(id);
            var size = command.CreateParameter();
            size.ParameterName = "$size";
            command.Parameters.Add(size);
            size.Value = PayloadBytes;
            for (var row = 0; row < 8; row++)
            {
                id.Value = row;
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Wide-frame recovery case from Turso <c>core/benches/mvcc_recovery_benchmark.rs</c>:
    /// one transaction commits many logical row operations.
    /// </summary>
    [BenchmarkCategory("Write")]
    [MemoryDiagnoser]
    [CategoriesColumn]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    public class TursoRecoveryWideFrameBenchmarks : TursoRecoveryScenarioSupport
    {
        [Params(100, 1_000)]
        public int OperationCount { get; set; }

        protected override void Populate(DbConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO t VALUES ($id, zeroblob(16))";
            var id = command.CreateParameter();
            id.ParameterName = "$id";
            command.Parameters.Add(id);
            for (var row = 0; row < OperationCount; row++)
            {
                id.Value = row;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Reusable startup fixture for additional Turso recovery shapes.</summary>
    public abstract class TursoRecoveryScenarioSupport
    {
        private string _root = string.Empty;
        private string _managedFixture = string.Empty;
        private string _nativeFixture = string.Empty;
        private string _managedWorking = string.Empty;
        private string _nativeWorking = string.Empty;

        [GlobalSetup]
        public void BuildScenarioFixtures()
        {
            _root = Path.Combine(Path.GetTempPath(), "ahtola-recovery-scenario-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _managedFixture = Path.Combine(_root, "managed-fixture.db");
            _nativeFixture = Path.Combine(_root, "native-fixture.db");
            _managedWorking = Path.Combine(_root, "managed-working.db");
            _nativeWorking = Path.Combine(_root, "native-working.db");
            Build<AhtolaConnection>(Path.Combine(_root, "managed-source.db"), _managedFixture);
            Build<SqliteConnection>(Path.Combine(_root, "native-source.db"), _nativeFixture);
        }

        [IterationSetup]
        public void CopyScenarioFixtures()
        {
            CopyDatabase(_managedFixture, _managedWorking);
            CopyDatabase(_nativeFixture, _nativeWorking);
        }

        [GlobalCleanup]
        public void CleanupScenarioFixtures()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [BenchmarkCategory("startup-recovery")]
        [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: open and recover prepared image")]
        public long NativeOpenAndRecover()
        {
            using var connection = new SqliteConnection($"Data Source={_nativeWorking};Pooling=False");
            connection.Open();
            return CountRows(connection);
        }

        [BenchmarkCategory("startup-recovery")]
        [Benchmark(Description = "Ahtola: open and recover prepared MVCC image")]
        public long ManagedOpenAndRecover()
        {
            using var connection = new AhtolaConnection($"Data Source={_managedWorking}");
            connection.Open();
            return CountRows(connection);
        }

        protected virtual void Populate(DbConnection connection)
        {
        }

        private void Build<TConnection>(string sourcePath, string snapshotPath)
            where TConnection : DbConnection, new()
        {
            using var connection = new TConnection { ConnectionString = $"Data Source={sourcePath}" };
            connection.Open();
            Run(connection, connection is AhtolaConnection ? "PRAGMA journal_mode=mvcc" : "PRAGMA journal_mode=WAL");
            if (connection is AhtolaConnection)
                Run(connection, "PRAGMA mvcc_checkpoint_threshold=-1");
            else
                Run(connection, "PRAGMA wal_autocheckpoint=0");
            Run(connection, "PRAGMA synchronous=FULL");
            Run(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v BLOB)");
            Populate(connection);
            CopyDatabase(sourcePath, snapshotPath);
            AssertRecoveryEvidence(snapshotPath, connection is AhtolaConnection ? "-log" : "-wal");
        }

        private static void CopyDatabase(string source, string destination)
        {
            foreach (var suffix in new[] { "", "-wal", "-journal", "-log" })
            {
                var target = destination + suffix;
                if (File.Exists(target))
                    File.Delete(target);
                var candidate = source + suffix;
                if (File.Exists(candidate))
                    File.Copy(candidate, target);
            }
        }

        private static void AssertRecoveryEvidence(string path, string suffix)
        {
            var evidence = path + suffix;
            if (!File.Exists(evidence) || new FileInfo(evidence).Length == 0)
                throw new InvalidOperationException($"Recovery fixture does not contain {suffix} evidence.");
        }

        private static void Run(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static long CountRows(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM t";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [IterationSetup]
    public void CopyFixtures()
    {
        CopyDatabase(_managedFixture, _managedWorking);
        CopyDatabase(_nativeFixture, _nativeWorking);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [BenchmarkCategory("startup-recovery")]
    [Benchmark(Baseline = true, Description = "Microsoft.Data.Sqlite: open committed transaction log")]
    public long NativeOpenAndRecover()
    {
        using var connection = new SqliteConnection($"Data Source={_nativeWorking};Pooling=False");
        connection.Open();
        return Scalar(connection);
    }

    [BenchmarkCategory("startup-recovery")]
    [Benchmark(Description = "Ahtola: open committed MVCC transaction log")]
    public long ManagedOpenAndRecover()
    {
        using var connection = new AhtolaConnection($"Data Source={_managedWorking}");
        connection.Open();
        return Scalar(connection);
    }

    private void Build<TConnection>(string sourcePath, string snapshotPath)
        where TConnection : DbConnection, new()
    {
        using var connection = new TConnection { ConnectionString = $"Data Source={sourcePath}" };
        connection.Open();
        Execute(
            connection,
            connection is AhtolaConnection ? "PRAGMA journal_mode=mvcc" : "PRAGMA journal_mode=WAL");
        if (connection is AhtolaConnection)
            Execute(connection, "PRAGMA mvcc_checkpoint_threshold=-1");
        else
            Execute(connection, "PRAGMA wal_autocheckpoint=0");
        Execute(connection, "PRAGMA synchronous=FULL");
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v BLOB)");

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES ($id, zeroblob(16))";
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);
        for (var row = 0; row < TransactionCount; row++)
        {
            id.Value = row;
            command.ExecuteNonQuery();
        }
        CopyDatabase(sourcePath, snapshotPath);
        AssertRecoveryEvidence(snapshotPath, connection is AhtolaConnection ? "-log" : "-wal");
    }

    private static void CopyDatabase(string source, string destination)
    {
        foreach (var suffix in new[] { "", "-wal", "-journal", "-log" })
        {
            var target = destination + suffix;
            if (File.Exists(target))
                File.Delete(target);
            var candidate = source + suffix;
            if (File.Exists(candidate))
                File.Copy(candidate, target);
        }
    }

    private static void AssertRecoveryEvidence(string path, string suffix)
    {
        var evidence = path + suffix;
        if (!File.Exists(evidence) || new FileInfo(evidence).Length == 0)
            throw new InvalidOperationException($"Recovery fixture does not contain {suffix} evidence.");
    }

    private static int Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static long Scalar(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM t";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
