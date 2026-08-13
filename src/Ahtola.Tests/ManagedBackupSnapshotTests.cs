using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedBackupSnapshotTests
{
    [Test]
    public void ManagedBackupCopiesSnapshotSchemaValuesAndHiddenRowids()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE data(integer_value INTEGER, real_value REAL, text_value TEXT, blob_value BLOB, null_value TEXT);");
        source.ExecuteNonQuery("CREATE TABLE aliases(id INTEGER PRIMARY KEY, value TEXT);");
        source.ExecuteNonQuery("CREATE TABLE generated(base INTEGER, doubled AS (base * 2) VIRTUAL);");
        source.ExecuteNonQuery("CREATE VIEW data_view AS SELECT integer_value, text_value FROM data;");
        source.ExecuteNonQuery("PRAGMA user_version = 123; PRAGMA application_id = 456;");
        destination.ExecuteNonQuery("PRAGMA user_version = 9; PRAGMA application_id = 10;");
        var sourceSchemaVersion = source.ExecuteScalar<long>("PRAGMA schema_version;");

        InsertData(source, 41, -17, 1.25, "first", [0, 1, 2], null);
        InsertData(source, 97, 42, -3.5, "second", [255, 4], "present");
        source.ExecuteNonQuery("INSERT INTO aliases(id, value) VALUES (71, 'rowid alias');");
        source.ExecuteNonQuery("INSERT INTO generated(base) VALUES (21);");

        source.BackupDatabase(destination);

        using (var reader = destination.ExecuteReader(
                   "SELECT rowid, integer_value, real_value, text_value, blob_value, null_value FROM data ORDER BY rowid;"))
        {
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(41);
            reader.GetInt64(1).Should().Be(-17);
            reader.GetDouble(2).Should().Be(1.25);
            reader.GetString(3).Should().Be("first");
            ((byte[])reader.GetValue(4)).Should().Equal(0, 1, 2);
            reader.IsDBNull(5).Should().BeTrue();

            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(97);
            reader.GetInt64(1).Should().Be(42);
            reader.GetDouble(2).Should().Be(-3.5);
            reader.GetString(3).Should().Be("second");
            ((byte[])reader.GetValue(4)).Should().Equal(255, 4);
            reader.GetString(5).Should().Be("present");
            reader.Read().Should().BeFalse();
        }

        destination.ExecuteScalar<string>("SELECT text_value FROM data_view WHERE integer_value = 42;").Should().Be("second");
        destination.ExecuteScalar<long>("SELECT rowid FROM aliases WHERE id = 71;").Should().Be(71);
        destination.ExecuteScalar<long>("SELECT doubled FROM generated WHERE base = 21;").Should().Be(42);
        destination.ExecuteScalar<long>("PRAGMA schema_version;").Should().Be(sourceSchemaVersion);
        destination.ExecuteScalar<long>("PRAGMA user_version;").Should().Be(123);
        destination.ExecuteScalar<long>("PRAGMA application_id;").Should().Be(456);

        InsertData(destination, 123, 9, 0, "after backup", [], null);
        destination.ExecuteScalar<long>("SELECT rowid FROM data WHERE text_value = 'after backup';").Should().Be(123);
    }

    [Test]
    public void ManagedBackupReplacesNonemptyDestination()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        source.ExecuteNonQuery("CREATE TABLE sqliteX(value TEXT); INSERT INTO sqliteX VALUES ('valid prefix');");
        destination.ExecuteNonQuery("CREATE TABLE sqliteY(value TEXT); INSERT INTO sqliteY VALUES ('destination');");

        source.BackupDatabase(destination);

        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
        destination.ExecuteScalar<string>("SELECT value FROM sqliteX;").Should().Be("valid prefix");
        destination.Invoking(connection => connection.ExecuteScalar<string>("SELECT value FROM sqliteY;"))
            .Should().Throw<SqliteException>().WithMessage("*no such table: sqliteY*");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(2);
    }

    [Test]
    public void ManagedBackupRollsBackDestinationAndReleasesSourceSnapshotOnCopyFailure()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE all_rowid_aliases(rowid TEXT, _rowid_ TEXT, oid TEXT);");
        source.ExecuteNonQuery("INSERT INTO all_rowid_aliases VALUES ('a', 'b', 'c');");
        source.ExecuteNonQuery("PRAGMA user_version = 123;");
        destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");
        destination.ExecuteNonQuery("PRAGMA user_version = 77;");

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("all_rowid_aliases"));

        destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(1);
        destination.ExecuteScalar<long>("PRAGMA user_version;").Should().Be(77);
        source.ExecuteScalar<string>("SELECT rowid FROM all_rowid_aliases;").Should().Be("a");
    }

    [Test]
    public void ManagedBackupRejectsActiveDestinationTransactionBeforeSnapshot()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("PRAGMA foreign_keys = ON;");

        using var transaction = destination.BeginTransaction();
        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        destination.ExecuteScalar<long>("PRAGMA foreign_keys;").Should().Be(1);
        transaction.Rollback();
    }

    [Test]
    public void ManagedBackupMapsRawDestinationTransactionToBusyWithoutChangingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");
        destination.ExecuteNonQuery("BEGIN;");

        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
        destination.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ManagedBackupCopiesActiveSourceTransactionWithoutCompletingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('committed');");

        using var transaction = source.BeginTransaction();
        source.ExecuteNonQuery("INSERT INTO source_data VALUES ('uncommitted');");

        source.BackupDatabase(destination);

        source.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
        transaction.Rollback();
        source.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(1);
        destination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
    }

    [Test]
    public void ManagedBackupCopiesActiveFileSourceSnapshotBeforeSourceRollbackAndReopen()
    {
        var sourcePath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection())
            {
                source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('committed');");
                using var transaction = source.BeginTransaction();
                source.ExecuteNonQuery("INSERT INTO source_data VALUES ('rolled back later');");

                source.BackupDatabase(destination);
                transaction.Rollback();
                destination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
            }

            using var reopenedSource = OpenManagedConnection(sourcePath);
            reopenedSource.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(1);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
        }
    }

    [Test]
    public void ManagedBackupPreservesActiveConstraintMetadataAcrossDestinationReopen()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            using (var transaction = source.BeginTransaction())
            {
                source.ExecuteNonQuery(
                    """
                    CREATE TABLE constrained(
                        id INTEGER PRIMARY KEY,
                        code TEXT CONSTRAINT uq_code UNIQUE ON CONFLICT IGNORE,
                        required INTEGER CONSTRAINT nn_required NOT NULL ON CONFLICT REPLACE DEFAULT (2 + 3),
                        amount DOUBLE PRECISION DEFAULT (abs(-4) + 1),
                        label CHARACTER VARYING(20),
                        CONSTRAINT positive CHECK (amount > 0),
                        CONSTRAINT metric_value UNIQUE (label, amount) ON CONFLICT IGNORE
                    );
                    INSERT INTO constrained(id, code, label) VALUES (1, 'A', 'X');
                    CREATE TABLE generated_key(
                        tenant TEXT,
                        sequence INTEGER,
                        base INTEGER NOT NULL,
                        doubled INTEGER AS (base * 2) VIRTUAL,
                        PRIMARY KEY(tenant, sequence)
                    );
                    INSERT INTO generated_key(tenant, sequence, base) VALUES ('tenant', 1, 7);
                    """);

                source.BackupDatabase(destination);
                transaction.Rollback();
                source.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE name = 'constrained';").Should().Be(0);
            }

            using var reopened = OpenManagedConnection(destinationPath);
            var schemaSql = reopened.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE name = 'constrained';");
            // Backups copy the stored CREATE text verbatim, including the original quoting.
            schemaSql.Should().Contain("CONSTRAINT uq_code UNIQUE ON CONFLICT IGNORE")
                .And.Contain("CONSTRAINT nn_required NOT NULL ON CONFLICT REPLACE DEFAULT (2 + 3)")
                .And.Contain("DOUBLE PRECISION DEFAULT (abs(-4) + 1)")
                .And.Contain("CHARACTER VARYING(20)")
                .And.Contain("CONSTRAINT positive CHECK (amount > 0)")
                .And.Contain("CONSTRAINT metric_value UNIQUE (label, amount) ON CONFLICT IGNORE");

            reopened.ExecuteScalar<long>(
                "SELECT required FROM constrained WHERE id = 1;").Should().Be(5);
            reopened.ExecuteNonQuery(
                "INSERT INTO constrained(id, code, required, label) VALUES (2, 'B', NULL, 'Y');");
            reopened.ExecuteScalar<long>(
                "SELECT required FROM constrained WHERE id = 2;").Should().Be(5);
            reopened.ExecuteNonQuery(
                "INSERT INTO constrained(id, code, label) VALUES (3, 'A', 'Z');");
            reopened.ExecuteNonQuery(
                "INSERT INTO constrained(id, code, label) VALUES (4, 'C', 'Y');");
            reopened.ExecuteScalar<long>("SELECT COUNT(*) FROM constrained;").Should().Be(2);

            reopened.Invoking(connection => connection.ExecuteNonQuery(
                    "UPDATE constrained SET amount = -1 WHERE id = 2;"))
                .Should().Throw<SqliteException>()
                .WithMessage("*CHECK constraint failed: positive*");
            reopened.ExecuteScalar<double>(
                "SELECT amount FROM constrained WHERE id = 2;").Should().Be(5);
            var generatedSchema = reopened.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE name = 'generated_key';");
            generatedSchema.Should().Contain("doubled INTEGER AS (base * 2) VIRTUAL")
                .And.Contain("PRIMARY KEY(tenant, sequence)");
            reopened.ExecuteScalar<long>(
                "SELECT doubled FROM generated_key WHERE tenant = 'tenant' AND sequence = 1;").Should().Be(14);
            reopened.Invoking(connection => connection.ExecuteNonQuery(
                    "INSERT INTO generated_key(tenant, sequence, base) VALUES ('tenant', 1, 9);"))
                .Should().Throw<SqliteException>()
                .WithMessage("*UNIQUE constraint failed: generated_key.tenant, generated_key.sequence*");
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupCopiesWhileSourceReaderRemainsActive()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        using var reader = source.ExecuteReader("SELECT value FROM source_data;");
        reader.Read().Should().BeTrue();

        source.BackupDatabase(destination);

        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
        reader.GetString(0).Should().Be("source");
    }

    [Test]
    public void ManagedBackupRejectsOpenDestinationReaderWithoutChangingIt()
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE destination_data(value TEXT); INSERT INTO destination_data VALUES ('destination');");
        using var reader = destination.ExecuteReader("SELECT value FROM destination_data;");
        reader.Read().Should().BeTrue();

        var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

        exception!.SqliteErrorCode.Should().Be(5);
        reader.GetString(0).Should().Be("destination");
        reader.Dispose();
        destination.ExecuteScalar<string>("SELECT value FROM destination_data;").Should().Be("destination");
    }

    [Test]
    public void ManagedBackupAllowsUnrelatedActiveAttachments()
    {
        var sourcePath = CreateManagedDatabasePath();
        var sourceAttachmentPath = CreateManagedDatabasePath();
        try
        {
            using var source = OpenManagedConnection(sourcePath);
            using var destination = OpenManagedConnection();
            source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
            source.ExecuteNonQuery($"ATTACH DATABASE '{sourceAttachmentPath}' AS source_aux;");
            source.ExecuteNonQuery("CREATE TABLE source_aux.marker(value TEXT); INSERT INTO source_aux.marker VALUES ('source attachment');");

            source.BackupDatabase(destination);

            destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
            source.ExecuteScalar<string>("SELECT value FROM source_aux.marker;").Should().Be("source attachment");
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(sourceAttachmentPath);
        }
    }

    [Test]
    public void ManagedBackupCopiesFreshAttachedSourceIntoMemory()
    {
        var sourcePath = CreateManagedDatabasePath();
        var attachmentPath = CreateManagedDatabasePath();
        try
        {
            using var source = OpenManagedConnection(sourcePath);
            using var destination = OpenManagedConnection();
            source.ExecuteNonQuery($"ATTACH DATABASE '{attachmentPath}' AS source_aux;");
            source.ExecuteNonQuery("CREATE TABLE source_aux.marker(value TEXT); INSERT INTO source_aux.marker VALUES ('attached');");

            source.BackupDatabase(destination, "main", "source_aux");

            destination.ExecuteScalar<string>("SELECT value FROM marker;").Should().Be("attached");
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(attachmentPath);
        }
    }

    [TestCase("WAL")]
    [TestCase("DELETE")]
    public void ManagedBackupRejectsActiveAttachedSourceSnapshotBeforeDestinationMutation(string journalMode)
    {
        var sourcePath = CreateManagedDatabasePath();
        var attachmentPath = CreateManagedDatabasePath();
        try
        {
            using (var attachment = OpenManagedConnection(attachmentPath))
            {
                attachment.ExecuteNonQuery("CREATE TABLE payload(value TEXT); INSERT INTO payload VALUES ('committed');");
                SetJournalMode(attachment, journalMode);
            }

            using var source = OpenManagedConnection(sourcePath);
            using var destination = OpenManagedConnection();
            SetJournalMode(source, journalMode);
            source.ExecuteNonQuery($"ATTACH DATABASE '{attachmentPath}' AS source_aux;");
            destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");
            using var transaction = source.BeginTransaction();
            source.ExecuteNonQuery("INSERT INTO source_aux.payload VALUES ('uncommitted');");

            var exception = Assert.Throws<SqliteException>(
                () => source.BackupDatabase(destination, "main", "source_aux"));

            exception!.SqliteErrorCode.Should().Be(5);
            exception.Message.Should().Contain("source database is locked");
            destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
            transaction.Rollback();
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(attachmentPath);
        }
    }

    [TestCase("WAL")]
    [TestCase("DELETE")]
    public void ManagedBackupRoutesAttachedTransactionsAcrossJournalModes(string journalMode)
    {
        var sourcePath = CreateManagedDatabasePath();
        var sourceAttachmentPath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        var destinationAttachmentPath = CreateManagedDatabasePath();
        try
        {
            using (var sourceAttachment = OpenManagedConnection(sourceAttachmentPath))
            {
                sourceAttachment.ExecuteNonQuery(
                    "CREATE TABLE payload(id INTEGER PRIMARY KEY, value TEXT);"
                    + " INSERT INTO payload VALUES (1, 'committed');");
                SetJournalMode(sourceAttachment, journalMode);
            }

            using (var destinationAttachment = OpenManagedConnection(destinationAttachmentPath))
            {
                destinationAttachment.ExecuteNonQuery(
                    "CREATE TABLE old_payload(value TEXT); INSERT INTO old_payload VALUES ('old');");
                SetJournalMode(destinationAttachment, journalMode);
            }

            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                SetJournalMode(source, journalMode);
                SetJournalMode(destination, journalMode);
                source.ExecuteNonQuery($"ATTACH DATABASE '{sourceAttachmentPath}' AS source_aux;");
                destination.ExecuteNonQuery($"ATTACH DATABASE '{destinationAttachmentPath}' AS destination_aux;");
                destination.ExecuteNonQuery("CREATE TABLE main_marker(value TEXT); INSERT INTO main_marker VALUES ('main');");

                using (var transaction = source.BeginTransaction())
                {
                    source.ExecuteNonQuery("INSERT INTO source_aux.payload VALUES (2, 'transaction');");
                    transaction.Commit();
                }

                source.BackupDatabase(destination, "destination_aux", "source_aux");

                destination.ExecuteScalar<long>("SELECT COUNT(*) FROM destination_aux.payload;").Should().Be(2);
                destination.ExecuteScalar<string>(
                    "SELECT value FROM destination_aux.payload WHERE id = 2;").Should().Be("transaction");
                destination.ExecuteScalar<string>("SELECT value FROM main_marker;").Should().Be("main");
                destination.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM destination_aux.sqlite_master WHERE name = 'old_payload';").Should().Be(0);
            }

            using var reopened = OpenManagedConnection(destinationAttachmentPath);
            reopened.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be(journalMode.ToLowerInvariant());
            reopened.ExecuteScalar<long>("SELECT COUNT(*) FROM payload;").Should().Be(2);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(sourceAttachmentPath);
            DeleteManagedDatabase(destinationPath);
            DeleteManagedDatabase(destinationAttachmentPath);
        }
    }

    [Test]
    public void ManagedBackupCopiesMemorySourceIntoAttachedFileDestination()
    {
        var destinationPath = CreateManagedDatabasePath();
        var attachmentPath = CreateManagedDatabasePath();
        try
        {
            using var source = OpenManagedConnection();
            using var destination = OpenManagedConnection(destinationPath);
            source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
            destination.ExecuteNonQuery("CREATE TABLE main_data(value TEXT); INSERT INTO main_data VALUES ('main');");
            destination.ExecuteNonQuery($"ATTACH DATABASE '{attachmentPath}' AS destination_aux;");
            destination.ExecuteNonQuery("CREATE TABLE destination_aux.old_data(value TEXT); INSERT INTO destination_aux.old_data VALUES ('old');");

            source.BackupDatabase(destination, "destination_aux", "main");

            destination.ExecuteScalar<string>("SELECT value FROM destination_aux.source_data;").Should().Be("source");
            destination.ExecuteScalar<string>("SELECT value FROM main_data;").Should().Be("main");
            destination.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM destination_aux.sqlite_master WHERE name = 'old_data';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(destinationPath);
            DeleteManagedDatabase(attachmentPath);
        }
    }

    [Test]
    public void ManagedBackupRefreshesDurableSourceCatalogAndHeaderBeforeSnapshot()
    {
        var sourcePath = CreateManagedDatabasePath();
        try
        {
            using var source = OpenManagedConnection(sourcePath);
            using var destination = OpenManagedConnection();
            source.ExecuteNonQuery(
                "CREATE TABLE source_data(value TEXT);"
                + " INSERT INTO source_data VALUES ('first');");

            long siblingSchemaVersion;
            using (var sibling = OpenManagedConnection(sourcePath))
            {
                sibling.ExecuteNonQuery(
                    "INSERT INTO source_data VALUES ('sibling');"
                    + " CREATE TABLE sibling_data(value TEXT);"
                    + " INSERT INTO sibling_data VALUES ('new catalog');");
                siblingSchemaVersion = sibling.ExecuteScalar<long>("PRAGMA schema_version;");
            }

            source.BackupDatabase(destination);

            destination.ExecuteScalar<long>("SELECT COUNT(*) FROM source_data;").Should().Be(2);
            destination.ExecuteScalar<string>("SELECT value FROM sibling_data;").Should().Be("new catalog");
            destination.ExecuteScalar<long>("PRAGMA schema_version;").Should().Be(siblingSchemaVersion);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
        }
    }

    [TestCase("main", "missing")]
    [TestCase("missing", "main")]
    public void ManagedBackupRejectsUnknownDatabaseNamesWithoutChangingDestination(
        string destinationName,
        string sourceName)
    {
        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
        destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");

        var exception = Assert.Throws<SqliteException>(
            () => source.BackupDatabase(destination, destinationName, sourceName));

        exception!.SqliteErrorCode.Should().Be(1);
        exception.Message.Should().Contain("no such database: missing");
        destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("destination");
    }

    [Test]
    public void ManagedBackupAtomicallyReplacesAndPersistsAFileBackedDestination()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE data(value TEXT, payload BLOB);");
                destination.ExecuteNonQuery(
                "CREATE TABLE old_data(value TEXT);"
                + " INSERT INTO old_data VALUES ('old');"
                + " CREATE TABLE older_data(value TEXT);");
                var sourceSchemaVersion = source.ExecuteScalar<long>("PRAGMA schema_version;");
                using var command = source.CreateCommand();
                command.CommandText = "INSERT INTO data(rowid, value, payload) VALUES (9, 'persisted', $payload);";
                command.Parameters.Add("$payload", SqliteType.Blob).Value = new byte[] { 6, 7, 8 };
                command.ExecuteNonQuery();

                source.BackupDatabase(destination);
                destination.ExecuteScalar<long>("PRAGMA schema_version;").Should().Be(sourceSchemaVersion);
            }

            using var reopened = OpenManagedConnection(destinationPath);
            using var reader = reopened.ExecuteReader("SELECT rowid, value, payload FROM data;");
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(9);
            reader.GetString(1).Should().Be("persisted");
            ((byte[])reader.GetValue(2)).Should().Equal(6, 7, 8);
            reopened.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'old_data';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupFailurePreservesFileDestinationAcrossReopen()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE inaccessible(rowid TEXT, _rowid_ TEXT, oid TEXT);");
                source.ExecuteNonQuery("INSERT INTO inaccessible VALUES ('a', 'b', 'c');");
                destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('durable');");

                source.Invoking(connection => connection.BackupDatabase(destination))
                    .Should().Throw<NotSupportedException>()
                    .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupRowidNotAccessible("inaccessible"));

                destination.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("durable");
            }

            using var reopened = OpenManagedConnection(destinationPath);
            reopened.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("durable");
            reopened.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'inaccessible';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupCopiesBetweenDistinctPhysicalFiles()
    {
        var sourcePath = CreateManagedDatabasePath();
        var destinationPath = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(sourcePath))
            using (var destination = OpenManagedConnection(destinationPath))
            {
                source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");
                destination.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('destination');");

                source.BackupDatabase(destination);

                destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
            }

            using var reopened = OpenManagedConnection(destinationPath);
            reopened.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
            reopened.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'preserved';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(sourcePath);
            DeleteManagedDatabase(destinationPath);
        }
    }

    [Test]
    public void ManagedBackupCopiesBetweenSameConnectionPhysicalDatabases()
    {
        var mainPath = CreateManagedDatabasePath();
        var attachmentPath = CreateManagedDatabasePath();
        try
        {
            using var connection = OpenManagedConnection(mainPath);
            connection.ExecuteNonQuery("CREATE TABLE main_data(value TEXT); INSERT INTO main_data VALUES ('main');");
            connection.ExecuteNonQuery($"ATTACH DATABASE '{attachmentPath}' AS auxiliary;");
            connection.ExecuteNonQuery("CREATE TABLE auxiliary.preserved(value TEXT); INSERT INTO auxiliary.preserved VALUES ('auxiliary');");

            connection.BackupDatabase(connection, "auxiliary", "main");

            connection.ExecuteScalar<string>("SELECT value FROM main_data;").Should().Be("main");
            connection.ExecuteScalar<string>("SELECT value FROM auxiliary.main_data;").Should().Be("main");
            connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM auxiliary.sqlite_master WHERE name = 'preserved';").Should().Be(0);
        }
        finally
        {
            DeleteManagedDatabase(mainPath);
            DeleteManagedDatabase(attachmentPath);
        }
    }

    [Test]
    public void ManagedBackupRejectsDistinctConnectionsToTheSameFile()
    {
        var path = CreateManagedDatabasePath();
        try
        {
            using (var source = OpenManagedConnection(path))
            using (var destination = OpenManagedConnection(path))
            {
                source.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('same file');");

                var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

                exception!.SqliteErrorCode.Should().Be(1);
                exception.Message.Should().Contain("source and destination must be distinct");
                source.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("same file");
            }

            using var reopened = OpenManagedConnection(path);
            reopened.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("same file");
        }
        finally
        {
            DeleteManagedDatabase(path);
        }
    }

    [Test]
    public void ManagedBackupRejectsCaseVariantPhysicalAliasOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("This regression exercises macOS physical path identity.");

        var path = CreateManagedDatabasePath();
        var aliasPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileName(path).ToUpperInvariant());
        try
        {
            using var source = OpenManagedConnection(path);
            using var destination = OpenManagedConnection(aliasPath);
            source.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('source');");

            var exception = Assert.Throws<SqliteException>(() => source.BackupDatabase(destination));

            exception!.SqliteErrorCode.Should().Be(1);
            exception.Message.Should().Contain("source and destination must be distinct");
            source.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("source");
        }
        finally
        {
            DeleteManagedDatabase(aliasPath);
            DeleteManagedDatabase(path);
        }
    }

    [Test]
    public void ManagedBackupSymbolicAliasCannotOpenAlongsideSource()
    {
        var path = CreateManagedDatabasePath();
        var aliasPath = CreateManagedDatabasePath();
        try
        {
            using var source = OpenManagedConnection(path);
            source.ExecuteNonQuery("CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES ('source');");
            try
            {
                File.CreateSymbolicLink(aliasPath, path);
                File.CreateSymbolicLink(aliasPath + "-wal", path + "-wal");
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Ignore("Creating symbolic links is not permitted on this host.");
            }
            catch (PlatformNotSupportedException)
            {
                Assert.Ignore("Symbolic links are not supported on this host.");
            }

            // Stage 6: symlink aliases resolve to the same canonical path and share
            // one process-local SHARED lease (refcount), same as dual open of the
            // same database file.
            using (var alias = OpenManagedConnection(aliasPath))
            {
                alias.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("source");
            }

            source.ExecuteScalar<string>("SELECT value FROM preserved;").Should().Be("source");
        }
        finally
        {
            DeleteManagedDatabase(aliasPath);
            DeleteManagedDatabase(path);
        }
    }

    [Test]
    public void ManagedIncrementalBlobWritesThroughTheManagedConnection()
    {
        using var connection = OpenManagedConnection();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data VALUES (X'0102');");

        using (var blob = new SqliteBlob(connection, "data", "value", 1))
            blob.Write([3], 0, 1);

        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(3, 2);
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenManagedConnection(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void SetJournalMode(SqliteConnection connection, string journalMode)
    {
        connection.ExecuteScalar<string>($"PRAGMA journal_mode={journalMode};")
            .Should().Be(journalMode.ToLowerInvariant());
    }

    private static void InsertData(
        SqliteConnection connection,
        long rowid,
        long integerValue,
        double realValue,
        string textValue,
        byte[] blobValue,
        string? nullValue)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO data(rowid, integer_value, real_value, text_value, blob_value, null_value)
            VALUES ($rowid, $integer_value, $real_value, $text_value, $blob_value, $null_value);
            """;
        command.Parameters.Add("$rowid", SqliteType.Integer).Value = rowid;
        command.Parameters.Add("$integer_value", SqliteType.Integer).Value = integerValue;
        command.Parameters.Add("$real_value", SqliteType.Real).Value = realValue;
        command.Parameters.Add("$text_value", SqliteType.Text).Value = textValue;
        command.Parameters.Add("$blob_value", SqliteType.Blob).Value = blobValue;
        command.Parameters.Add("$null_value", SqliteType.Text).Value = (object?)nullValue ?? DBNull.Value;
        command.ExecuteNonQuery();
    }

    private static string CreateManagedDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-backup-snapshot-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"backup-{Guid.NewGuid():N}.db");
    }

    private static void DeleteManagedDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
