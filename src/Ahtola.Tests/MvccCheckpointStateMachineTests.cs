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
