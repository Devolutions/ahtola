using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Managed MVCC checkpoint skeleton (Turso <c>CheckpointStateMachine</c> phases):
/// materialize store → catalog, truncate logical log, GC, cold reopen.
/// </summary>
public sealed class MvccCheckpointStateMachineTests
{
    [Test]
    public void TruncateCheckpointMaterializesAndSurvivesColdReopen()
    {
        using var db = new CheckpointFileDatabase();
        using (var connection = db.Connect())
        {
            connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
            connection.ExecuteNonQuery("BEGIN CONCURRENT;");
            connection.ExecuteNonQuery("INSERT INTO t VALUES (11);");
            connection.ExecuteNonQuery("INSERT INTO t VALUES (22);");
            connection.ExecuteNonQuery("COMMIT;");

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var reader = cmd.ExecuteReader();
                reader.Read().Should().BeTrue();
                Convert.ToInt64(reader.GetValue(0)).Should().Be(0L); // busy
            }

            Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(2L);
            Convert.ToInt64(Scalar(connection, "SELECT SUM(v) FROM t;")).Should().Be(33L);
        }

        // Drop shared store so reopen reconstructs from durable catalog + empty log.
        db.CloseAll();

        using (var reopened = db.Connect())
        {
            ReadValue(reopened, "PRAGMA journal_mode;").Should().Be("mvcc");
            Convert.ToInt64(Scalar(reopened, "SELECT COUNT(*) FROM t;")).Should().Be(2L);
            Convert.ToInt64(Scalar(reopened, "SELECT SUM(v) FROM t;")).Should().Be(33L);
        }

