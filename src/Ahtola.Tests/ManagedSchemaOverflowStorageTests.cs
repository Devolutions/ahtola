using System.Buffers.Binary;
using AwesomeAssertions;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using NativeSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSchemaOverflowStorageTests
{
    private const int MaximumPageSchemaPayloadLength = 70_000;
    private const int SmallPageSchemaPayloadLength = 6_000;
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [TestCase(512)]
    [TestCase(1024)]
    [TestCase(2048)]
    [TestCase(4096)]
    [TestCase(8192)]
    [TestCase(16384)]
    [TestCase(32768)]
    [TestCase(65536)]
    public void LargeTableSchemaUsesSqliteOverflowAcrossEverySupportedPageSize(int pageSize)
    {
        var path = CreateDatabasePath($"page-size-{pageSize}");
        var defaultValue = LargeValue('p', MaximumPageSchemaPayloadLength);
        try
        {
            CreatePageSizeDatabase(path, PhysicalFileSystem.Instance, pageSize);

            string storedSql;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, BuildLargeTableSql("wide", defaultValue));
                var schemaVersion = ScalarInteger(connection, "PRAGMA schema_version;");
                Execute(connection, "INSERT INTO \"wide\" (\"id\") VALUES (1);");
                ScalarInteger(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);
                storedSql = ScalarText(
                    connection,
                    "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'wide';");
            }

            var header = ReadHeader(PhysicalFileSystem.Instance, path);
            var schemaCell = FindSchemaCell(PhysicalFileSystem.Instance, path, "wide");
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                schemaCell.PayloadLength,
                header.UsableSpace);
            layout.UsesOverflow.Should().BeTrue();
            schemaCell.LocalPayloadLength.Should().Be(layout.LocalPayloadLength);
            schemaCell.OverflowPages.Should().NotBeEmpty();

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                ScalarText(
                    connection,
                    "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'wide';")
                    .Should().Be(storedSql);
                ScalarInteger(connection, "SELECT length(required) FROM wide WHERE id = 1;")
                    .Should().Be(defaultValue.Length);
                ScalarInteger(connection, "SELECT doubled FROM wide WHERE id = 1;").Should().Be(14);
            }

            VerifyLargeTableWithSqlite(path, pageSize, storedSql, defaultValue.Length);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SingleLargeSchemaRowUsesSqliteOneChildInteriorRoot()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "single-schema-overflow.db";
        var payload = FindSingleRowRootPromotionPayload();
        CreatePageSizeDatabase(path, fileSystem, SqlitePageSize.Minimum);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, BuildLargeTableSql("only_table", payload));

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var root = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(1),
                header.UsableSpace,
                isFirstPage: true);
            root.Cells.Should().BeEmpty();
            root.Header.RightMostChildPage.Should().BeGreaterThan(1);
        }

        FindSchemaCell(fileSystem, path, "only_table").OverflowPages.Should().NotBeEmpty();
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        ScalarText(
            reopenedConnection,
            "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'only_table';")
            .Should().Contain(payload);
    }

    [Test]
    public void LargeCatalogAlterDropAndRebuildReusesPagesAndPreservesSchemaMetadata()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "schema-overflow-rewrite.db";
        var payload = LargeValue('r', SmallPageSchemaPayloadLength);
        var firstIndexName = "index_" + new string('i', SmallPageSchemaPayloadLength);
        var secondIndexName = "replacement_" + new string('j', SmallPageSchemaPayloadLength);
        CreatePageSizeDatabase(path, fileSystem, SqlitePageSize.Minimum);

        uint boundedPageCount;
        long finalSchemaVersion;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, BuildLargeMutableTableSql("events", payload));
            Execute(connection, BuildLargeConstraintTableSql("metadata", payload));
            Execute(connection, "CREATE TABLE \"audit\" (\"note\" TEXT NOT NULL);");
            Execute(connection, BuildLargeIndexSql(firstIndexName, "events", "required"));
            Execute(connection, BuildLargeViewSql("large_view", payload));
            Execute(connection, BuildLargeTriggerSql("large_trigger", "events", payload));

            var schemaVersionAfterCreate = ScalarInteger(connection, "PRAGMA schema_version;");
            Execute(connection, "INSERT INTO \"events\" (\"id\") VALUES (1);");
            Execute(connection, "INSERT INTO \"metadata\" (\"id\") VALUES (1);");
            ScalarInteger(connection, "PRAGMA schema_version;").Should().Be(schemaVersionAfterCreate);
            ScalarInteger(connection, "SELECT COUNT(*) FROM audit;").Should().Be(1);

            Execute(connection, "ALTER TABLE \"events\" RENAME COLUMN \"required\" TO \"payload_value\";");
            Execute(connection, "DROP TRIGGER \"large_trigger\";");
            Execute(connection, "ALTER TABLE \"events\" RENAME TO \"events_renamed\";");
            Execute(connection, $"DROP INDEX {QuoteIdentifier(firstIndexName)};");
            Execute(connection, "DROP VIEW \"large_view\";");
            Execute(connection, BuildLargeMutableTableSql("events_rebuild", payload));
            Execute(
                connection,
                """
                INSERT INTO "events_rebuild" ("id", "required", "base")
                SELECT "id", "payload_value", "base" FROM "events_renamed";
                """);
            Execute(connection, "DROP TABLE \"events_renamed\";");
            Execute(connection, "ALTER TABLE \"events_rebuild\" RENAME TO \"events_final\";");
            Execute(connection, BuildLargeIndexSql(secondIndexName, "events_final", "required"));
            Execute(connection, BuildLargeViewSql("large_view", payload));
            Execute(connection, BuildLargeTriggerSql("large_trigger", "events_final", payload));
            boundedPageCount = ReadHeaderFromMainStore(fileSystem, path).DatabaseSizeInPages;
            Execute(connection, "DROP TRIGGER \"large_trigger\";");
            Execute(connection, $"DROP INDEX {QuoteIdentifier(secondIndexName)};");
            Execute(connection, "DROP VIEW \"large_view\";");
            ReadHeaderFromMainStore(fileSystem, path).FreelistPageCount.Should().BeGreaterThan(0);
            Execute(connection, BuildLargeIndexSql(secondIndexName, "events_final", "required"));
            Execute(connection, BuildLargeViewSql("large_view", payload));
            Execute(connection, BuildLargeTriggerSql("large_trigger", "events_final", payload));
            ReadHeaderFromMainStore(fileSystem, path).DatabaseSizeInPages.Should().Be(boundedPageCount);
            Execute(connection, "INSERT INTO \"events_final\" (\"id\") VALUES (2);");
            finalSchemaVersion = ScalarInteger(connection, "PRAGMA schema_version;");
            finalSchemaVersion.Should().BeGreaterThan(schemaVersionAfterCreate);
            ScalarInteger(connection, "SELECT COUNT(*) FROM audit;").Should().Be(2);
            ScalarInteger(connection, "SELECT base FROM events_final WHERE id = 1;").Should().Be(7);
            ScalarInteger(connection, "SELECT doubled FROM metadata WHERE id = 1;").Should().Be(14);
        }

        foreach (var name in new[] { "events_final", "metadata", secondIndexName, "large_view", "large_trigger" })
            FindSchemaCell(fileSystem, path, name).OverflowPages.Should().NotBeEmpty();

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "PRAGMA schema_version;").Should().Be(finalSchemaVersion);
        ScalarInteger(reopenedConnection, "SELECT length(required) FROM events_final WHERE id = 2;")
            .Should().Be(payload.Length);
        ScalarInteger(reopenedConnection, "SELECT base FROM events_final WHERE id = 2;").Should().Be(7);
        ScalarInteger(reopenedConnection, "SELECT doubled FROM metadata WHERE id = 1;").Should().Be(14);
        ScalarInteger(
            reopenedConnection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'metadata' AND sql IS NULL;")
            .Should().Be(1);
        var metadataSql = ScalarText(
            reopenedConnection,
            "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'metadata';");
        metadataSql.Should().Contain("PRIMARY KEY")
            .And.Contain("\"code\" DESC")
            .And.Contain("CHECK")
            .And.Contain("NOT NULL")
            .And.Contain("DEFAULT")
            .And.Contain("VIRTUAL");
    }

    [Test]
    public void EncryptedSmallPageCatalogOverflowReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-schema-overflow.db";
        var payload = LargeValue('e', SmallPageSchemaPayloadLength);
        CreatePageSizeDatabase(path, fileSystem, SqlitePageSize.Minimum);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, BuildLargeTableSql("encrypted_events", payload));
            Execute(connection, "CREATE TABLE \"audit\" (\"note\" TEXT);");
            Execute(connection, BuildLargeTriggerSql("encrypted_trigger", "encrypted_events", payload));
            Execute(connection, "INSERT INTO encrypted_events(id) VALUES (1);");
        }

        FindSchemaCell(fileSystem, path, "encrypted_events").OverflowPages.Should().NotBeEmpty();
        FindSchemaCell(fileSystem, path, "encrypted_trigger").OverflowPages.Should().NotBeEmpty();

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT length(required) FROM encrypted_events;")
            .Should().Be(payload.Length);
        ScalarText(reopenedConnection, "SELECT note FROM audit;").Should().Be(payload);
    }

    [Test]
    public void ManagedBackupCopiesOverflowingSchemaDefinitionsToAFileSnapshot()
    {
        var sourcePath = CreateDatabasePath("backup-source");
        var destinationPath = CreateDatabasePath("backup-destination");
        var payload = LargeValue('b', MaximumPageSchemaPayloadLength);
        try
        {
            using (var source = OpenManagedProvider(sourcePath))
            using (var destination = OpenManagedProvider(destinationPath))
            {
                Execute(source, BuildLargeTableSql("backup_data", payload));
                Execute(source, BuildLargeConstraintTableSql("backup_constraints", payload));
                Execute(source, "CREATE TABLE \"audit\" (\"note\" TEXT);");
                Execute(source, BuildLargeViewSql("backup_view", payload));
                Execute(source, BuildLargeTriggerSql("backup_trigger", "backup_data", payload));
                Execute(source, "INSERT INTO backup_data(id) VALUES (1);");
                Execute(source, "INSERT INTO backup_constraints(id) VALUES (1);");

                source.BackupDatabase(destination);
            }

            foreach (var name in new[] { "backup_data", "backup_constraints", "backup_view", "backup_trigger" })
                FindSchemaCell(PhysicalFileSystem.Instance, destinationPath, name).OverflowPages.Should().NotBeEmpty();

            using (var reopened = EmbeddedDatabase.OpenFile(destinationPath, readOnly: true))
            using (var connection = reopened.Connect())
            {
                ScalarInteger(connection, "SELECT length(required) FROM backup_data;")
                    .Should().Be(payload.Length);
                ScalarText(connection, "SELECT note FROM audit;").Should().Be(payload);
                ScalarText(connection, "SELECT value FROM backup_view;").Should().Be(payload);
                ScalarInteger(connection, "SELECT doubled FROM backup_constraints;").Should().Be(14);
            }

            VerifyIntegrityWithSqlite(destinationPath);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
        }
    }

    [Test]
    public void CombinedWithoutRowidForeignKeyCatalogSurvivesMigrationBackupAndReopen()
    {
        var sourcePath = CreateDatabasePath("combined-catalog-source");
        var destinationPath = CreateDatabasePath("combined-catalog-destination");
        var payload = LargeValue('m', SmallPageSchemaPayloadLength);
        try
        {
            using (var source = OpenManagedProvider(sourcePath))
            using (var destination = OpenManagedProvider(destinationPath))
            {
                Execute(source, "PRAGMA foreign_keys=ON;");
                Execute(
                    source,
                    $"""
                    CREATE TABLE parent(
                        tenant TEXT COLLATE NOCASE,
                        sequence INTEGER,
                        payload TEXT COLLATE RTRIM NOT NULL DEFAULT '{payload}',
                        normalized TEXT COLLATE NOCASE AS (lower(payload)) VIRTUAL UNIQUE,
                        PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC)
                    ) WITHOUT ROWID;
                    """);
                Execute(
                    source,
                    $"""
                    CREATE TABLE child(
                        tenant TEXT COLLATE NOCASE,
                        sequence INTEGER,
                        normalized TEXT COLLATE NOCASE,
                        note TEXT NOT NULL DEFAULT '{payload}',
                        PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                        FOREIGN KEY(tenant, sequence) REFERENCES parent(tenant, sequence)
                            ON UPDATE CASCADE ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
                        FOREIGN KEY(normalized) REFERENCES parent(normalized)
                    ) WITHOUT ROWID;
                    """);
                Execute(
                    source,
                    "CREATE INDEX parent_payload_tenant "
                        + "ON parent(payload COLLATE RTRIM DESC, tenant COLLATE BINARY ASC);");
                Execute(
                    source,
                    "CREATE INDEX parent_tenant ON parent(tenant COLLATE NOCASE ASC);");
                Execute(source, "INSERT INTO parent(tenant, sequence, payload) VALUES ('alpha', 1, 'Key');");
                Execute(source, "INSERT INTO child(tenant, sequence, normalized) VALUES ('ALPHA', 1, 'KEY');");
                Execute(source, "PRAGMA journal_mode=DELETE;");
                Execute(source, $"PRAGMA page_size={SqlitePageSize.Minimum};");
                Execute(source, "VACUUM;");

                source.BackupDatabase(destination);
            }

            FindSchemaCell(PhysicalFileSystem.Instance, destinationPath, "parent")
                .OverflowPages.Should().NotBeEmpty();
            FindSchemaCell(PhysicalFileSystem.Instance, destinationPath, "child")
                .OverflowPages.Should().NotBeEmpty();

            using (var reopened = EmbeddedDatabase.OpenFile(destinationPath))
            using (var connection = reopened.Connect())
            {
                Execute(connection, "PRAGMA foreign_keys=ON;");
                ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name='parent';")
                    .Should().Contain("COLLATE NOCASE")
                    .And.Contain("sequence DESC")
                    .And.Contain("VIRTUAL")
                    .And.Contain(payload);
                ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name='child';")
                    .Should().Contain("ON UPDATE CASCADE")
                    .And.Contain("ON DELETE CASCADE")
                    .And.Contain("DEFERRABLE INITIALLY DEFERRED")
                    .And.Contain(payload);

                Execute(
                    connection,
                    "UPDATE parent SET tenant='beta', sequence=2 WHERE tenant='alpha' AND sequence=1;");
                ScalarText(connection, "SELECT tenant || ':' || sequence FROM child;")
                    .Should().Be("beta:2");
            }

            using (var sqlite = new NativeSqliteConnection($"Data Source={destinationPath};Pooling=False"))
            {
                sqlite.Open();
                ExecuteScalarText(
                    sqlite,
                    """
                    SELECT group_concat(name || ':' || coll || ':' || desc || ':' || key, ',')
                    FROM pragma_index_xinfo('parent_payload_tenant');
                    """).Should().Be(
                        "payload:RTRIM:1:1,tenant:BINARY:0:1,tenant:NOCASE:0:0,sequence:BINARY:1:0");
                ExecuteScalarText(
                    sqlite,
                    """
                    SELECT group_concat(name || ':' || coll || ':' || desc || ':' || key, ',')
                    FROM pragma_index_xinfo('parent_tenant');
                    """).Should().Be("tenant:NOCASE:0:1,sequence:BINARY:1:0");
                ExecuteScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            }
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
        }
    }

    [Test]
    public void AttachedDatabasePersistsAndRefreshesAnOverflowingSchema()
    {
        var fileSystem = new InMemoryFileSystem();
        var payload = LargeValue('a', MaximumPageSchemaPayloadLength);

        using (var main = EmbeddedDatabase.OpenFile("schema-overflow-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'schema-overflow-aux.db' AS aux;");
            Execute(connection, BuildLargeTableSql("aux.attached_data", payload));
            Execute(connection, "INSERT INTO aux.attached_data(id) VALUES (1);");
            Execute(connection, "DETACH DATABASE aux;");
            Execute(connection, "ATTACH DATABASE 'schema-overflow-aux.db' AS aux;");
            ScalarInteger(connection, "SELECT length(required) FROM aux.attached_data;")
                .Should().Be(payload.Length);
            Execute(connection, "DETACH DATABASE aux;");
        }

        FindSchemaCell(fileSystem, "schema-overflow-aux.db", "attached_data")
            .OverflowPages.Should().NotBeEmpty();
        using var reopened = EmbeddedDatabase.OpenFile("schema-overflow-aux.db", fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT doubled FROM attached_data;").Should().Be(14);
    }

    [Test]
    public void DeleteJournalVacuumMigratesOverflowingConstraintSchema()
    {
        var path = CreateDatabasePath("delete-migration");
        var payload = LargeValue('d', MaximumPageSchemaPayloadLength);
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, BuildLargeConstraintTableSql("metadata", payload));
                Execute(connection, "INSERT INTO metadata(id) VALUES (1);");
                ScalarText(connection, "PRAGMA journal_mode=DELETE;").Should().Be("delete");
                Execute(connection, $"PRAGMA page_size={SqlitePageSize.Minimum};");
                Execute(connection, "VACUUM;");
                Execute(connection, BuildLargeViewSql("delete_view", payload));
                ScalarInteger(connection, "SELECT doubled FROM metadata;").Should().Be(14);
            }

            var header = ReadHeaderFromMainStore(PhysicalFileSystem.Instance, path);
            header.PageSize.Should().Be(SqlitePageSize.Minimum);
            header.WriteVersion.Should().Be(SqliteFileFormatVersion.Legacy);
            header.ReadVersion.Should().Be(SqliteFileFormatVersion.Legacy);
            FindSchemaCell(PhysicalFileSystem.Instance, path, "metadata").OverflowPages.Should().NotBeEmpty();
            FindSchemaCell(PhysicalFileSystem.Instance, path, "delete_view").OverflowPages.Should().NotBeEmpty();

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                ScalarText(connection, "PRAGMA journal_mode;").Should().Be("delete");
                ScalarText(connection, "SELECT value FROM delete_view;").Should().Be(payload);
                ScalarInteger(connection, "SELECT doubled FROM metadata;").Should().Be(14);
            }

            VerifyIntegrityWithSqlite(path);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ReusedPooledHandleRefreshesOverflowingCatalog()
    {
        var path = CreateDatabasePath("pool-refresh");
        var payload = LargeValue('o', MaximumPageSchemaPayloadLength);
        ManagedSqliteConnection.ClearAllPools();
        try
        {
            using var writer = OpenManagedProvider(path, pooling: true);
            using var stale = OpenManagedProvider(path, pooling: true);
            stale.Close();

            Execute(writer, BuildLargeConstraintTableSql("pooled_metadata", payload));
            Execute(writer, "INSERT INTO pooled_metadata(id) VALUES (1);");
            writer.Close();

            stale.Open();
            ScalarInteger(stale, "SELECT doubled FROM pooled_metadata;").Should().Be(14);
            ScalarInteger(
                stale,
                "SELECT length(sql) FROM sqlite_schema WHERE type = 'table' AND name = 'pooled_metadata';")
                .Should().BeGreaterThan(payload.Length);
        }
        finally
        {
            ManagedSqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SqliteDeleteJournalPageSizeMigrationHandsOverflowingSchemaBackToManagedWal()
    {
        const int migratedPageSize = 8192;
        var path = CreateDatabasePath("page-size-migration");
        var payload = LargeValue('m', MaximumPageSchemaPayloadLength);
        try
        {
            CreatePageSizeDatabase(path, PhysicalFileSystem.Instance, 4096);
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, BuildLargeTableSql("wide", payload));
                Execute(connection, "INSERT INTO wide(id) VALUES (1);");
            }

            using (var sqlite = new NativeSqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                ExecuteScalarText(sqlite, "PRAGMA journal_mode=DELETE;").Should().Be("delete");
                Execute(sqlite, $"PRAGMA page_size={migratedPageSize};");
                Execute(sqlite, "VACUUM;");
                Execute(sqlite, "UPDATE wide SET base = 8;");
                ExecuteScalarText(sqlite, "PRAGMA journal_mode=WAL;").Should().Be("wal");
            }
            NativeSqliteConnection.ClearAllPools();
            ReplaceWalWithEmptyFile(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ScalarInteger(connection, "PRAGMA page_size;").Should().Be(migratedPageSize);
                ScalarInteger(connection, "SELECT length(required) FROM wide;")
                    .Should().Be(payload.Length);
                ScalarInteger(connection, "SELECT doubled FROM wide;").Should().Be(16);
                Execute(connection, "UPDATE wide SET base = 7;");
                Execute(connection, BuildLargeViewSql("migration_view", payload));
            }

            ReadHeader(PhysicalFileSystem.Instance, path).PageSize.Should().Be(migratedPageSize);
            FindSchemaCell(PhysicalFileSystem.Instance, path, "wide").OverflowPages.Should().NotBeEmpty();
            FindSchemaCell(PhysicalFileSystem.Instance, path, "migration_view").OverflowPages.Should().NotBeEmpty();
            VerifyLargeTableWithSqlite(path, migratedPageSize, expectedSql: null, payload.Length);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void RolledBackAndFailedOverflowingSchemaWritesDoNotPublishPages()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "schema-overflow-precommit-failure.db";
        var payload = LargeValue('f', MaximumPageSchemaPayloadLength);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY);");
            var stableSchemaVersion = ScalarInteger(connection, "PRAGMA schema_version;");

            Execute(connection, "BEGIN;");
            Execute(connection, BuildLargeViewSql("rolled_back_view", payload));
            Execute(connection, "ROLLBACK;");
            ScalarInteger(connection, "PRAGMA schema_version;").Should().Be(stableSchemaVersion);
            ScalarInteger(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'rolled_back_view';").Should().Be(0);

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() => Execute(
                connection,
                BuildLargeViewSql("failed_view", payload)));
            faults.ClearScheduled();
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM stable;").Should().Be(0);
        ScalarInteger(
            reopenedConnection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('rolled_back_view', 'failed_view');")
            .Should().Be(0);
        ScalarInteger(reopenedConnection, "PRAGMA schema_version;").Should().Be(1);
    }

    [Test]
    public void PostCommitCheckpointFailureRecoversCommittedSchemaOverflowFromWal()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "schema-overflow-postcommit-failure.db";
        var payload = LargeValue('w', MaximumPageSchemaPayloadLength);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY);");
            faults.FailNext(FileSystemOperation.SetLength);
            Assert.Throws<EmbeddedPostCommitMaintenanceException>(() => Execute(
                connection,
                BuildLargeViewSql("committed_view", payload)));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarText(reopenedConnection, "SELECT value FROM committed_view;").Should().Be(payload);
        FindSchemaCell(fileSystem, path, "committed_view").OverflowPages.Should().NotBeEmpty();
    }

    [TestCase("out-of-range")]
    [TestCase("truncated")]
    [TestCase("cycle")]
    public void CorruptSchemaOverflowChainFailsClosedWithoutWriting(string corruption)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = $"schema-overflow-corrupt-{corruption}.db";
        var payload = LargeValue('c', MaximumPageSchemaPayloadLength);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE stable(id INTEGER PRIMARY KEY);");
            Execute(connection, BuildLargeViewSql("corrupt_view", payload));
        }

        var location = FindSchemaCell(fileSystem, path, "corrupt_view");
        location.OverflowPages.Should().HaveCountGreaterThan(1);
        CorruptSchemaOverflow(fileSystem, path, location, corruption);
        ReplaceWalWithEmptyFile(fileSystem, path);
        var writesBeforeOpen = faults.GetOperationCount(FileSystemOperation.Write);

        var exception = Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));

        exception!.Message.Should().Contain("invalid sqlite_schema b-tree");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeOpen);
    }

    private static string BuildLargeTableSql(string name, string defaultValue)
        => $"CREATE TABLE {QuoteQualifiedIdentifier(name)} ("
           + "\"id\" INTEGER PRIMARY KEY, "
           + $"\"required\" TEXT NOT NULL DEFAULT '{defaultValue}', "
           + "\"base\" INTEGER NOT NULL DEFAULT 7, "
           + "\"doubled\" INTEGER AS (\"base\" * 2) VIRTUAL)";

    private static string BuildLargeMutableTableSql(string name, string defaultValue)
        => $"CREATE TABLE {QuoteQualifiedIdentifier(name)} ("
           + "\"id\" INTEGER PRIMARY KEY, "
           + $"\"required\" TEXT NOT NULL DEFAULT '{defaultValue}', "
           + "\"base\" INTEGER NOT NULL DEFAULT 7)";

    private static string BuildLargeConstraintTableSql(string name, string defaultValue)
        => $"CREATE TABLE {QuoteIdentifier(name)} ("
           + "\"id\" INTEGER NOT NULL, "
           + $"\"code\" TEXT NOT NULL DEFAULT '{defaultValue}', "
           + "\"base\" INTEGER NOT NULL DEFAULT 7 CHECK (\"base\" > 0), "
           + "\"doubled\" INTEGER AS (\"base\" * 2) VIRTUAL, "
           + $"CONSTRAINT {QuoteIdentifier(name + "_pk")} PRIMARY KEY (\"id\", \"code\" DESC))";

    private static string BuildLargeIndexSql(string name, string tableName, string columnName)
        => $"CREATE INDEX {QuoteIdentifier(name)} ON {QuoteIdentifier(tableName)} ({QuoteIdentifier(columnName)})";

    private static string BuildLargeViewSql(string name, string value)
        => $"CREATE VIEW {QuoteIdentifier(name)} AS SELECT '{value}' AS \"value\"";

    private static string BuildLargeTriggerSql(string name, string tableName, string value)
        => $"""
            CREATE TRIGGER {QuoteIdentifier(name)} AFTER INSERT ON {QuoteIdentifier(tableName)}
            BEGIN
                INSERT INTO "audit" VALUES ('{value}');
            END
            """;

    private static string LargeValue(char value, int length) => new(value, length);

    private static string FindSingleRowRootPromotionPayload()
    {
        const string name = "only_table";
        var usableSpace = SqlitePageSize.Minimum;
        for (var length = 8_000; length < 8_000 + usableSpace; length++)
        {
            var value = LargeValue('s', length);
            var record = SqliteRecordCodec.Encode(
                [
                    SqlValue.Text("table"),
                    SqlValue.Text(name),
                    SqlValue.Text(name),
                    SqlValue.Integer(2),
                    SqlValue.Text(BuildLargeTableSql(name, value)),
                ],
                SqliteTextEncoding.Utf8);
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                usableSpace);
            if (!layout.UsesOverflow)
                continue;

            var cell = SqliteTableLeafCell.Create(
                rowId: 1,
                checked((ulong)record.Length),
                record.AsSpan(0, layout.LocalPayloadLength),
                firstOverflowPage: 2,
                usableSpace);
            if (!CanBuildSchemaLeaf(cell, isFirstPage: true)
                && CanBuildSchemaLeaf(cell, isFirstPage: false))
            {
                return value;
            }
        }

        throw new InvalidOperationException("Could not construct the SQLite page-one schema overflow edge.");
    }

    private static bool CanBuildSchemaLeaf(SqliteTableLeafCell cell, bool isFirstPage)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(
                SqlitePageSize.Minimum,
                SqlitePageSize.Minimum,
                isFirstPage);
            builder.Append(cell);
            _ = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string QuoteQualifiedIdentifier(string identifier)
        => string.Join(".", identifier.Split('.').Select(QuoteIdentifier));

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void CreatePageSizeDatabase(
        string path,
        IFileSystem fileSystem,
        int pageSize)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = pageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(
                       pageSize,
                       salt1: unchecked((uint)(pageSize + 17)),
                       salt2: unchecked((uint)(pageSize + 31))),
                   header))
        {
        }
    }

    private static SchemaCellLocation FindSchemaCell(
        IFileSystem fileSystem,
        string path,
        string name)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var overflowReader = new SqliteOverflowChainReader(pager, header);
        var seenPages = new HashSet<uint>();
        return FindSchemaCell(
                   pager,
                   header,
                   overflowReader,
                   pageNumber: 1,
                   isFirstPage: true,
                   name,
                   seenPages)
               ?? throw new InvalidOperationException($"sqlite_schema entry '{name}' was not found.");
    }

    private static SchemaCellLocation? FindSchemaCell(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        SqliteOverflowChainReader overflowReader,
        uint pageNumber,
        bool isFirstPage,
        string name,
        ISet<uint> seenPages)
    {
        if (!seenPages.Add(pageNumber))
            throw new InvalidDataException($"sqlite_schema b-tree contains a cycle at page {pageNumber}.");

        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page, isFirstPage);
        if (pageHeader.PageType == SqliteBtreePageType.TableLeaf)
        {
            var leaf = SqliteTableLeafPageView.Parse(page, header.UsableSpace, isFirstPage);
            foreach (var locatedCell in leaf.Cells)
            {
                var cell = locatedCell.Cell;
                var payload = cell.FirstOverflowPage is null
                    ? cell.LocalPayload.ToArray()
                    : overflowReader.ReadPayload(cell);
                var values = SqliteRecordCodec.Decode(payload, header.TextEncoding);
                if (values.Length < 2 || values[1].Kind != SqlValueKind.Text
                    || !string.Equals(values[1].AsText(), name, StringComparison.Ordinal))
                {
                    continue;
                }

                var overflowLength = cell.PayloadLength - checked((ulong)cell.LocalPayload.Length);
                if (cell.FirstOverflowPage is not { } firstOverflowPage || overflowLength == 0)
                {
                    return new SchemaCellLocation(
                        pageNumber,
                        locatedCell.Offset,
                        cell.PayloadLength,
                        cell.LocalPayload.Length,
                        0,
                        Array.Empty<uint>());
                }

                return new SchemaCellLocation(
                    pageNumber,
                    locatedCell.Offset,
                    cell.PayloadLength,
                    cell.LocalPayload.Length,
                    firstOverflowPage,
                    overflowReader.Traverse(firstOverflowPage, overflowLength));
            }

            return null;
        }

        if (pageHeader.PageType != SqliteBtreePageType.TableInterior)
            throw new InvalidDataException($"sqlite_schema page {pageNumber} is not a table b-tree page.");

        var interior = SqliteTableInteriorPageView.Parse(page, header.UsableSpace, isFirstPage);
        foreach (var childPage in interior.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(interior.Header.RightMostChildPage))
        {
            var result = FindSchemaCell(
                pager,
                header,
                overflowReader,
                childPage,
                isFirstPage: false,
                name,
                seenPages);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static void CorruptSchemaOverflow(
        IFileSystem fileSystem,
        string path,
        SchemaCellLocation location,
        string corruption)
    {
        using var store = SqlitePageStore.Open(fileSystem, path);
        switch (corruption)
        {
            case "out-of-range":
                {
                    var leafPage = store.ReadPage(location.LeafPageNumber);
                    if (!SqliteVarint.TryRead(
                            leafPage.AsSpan(location.CellOffset),
                            out _,
                            out var payloadLengthBytes)
                        || !SqliteVarint.TryRead(
                            leafPage.AsSpan(location.CellOffset + payloadLengthBytes),
                            out _,
                            out var rowIdBytes))
                    {
                        throw new InvalidDataException("Could not locate the schema cell overflow pointer.");
                    }

                    var overflowPointerOffset = checked(
                        location.CellOffset
                        + payloadLengthBytes
                        + rowIdBytes
                        + location.LocalPayloadLength);
                    BinaryPrimitives.WriteUInt32BigEndian(
                        leafPage.AsSpan(overflowPointerOffset, sizeof(uint)),
                        checked(store.PageCount + 1));
                    store.WritePage(location.LeafPageNumber, leafPage);
                    break;
                }
            case "truncated":
                {
                    var overflowPage = store.ReadPage(location.FirstOverflowPage);
                    BinaryPrimitives.WriteUInt32BigEndian(overflowPage, 0);
                    store.WritePage(location.FirstOverflowPage, overflowPage);
                    break;
                }
            case "cycle":
                {
                    var overflowPage = store.ReadPage(location.FirstOverflowPage);
                    BinaryPrimitives.WriteUInt32BigEndian(
                        overflowPage,
                        location.FirstOverflowPage);
                    store.WritePage(location.FirstOverflowPage, overflowPage);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption kind.");
        }

        store.Flush();
    }

    private static void ReplaceWalWithEmptyFile(IFileSystem fileSystem, string path)
    {
        var header = ReadHeaderFromMainStore(fileSystem, path);
        fileSystem.DeleteFile(path + "-wal");
        fileSystem.DeleteFile(path + "-shm");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 101, salt2: 103)))
        {
        }
        using (fileSystem.OpenFile(path + "-shm", FileOpenMode.OpenOrCreate))
        {
        }
    }

    private static SqliteDatabaseHeader ReadHeader(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
    }

    private static SqliteDatabaseHeader ReadHeaderFromMainStore(IFileSystem fileSystem, string path)
    {
        using var store = SqlitePageStore.Open(fileSystem, path);
        return store.Header;
    }

    private static void VerifyLargeTableWithSqlite(
        string path,
        int pageSize,
        string? expectedSql,
        int expectedPayloadLength)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new NativeSqliteConnection($"Data Source={verificationPath};Pooling=False");
            sqlite.Open();
            ExecuteScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Convert.ToInt32(ExecuteScalar(sqlite, "PRAGMA page_size;")).Should().Be(pageSize);
            if (expectedSql is not null)
            {
                Convert.ToString(
                    ExecuteScalar(
                        sqlite,
                        "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'wide';"))
                    .Should().Be(expectedSql);
            }

            Convert.ToInt32(ExecuteScalar(sqlite, "SELECT length(required) FROM wide WHERE id = 1;"))
                .Should().Be(expectedPayloadLength);
            Convert.ToInt32(ExecuteScalar(sqlite, "SELECT doubled FROM wide WHERE id = 1;"))
                .Should().Be(14);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static void VerifyIntegrityWithSqlite(string path)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new NativeSqliteConnection($"Data Source={verificationPath};Pooling=False");
            sqlite.Open();
            ExecuteScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static ManagedSqliteConnection OpenManagedProvider(string path, bool pooling = false)
    {
        var connection = new ManagedSqliteConnection(
            $"Data Source={path};Pooling={pooling};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static void Execute(ManagedSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(NativeSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(NativeSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ExecuteScalarText(NativeSqliteConnection connection, string sql)
        => Convert.ToString(ExecuteScalar(connection, sql))
           ?? throw new InvalidDataException($"SQLite query returned NULL: {sql}");

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long ScalarInteger(ManagedSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-schema-overflow-storage-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private readonly record struct SchemaCellLocation(
        uint LeafPageNumber,
        int CellOffset,
        ulong PayloadLength,
        int LocalPayloadLength,
        uint FirstOverflowPage,
        IReadOnlyList<uint> OverflowPages);
}
