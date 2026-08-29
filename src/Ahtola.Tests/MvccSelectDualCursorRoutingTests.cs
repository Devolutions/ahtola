using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// End-to-end dual-cursor SQL routing under <c>PRAGMA journal_mode=mvcc</c> +
/// <c>BEGIN CONCURRENT</c>: peer uncommitted writes stay invisible, own writes
/// are visible, post-commit SI, and same-row WW conflicts surface on the SQL path.
/// </summary>
public sealed class MvccSelectDualCursorRoutingTests
{
    [Test]
    public void PeerUncommittedInsertIsInvisibleUntilCommit()
    {
        using var db = new RoutingFileDatabase();
        using var writer = db.Connect();
        using var reader = db.Connect();

        writer.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        ReadValue(reader, "PRAGMA journal_mode;").Should().Be("mvcc");

        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("INSERT INTO t VALUES (42);");

        reader.ExecuteNonQuery("BEGIN CONCURRENT;");
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 42;")).Should().Be(0L);

        writer.ExecuteNonQuery("COMMIT;");

        // Reader snapshot began before the commit — SI keeps the insert dark.
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 42;")).Should().Be(0L);
        reader.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 42;")).Should().Be(1L);
    }

    [Test]
    public void WriterSeesOwnUncommittedInsertViaSelect()
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (7);");
        Convert.ToInt64(Scalar(connection, "SELECT v FROM t WHERE v = 7;")).Should().Be(7L);
        connection.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void PeerUncommittedDeleteKeepsBaseVisibleToSibling()
    {
        using var db = new RoutingFileDatabase();
        using var seeder = db.Connect();
        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        using var deleter = db.Connect();
        using var reader = db.Connect();
        deleter.ExecuteNonQuery("BEGIN CONCURRENT;");
        reader.ExecuteNonQuery("BEGIN CONCURRENT;");

        deleter.ExecuteNonQuery("DELETE FROM t WHERE v = 1;");
        Convert.ToInt64(Scalar(deleter, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(0L);
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(1L);

        deleter.ExecuteNonQuery("COMMIT;");
        // Reader began before delete commit — still sees the base row under SI.
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(1L);
        reader.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t WHERE v = 1;")).Should().Be(0L);
    }

    [Test]
    public void ConcurrentUpdateOfSameBaseRowRaisesWriteWriteConflict()
    {
        using var db = new RoutingFileDatabase();
        using var seeder = db.Connect();
        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        using var a = db.Connect();
        using var b = db.Connect();
        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("UPDATE t SET v = 10 WHERE v = 1;");

        var error = Capture(() => b.ExecuteNonQuery("UPDATE t SET v = 20 WHERE v = 1;"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("write-write conflict");
        b.ExecuteNonQuery("ROLLBACK;");
        a.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(a, "SELECT v FROM t;")).Should().Be(10L);
    }

    [Test]
    public void ConcurrentDeleteOfSameBaseRowRaisesWriteWriteConflict()
    {
        using var db = new RoutingFileDatabase();
        using var seeder = db.Connect();
        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (3);");

        using var a = db.Connect();
        using var b = db.Connect();
        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("DELETE FROM t WHERE v = 3;");

        var error = Capture(() => b.ExecuteNonQuery("DELETE FROM t WHERE v = 3;"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("write-write conflict");
        b.ExecuteNonQuery("ROLLBACK;");
        a.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void PostCommitSelectSeesPeerInsertAndUpdate()
    {
        using var db = new RoutingFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();
        a.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");

        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (100);");
        a.ExecuteNonQuery("COMMIT;");

        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("UPDATE t SET v = 200 WHERE v = 100;");
        b.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(a, "SELECT v FROM t;")).Should().Be(200L);
    }

    [Test]
    public void ConcurrentIndexScanMergesVersionStoreRowsAtThePinnedSnapshot()
    {
        using var db = new RoutingFileDatabase(
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);
            CREATE INDEX ix_t_value ON t(value);
            """);
        using var seeder = db.Connect();
        using var writer = db.Connect();
        using var reader = db.Connect();

        seeder.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        seeder.ExecuteNonQuery("INSERT INTO t VALUES (1, 'base');");

        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        reader.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("INSERT INTO t VALUES (2, 'overlay');");

        Convert.ToInt64(Scalar(
            writer,
            "SELECT id FROM t INDEXED BY ix_t_value WHERE value = 'overlay';")).Should().Be(2L);
        Convert.ToInt64(Scalar(
            reader,
            "SELECT COUNT(*) FROM t INDEXED BY ix_t_value WHERE value = 'overlay';")).Should().Be(0L);

        writer.ExecuteNonQuery("COMMIT;");
        Convert.ToInt64(Scalar(
            reader,
            "SELECT COUNT(*) FROM t INDEXED BY ix_t_value WHERE value = 'overlay';")).Should().Be(0L);
        reader.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(
            reader,
            "SELECT id FROM t INDEXED BY ix_t_value WHERE value = 'overlay';")).Should().Be(2L);
    }

    [Test]
    public void ConcurrentIndexScanSuppressesTheOldKeyAfterAnUpdate()
    {
        using var db = new RoutingFileDatabase(
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);
            CREATE INDEX ix_t_value ON t(value);
            """);
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 'before');");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("UPDATE t SET value = 'after' WHERE id = 1;");

        Convert.ToInt64(Scalar(
            connection,
            "SELECT COUNT(*) FROM t INDEXED BY ix_t_value WHERE value = 'before';")).Should().Be(0L);
        Convert.ToInt64(Scalar(
            connection,
            "SELECT id FROM t INDEXED BY ix_t_value WHERE value = 'after';")).Should().Be(1L);
        connection.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void ConcurrentSchemaChangePublishesItsCookieAfterTheCatalogCommit()
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        var before = Convert.ToInt64(Scalar(connection, "PRAGMA schema_version;"));
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("CREATE INDEX ix_t_v ON t(v);");
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(connection, "PRAGMA schema_version;")).Should().Be(before + 1);
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        Convert.ToInt64(Scalar(
            connection,
            "SELECT v FROM t INDEXED BY ix_t_v WHERE v = 1;")).Should().Be(1L);
    }

    [Test]
    public void ConcurrentSchemaChangeFailsBusyWhileAPeerSnapshotIsOpen()
    {
        using var db = new RoutingFileDatabase();
        using var writer = db.Connect();
        using var schemaWriter = db.Connect();

        writer.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");

        var error = Capture(() => schemaWriter.ExecuteNonQuery("CREATE INDEX ix_t_v ON t(v);"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("locked");

        schemaWriter.ExecuteNonQuery("ROLLBACK;");
        writer.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ReadOnlyPeerSurvivesRejectedDdlBeforeTheNextSchemaGenerationCommits()
    {
        using var db = new RoutingFileDatabase();
        using var reader = db.Connect();
        using var schemaWriter = db.Connect();

        reader.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        reader.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        var rejected = Capture(
            () => schemaWriter.ExecuteNonQuery("CREATE INDEX ix_t_v ON t(v);"));
        rejected.Should().NotBeNull();
        rejected!.Message.Should().ContainEquivalentOf("locked");
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t;")).Should().Be(0L);

        schemaWriter.ExecuteNonQuery("ROLLBACK;");
        reader.ExecuteNonQuery("COMMIT;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("CREATE INDEX ix_t_v ON t(v);");
        schemaWriter.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(
            reader,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_t_v';")).Should().Be(1L);
    }

    [Test]
    public void WritePeerSurvivesRejectedDdlAndPublishesBeforeTheSchemaOwnerRetries()
    {
        using var db = new RoutingFileDatabase();
        using var writer = db.Connect();
        using var schemaWriter = db.Connect();

        writer.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("INSERT INTO t VALUES (41);");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        var rejected = Capture(
            () => schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);"));
        rejected.Should().NotBeNull();
        rejected!.Message.Should().ContainEquivalentOf("locked");

        schemaWriter.ExecuteNonQuery("ROLLBACK;");
        writer.ExecuteNonQuery("COMMIT;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);");
        schemaWriter.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(writer, "SELECT v FROM t;")).Should().Be(41L);
        Convert.ToInt64(Scalar(
            writer,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'added';")).Should().Be(1L);
    }

    [TestCase("IMMEDIATE")]
    [TestCase("EXCLUSIVE")]
    public void SchemaOwnerBlocksClassicMvccWriterAdmission(string mode)
    {
        using var db = new RoutingFileDatabase();
        using var schemaOwner = db.Connect();
        using var peer = db.Connect();

        schemaOwner.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        schemaOwner.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaOwner.ExecuteNonQuery("CREATE TABLE pending(v INTEGER);");

        var rejected = Capture(() => peer.ExecuteNonQuery($"BEGIN {mode};"));
        rejected.Should().NotBeNull();
        rejected!.Message.Should().ContainEquivalentOf("locked");

        schemaOwner.ExecuteNonQuery("ROLLBACK;");
        peer.ExecuteNonQuery($"BEGIN {mode};");
        peer.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void DeferredClassicWriterBlocksConcurrentSchemaPublicationUntilCommit()
    {
        using var db = new RoutingFileDatabase();
        using var classicWriter = db.Connect();
        using var schemaWriter = db.Connect();

        classicWriter.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        classicWriter.ExecuteNonQuery("BEGIN;");
        classicWriter.ExecuteNonQuery("INSERT INTO t VALUES (99);");

        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        var rejected = Capture(
            () => schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);"));
        rejected.Should().NotBeNull();
        rejected!.Message.Should().ContainEquivalentOf("locked");
        schemaWriter.ExecuteNonQuery("ROLLBACK;");

        classicWriter.ExecuteNonQuery("COMMIT;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);");
        schemaWriter.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(classicWriter, "SELECT COUNT(*) FROM t WHERE v=99;"))
            .Should().Be(1L);
    }

    [Test]
    public void SchemaOwnerBlocksAutocommitClassicWritesUntilPublication()
    {
        using var db = new RoutingFileDatabase();
        using var schemaWriter = db.Connect();
        using var classicWriter = db.Connect();

        schemaWriter.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);");

        var rejected = Capture(() => classicWriter.ExecuteNonQuery("INSERT INTO t VALUES (99);"));
        rejected.Should().NotBeNull();
        rejected!.Message.Should().ContainEquivalentOf("locked");

        schemaWriter.ExecuteNonQuery("COMMIT;");
        classicWriter.ExecuteNonQuery("INSERT INTO t VALUES (99);");
        Convert.ToInt64(Scalar(classicWriter, "SELECT COUNT(*) FROM t WHERE v=99;"))
            .Should().Be(1L);
    }

    [Test]
    public async Task ClassicWriterWaitsForSchemaPublicationWithinBusyTimeout()
    {
        using var db = new RoutingFileDatabase();
        using var schemaWriter = db.Connect();
        using var classicWriter = db.Connect();

        schemaWriter.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        classicWriter.ExecuteNonQuery("PRAGMA busy_timeout=2000;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);");

        var write = Task.Run(() => classicWriter.ExecuteNonQuery("INSERT INTO t VALUES (99);"));
        await Task.Delay(100);
        write.IsCompleted.Should().BeFalse();

        schemaWriter.ExecuteNonQuery("COMMIT;");
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        Convert.ToInt64(Scalar(classicWriter, "SELECT COUNT(*) FROM t WHERE v=99;"))
            .Should().Be(1L);
    }

    [Test]
    public async Task SchemaPublicationWaitsForDeferredClassicWriterWithinBusyTimeout()
    {
        using var db = new RoutingFileDatabase();
        using var classicWriter = db.Connect();
        using var schemaWriter = db.Connect();

        classicWriter.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        schemaWriter.ExecuteNonQuery("PRAGMA busy_timeout=2000;");
        classicWriter.ExecuteNonQuery("BEGIN;");
        classicWriter.ExecuteNonQuery("INSERT INTO t VALUES (99);");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");

        var schemaChange = Task.Run(
            () => Capture(
                () => schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);")));
        await Task.Delay(100);
        schemaChange.IsCompleted.Should().BeFalse();

        classicWriter.ExecuteNonQuery("COMMIT;");
        var staleSchema = await schemaChange.WaitAsync(TimeSpan.FromSeconds(5));
        staleSchema.Should().NotBeNull();
        staleSchema!.Message.Should().ContainEquivalentOf("locked");
        schemaWriter.ExecuteNonQuery("ROLLBACK;");
        schemaWriter.ExecuteNonQuery("BEGIN CONCURRENT;");
        schemaWriter.ExecuteNonQuery("CREATE TABLE added(v INTEGER);");
        schemaWriter.ExecuteNonQuery("COMMIT;");
        Convert.ToInt64(Scalar(schemaWriter, "SELECT COUNT(*) FROM t WHERE v=99;"))
            .Should().Be(1L);
    }

    [Test]
    public async Task MvccBeginRegistersBeforeCatalogCloneCanRaceSchemaPublication()
    {
        var fileSystem = new Ahtola.Core.Storage.InMemoryFileSystem();
        const string path = "mvcc-begin-schema-race.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reader = database.Connect();
        using var schemaWriter = database.Connect();
        Execute(reader, "CREATE TABLE t(v INTEGER);");
        Execute(reader, "PRAGMA journal_mode=mvcc;");
        Exception? rejected = null;
        var snapshotGateHeld = false;
        Task? schemaAttempt = null;
        using var peerStarted = new ManualResetEventSlim();

        try
        {
            EmbeddedConnection.AfterMvccBeginBeforeCatalogSnapshotForTesting = () =>
            {
                EmbeddedConnection.AfterMvccBeginBeforeCatalogSnapshotForTesting = null;
                snapshotGateHeld = database.IsTransactionSnapshotGateHeldForTesting;
                schemaAttempt = Task.Run(() =>
                {
                    peerStarted.Set();
                    Execute(schemaWriter, "BEGIN CONCURRENT;");
                    rejected = Capture(
                        () => Execute(schemaWriter, "CREATE TABLE raced(v INTEGER);"));
                    Execute(schemaWriter, "ROLLBACK;");
                });
                peerStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                Thread.Sleep(100);
            };
            Execute(reader, "BEGIN CONCURRENT;");
        }
        finally
        {
            EmbeddedConnection.AfterMvccBeginBeforeCatalogSnapshotForTesting = null;
        }

        await schemaAttempt!.WaitAsync(TimeSpan.FromSeconds(5));
        snapshotGateHeld.Should().BeTrue();
        rejected.Should().NotBeNull();
        rejected!.Message.Should().ContainEquivalentOf("locked");
        ReadEmbeddedScalar(
            reader,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'raced';").Should().Be(0L);
        Execute(reader, "ROLLBACK;");
    }

    [Test]
    public async Task MvccBeginPinsItsCatalogBeforeAPeerCheckpointCanPublish()
    {
        var fileSystem = new Ahtola.Core.Storage.InMemoryFileSystem();
        const string path = "mvcc-begin-checkpoint-race.db";
        using var readerDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reader = readerDatabase.Connect();
        Execute(reader, "CREATE TABLE t(v INTEGER);");
        Execute(reader, "PRAGMA journal_mode=mvcc;");
        using var peerDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var peer = peerDatabase.Connect();
        Task? peerCheckpoint = null;
        using var peerStarted = new ManualResetEventSlim();

        try
        {
            EmbeddedConnection.AfterMvccBeginBeforeCatalogSnapshotForTesting = () =>
            {
                EmbeddedConnection.AfterMvccBeginBeforeCatalogSnapshotForTesting = null;
                peerCheckpoint = Task.Run(() =>
                {
                    peerStarted.Set();
                    Execute(peer, "BEGIN CONCURRENT;");
                    Execute(peer, "INSERT INTO t VALUES (42);");
                    Execute(peer, "COMMIT;");
                    Execute(peer, "PRAGMA wal_checkpoint(TRUNCATE);");
                });
                peerStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                Thread.Sleep(100);
            };

            Execute(reader, "BEGIN CONCURRENT;");
        }
        finally
        {
            EmbeddedConnection.AfterMvccBeginBeforeCatalogSnapshotForTesting = null;
        }

        await peerCheckpoint!.WaitAsync(TimeSpan.FromSeconds(5));
        ReadEmbeddedScalar(reader, "SELECT COUNT(*) FROM t WHERE v=42;").Should().Be(0L);
        Execute(reader, "ROLLBACK;");
    }

    [Test]
    public void FailedOrSavepointRolledBackConcurrentDdlReleasesTheSchemaGate()
    {
        using var db = new RoutingFileDatabase();
        using var writer = db.Connect();
        using var peer = db.Connect();

        writer.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        var failure = Capture(() => writer.ExecuteNonQuery("CREATE INDEX ix_bad ON t(no_such_column);"));
        failure.Should().NotBeNull();

        peer.ExecuteNonQuery("BEGIN CONCURRENT;");
        peer.ExecuteNonQuery("ROLLBACK;");

        writer.ExecuteNonQuery("SAVEPOINT ddl;");
        writer.ExecuteNonQuery("CREATE INDEX ix_t_v ON t(v);");
        writer.ExecuteNonQuery("ROLLBACK TO ddl;");

        peer.ExecuteNonQuery("BEGIN CONCURRENT;");
        peer.ExecuteNonQuery("ROLLBACK;");
        writer.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ConcurrentDdlRejectsATransactionWhoseDataSnapshotIsStale()
    {
        using var db = new RoutingFileDatabase();
        using var stale = db.Connect();
        using var writer = db.Connect();

        stale.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        stale.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        writer.ExecuteNonQuery("COMMIT;");

        var error = Capture(() => stale.ExecuteNonQuery("CREATE INDEX ix_t_v ON t(v);"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("locked");
        stale.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ConcurrentDropAndRecreateRetiresThePriorTableIdentity()
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        connection.ExecuteNonQuery("COMMIT;");

        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("DROP TABLE t;");
        connection.ExecuteNonQuery("CREATE TABLE t(v INTEGER);");
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(0L);
        connection.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        Convert.ToInt64(Scalar(connection, "SELECT v FROM t;")).Should().Be(2L);
    }

    [Test]
    public void AbortedConcurrentTriggerStatementRollsBackItsMvccOverlay()
    {
        using var db = new RoutingFileDatabase(
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER);
            CREATE TRIGGER abort_insert AFTER INSERT ON t WHEN NEW.id = 1 BEGIN
                SELECT RAISE(ABORT, 'abort-trigger');
            END;
            """);
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        var error = Capture(() => connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 1);"));
        error.Should().NotBeNull();

        connection.ExecuteNonQuery("INSERT INTO t VALUES (2, 2);");
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
        Convert.ToInt64(Scalar(connection, "SELECT id FROM t;")).Should().Be(2L);
    }

    [Test]
    public void ConcurrentInsertAgainstWithoutRowidTableIsVisibleToWriterAndSurvivesCommit()
    {
        using var db = new RoutingFileDatabase(
            "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID;");
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 'insert');");
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
        Convert.ToString(Scalar(connection, "SELECT value FROM t WHERE id = 1;")).Should().Be("insert");
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToString(Scalar(connection, "SELECT value FROM t WHERE id = 1;")).Should().Be("insert");
    }

    [Test]
    public void ConcurrentUpdateAndDeleteAgainstWithoutRowidTableUseThePrimaryKeyIdentity()
    {
        using var db = new RoutingFileDatabase(
            "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID;");
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 'before');");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (2, 'stable');");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        // Move the first key after an untouched key so clustered primary-key
        // sorting cannot change the row identity reported to MVCC.
        connection.ExecuteNonQuery("UPDATE t SET id = 3, value = 'after' WHERE id = 1;");
        Convert.ToString(Scalar(connection, "SELECT value FROM t WHERE id = 3;")).Should().Be("after");
        connection.ExecuteNonQuery("COMMIT;");

        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("DELETE FROM t WHERE id = 3;");
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
        connection.ExecuteNonQuery("COMMIT;");
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
    }

    [Test]
    public void ConcurrentDuplicateWithoutRowidInsertRaisesAWriteConflict()
    {
        using var db = new RoutingFileDatabase(
            "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID;");
        using var first = db.Connect();
        using var second = db.Connect();

        first.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        first.ExecuteNonQuery("BEGIN CONCURRENT;");
        second.ExecuteNonQuery("BEGIN CONCURRENT;");
        first.ExecuteNonQuery("INSERT INTO t VALUES (1, 'first');");

        var error = Capture(() => second.ExecuteNonQuery("INSERT INTO t VALUES (1, 'second');"));
        error.Should().NotBeNull();
        error!.Message.Should().ContainEquivalentOf("write-write conflict");
        second.ExecuteNonQuery("ROLLBACK;");
        first.ExecuteNonQuery("COMMIT;");

        Convert.ToString(Scalar(first, "SELECT value FROM t WHERE id = 1;")).Should().Be("first");
    }

    [Test]
    public void ConcurrentRowTriggerDmlAgainstWithoutRowidTableIsVersioned()
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();
        connection.ExecuteNonQuery(
            """
            CREATE TABLE sink(id INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID;
            CREATE TRIGGER t_after_insert AFTER INSERT ON t BEGIN
                INSERT INTO sink VALUES (NEW.v, 'trigger');
                UPDATE sink SET id = id + 10 WHERE id = NEW.v;
            END;
            """);

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");

        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sink;")).Should().Be(1L);
        Convert.ToInt64(Scalar(connection, "SELECT id FROM sink;")).Should().Be(11L);
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sink;")).Should().Be(1L);
        Convert.ToInt64(Scalar(connection, "SELECT id FROM sink;")).Should().Be(11L);
    }

    [Test]
    public void ConcurrentForeignKeyCascadeAgainstWithoutRowidTableUsesPrimaryKeyIdentity()
    {
        using var db = new RoutingFileDatabase();
        using var connection = db.Connect();
        connection.ExecuteNonQuery(
            """
            PRAGMA foreign_keys=ON;
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(
                id INTEGER PRIMARY KEY,
                parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE
            ) WITHOUT ROWID;
            INSERT INTO parent VALUES (1);
            INSERT INTO child VALUES (1, 1);
            """);

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");

        connection.ExecuteNonQuery("DELETE FROM parent WHERE id = 1;");
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM parent;")).Should().Be(0L);
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM child;")).Should().Be(0L);
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM parent;")).Should().Be(0L);
        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM child;")).Should().Be(0L);
    }

    [Test]
    public void FailedConcurrentDdlCommitDoesNotPublishItsSchemaGenerationOrRows()
    {
        var faults = new Ahtola.Core.Storage.DeterministicFaultInjector();
        var fileSystem = new Ahtola.Core.Storage.InMemoryFileSystem(faults);
        const string path = "mvcc-schema-publication-fault.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE original(v INTEGER);");
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO original VALUES (1);");
        Execute(connection, "COMMIT;");

        var cookie = ReadEmbeddedScalar(connection, "PRAGMA schema_version;");
        var generation = database.MvStore!.SchemaGeneration;
        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO original VALUES (2);");
        Execute(connection, "CREATE TABLE discarded(v INTEGER);");
        Execute(connection, "INSERT INTO discarded VALUES (99);");

        faults.FailNext(Ahtola.Core.Storage.FileSystemOperation.Write);
        Assert.Throws<IOException>(() => Execute(connection, "COMMIT;"));

        ReadEmbeddedScalar(connection, "PRAGMA schema_version;").Should().Be(cookie);
        database.MvStore!.SchemaGeneration.Should().Be(generation);
        ReadEmbeddedScalar(connection, "SELECT COUNT(*) FROM original;").Should().Be(1L);
        ReadEmbeddedScalar(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'discarded';").Should().Be(0L);

        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "CREATE TABLE committed(v INTEGER);");
        Execute(connection, "COMMIT;");
        ReadEmbeddedScalar(connection, "PRAGMA schema_version;").Should().Be(cookie + 1);
        database.MvStore!.SchemaGeneration.Should().Be(generation + 1);
    }

    [Test]
    public void ConcurrentDmlAndDdlCommitAsOnePagerPublishedSchemaGeneration()
    {
        var fileSystem = new Ahtola.Core.Storage.InMemoryFileSystem();
        const string path = "mvcc-schema-and-dml-commit.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE original(v INTEGER);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            var generation = database.MvStore!.SchemaGeneration;

            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "INSERT INTO original VALUES (1);");
            Execute(connection, "CREATE TABLE added(v INTEGER);");
            Execute(connection, "INSERT INTO added VALUES (2);");
            Execute(connection, "COMMIT;");

            database.MvStore!.SchemaGeneration.Should().Be(generation + 1);
            ReadEmbeddedScalar(connection, "SELECT v FROM original;").Should().Be(1L);
            ReadEmbeddedScalar(connection, "SELECT v FROM added;").Should().Be(2L);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadEmbeddedScalar(reopenedConnection, "SELECT v FROM original;").Should().Be(1L);
        ReadEmbeddedScalar(reopenedConnection, "SELECT v FROM added;").Should().Be(2L);
    }

    [Test]
    public void ConcurrentLimitOneAllocationDoesNotScaleWithTheBaseCatalog()
    {
        const int largeRowCount = 10_000;
        var smallAllocation = MeasureConcurrentLimitOneAllocation(rowCount: 10);
        var largeAllocation = MeasureConcurrentLimitOneAllocation(largeRowCount);

        largeAllocation.Should().BeLessThan(
            smallAllocation + 256 * 1024,
            "a warmed LIMIT 1 scan should retain only the lazy cursor peeks and consumed row");
    }

    [Test]
    public void ConcurrentIndexLimitOneAllocationDoesNotScaleWithTheBaseCatalog()
    {
        const int largeRowCount = 10_000;
        var smallAllocation = MeasureConcurrentIndexLimitOneAllocation(rowCount: 10);
        var largeAllocation = MeasureConcurrentIndexLimitOneAllocation(largeRowCount);

        largeAllocation.Should().BeLessThan(
            smallAllocation + 256 * 1024,
            "a warmed index LIMIT 1 scan should not rebuild a materialized MVCC overlay");
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ReadValue(SqliteConnection connection, string sql)
        => Convert.ToString(Scalar(connection, sql)) ?? string.Empty;

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception) when (exception is SqliteException or EmbeddedSqlException)
        {
            return exception;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        _ = statement.Step();
    }

    private static long ReadEmbeddedScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long MeasureConcurrentLimitOneAllocation(int rowCount)
    {
        var fileSystem = new Ahtola.Core.Storage.InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"mvcc-limit-{rowCount}.db", fileSystem);
        using var connection = database.Connect();
        using (var create = connection.Prepare("CREATE TABLE t(v INTEGER);"))
            _ = create.Step();

        var values = new System.Text.StringBuilder("INSERT INTO t VALUES ");
        for (var value = 1; value <= rowCount; value++)
        {
            if (value != 1)
                values.Append(',');
            values.Append('(').Append(value).Append(')');
        }
        values.Append(';');
        using (var seed = connection.Prepare(values.ToString()))
            _ = seed.Step();
        using (var mode = connection.Prepare("PRAGMA journal_mode=mvcc;"))
            _ = mode.Step();
        using (var begin = connection.Prepare("BEGIN CONCURRENT;"))
            _ = begin.Step();

        // Build and cache the rowid scan order before measuring statement execution.
        ExecuteLimitOne(connection);
        ExecuteLimitOne(connection);

        var before = GC.GetAllocatedBytesForCurrentThread();
        ExecuteLimitOne(connection);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (var rollback = connection.Prepare("ROLLBACK;"))
            _ = rollback.Step();
        return allocated;
    }

    private static long MeasureConcurrentIndexLimitOneAllocation(int rowCount)
    {
        var fileSystem = new Ahtola.Core.Storage.InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"mvcc-index-limit-{rowCount}.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "CREATE INDEX ix_t_v ON t(v);");

        var values = new System.Text.StringBuilder("INSERT INTO t VALUES ");
        for (var value = rowCount; value >= 1; value--)
        {
            if (value != rowCount)
                values.Append(',');
            values.Append('(').Append(value).Append(')');
        }
        values.Append(';');
        Execute(connection, values.ToString());
        Execute(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "BEGIN CONCURRENT;");

        ExecuteIndexLimitOne(connection);
        ExecuteIndexLimitOne(connection);

        var before = GC.GetAllocatedBytesForCurrentThread();
        ExecuteIndexLimitOne(connection);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Execute(connection, "ROLLBACK;");
        return allocated;
    }

    private static void ExecuteLimitOne(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT v FROM t LIMIT 1;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(1L);
    }

    private static void ExecuteIndexLimitOne(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare(
            "SELECT v FROM t INDEXED BY ix_t_v WHERE v > 0 LIMIT 1;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(1L);
    }

    private sealed class RoutingFileDatabase : IDisposable
    {
        private readonly List<SqliteConnection> _connections = [];

        public RoutingFileDatabase(string schema = "CREATE TABLE t(v INTEGER);")
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Ahtola-mvcc-routing-{Guid.NewGuid():N}.db");

            using var seed = new SqliteConnection($"Data Source={Path};Local Provider=Managed");
            seed.Open();
            seed.ExecuteNonQuery(schema);
        }

        public string Path { get; }

        public SqliteConnection Connect()
        {
            var connection = new SqliteConnection($"Data Source={Path};Local Provider=Managed;Default Timeout=1");
            connection.Open();
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();

            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
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