        var logPath = db.Path + "-log";
        File.Exists(logPath).Should().BeTrue();
        new FileInfo(logPath).Length.Should().BeLessThanOrEqualTo(64); // header-only
    }

    [Test]
    public void PassiveCheckpointReportsBusyWhileConcurrentTxOpen()
    {
        using var db = new CheckpointFileDatabase();
        using var connection = db.Connect();
        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            using var reader = cmd.ExecuteReader();
            reader.Read().Should().BeTrue();
            Convert.ToInt64(reader.GetValue(0)).Should().Be(1L); // busy
        }

        connection.ExecuteNonQuery("COMMIT;");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = cmd.ExecuteReader();
            reader.Read().Should().BeTrue();
            Convert.ToInt64(reader.GetValue(0)).Should().Be(0L);
        }
    }

    [Test]
    public void TruncateAfterDeletesLeavesCatalogEmptyOnReopen()
    {
        using var db = new CheckpointFileDatabase();
        using (var connection = db.Connect())
        {
            connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
            connection.ExecuteNonQuery("INSERT INTO t VALUES (5);");
            connection.ExecuteNonQuery("BEGIN CONCURRENT;");
            connection.ExecuteNonQuery("DELETE FROM t WHERE v = 5;");
            connection.ExecuteNonQuery("COMMIT;");
            connection.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
            Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(0L);
        }

        db.CloseAll();

        using var reopened = db.Connect();
        Convert.ToInt64(Scalar(reopened, "SELECT COUNT(*) FROM t;")).Should().Be(0L);
    }

    [Test]
    public void GarbageCollectClearsStoreWhenNoActiveReaders()
    {
        var store = new MvStore();
        var tx = store.BeginTransaction();
        var tableId = store.GetOrCreateTableId("t");
        var rowId = new MvccRowId(tableId, 1);
        store.Insert(tx.Id, rowId, [SqlValue.Integer(9)]);
        store.Commit(tx.Id);

        store.VersionChainCount.Should().BeGreaterThan(0);
        store.GarbageCollectAfterCheckpoint();
        store.VersionChainCount.Should().Be(0);
    }

    [Test]
    public void TruncateRetainsWalWhenLogicalLogRetirementFails()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "mvcc-log-retirement-failure.db";
        long logLength;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(v INTEGER);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "INSERT INTO t VALUES (41);");
            Execute(connection, "COMMIT;");

            logLength = ReadFileLength(fileSystem, path + "-log");
            logLength.Should().BeGreaterThan(56);
            faults.FailNext(FileSystemOperation.SetLength);

            Assert.Throws<IOException>(() => database.RunMvccCheckpoint("TRUNCATE"));

            ReadFileLength(fileSystem, path + "-log").Should().Be(logLength);
            AssertCommittedWal(fileSystem, path + "-wal");

            // A failed durability phase must release process-local admission.
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "ROLLBACK;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadScalar(reopenedConnection, "SELECT v FROM t;").Should().Be(41L);
    }

    [Test]
    public void TruncateRetiresLogicalLogBeforeWalResetAndRecoversAfterResetFault()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "mvcc-wal-reset-failure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(v INTEGER);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "INSERT INTO t VALUES (73);");
            Execute(connection, "COMMIT;");

            // The first SetLength retires the logical log; the next one is the
            // WAL reset. A failure there must leave the durable WAL recoverable.
            faults.FailNextAfter(FileSystemOperation.SetLength, FileSystemOperation.SetLength);

            Assert.Throws<IOException>(() => database.RunMvccCheckpoint("TRUNCATE"));

            ReadFileLength(fileSystem, path + "-log").Should().Be(56);
            AssertCommittedWal(fileSystem, path + "-wal");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadScalar(connection, "SELECT v FROM t;").Should().Be(73L);
            reopened.RunMvccCheckpoint("TRUNCATE").Busy.Should().BeFalse();
        }

        using var finalWal = SqliteWalFile.Open(fileSystem, path + "-wal", readOnly: true);
        finalWal.ScanRecovery().LastCommittedFrameNumber.Should().Be(0);
    }

    [TestCase(FileSystemOperation.Write)]
    [TestCase(FileSystemOperation.FlushToDisk)]
    public void MaterializationFaultColdReopensFromTheLogicalLog(FileSystemOperation operation)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = $"mvcc-{operation}-materialization-failure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(v INTEGER);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "INSERT INTO t VALUES (59);");
            Execute(connection, "COMMIT;");

            faults.FailNext(operation);
            Assert.Throws<IOException>(() => database.RunMvccCheckpoint("TRUNCATE"));
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadScalar(connection, "SELECT v FROM t;").Should().Be(59L);
            reopened.RunMvccCheckpoint("TRUNCATE").Busy.Should().BeFalse();
        }

        using var final = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var finalConnection = final.Connect();
        ReadScalar(finalConnection, "SELECT v FROM t;").Should().Be(59L);
    }

    [Test]
    public void ActiveConcurrentReaderPreventsLogicalLogRetirement()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "mvcc-reader-floor.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var writer = database.Connect();
        using var reader = database.Connect();
        Execute(writer, "CREATE TABLE t(v INTEGER);");
        Execute(writer, "PRAGMA journal_mode=mvcc;");
        Execute(writer, "BEGIN CONCURRENT;");
        Execute(writer, "INSERT INTO t VALUES (91);");
        Execute(writer, "COMMIT;");

        var logLength = ReadFileLength(fileSystem, path + "-log");
        Execute(reader, "BEGIN CONCURRENT;");

        var blocked = database.RunMvccCheckpoint("TRUNCATE");
        blocked.Busy.Should().BeTrue();
        blocked.CompletedThrough.Should().Be(MvccCheckpointPhase.AcquireLock);
        ReadFileLength(fileSystem, path + "-log").Should().Be(logLength);

        Execute(reader, "ROLLBACK;");
        database.RunMvccCheckpoint("TRUNCATE").Busy.Should().BeFalse();
        ReadFileLength(fileSystem, path + "-log").Should().Be(56);
    }

    [Test]
    public void SharedCheckpointLeaseBlocksAdmissionFromASecondDatabaseInstance()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "mvcc-shared-checkpoint-lease.db";
        using var first = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var second = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var firstConnection = first.Connect();
        using var secondConnection = second.Connect();
        Execute(firstConnection, "CREATE TABLE t(v INTEGER);");
        Execute(firstConnection, "PRAGMA journal_mode=mvcc;");
        second.EnsureMvccAttachedIfDurable();

        ReferenceEquals(first.MvStore, second.MvStore).Should().BeTrue();
        first.MvStore!.TryAcquireCheckpoint(out var lease).Should().BeTrue();
        using (lease)
        {
            Assert.Throws<EmbeddedBusyException>(
                () => Execute(secondConnection, "BEGIN CONCURRENT;"));
        }

        Execute(secondConnection, "BEGIN CONCURRENT;");
        Execute(secondConnection, "ROLLBACK;");
    }

    [Test]
    public void TruncatePreservesTypedObjectRowsAcrossColdReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "mvcc-typed-checkpoint.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID;");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "INSERT INTO t VALUES ('tenant', 'value');");
            Execute(connection, "COMMIT;");

            database.RunMvccCheckpoint("TRUNCATE").Busy.Should().BeFalse();
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadText(reopenedConnection, "SELECT v FROM t WHERE k = 'tenant';").Should().Be("value");
    }

    [Test]
    public void CheckpointUpgradesAHeaderOnlyLegacyLogicalLogBeforeTypedWrites()
    {
        const string path = "mvcc-v3-checkpoint-upgrade.db";
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA journal_mode=mvcc;");
        }

        var logPath = MvccLogicalLog.LogPathForDatabase(path);
        using (var file = fileSystem.OpenFile(logPath, FileOpenMode.OpenExisting))
        {
            var header = new byte[56];
            file.Read(0, header).Should().Be(header.Length);
            header[4] = 3;
            header.AsSpan(52).Clear();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(52),
                Crc32C.Compute(header));
            file.Write(0, header);
            file.FlushToDisk();
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        reopened.MvStore!.LogicalLog!.RequiresVersion4Upgrade.Should().BeTrue();
        reopened.RunMvccCheckpoint("PASSIVE").Busy.Should().BeFalse();
        reopened.MvStore!.LogicalLog!.RequiresVersion4Upgrade.Should().BeFalse();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        _ = statement.Step();
    }

    private static string ReadValue(SqliteConnection connection, string sql)
        => Convert.ToString(Scalar(connection, sql)) ?? string.Empty;

    private static long ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string ReadText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static long ReadFileLength(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        return file.Length;
    }

    private static void AssertCommittedWal(IFileSystem fileSystem, string path)
    {
        using var wal = SqliteWalFile.Open(fileSystem, path, readOnly: true);
        var recovery = wal.ScanRecovery();
        recovery.StopReason.Should().Be(SqliteWalRecoveryStopReason.EndOfFile);
        recovery.LastCommittedFrameNumber.Should().BeGreaterThan(0);
        recovery.LastCommittedFrameNumber.Should().Be(recovery.LastValidFrameNumber);
    }

    private sealed class CheckpointFileDatabase : IDisposable
    {
        private readonly List<SqliteConnection> _connections = [];

        public CheckpointFileDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Ahtola-mvcc-ckpt-{Guid.NewGuid():N}.db");

            using var seed = new SqliteConnection($"Data Source={Path};Local Provider=Managed");
            seed.Open();
            seed.ExecuteNonQuery("CREATE TABLE t(v INTEGER);");
        }

        public string Path { get; }

        public SqliteConnection Connect()
        {
            var connection = new SqliteConnection($"Data Source={Path};Local Provider=Managed;Default Timeout=1");
            connection.Open();
            _connections.Add(connection);
            return connection;
        }

        public void CloseAll()
        {
            foreach (var connection in _connections)
                connection.Dispose();
            _connections.Clear();
        }

        public void Dispose()
        {
            CloseAll();

            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal", "-log" })
            {
                var candidate = Path + suffix;
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    File.Delete(candidate);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }
}
