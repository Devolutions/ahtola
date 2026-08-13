using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedAutoIncrementDurabilityTests
{
    private const string EncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [TestCase(false)]
    [TestCase(true)]
    public void SequenceSurvivesWalAndDeleteJournalReopen(bool deleteJournal)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = deleteJournal ? "sequence-delete.db" : "sequence-wal.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)");
            Execute(connection, "INSERT INTO data(value) VALUES ('first')");
            Execute(connection, "INSERT INTO data(id, value) VALUES (20, 'high')");
            Execute(connection, "DELETE FROM data WHERE id = 20");
            if (deleteJournal)
                Execute(connection, "PRAGMA journal_mode=DELETE");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data(value) VALUES ('reopened')");
        ReadIntegers(
                reopenedConnection,
                "SELECT id FROM data ORDER BY id")
            .Should().Equal(1, 21);
        ReadInteger(
                reopenedConnection,
                "SELECT seq FROM sqlite_sequence WHERE name = 'data'")
            .Should().Be(21);
        ReadText(reopenedConnection, "PRAGMA journal_mode")
            .Should().Be(deleteJournal ? "delete" : "wal");
    }

    [Test]
    public void DeleteJournalPageMigrationPreservesOverflowSequenceState()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-page-migration.db";
        var payload = new string('x', 12_000);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var initialConnection = database.Connect())
        {
            Execute(initialConnection, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)");
            Execute(initialConnection, $"INSERT INTO data(id, value) VALUES (50, '{payload}')");
            Execute(initialConnection, "DELETE FROM data WHERE id = 50");
            Execute(initialConnection, "PRAGMA journal_mode=DELETE");
            Execute(initialConnection, "PRAGMA page_size=512");
            Execute(initialConnection, "VACUUM");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = reopened.Connect();
        Execute(connection, "INSERT INTO data(value) VALUES ('after-migration')");
        ReadInteger(connection, "SELECT id FROM data").Should().Be(51);
        ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(51);
        ReadInteger(connection, "PRAGMA page_size").Should().Be(512);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedCommitRecoversTableAndSequenceAtomically(bool deleteJournal)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = deleteJournal ? "sequence-failure-delete.db" : "sequence-failure-wal.db";
        var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
        Execute(connection, "INSERT INTO data DEFAULT VALUES");
        if (deleteJournal)
            Execute(connection, "PRAGMA journal_mode=DELETE");

        faults.FailNext(FileSystemOperation.Write);
        Assert.Throws<IOException>(() => Execute(connection, "INSERT INTO data DEFAULT VALUES"));

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadIntegers(recoveredConnection, "SELECT id FROM data ORDER BY id").Should().Equal(1);
        ReadInteger(recoveredConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(1);
        Execute(recoveredConnection, "INSERT INTO data DEFAULT VALUES");
        ReadIntegers(recoveredConnection, "SELECT id FROM data ORDER BY id").Should().Equal(1, 2);
        ReadInteger(recoveredConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(2);
    }

    [Test]
    public void AbruptReopenRecoversCommittedSequenceWithItsTable()
    {
        var fileSystem = new InMemoryFileSystem();
        var crashed = EmbeddedDatabase.OpenFile("sequence-crash.db", fileSystem);
        var crashedConnection = crashed.Connect();
        Execute(crashedConnection, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
        Execute(crashedConnection, "INSERT INTO data(id) VALUES (100)");
        Execute(crashedConnection, "DELETE FROM data WHERE id = 100");

        using var recovered = EmbeddedDatabase.OpenFile("sequence-crash.db", fileSystem);
        using var connection = recovered.Connect();
        Execute(connection, "INSERT INTO data DEFAULT VALUES");
        ReadInteger(connection, "SELECT id FROM data").Should().Be(101);
        ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(101);
    }

    [Test]
    public void SequenceOnlyIgnoreAndUpsertChangesPersistAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-only-change.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)");
            Execute(connection, "INSERT INTO data(value) VALUES ('seed')");
            Execute(connection, "INSERT OR IGNORE INTO data(value) VALUES ('seed')");
            Execute(
                connection,
                "INSERT INTO data(value) VALUES ('seed') ON CONFLICT(value) DO NOTHING");
            ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(3);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data(value) VALUES ('after-reopen')");
        ReadInteger(reopenedConnection, "SELECT id FROM data WHERE value = 'after-reopen'").Should().Be(4);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(4);
    }

    [Test]
    public void OrFailPersistsPriorRowsWithoutPublishingAttemptedSequence()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-fail.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT UNIQUE)");
            Execute(connection, "INSERT INTO data(value) VALUES ('seed')");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "INSERT OR FAIL INTO data(value) VALUES ('first'), ('seed'), ('last')"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadIntegers(reopenedConnection, "SELECT id FROM data ORDER BY id").Should().Equal(1, 2);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(1);
        Execute(reopenedConnection, "INSERT INTO data(value) VALUES ('after-fail')");
        ReadInteger(reopenedConnection, "SELECT id FROM data WHERE value = 'after-fail'").Should().Be(3);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(3);
    }

    [Test]
    public void ExplicitTransactionCommitPublishesTableAndSequenceTogether()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-transaction-commit.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var transactionConnection = database.Connect())
        {
            Execute(transactionConnection, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
            Execute(transactionConnection, "BEGIN");
            Execute(transactionConnection, "INSERT INTO data DEFAULT VALUES");
            Execute(transactionConnection, "INSERT INTO data DEFAULT VALUES");
            Execute(transactionConnection, "COMMIT");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = reopened.Connect();
        ReadIntegers(connection, "SELECT id FROM data ORDER BY id").Should().Equal(1, 2);
        ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(2);
    }

    [Test]
    public void ForeignKeyActionsPreserveIndependentSequenceHighWaterMarks()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-foreign-key-actions.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys=ON");
            Execute(
                connection,
                "CREATE TABLE parent("
                + "id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT UNIQUE)");
            Execute(
                connection,
                "CREATE TABLE child("
                + "id INTEGER PRIMARY KEY AUTOINCREMENT, "
                + "parent_id INTEGER REFERENCES parent(id) "
                + "ON UPDATE CASCADE ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED)");
            Execute(connection, "BEGIN");
            Execute(connection, "INSERT INTO parent(code) VALUES ('first')");
            Execute(connection, "INSERT INTO child(parent_id) VALUES (1)");
            Execute(connection, "COMMIT");
            Execute(connection, "UPDATE parent SET id = 10 WHERE id = 1");
            Execute(connection, "INSERT INTO parent(code) VALUES ('second')");
            Execute(connection, "DELETE FROM parent WHERE id = 10");
            Execute(connection, "INSERT INTO child(parent_id) VALUES (11)");

            ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'parent'")
                .Should().Be(11);
            ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'child'")
                .Should().Be(2);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadInteger(reopenedConnection, "SELECT id FROM parent").Should().Be(11);
        ReadInteger(reopenedConnection, "SELECT id FROM child").Should().Be(2);
        ReadInteger(reopenedConnection, "SELECT parent_id FROM child").Should().Be(11);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'parent'")
            .Should().Be(11);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'child'")
            .Should().Be(2);
    }

    [Test]
    public void DropCascadeTriggerAdvancesSequenceAtomicallyAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-drop-cascade-trigger.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys=ON");
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
            Execute(
                connection,
                "CREATE TABLE child("
                + "id INTEGER PRIMARY KEY, "
                + "parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)");
            Execute(
                connection,
                "CREATE TABLE audit("
                + "id INTEGER PRIMARY KEY AUTOINCREMENT, note TEXT)");
            Execute(
                connection,
                "CREATE TRIGGER child_deleted AFTER DELETE ON child BEGIN "
                + "INSERT INTO audit(note) VALUES ('cascade'); END");
            Execute(connection, "INSERT INTO parent VALUES (1)");
            Execute(connection, "INSERT INTO child VALUES (1, 1)");
            Execute(connection, "DROP TABLE parent");

            ReadInteger(connection, "SELECT count(*) FROM child").Should().Be(0);
            ReadInteger(connection, "SELECT id FROM audit").Should().Be(1);
            ReadInteger(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'audit'")
                .Should().Be(1);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadInteger(reopenedConnection, "SELECT count(*) FROM child").Should().Be(0);
        ReadInteger(reopenedConnection, "SELECT id FROM audit").Should().Be(1);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'audit'")
            .Should().Be(1);
    }

    [Test]
    public void OverflowingAutoincrementSchemaSurvivesPageMigrationAndSqliteHandoff()
    {
        var path = CreatePhysicalDatabasePath();
        var defaultValue = new string('s', 6_000);
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    "CREATE TABLE data("
                    + "id INTEGER PRIMARY KEY AUTOINCREMENT, "
                    + $"payload TEXT DEFAULT '{defaultValue}')");
                Execute(connection, "INSERT INTO data(id) VALUES (50)");
                Execute(connection, "DELETE FROM data");
                Execute(connection, "PRAGMA journal_mode=DELETE");
                Execute(connection, "PRAGMA page_size=512");
                Execute(connection, "VACUUM");
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                ReadScalar(sqlite, "PRAGMA integrity_check").Should().Be("ok");
                Convert.ToInt64(ReadScalar(sqlite, "PRAGMA page_size")).Should().Be(512);
                Convert.ToInt64(ReadScalar(sqlite, "SELECT seq FROM sqlite_sequence WHERE name = 'data'"))
                    .Should().Be(50);
                Convert.ToString(ReadScalar(
                        sqlite,
                        "SELECT sql FROM sqlite_schema WHERE name = 'data'"))!
                    .Should().Contain("AUTOINCREMENT")
                    .And.Contain(defaultValue);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Execute(connection, "INSERT INTO data DEFAULT VALUES");
                ReadInteger(connection, "SELECT id FROM data").Should().Be(51);
                ReadInteger(connection, "SELECT length(payload) FROM data").Should().Be(defaultValue.Length);
                Execute(connection, "PRAGMA journal_mode=DELETE");
            }

            using var final = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            final.Open();
            ReadScalar(final, "PRAGMA integrity_check").Should().Be("ok");
            Convert.ToInt64(ReadScalar(final, "SELECT seq FROM sqlite_sequence WHERE name = 'data'"))
                .Should().Be(51);
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void FileBackedWithoutRowidRejectionLeavesNoSequenceCatalogMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "sequence-without-rowid-rejection.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(
                    () => Execute(
                        connection,
                        "CREATE TABLE rejected("
                        + "id INTEGER PRIMARY KEY AUTOINCREMENT) WITHOUT ROWID"))!
                .Message.Should().Be("AUTOINCREMENT not allowed on WITHOUT ROWID tables");
            ReadInteger(
                    connection,
                    "SELECT count(*) FROM sqlite_schema "
                    + "WHERE name IN ('rejected', 'sqlite_sequence')")
                .Should().Be(0);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadInteger(
                reopenedConnection,
                "SELECT count(*) FROM sqlite_schema "
                + "WHERE name IN ('rejected', 'sqlite_sequence')")
            .Should().Be(0);
    }

    [Test]
    public void ManagedAndMicrosoftDataSqliteRoundTripTheSameSequenceFile()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Execute(sqlite, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)");
                Execute(sqlite, "INSERT INTO data(value) VALUES ('native')");
                Execute(sqlite, "INSERT INTO data(id, value) VALUES (20, 'high')");
                Execute(sqlite, "DELETE FROM data WHERE id = 20");
            }

            using (var managed = EmbeddedDatabase.OpenFile(path))
            using (var connection = managed.Connect())
            {
                Execute(connection, "INSERT INTO data(value) VALUES ('managed')");
                ReadInteger(connection, "SELECT id FROM data WHERE value = 'managed'").Should().Be(21);
                Execute(connection, "PRAGMA journal_mode=DELETE");
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                ReadScalar(sqlite, "PRAGMA integrity_check").Should().Be("ok");
                Execute(sqlite, "INSERT INTO data(value) VALUES ('native-again')");
                Convert.ToInt64(ReadScalar(sqlite, "SELECT id FROM data WHERE value = 'native-again'"))
                    .Should().Be(22);
                Convert.ToInt64(ReadScalar(sqlite, "SELECT seq FROM sqlite_sequence WHERE name = 'data'"))
                    .Should().Be(22);
            }

            using (var managed = EmbeddedDatabase.OpenFile(path))
            using (var connection = managed.Connect())
            {
                Execute(connection, "INSERT INTO data(value) VALUES ('managed-again')");
                ReadInteger(connection, "SELECT id FROM data WHERE value = 'managed-again'").Should().Be(23);
                Execute(connection, "PRAGMA journal_mode=DELETE");
            }

            using var final = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            final.Open();
            ReadScalar(final, "PRAGMA integrity_check").Should().Be("ok");
            Convert.ToInt64(ReadScalar(final, "SELECT seq FROM sqlite_sequence WHERE name = 'data'"))
                .Should().Be(23);
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void ManagedBackupPreservesSequenceBeyondTheLiveMaximum()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)");
        destination.ExecuteNonQuery("CREATE TABLE stale(id INTEGER PRIMARY KEY AUTOINCREMENT)");
        source.ExecuteNonQuery("INSERT INTO data(value) VALUES ('first')");
        source.ExecuteNonQuery("INSERT INTO data(id, value) VALUES (50, 'deleted')");
        source.ExecuteNonQuery("DELETE FROM data WHERE id = 50");

        source.BackupDatabase(destination);
        destination.ExecuteScalar<long>(
                "SELECT value FROM __turso_internal_seq___turso_internal_autoincrement_data")
            .Should().Be(50);
        destination.ExecuteNonQuery("INSERT INTO data(value) VALUES ('after-backup')");

        destination.ExecuteScalar<long>(
                "SELECT value FROM __turso_internal_seq___turso_internal_autoincrement_data")
            .Should().Be(51);
        destination.ExecuteScalar<long>("SELECT id FROM data WHERE value = 'after-backup'")
            .Should().Be(51);
        destination.ExecuteScalar<long>("SELECT seq FROM sqlite_sequence WHERE name = 'data'")
            .Should().Be(51);
    }

    [Test]
    public void ManagedBackupPreservesAnalyzeStatistics()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery(
            """
            CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);
            CREATE INDEX data_value ON data(value);
            INSERT INTO data VALUES (1, 'one'), (2, 'two'), (3, 'two');
            ANALYZE data;
            """);
        destination.ExecuteNonQuery(
            """
            CREATE TABLE stale(id INTEGER PRIMARY KEY, value TEXT);
            CREATE INDEX stale_value ON stale(value);
            INSERT INTO stale VALUES (1, 'stale');
            ANALYZE stale;
            """);

        source.BackupDatabase(destination);

        destination.ExecuteScalar<string>(
                "SELECT stat FROM sqlite_stat1 WHERE tbl = 'data' AND idx = 'data_value'")
            .Should().Be("3 2");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_stat1 WHERE tbl = 'stale'")
            .Should().Be(0);
    }

    [Test]
    public void AttachedDatabasesKeepIndependentSequenceState()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("sequence-attach-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH DATABASE 'sequence-attach-aux.db' AS aux");
        Execute(connection, "CREATE TABLE main.data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
        Execute(connection, "CREATE TABLE aux.data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
        Execute(connection, "INSERT INTO main.data(id) VALUES (10)");
        Execute(connection, "DELETE FROM main.data");
        Execute(connection, "INSERT INTO aux.data(id) VALUES (20)");
        Execute(connection, "DELETE FROM aux.data");
        Execute(connection, "INSERT INTO main.data DEFAULT VALUES");
        Execute(connection, "INSERT INTO aux.data DEFAULT VALUES");

        ReadInteger(connection, "SELECT id FROM main.data").Should().Be(11);
        ReadInteger(connection, "SELECT id FROM aux.data").Should().Be(21);
        ReadInteger(
                connection,
                "SELECT seq FROM main.sqlite_sequence WHERE name = 'data'")
            .Should().Be(11);
        ReadInteger(
                connection,
                "SELECT seq FROM aux.sqlite_sequence WHERE name = 'data'")
            .Should().Be(21);
    }

    [Test]
    public void PooledSiblingRefreshesSequenceBeforeAllocating()
    {
        ManagedSqliteConnection.ClearAllPools();
        var path = CreatePhysicalDatabasePath();
        try
        {
            using var writer = OpenManagedConnection(path, pooling: true);
            using var stale = OpenManagedConnection(path, pooling: true);
            stale.Close();

            writer.ExecuteNonQuery("CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
            writer.ExecuteNonQuery("INSERT INTO data(id) VALUES (40); DELETE FROM data;");
            writer.Close();

            stale.Open();
            stale.ExecuteNonQuery("INSERT INTO data DEFAULT VALUES");
            stale.ExecuteScalar<long>("SELECT id FROM data").Should().Be(41);
            stale.ExecuteScalar<long>("SELECT seq FROM sqlite_sequence WHERE name = 'data'").Should().Be(41);
        }
        finally
        {
            ManagedSqliteConnection.ClearAllPools();
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void EncryptedReopenPreservesSequence()
    {
        var inner = new InMemoryFileSystem();
        using (var encryption = AhtolaEncryptionOptions.FromHex(
                   AhtolaEncryptionCipher.Aes256Gcm,
                   EncryptionKey))
        using (var fileSystem = new AhtolaEncryptionFileSystem(inner, encryption))
        using (var database = EmbeddedDatabase.OpenFile("sequence-encrypted.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY AUTOINCREMENT)");
            Execute(connection, "INSERT INTO data(id) VALUES (30)");
            Execute(connection, "DELETE FROM data");
        }

        using var reopenEncryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        using var reopenedFileSystem = new AhtolaEncryptionFileSystem(inner, reopenEncryption);
        using var reopened = EmbeddedDatabase.OpenFile("sequence-encrypted.db", reopenedFileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "INSERT INTO data DEFAULT VALUES");
        ReadInteger(reopenedConnection, "SELECT id FROM data").Should().Be(31);
        ReadInteger(reopenedConnection, "SELECT seq FROM sqlite_sequence WHERE name = 'data'")
            .Should().Be(31);
    }

    private static ManagedSqliteConnection OpenManagedConnection()
        => OpenManagedConnection(":memory:", pooling: false);

    private static ManagedSqliteConnection OpenManagedConnection(string path, bool pooling)
    {
        var connection = new ManagedSqliteConnection(
            $"Data Source={path};Local Provider=Managed;Pooling={pooling}");
        connection.Open();
        return connection;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static long ReadInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long[] ReadIntegers(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<long>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsInteger());
        return values.ToArray();
    }

    private static string ReadText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ReadScalar(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string CreatePhysicalDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-autoincrement-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"sequence-{Guid.NewGuid():N}.db");
    }

    private static void DeletePhysicalDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
