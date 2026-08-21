using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedJournalPageMigrationTests
{
    private const string EncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void DeleteModePersistsWritesAndCanReenterWalAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "journal-transition.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, """
                CREATE TABLE data(value TEXT);
                INSERT INTO data VALUES ('before');
                CREATE TABLE keyed(
                    tenant TEXT,
                    sequence INTEGER,
                    value TEXT,
                    PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                    UNIQUE(value)
                ) WITHOUT ROWID;
                CREATE INDEX keyed_value ON keyed(value DESC);
                INSERT INTO keyed VALUES ('alpha', 1, 'before');
                """);
            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "INSERT INTO data VALUES ('delete');");
            Execute(connection, "INSERT INTO keyed VALUES ('Alpha', 2, 'delete');");
        }

        fileSystem.FileExists(path + "-wal").Should().BeFalse();
        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            store.Header.WriteVersion.Should().Be(SqliteFileFormatVersion.Legacy);
            store.Header.ReadVersion.Should().Be(SqliteFileFormatVersion.Legacy);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            ReadValue(connection, "SELECT COUNT(*) FROM data;").Should().Be(SqlValue.Integer(2));
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
            ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));
            Execute(connection, "INSERT INTO data VALUES ('wal');");
            Execute(connection, "INSERT INTO keyed VALUES ('beta', 3, 'wal');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
        ReadValue(reopenedConnection, "SELECT COUNT(*) FROM data;").Should().Be(SqlValue.Integer(3));
        ReadValue(reopenedConnection, "SELECT COUNT(*) FROM keyed;").Should().Be(SqlValue.Integer(3));
        ReadValue(reopenedConnection, "SELECT value FROM keyed LIMIT 1;").Should().Be(SqlValue.Text("delete"));
    }

    [TestCase(512)]
    [TestCase(1024)]
    [TestCase(2048)]
    [TestCase(4096)]
    [TestCase(8192)]
    [TestCase(16384)]
    [TestCase(32768)]
    [TestCase(65536)]
    public void DeleteModeVacuumMigratesNonemptyDatabasePageSizeAtomically(int pageSize)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = $"page-size-{pageSize}.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, """
                CREATE TABLE keyed(
                    tenant TEXT,
                    sequence INTEGER,
                    value TEXT,
                    doubled INTEGER GENERATED ALWAYS AS (sequence * 2) VIRTUAL,
                    PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                    UNIQUE(value)
                ) WITHOUT ROWID;
                """);
            Execute(connection, "CREATE INDEX keyed_doubled ON keyed(doubled DESC);");
            for (var index = 0; index < 40; index++)
            {
                Execute(connection, $"INSERT INTO data VALUES ({index}, 'value-{index:D2}');");
                Execute(
                    connection,
                    $"INSERT INTO keyed(tenant, sequence, value) VALUES ('tenant-{index % 4}', {index}, 'keyed-{index:D2}');");
            }

            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, $"PRAGMA page_size={pageSize};");
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));
            Execute(connection, "VACUUM;");
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(pageSize));
            ReadValue(connection, "SELECT COUNT(*) FROM data;").Should().Be(SqlValue.Integer(40));
            ReadValue(connection, "SELECT COUNT(*) FROM keyed;").Should().Be(SqlValue.Integer(40));
            ReadValue(connection, "SELECT doubled FROM keyed WHERE tenant = 'tenant-3' AND sequence = 39;")
                .Should().Be(SqlValue.Integer(78));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(pageSize));
        ReadValue(reopenedConnection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
        ReadValue(reopenedConnection, "SELECT value FROM data WHERE id=39;")
            .Should().Be(SqlValue.Text("value-39"));
        ReadValue(reopenedConnection, "SELECT value FROM keyed WHERE tenant = 'tenant-3' AND sequence = 39;")
            .Should().Be(SqlValue.Text("keyed-39"));
    }

    [Test]
    public void PageSizeMigrationPreservesCompositeForeignKeyActionsAndCatalogText()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "page-size-foreign-keys.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b));");
            Execute(
                connection,
                "CREATE TABLE child(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                    + "FOREIGN KEY(a, b) REFERENCES parent "
                    + "ON UPDATE CASCADE ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED);");
            Execute(connection, "INSERT INTO parent VALUES (1, 2);");
            for (var index = 0; index < 80; index++)
                Execute(connection, $"INSERT INTO child VALUES ({index}, 1, 2);");

            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "PRAGMA page_size=1024; VACUUM;");
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(1024));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        ReadValue(reopenedConnection, "SELECT sql FROM sqlite_schema WHERE name = 'child';")
            .AsText().Should().Contain("ON UPDATE CASCADE")
            .And.Contain("DEFERRABLE INITIALLY DEFERRED");
        Execute(reopenedConnection, "UPDATE parent SET a = 3, b = 4;");
        ReadValue(reopenedConnection, "SELECT COUNT(*) FROM child WHERE a = 3 AND b = 4;")
            .Should().Be(SqlValue.Integer(80));
    }

    [Test]
    public void WalModeAndInvalidPageSizeAssignmentsDoNotChangeTheFormat()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("ignored-page-size.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value INTEGER);");

        Execute(connection, "PRAGMA page_size=8192;");
        Execute(connection, "VACUUM;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));

        ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        Execute(connection, "PRAGMA page_size=0; PRAGMA page_size=511; PRAGMA page_size=513; PRAGMA page_size=131072;");
        Execute(connection, "VACUUM;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));

        Execute(connection, "PRAGMA page_size=8192; PRAGMA page_size=1234;");
        Execute(connection, "VACUUM;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8192));

        Execute(connection, "PRAGMA page_size=16384;");
        ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));
        Execute(connection, "VACUUM;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8192));
        ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        Execute(connection, "VACUUM;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8192));

        ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));
        Execute(connection, "PRAGMA page_size=16384;");
        ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        Execute(connection, "VACUUM;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(16384));
        ReadValue(connection, "PRAGMA journal_mode=unknown;").Should().Be(SqlValue.Text("delete"));
    }

    [TestCase("TRUNCATE")]
    [TestCase("PERSIST")]
    [TestCase("MEMORY")]
    [TestCase("OFF")]
    public void UnsupportedJournalModePreservesTheCurrentWalMode(string requestedMode)
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("unsupported-journal-mode.db", fileSystem);
        using var connection = database.Connect();

        ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));

        ReadValue(connection, $"PRAGMA journal_mode={requestedMode};").Should().Be(SqlValue.Text("wal"));
        ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
    }

    [Test]
    public void MvccJournalModeIsAcceptedOnAWalDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("mvcc-journal-mode.db", fileSystem);
        using var connection = database.Connect();

        ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));
        ReadValue(connection, "PRAGMA journal_mode=MVCC;").Should().Be(SqlValue.Text("mvcc"));
        ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("mvcc"));
    }

    [Test]
    public void ReadOnlyPageSizeAssignmentIsANoOp()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "readonly-page-size.db";
        using (EmbeddedDatabase.OpenFile(path, fileSystem))
        {
        }

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = database.Connect();
        Execute(connection, "PRAGMA page_size=8192;");
        ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));
    }

    [Test]
    public void EmptyDatabaseMigratesAndReopensAtTheRequestedPageSize()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "empty-page-size.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "PRAGMA page_size=8192; VACUUM;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8192));
        ReadValue(reopenedConnection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
    }

    [Test]
    public void ActivePagerReaderPreventsFormatTransition()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = SqlitePager.Create(
            fileSystem,
            "active-reader.db",
            "active-reader.db-wal",
            SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 1, salt2: 2));
        using (pager.BeginReadTransaction())
        {
            Assert.Throws<SqlitePagerBusyException>(() => pager.SwitchJournalMode(SqliteJournalMode.Delete));
        }

        pager.SwitchJournalMode(SqliteJournalMode.Delete).Should().Be(SqliteJournalMode.Delete);
    }

    [Test]
    public void DeleteModeWriterReservesBesideReaderButCommitWaitsForExclusive()
    {
        var fileSystem = new InMemoryFileSystem();
        using var pager = SqlitePager.Create(
            fileSystem,
            "active-delete-reader.db",
            "active-delete-reader.db-wal",
            SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 11, salt2: 12));
        pager.SwitchJournalMode(SqliteJournalMode.Delete);

        using var reader = pager.BeginReadTransaction();
        using var transaction = pager.BeginTransaction(pager.CommittedPageCount, TimeSpan.Zero);
        transaction.WritePage(1, reader.ReadPage(1));

        var busy = Assert.Throws<SqlitePagerBusyException>(() => transaction.Commit());
        busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        busy.Reason.Should().Be(SqlitePagerBusyReason.Busy);
        transaction.State.Should().Be(SqlitePagerTransactionState.Active);

        reader.Dispose();
        transaction.Commit();
        transaction.State.Should().Be(SqlitePagerTransactionState.Committed);
    }

    [Test]
    public void IdleSiblingPagerMustReopenAfterJournalModeTransition()
    {
        var fileSystem = new InMemoryFileSystem();
        using var primary = SqlitePager.Create(
            fileSystem,
            "sibling-mode.db",
            "sibling-mode.db-wal",
            SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 3, salt2: 4));
        using var sibling = SqlitePager.Open(fileSystem, "sibling-mode.db", "sibling-mode.db-wal");

        primary.SwitchJournalMode(SqliteJournalMode.Delete).Should().Be(SqliteJournalMode.Delete);
        Assert.Throws<InvalidDataException>(() => sibling.ReadCommittedPage(1))!
            .Message.Should().Contain("journal mode changed");
    }

    [Test]
    public void IdleSiblingPagerMustReopenAfterWalRoundTrip()
    {
        var fileSystem = new InMemoryFileSystem();
        using var primary = SqlitePager.Create(
            fileSystem,
            "sibling-wal-roundtrip.db",
            "sibling-wal-roundtrip.db-wal",
            SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 7, salt2: 8));
        using var sibling = SqlitePager.Open(
            fileSystem,
            "sibling-wal-roundtrip.db",
            "sibling-wal-roundtrip.db-wal");

        primary.SwitchJournalMode(SqliteJournalMode.Delete);
        primary.SwitchJournalMode(SqliteJournalMode.Wal);
        Assert.Throws<InvalidDataException>(() => sibling.ReadCommittedPage(1))!
            .Message.Should().Contain("WAL storage changed");
    }

    [Test]
    public void IdleSiblingPagerMustReopenAfterPageSizeReplacement()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("sibling-page-size.db", fileSystem);
        using var connection = database.Connect();
        ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        using var sibling = SqlitePager.Open(
            fileSystem,
            "sibling-page-size.db",
            "sibling-page-size.db-wal");

        Execute(connection, "PRAGMA page_size=8192; VACUUM;");
        Assert.Throws<InvalidDataException>(() => sibling.ReadCommittedPage(1))!
            .Message.Should().Contain("page size changed");
    }

    [Test]
    public void FormatTransitionsRejectTransactionsAndTargetAttachedSchemasIndependently()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("transition-busy.db", fileSystem);
        using var connection = database.Connect();

        Execute(connection, "BEGIN;");
        Assert.Throws<EmbeddedSqlException>(() => ReadValue(connection, "PRAGMA journal_mode=DELETE;"))!
            .Message.Should().Be("cannot change journal mode while a transaction is active");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "VACUUM;"))!
            .Message.Should().Be("cannot VACUUM from within a transaction");
        Execute(connection, "ROLLBACK;");

        Execute(connection, "ATTACH 'attached.db' AS other;");
        ReadValue(connection, "PRAGMA other.journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        ReadValue(connection, "PRAGMA main.journal_mode;").Should().Be(SqlValue.Text("wal"));
        Execute(connection, "PRAGMA other.page_size=1024;");
        Execute(connection, "VACUUM other;");
        ReadValue(connection, "PRAGMA other.page_size;").Should().Be(SqlValue.Integer(1024));
        ReadValue(connection, "PRAGMA main.journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        Execute(connection, "VACUUM main;");
    }

    [Test]
    public void FormatTransitionsRejectSiblingTransactionAndDeleteModeHandsOffToSqlite()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-journal-handoff-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var primary = database.Connect())
            using (var sibling = database.Connect())
            {
                Execute(primary, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('managed');");
                Execute(sibling, "BEGIN;");
                Assert.Throws<EmbeddedSqlException>(() => ReadValue(primary, "PRAGMA journal_mode=DELETE;"))!
                    .Message.Should().Be("cannot change journal mode while a transaction is active");
                Execute(sibling, "ROLLBACK;");
                ReadValue(primary, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            }

            using (var native = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                using var insert = native.CreateCommand();
                insert.CommandText = "INSERT INTO data VALUES ('sqlite')";
                insert.ExecuteNonQuery().Should().Be(1);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
                ReadValue(connection, "SELECT COUNT(*) FROM data;").Should().Be(SqlValue.Integer(2));
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    public void EncryptedDeleteModeAndPageSizeMigrationReopenWithTheSameFormatBoundary()
    {
        var inner = new InMemoryFileSystem();
        using var options = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        using var fileSystem = new AhtolaEncryptionFileSystem(inner, options);
        const string path = "encrypted-migration.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('encrypted');");
            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "PRAGMA page_size=8192;");
            Execute(connection, "VACUUM;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
        ReadValue(reopenedConnection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8192));
        ReadValue(reopenedConnection, "SELECT value FROM data;").Should().Be(SqlValue.Text("encrypted"));
    }

    [Test]
    public void Utf16DatabaseMigrationPreservesEncodingAndText()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-utf16-migration-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        try
        {
            using (var native = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                using var command = native.CreateCommand();
                command.CommandText = """
                    PRAGMA encoding='UTF-16le';
                    CREATE TABLE data(value TEXT);
                    INSERT INTO data VALUES ('héllo');
                    """;
                command.ExecuteNonQuery();
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadValue(connection, "PRAGMA encoding;").Should().Be(SqlValue.Text("UTF-16le"));
                ReadValue(connection, "PRAGMA temp.encoding;").Should().Be(SqlValue.Text("UTF-16le"));
                Execute(connection, "PRAGMA page_size=8192; VACUUM;");
                ReadValue(connection, "SELECT value FROM data;").Should().Be(SqlValue.Text("héllo"));
            }

            using var reopened = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            reopened.Open();
            using var verification = reopened.CreateCommand();
            verification.CommandText = "SELECT value FROM data";
            verification.ExecuteScalar().Should().Be("héllo");
            verification.CommandText = "PRAGMA encoding";
            verification.ExecuteScalar().Should().Be("UTF-16le");
            verification.CommandText = "PRAGMA page_size";
            verification.ExecuteScalar().Should().Be(8192L);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    [NonParallelizable]
    public void PooledFacadeMigrationPreservesDurableConstraintCatalog()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-pooled-migration-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=True;Local Provider=Managed";
        Ahtola.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            using (var connection = new Ahtola.Data.Sqlite.SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE parent(id INTEGER PRIMARY KEY);
                    CREATE TABLE child(
                        id INTEGER PRIMARY KEY,
                        parent_id INTEGER REFERENCES parent(id),
                        code TEXT UNIQUE,
                        quantity INTEGER DEFAULT (2 + 3),
                        CONSTRAINT positive CHECK (quantity > 0)
                    );
                    CREATE TABLE generated_key(
                        tenant TEXT,
                        sequence INTEGER,
                        base INTEGER NOT NULL,
                        doubled INTEGER AS (base * 2) VIRTUAL,
                        PRIMARY KEY(tenant, sequence)
                    );
                    INSERT INTO parent VALUES (1);
                    INSERT INTO child(id, parent_id, code) VALUES (1, 1, 'a');
                    INSERT INTO generated_key(tenant, sequence, base) VALUES ('tenant', 1, 7);
                    PRAGMA journal_mode=DELETE;
                    PRAGMA page_size=8192;
                    VACUUM;
                    """;
                command.ExecuteNonQuery();
            }

            using var reopened = new Ahtola.Data.Sqlite.SqliteConnection(connectionString);
            reopened.Open();
            using var verification = reopened.CreateCommand();
            verification.CommandText = "PRAGMA page_size";
            verification.ExecuteScalar().Should().Be(8192L);
            verification.CommandText = "SELECT quantity FROM child WHERE id=1";
            verification.ExecuteScalar().Should().Be(5L);
            verification.CommandText = "SELECT sql FROM sqlite_schema WHERE name='child'";
            verification.ExecuteScalar()!.ToString().Should()
                .Contain("REFERENCES parent(id)")
                .And.Contain("UNIQUE")
                .And.Contain("DEFAULT (2 + 3)")
                .And.Contain("CONSTRAINT positive CHECK (quantity > 0)");
            verification.CommandText = "SELECT sql FROM sqlite_schema WHERE name='generated_key'";
            verification.ExecuteScalar()!.ToString().Should()
                .Contain("doubled INTEGER AS (base * 2) VIRTUAL")
                .And.Contain("PRIMARY KEY(tenant, sequence)");
            verification.CommandText =
                "SELECT doubled FROM generated_key WHERE tenant='tenant' AND sequence=1";
            verification.ExecuteScalar().Should().Be(14L);

            verification.CommandText = "PRAGMA foreign_keys=ON";
            verification.ExecuteNonQuery();
            verification.CommandText = "INSERT INTO child(id, parent_id, code) VALUES (2, 99, 'b')";
            Assert.Throws<Ahtola.Data.Sqlite.SqliteException>(() => verification.ExecuteNonQuery())!
                .Message.Should().Contain("FOREIGN KEY constraint failed");
            verification.CommandText = "INSERT INTO child VALUES (2, 1, 'b', -1)";
            Assert.Throws<Ahtola.Data.Sqlite.SqliteException>(() => verification.ExecuteNonQuery())!
                .Message.Should().Contain("CHECK constraint failed: positive");
            verification.CommandText = "INSERT INTO child(id, parent_id, code) VALUES (2, 1, 'a')";
            Assert.Throws<Ahtola.Data.Sqlite.SqliteException>(() => verification.ExecuteNonQuery())!
                .Message.Should().Contain("UNIQUE constraint failed");
            verification.CommandText =
                "INSERT INTO generated_key(tenant, sequence, base) VALUES ('tenant', 1, 9)";
            Assert.Throws<Ahtola.Data.Sqlite.SqliteException>(() => verification.ExecuteNonQuery())!
                .Message.Should().Contain("UNIQUE constraint failed: generated_key.tenant, generated_key.sequence");
        }
        finally
        {
            Ahtola.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    public void InterruptedJournalModeTransitionRecoversTheOriginalWalFormat()
    {
        const string path = "interrupted-mode.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FailTargetOperationFileSystem(inner, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            fileSystem.ArmFlushFailure();
            Assert.Throws<IOException>(() => ReadValue(connection, "PRAGMA journal_mode=DELETE;"));
        }

        fileSystem.Disarm();
        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));
        }

        fileSystem.FileExists(path + "-journal").Should().BeFalse();
    }

    [Test]
    public void InterruptedDeleteModeWriteRecoversThePreviousTransaction()
    {
        const string path = "interrupted-delete-write.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FailTargetOperationFileSystem(inner, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('before');");
            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));

            fileSystem.ArmFlushFailure();
            Assert.Throws<IOException>(() => Execute(connection, "UPDATE data SET value='after';"));
        }

        fileSystem.Disarm();
        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
            ReadValue(connection, "SELECT value FROM data;").Should().Be(SqlValue.Text("before"));
        }

        fileSystem.FileExists(path + "-journal").Should().BeFalse();
    }

    [Test]
    public void InterruptedDeleteModeGrowthRestoresPageOneAndOriginalLength()
    {
        const string path = "interrupted-delete-growth.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FailTargetOperationFileSystem(inner, path);

        using (var pager = SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 9, salt2: 10)))
        {
            pager.SwitchJournalMode(SqliteJournalMode.Delete);
            using var sibling = SqlitePager.Open(fileSystem, path, path + "-wal");
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, new byte[SqlitePageSize.Default]);
            fileSystem.ArmFlushFailure();
            Assert.Throws<IOException>(() => transaction.Commit());
            Assert.Throws<InvalidDataException>(() => sibling.ReadCommittedPage(1))!
                .Message.Should().Contain("hot rollback journal");
        }

        fileSystem.Disarm();
        using var reopened = SqlitePager.Open(fileSystem, path, path + "-wal");
        reopened.CommittedPageCount.Should().Be(1);
        var header = SqliteDatabaseHeader.Parse(reopened.ReadCommittedPage(1));
        header.DatabaseSizeInPages.Should().Be(1);
        fileSystem.FileExists(path + "-journal").Should().BeFalse();
    }

    [Test]
    public void ReadOnlyOpenPreservesHotJournalForWritableRecovery()
    {
        const string path = "readonly-hot-journal.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FailTargetOperationFileSystem(inner, path);
        LeaveHotJournal(fileSystem, path);

        Assert.Throws<InvalidDataException>(() =>
        {
            using var ignored = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        })!.Message.Should().Contain("hot rollback journal");
        fileSystem.FileExists(path + "-journal").Should().BeTrue();

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = recovered.Connect();
        ReadValue(connection, "SELECT value FROM data;").Should().Be(SqlValue.Text("before"));
        fileSystem.FileExists(path + "-journal").Should().BeFalse();
    }

    [Test]
    public void TruncatedHotJournalIsRejectedWithoutChangingRecoveryEvidence()
    {
        const string path = "truncated-hot-journal.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FailTargetOperationFileSystem(inner, path);
        LeaveHotJournal(fileSystem, path);

        using (var journal = fileSystem.OpenFile(path + "-journal", FileOpenMode.OpenExisting))
        {
            journal.SetLength(512);
            journal.FlushToDisk();
        }

        var databaseBeforeRecovery = ReadAllBytes(fileSystem, path);
        var journalBeforeRecovery = ReadAllBytes(fileSystem, path + "-journal");
        Assert.Throws<InvalidDataException>(() =>
        {
            using var ignored = EmbeddedDatabase.OpenFile(path, fileSystem);
        })!.Message.Should().Contain("truncated before its declared page records");
        ReadAllBytes(fileSystem, path).Should().Equal(databaseBeforeRecovery);
        ReadAllBytes(fileSystem, path + "-journal").Should().Equal(journalBeforeRecovery);
    }

    [Test]
    public void InterruptedPageSizeReplacementRecoversTheOriginalFile()
    {
        const string path = "interrupted-page-size.db";
        var inner = new InMemoryFileSystem();
        var fileSystem = new FailTargetOperationFileSystem(inner, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('preserved');");
            ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "PRAGMA page_size=8192;");

            fileSystem.ArmWriteFailure();
            Assert.Throws<IOException>(() => Execute(connection, "VACUUM;"));
        }

        fileSystem.Disarm();
        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));
            ReadValue(connection, "SELECT value FROM data;").Should().Be(SqlValue.Text("preserved"));
        }

        fileSystem.FileExists(path + "-journal").Should().BeFalse();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static void LeaveHotJournal(FailTargetOperationFileSystem fileSystem, string path)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('before');");
        ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
        fileSystem.ArmFlushFailure();
        Assert.Throws<IOException>(() => Execute(connection, "UPDATE data SET value='after';"));
        fileSystem.Disarm();
    }

    private static byte[] ReadAllBytes(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var contents = new byte[checked((int)file.Length)];
        file.Read(0, contents).Should().Be(contents.Length);
        return contents;
    }

    private sealed class FailTargetOperationFileSystem(IFileSystem inner, string targetPath) : IFileSystem
    {
        private FailureOperation _operation;
        private int _armed;

        public bool FileExists(string path) => inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
            => new FailureFile(this, inner.OpenFile(path, mode, readOnly), path == targetPath);

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public void ArmWriteFailure()
        {
            _operation = FailureOperation.Write;
            Volatile.Write(ref _armed, 1);
        }

        public void ArmFlushFailure()
        {
            _operation = FailureOperation.Flush;
            Volatile.Write(ref _armed, 1);
        }

        public void Disarm() => Volatile.Write(ref _armed, 0);

        private void FailIfArmed(FailureOperation operation, bool isTarget)
        {
            if (isTarget
                && _operation == operation
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new IOException($"Injected {operation.ToString().ToLowerInvariant()} failure.");
            }
        }

        private enum FailureOperation
        {
            Write,
            Flush,
        }

        private sealed class FailureFile(
            FailTargetOperationFileSystem owner,
            IFile innerFile,
            bool isTarget) : IFile
        {
            public long Length => innerFile.Length;

            public bool IsReadOnly => innerFile.IsReadOnly;

            public int Read(long position, Span<byte> destination) => innerFile.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source)
            {
                owner.FailIfArmed(FailureOperation.Write, isTarget);
                innerFile.Write(position, source);
            }

            public void SetLength(long length) => innerFile.SetLength(length);

            public void FlushToDisk()
            {
                owner.FailIfArmed(FailureOperation.Flush, isTarget);
                innerFile.FlushToDisk();
            }

            public void Dispose() => innerFile.Dispose();
        }
    }
}
