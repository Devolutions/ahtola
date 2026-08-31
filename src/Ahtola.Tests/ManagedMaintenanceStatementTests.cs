using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using System.Reflection;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedMaintenanceStatementTests
{
    private const string EncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void CatalogAndStoragePragmasMatchSqliteAcrossMainTempAndAttachedSchemas()
    {
        var mainPath = CreateDatabasePath("pragma-main");
        var attachedPath = CreateDatabasePath("pragma-attached");
        try
        {
            Dictionary<string, string[]> expected;
            using (var database = EmbeddedDatabase.OpenFile(mainPath))
            using (var connection = database.Connect())
            {
                Execute(connection, CreateMainCatalogSql);
                Execute(connection, CreateTempCatalogSql);
                Execute(
                    connection,
                    $"ATTACH '{EscapeSqlLiteral(attachedPath)}' AS aux;"
                    + CreateAttachedCatalogSql);

                var indexList = ReadRows(connection, "PRAGMA index_list(wr);");
                var primaryKeyIndex = indexList.Single(row => row[3].AsText() == "pk")[1].AsText();
                var uniqueIndex = indexList.Single(row => row[3].AsText() == "u")[1].AsText();
                var queries = BuildDifferentialPragmaQueries(primaryKeyIndex, uniqueIndex);
                expected = queries.ToDictionary(
                    query => query,
                    query => SerializeRows(ReadRows(connection, query)),
                    StringComparer.Ordinal);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={mainPath};Pooling=False");
            sqlite.Open();
            ExecuteNative(sqlite, CreateTempCatalogSql);
            ExecuteNative(sqlite, $"ATTACH '{EscapeSqlLiteral(attachedPath)}' AS aux;");

            foreach (var (query, rows) in expected)
            {
                SerializeRows(QueryNative(sqlite, query))
                    .Should()
                    .Equal(rows, $"the managed result for {query} should match SQLite");
            }
            ReadNativeText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(mainPath);
            DeleteDatabase(attachedPath);
        }
    }

    [Test]
    public void AnalyzeRefreshesTargetStatisticsAndRollsBackWithTheTransaction()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "analyze-statistics.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(
            connection,
            """
            CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);
            CREATE INDEX data_value ON data(value);
            INSERT INTO data VALUES (1, 'one');
            INSERT INTO data VALUES (2, 'two');
            INSERT INTO data VALUES (3, 'two');
            CREATE TABLE unindexed(value TEXT);
            INSERT INTO unindexed VALUES ('first'), ('second');
            CREATE TABLE empty(value TEXT);
            CREATE TABLE composite(a INTEGER, b INTEGER);
            CREATE INDEX composite_ab ON composite(a, b);
            INSERT INTO composite VALUES (1, 1), (1, 2), (2, 1), (2, 1);
            CREATE TABLE partial(value TEXT);
            CREATE INDEX partial_value ON partial(value) WHERE value = 'include';
            INSERT INTO partial VALUES ('include'), ('include'), ('exclude');
            """);

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        foreach (var sql in new[]
                 {
                     "ANALYZE;",
                     "ANALYZE main;",
                     "ANALYZE data;",
                     "ANALYZE 'data';",
                     "ANALYZE main.data;",
                     "ANALYZE data_value;",
                 })
        {
            Execute(connection, sql);
            SerializeRows(ReadRows(
                    connection,
                    "SELECT tbl, idx, stat FROM sqlite_stat1 WHERE tbl = 'data' ORDER BY rowid;"))
                .Should()
                .Equal(["T:data|T:data_value|T:3 2"]);
        }

        ReadText(connection, "SELECT stat FROM sqlite_stat1 WHERE tbl = 'unindexed' AND idx IS NULL;")
            .Should().Be("2");
        ReadInteger(connection, "SELECT COUNT(*) FROM sqlite_stat1 WHERE tbl = 'empty';").Should().Be(0);
        ReadText(connection, "SELECT stat FROM sqlite_stat1 WHERE idx = 'composite_ab';").Should().Be("4 2 2");
        ReadText(connection, "SELECT stat FROM sqlite_stat1 WHERE tbl = 'partial' AND idx IS NULL;")
            .Should().Be("3");
        ReadText(connection, "SELECT stat FROM sqlite_stat1 WHERE idx = 'partial_value';").Should().Be("2 2");
        faults.GetOperationCount(FileSystemOperation.Write).Should().BeGreaterThan(writesBefore);
        ReadInteger(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_stat1';")
            .Should().Be(1);

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO data VALUES (4, 'three');");
        Execute(connection, "ANALYZE data;");
        ReadText(connection, "SELECT stat FROM sqlite_stat1 WHERE idx = 'data_value';").Should().Be("4 2");
        Execute(connection, "ROLLBACK;");
        ReadInteger(connection, "SELECT COUNT(*) FROM data;").Should().Be(3);
        ReadText(connection, "SELECT stat FROM sqlite_stat1 WHERE idx = 'data_value';").Should().Be("3 2");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ANALYZE missing;"))!
            .Message.Should().Be("no such table or index: missing");
    }

    [Test]
    public void AnalyzeStatisticsSurviveFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "analyze-reopen.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX data_value ON data(value);
                INSERT INTO data VALUES (1, 'one'), (2, 'two'), (3, 'two');
                ANALYZE data;
                """);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        SerializeRows(ReadRows(reopenedConnection, "SELECT tbl, idx, stat FROM sqlite_stat1 ORDER BY rowid;"))
            .Should()
            .Equal(["T:data|T:data_value|T:3 2"]);
    }

    [Test]
    public void AnalyzeRoutesAttachedDatabaseAndQualifiedTargets()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("analyze-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH DATABASE 'analyze-aux.db' AS aux;");
        Execute(
            connection,
            """
            CREATE TABLE aux.data(id INTEGER PRIMARY KEY, value TEXT);
            CREATE INDEX aux.data_value ON data(value);
            INSERT INTO aux.data VALUES (1, 'one'), (2, 'two'), (3, 'two');
            ANALYZE aux;
            """);

        ReadText(connection, "SELECT stat FROM aux.sqlite_stat1 WHERE idx = 'data_value';")
            .Should().Be("3 2");
        Execute(connection, "ANALYZE aux.data;");
        ReadText(connection, "SELECT stat FROM aux.sqlite_stat1 WHERE idx = 'data_value';")
            .Should().Be("3 2");
    }

    [Test]
    public void WithoutRowidPrimaryIndexMetadataTracksRenameAndReindexTargets()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "reindex-renamed-without-rowid.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(
            connection,
            """
            CREATE TABLE original(
                tenant TEXT,
                sequence INTEGER,
                PRIMARY KEY(tenant, sequence DESC)
            ) WITHOUT ROWID, STRICT;
            ALTER TABLE original RENAME TO renamed;
            """);

        var primaryKeyIndex = ReadRows(connection, "PRAGMA index_list(renamed);")
            .Single(row => row[3].AsText() == "pk")[1]
            .AsText();
        primaryKeyIndex.Should().Be("sqlite_autoindex_renamed_1");
        ReadRows(connection, "PRAGMA index_info(renamed);").Should().HaveCount(2);
        Execute(connection, $"REINDEX {primaryKeyIndex};");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "REINDEX sqlite_autoindex_original_1;"))!
            .Message.Should().Be("unable to identify the object to be reindexed");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReindexRepairsRichCatalogsWithoutChangingSchemaOrAutoincrement(bool deleteJournal)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = deleteJournal ? "reindex-delete.db" : "reindex-wal.db";
        var attachedPath = deleteJournal ? "reindex-attached-delete.db" : "reindex-attached-wal.db";
        long schemaVersion;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, CreateReindexCatalogSql);
            if (deleteJournal)
                ReadText(connection, "PRAGMA journal_mode=DELETE;").Should().Be("delete");

            schemaVersion = ReadInteger(connection, "PRAGMA schema_version;");
            CorruptIndexRoot(database, fileSystem, path, "items_active");
            Execute(connection, "BEGIN;");
            Execute(connection, "REINDEX items_active;");
            Execute(connection, "COMMIT;");
            AssertIndexRootValid(database, fileSystem, path, "items_active");
            VerifyReindexedCatalog(fileSystem, path);
            ReadInteger(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);

            CorruptIndexRoot(database, fileSystem, path, "items_active");
            Execute(connection, "SAVEPOINT reindex_state;");
            Execute(connection, "REINDEX items_active;");
            Execute(connection, "ROLLBACK TO reindex_state;");
            Execute(connection, "RELEASE reindex_state;");
            AssertIndexRootCorrupted(database, fileSystem, path, "items_active");
            Execute(connection, "REINDEX main.items_active;");
            AssertIndexRootValid(database, fileSystem, path, "items_active");
            VerifyReindexedCatalog(fileSystem, path);
            ReadInteger(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);

            Execute(connection, "REINDEX 'items_active';");
            Execute(connection, "REINDEX main.items;");
            Execute(connection, "REINDEX keyed;");
            var primaryKeyIndex = ReadRows(connection, "PRAGMA index_list(keyed);")
                .Single(row => row[3].AsText() == "pk")[1]
                .AsText();
            Execute(connection, $"REINDEX {primaryKeyIndex};");

            Execute(
                connection,
                $"ATTACH '{EscapeSqlLiteral(attachedPath)}' AS aux;"
                + """
                CREATE TABLE aux.entries(id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX aux.entries_expr ON entries(lower(value)) WHERE id > 0;
                INSERT INTO aux.entries VALUES (1, 'attached');
                """);
            // Bare REINDEX walks temp/main/attached (Turso collect_all_reindex_targets).
            Execute(connection, "REINDEX;");
            Execute(connection, "REINDEX aux.entries_expr;");
            Execute(connection, "DETACH aux;");

            Execute(connection, "REINDEX NOCASE;");
            Execute(connection, "REINDEX;");
            Execute(connection, "VACUUM;");
            Execute(connection, "INSERT INTO items(code, label, score) VALUES ('p1', 'after', 1);");
            ReadInteger(connection, "SELECT id FROM items WHERE label = 'after';").Should().Be(101);
            ReadInteger(connection, "PRAGMA schema_version;").Should().Be(schemaVersion + 1);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadInteger(
                reopenedConnection,
                """
                SELECT COUNT(*) FROM items
                WHERE score > 0 AND normalized = lower(label);
                """)
            .Should().Be(25);
        ReadInteger(
                reopenedConnection,
                "SELECT seq FROM sqlite_sequence WHERE name = 'items';")
            .Should().Be(101);
        ReadInteger(reopenedConnection, "PRAGMA schema_version;").Should().Be(schemaVersion + 1);
    }

    public enum ReindexCoexistingDml
    {
        Insert,
        Update,
        Delete,
    }

    /// <summary>
    /// A targeted, in-place REINDEX (<c>EmbeddedFileStore.TryPersistTargetedIndexRebuild</c>)
    /// only ever rewrites the selected index's own b-tree; it never re-derives or rewrites any
    /// table's row storage. If it were persisted as the sole durable mutation whenever it shared
    /// a transaction with row-level DML, the DML would apply correctly to the in-memory catalog
    /// but never reach disk, so it would silently vanish the moment the store is reopened even
    /// though the rebuilt index still (correctly) reflects the in-memory row. Commit must widen
    /// to the full-catalog rewrite whenever anything besides the targeted rebuild changed.
    /// </summary>
    [TestCase(ReindexCoexistingDml.Insert)]
    [TestCase(ReindexCoexistingDml.Update)]
    [TestCase(ReindexCoexistingDml.Delete)]
    public void TargetedReindexPersistsCoexistingDmlInTheSameTransactionAtomically(ReindexCoexistingDml dml)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = $"reindex-with-{dml.ToString().ToLowerInvariant()}.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX data_value ON data(value);
                INSERT INTO data VALUES (1, 'one'), (2, 'two'), (3, 'three');
                """);

            Execute(connection, "BEGIN;");
            switch (dml)
            {
                case ReindexCoexistingDml.Insert:
                    Execute(connection, "INSERT INTO data VALUES (4, 'four');");
                    break;
                case ReindexCoexistingDml.Update:
                    Execute(connection, "UPDATE data SET value = 'deux' WHERE id = 2;");
                    break;
                case ReindexCoexistingDml.Delete:
                    Execute(connection, "DELETE FROM data WHERE id = 3;");
                    break;
            }
            Execute(connection, "REINDEX data_value;");
            Execute(connection, "COMMIT;");

            AssertReindexCoexistingDmlApplied(connection, dml);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        AssertReindexCoexistingDmlApplied(reopenedConnection, dml);
        ReadText(reopenedConnection, "PRAGMA integrity_check;").Should().Be("ok");
    }

    private static void AssertReindexCoexistingDmlApplied(EmbeddedConnection connection, ReindexCoexistingDml dml)
    {
        switch (dml)
        {
            case ReindexCoexistingDml.Insert:
                ReadInteger(connection, "SELECT COUNT(*) FROM data;").Should().Be(4);
                ReadText(connection, "SELECT value FROM data WHERE id = 4;").Should().Be("four");
                // Index-driven lookup: proves the rebuilt data_value tree agrees with the
                // inserted row rather than only the in-memory table.
                ReadInteger(connection, "SELECT id FROM data WHERE value = 'four';").Should().Be(4);
                break;
            case ReindexCoexistingDml.Update:
                ReadInteger(connection, "SELECT COUNT(*) FROM data;").Should().Be(3);
                ReadText(connection, "SELECT value FROM data WHERE id = 2;").Should().Be("deux");
                ReadInteger(connection, "SELECT id FROM data WHERE value = 'deux';").Should().Be(2);
                ReadInteger(connection, "SELECT COUNT(*) FROM data WHERE value = 'two';").Should().Be(0);
                break;
            case ReindexCoexistingDml.Delete:
                ReadInteger(connection, "SELECT COUNT(*) FROM data;").Should().Be(2);
                ReadInteger(connection, "SELECT COUNT(*) FROM data WHERE id = 3;").Should().Be(0);
                ReadInteger(connection, "SELECT COUNT(*) FROM data WHERE value = 'three';").Should().Be(0);
                break;
        }
    }

    /// <summary>
    /// Distinct from <see cref="TargetedReindexPersistsCoexistingDmlInTheSameTransactionAtomically"/>
    /// above: that test proves the statement's normal-completion path
    /// (<c>transactionState.HasNonTargetedIndexRebuildChange</c> set right after
    /// <c>HasChanges = true</c> when a statement changed something other than a pure targeted
    /// index rebuild) widens commit off the targeted-rebuild-only persistence path. This test
    /// proves the same for an *exception* path that still preserves a statement's partial row
    /// changes: <c>EmbeddedConflictFailException</c> (ON CONFLICT FAIL). SQLite's OR FAIL
    /// semantics abort only the current statement, not the whole transaction, and do not roll
    /// back rows the statement already inserted before it hit the conflict -- so a multi-row
    /// <c>INSERT OR FAIL</c> that inserts row 2 before conflicting on row 3 still leaves row 2
    /// committed to this transaction. That preserved prefix is table DML, never a targeted index
    /// rebuild. If the catch handler only set <c>HasChanges</c> without also setting
    /// <c>HasNonTargetedIndexRebuildChange</c>, an earlier targeted <c>REINDEX</c> in the same
    /// transaction would leave commit believing the sole durable mutation was the targeted
    /// rebuild (<c>TargetedIndexRebuildNames</c> stays the only non-empty signal), so the
    /// preserved partial INSERT would be visible in-memory before commit but silently vanish the
    /// moment the store is reopened, because a targeted rebuild never rewrites table row storage.
    /// </summary>
    [Test]
    public void TargetedReindexPersistsConflictFailPreservedPrefixInTheSameTransactionAtomically()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "reindex-with-conflict-fail-prefix.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE);
                CREATE INDEX data_scratch ON data(id);
                INSERT INTO data VALUES (1, 'one');
                """);

            Execute(connection, "BEGIN;");
            Execute(connection, "REINDEX data_scratch;");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "INSERT OR FAIL INTO data VALUES (2, 'two'), (3, 'one');"));
            Execute(connection, "COMMIT;");

            // The failed statement's already-inserted row (id 2) is visible in-memory before
            // reopen -- this much would already pass even with the bug, since the bug only drops
            // the durable write on disk, not the in-memory catalog.
            ReadInteger(connection, "SELECT COUNT(*) FROM data;").Should().Be(2);
            ReadText(connection, "SELECT value FROM data WHERE id = 2;").Should().Be("two");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        // Without HasNonTargetedIndexRebuildChange on the EmbeddedConflictFailException path,
        // this reopen would only show the original seed row: the preserved partial INSERT
        // vanished because commit (incorrectly) took the targeted-index-rebuild-only persistence
        // path, which never rewrites table row storage.
        ReadInteger(reopenedConnection, "SELECT COUNT(*) FROM data;").Should().Be(2);
        ReadText(reopenedConnection, "SELECT value FROM data WHERE id = 2;").Should().Be("two");
        ReadInteger(reopenedConnection, "SELECT id FROM data WHERE id = 2;").Should().Be(2);
        ReadText(reopenedConnection, "PRAGMA integrity_check;").Should().Be("ok");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReindexWriteFailureRecoversWithoutPublishingAPartialTree(bool deleteJournal)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = deleteJournal ? "reindex-failure-delete.db" : "reindex-failure-wal.db";
        var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        var connection = database.Connect();
        try
        {
            Execute(
                connection,
                """
                CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);
                CREATE UNIQUE INDEX data_value ON data(lower(value), id DESC) WHERE id > 0;
                INSERT INTO data VALUES (1, 'one'), (2, 'two'), (3, 'three');
                """);
            if (deleteJournal)
                ReadText(connection, "PRAGMA journal_mode=DELETE;").Should().Be("delete");

            var mainFileBefore = ReadAllBytes(fileSystem, path);
            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "REINDEX missing_index;"))!
                .Message.Should().Be("unable to identify the object to be reindexed");
            faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
            Assert.Throws<IOException>(() => Execute(connection, "REINDEX data_value;"));
            ReadAllBytes(fileSystem, path).Should().Equal(mainFileBefore);
        }
        finally
        {
            connection.Dispose();
            database.Dispose();
        }

        faults.ClearScheduled();
        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadInteger(recoveredConnection, "SELECT COUNT(*) FROM data;").Should().Be(3);
        Execute(recoveredConnection, "REINDEX data_value;");
        Execute(recoveredConnection, "UPDATE data SET value = 'changed' WHERE id = 2;");
        ReadText(recoveredConnection, "SELECT value FROM data WHERE id = 2;").Should().Be("changed");
    }

    [Test]
    public void ReindexAndHeaderPragmasSurviveEncryptionAndPooledRefresh()
    {
        var inner = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        using var fileSystem = new AhtolaEncryptionFileSystem(inner, encryption);
        const string encryptedPath = "maintenance-encrypted.db";
        using (var database = EmbeddedDatabase.OpenFile(encryptedPath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX data_expr ON data(lower(value)) WHERE id > 0;
                INSERT INTO data VALUES (1, 'Encrypted');
                PRAGMA user_version=73;
                REINDEX data_expr;
                VACUUM;
                """);
        }
        using (var reopened = EmbeddedDatabase.OpenFile(encryptedPath, fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadInteger(connection, "PRAGMA user_version;").Should().Be(73);
            ReadText(connection, "SELECT value FROM data WHERE lower(value)='encrypted';")
                .Should().Be("Encrypted");
        }

        var pooledFileSystem = new InMemoryFileSystem();
        const string pooledPath = "maintenance-pooled.db";
        using var primaryDatabase = EmbeddedDatabase.OpenFile(pooledPath, pooledFileSystem);
        using var primary = primaryDatabase.Connect();
        Execute(primary, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(primary, "CREATE INDEX data_value ON data(value);");
        using var staleDatabase = EmbeddedDatabase.OpenFile(pooledPath, pooledFileSystem);
        using var stale = staleDatabase.Connect();

        Execute(primary, "PRAGMA user_version=88;");
        Execute(primary, "REINDEX data_value;");
        // Native parity: the sibling's fresh autocommit statements refresh the catalog
        // at statement start, so it observes primary's committed pragma without a manual
        // reset and the write succeeds against the latest committed view.
        ReadInteger(stale, "PRAGMA user_version;").Should().Be(88);
        Execute(stale, "INSERT INTO data VALUES (1, 'refreshed');");
        // An explicit pool reset still leaves the connection fully usable.
        stale.ResetForPooling();
        ReadInteger(stale, "PRAGMA user_version;").Should().Be(88);
        ReadText(stale, "SELECT value FROM data WHERE id = 1;").Should().Be("refreshed");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HeaderPragmaWriteFailureRecoversWithoutPublishingMetadata(bool deleteJournal)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = deleteJournal ? "header-failure-delete.db" : "header-failure-wal.db";
        var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        var connection = database.Connect();
        try
        {
            Execute(connection, "CREATE TABLE data(value INTEGER);");
            if (deleteJournal)
                ReadText(connection, "PRAGMA journal_mode=DELETE;").Should().Be("delete");

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() => Execute(connection, "PRAGMA user_version=91;"));
        }
        finally
        {
            connection.Dispose();
            database.Dispose();
        }

        faults.ClearScheduled();
        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadInteger(recoveredConnection, "PRAGMA user_version;").Should().Be(0);
        Execute(recoveredConnection, "PRAGMA user_version=91;");
        ReadInteger(recoveredConnection, "PRAGMA user_version;").Should().Be(91);
    }

    [Test]
    public async Task CanceledHeaderPragmaWaitingForTheDatabaseLockDoesNotCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "header-cancellation.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(value INTEGER);");

        var gate = typeof(EmbeddedDatabase)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(database)
            ?? throw new InvalidOperationException("Managed database gate was not found.");
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                gateHeld.Set();
                releaseGate.Wait();
            }
        });
        try
        {
            gateHeld.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            using var started = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var write = Task.Run(() =>
            {
                using var statement = connection.Prepare("PRAGMA user_version=91;");
                started.Set();
                return statement.Step(cancellation.Token);
            });
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            await Task.Delay(50);
            cancellation.Cancel();
            releaseGate.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.ThrowsAsync<OperationCanceledException>(async () => await write);
            ReadInteger(connection, "PRAGMA user_version;").Should().Be(0);
            using var verifier = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
            using var verificationConnection = verifier.Connect();
            ReadInteger(verificationConnection, "PRAGMA user_version;").Should().Be(0);
        }
        finally
        {
            releaseGate.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task ConcurrentHeaderAndSchemaUpdatesDoNotOverwriteEachOther()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "header-concurrency.db";
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var schemaConnection = database.Connect();
        using var headerConnection = database.Connect();
        Execute(schemaConnection, "CREATE TABLE seed(value INTEGER);");

        const int iterations = 32;
        for (var iteration = 1; iteration <= iterations; iteration++)
        {
            using var start = new Barrier(2);
            var tableName = $"schema_{iteration}";
            var userVersion = iteration;
            var schemaWrite = Task.Run(() =>
            {
                start.SignalAndWait();
                Execute(schemaConnection, $"CREATE TABLE {tableName}(value INTEGER);");
            });
            var headerWrite = Task.Run(() =>
            {
                start.SignalAndWait();
                Execute(headerConnection, $"PRAGMA user_version={userVersion};");
            });
            await Task.WhenAll(schemaWrite, headerWrite).WaitAsync(TimeSpan.FromSeconds(10));
        }

        ReadInteger(schemaConnection, "PRAGMA schema_version;").Should().Be(iterations + 1);
        ReadInteger(schemaConnection, "PRAGMA user_version;").Should().Be(iterations);
    }

    private static readonly string CreateMainCatalogSql =
        """
        CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT COLLATE NOCASE UNIQUE);
        CREATE TABLE wr(
            tenant TEXT COLLATE NOCASE,
            sequence INTEGER,
            code TEXT COLLATE NOCASE UNIQUE,
            doubled INTEGER GENERATED ALWAYS AS (sequence * 2) VIRTUAL,
            PRIMARY KEY(tenant, sequence DESC),
            FOREIGN KEY(code) REFERENCES parent(code) ON DELETE CASCADE
        ) WITHOUT ROWID, STRICT;
        CREATE INDEX wr_expr
            ON wr(lower(code) || ':' || sequence DESC)
            WHERE sequence > 0;
        CREATE TABLE indexed(id INTEGER PRIMARY KEY, value TEXT);
        CREATE INDEX metadata_collision ON indexed(value);
        """;

    private static readonly string CreateTempCatalogSql =
        """
        CREATE TEMP TABLE temp_strict(id INTEGER PRIMARY KEY, value ANY) STRICT;
        CREATE INDEX temp.temp_value ON temp_strict(value DESC);
        CREATE TEMP TABLE metadata_collision(value TEXT);
        CREATE TEMP TABLE temp_wr(
            tenant TEXT,
            sequence INTEGER,
            PRIMARY KEY(tenant, sequence DESC)
        ) WITHOUT ROWID, STRICT;
        """;

    private static readonly string CreateAttachedCatalogSql =
        """
        CREATE TABLE aux.attached(id INTEGER PRIMARY KEY, value TEXT UNIQUE);
        CREATE INDEX aux.attached_value ON attached(value DESC);
        CREATE TABLE aux.attached_wr(
            tenant TEXT,
            sequence INTEGER,
            PRIMARY KEY(tenant, sequence DESC)
        ) WITHOUT ROWID, STRICT;
        """;

    private static readonly string CreateReindexCatalogSql =
        """
        PRAGMA foreign_keys=ON;
        CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT UNIQUE);
        INSERT INTO parent VALUES (1, 'p0'), (2, 'p1');
        CREATE TABLE items(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT REFERENCES parent(code),
            label TEXT,
            score INTEGER,
            normalized TEXT GENERATED ALWAYS AS (lower(label)) VIRTUAL
        ) STRICT;
        CREATE UNIQUE INDEX items_active
            ON items(normalized COLLATE NOCASE DESC, score)
            WHERE score > 0;
        CREATE INDEX items_prefix
            ON items(lower(label) || ':' || length(code), code DESC);
        CREATE TABLE keyed(
            tenant TEXT,
            sequence INTEGER,
            value TEXT UNIQUE,
            PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC)
        ) WITHOUT ROWID, STRICT;
        CREATE INDEX keyed_expr
            ON keyed(lower(value), sequence DESC)
            WHERE sequence > 0;
        INSERT INTO items(code, label, score) VALUES
            ('p0', 'label-01', 1), ('p0', 'label-02', 2), ('p0', 'label-03', 3),
            ('p0', 'label-04', 4), ('p0', 'label-05', 5), ('p0', 'label-06', 6),
            ('p0', 'label-07', 7), ('p0', 'label-08', 8), ('p0', 'label-09', 9),
            ('p0', 'label-10', 10), ('p0', 'label-11', 11), ('p0', 'label-12', 12),
            ('p1', 'label-13', 13), ('p1', 'label-14', 14), ('p1', 'label-15', 15),
            ('p1', 'label-16', 16), ('p1', 'label-17', 17), ('p1', 'label-18', 18),
            ('p1', 'label-19', 19), ('p1', 'label-20', 20), ('p1', 'label-21', 21),
            ('p1', 'label-22', 22), ('p1', 'label-23', 23), ('p1', 'label-24', 24);
        INSERT INTO items(id, code, label, score) VALUES (100, 'p1', 'discarded', 100);
        DELETE FROM items WHERE id=100;
        INSERT INTO keyed VALUES
            ('tenant', 1, 'one'), ('Tenant', 2, 'two'), ('tenant', 3, 'three');
        """;

    private static string[] BuildDifferentialPragmaQueries(
        string primaryKeyIndex,
        string uniqueIndex)
        =>
        [
            "PRAGMA database_list;",
            "PRAGMA aux.database_list;",
            "PRAGMA table_list;",
            "PRAGMA table_info(wr);",
            "PRAGMA table_info('wr');",
            "PRAGMA table_xinfo(wr);",
            "PRAGMA index_list(wr);",
            "PRAGMA index_info(wr);",
            "PRAGMA index_xinfo(wr);",
            $"PRAGMA index_info({primaryKeyIndex});",
            $"PRAGMA index_xinfo({primaryKeyIndex});",
            $"PRAGMA index_info({uniqueIndex});",
            $"PRAGMA index_xinfo({uniqueIndex});",
            "PRAGMA index_info(wr_expr);",
            "PRAGMA index_xinfo(wr_expr);",
            "PRAGMA index_info(metadata_collision);",
            "PRAGMA index_xinfo(metadata_collision);",
            "PRAGMA main.index_info(metadata_collision);",
            "PRAGMA main.index_xinfo(metadata_collision);",
            "PRAGMA foreign_key_list(wr);",
            "PRAGMA foreign_key_check(wr);",
            "PRAGMA temp.table_info(temp_strict);",
            "PRAGMA temp.index_xinfo(temp_value);",
            "PRAGMA index_info(temp_wr);",
            "PRAGMA index_xinfo(temp_wr);",
            "PRAGMA aux.table_info(attached);",
            "PRAGMA aux.index_list(attached);",
            "PRAGMA aux.index_xinfo(attached_value);",
            "PRAGMA index_info(attached_wr);",
            "PRAGMA index_xinfo(attached_wr);",
            "PRAGMA encoding;",
            "PRAGMA page_count;",
            "PRAGMA freelist_count;",
            "PRAGMA aux.page_count;",
            "PRAGMA aux.freelist_count;",
        ];

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static long ReadInteger(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsInteger();

    private static string ReadText(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsText();

    private static string[] SerializeRows(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => string.Join("|", row.Select(Serialize))).Order().ToArray();

    private static string Serialize(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "N:",
            SqlValueKind.Integer => $"I:{value.AsInteger()}",
            SqlValueKind.Real => $"R:{value.AsReal():R}",
            SqlValueKind.Text => $"T:{value.AsText()}",
            SqlValueKind.Blob => $"B:{Convert.ToHexString(value.AsBlob().Span)}",
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };

    private static List<object?[]> QueryNative(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private static string[] SerializeRows(IEnumerable<object?[]> rows)
        => rows.Select(row => string.Join("|", row.Select(SerializeNative))).Order().ToArray();

    private static string SerializeNative(object? value)
        => value switch
        {
            null or DBNull => "N:",
            long integer => $"I:{integer}",
            double real => $"R:{real:R}",
            string text => $"T:{text}",
            byte[] blob => $"B:{Convert.ToHexString(blob)}",
            _ => throw new InvalidOperationException($"Unknown native SQLite value type {value.GetType()}."),
        };

    private static void ExecuteNative(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadNativeInteger(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadNativeText(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static byte[] ReadAllBytes(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var bytes = new byte[checked((int)file.Length)];
        file.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }

    private static long GetIndexRootPage(EmbeddedDatabase database, string indexName)
    {
        var store = typeof(EmbeddedDatabase)
            .GetField("_fileStore", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(database)
            ?? throw new InvalidOperationException("Managed file database has no file store.");
        var rootPages = store.GetType()
            .GetField("_indexRootPages", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(store) as IReadOnlyDictionary<string, uint>
            ?? throw new InvalidOperationException("Managed file store has no index root map.");
        return rootPages[indexName];
    }

    private static int GetDatabasePageSize(EmbeddedDatabase database)
        => (int)(typeof(EmbeddedDatabase)
            .GetMethod("GetPageSize", BindingFlags.Instance | BindingFlags.NonPublic)?
            .Invoke(database, null)
            ?? throw new InvalidOperationException("Managed database page-size accessor was not found."));

    private static void CorruptIndexRoot(
        EmbeddedDatabase database,
        IFileSystem fileSystem,
        string path,
        string indexName)
    {
        var rootPage = GetIndexRootPage(database, indexName);
        var pageSize = GetDatabasePageSize(database);
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        file.Write(checked((rootPage - 1) * pageSize), new byte[pageSize]);
        file.FlushToDisk();
        AssertIndexRootCorrupted(database, fileSystem, path, indexName);
    }

    private static void AssertIndexRootCorrupted(
        EmbeddedDatabase database,
        IFileSystem fileSystem,
        string path,
        string indexName)
        => ReadIndexRoot(database, fileSystem, path, indexName)
            .Should()
            .OnlyContain(value => value == 0);

    private static void AssertIndexRootValid(
        EmbeddedDatabase database,
        IFileSystem fileSystem,
        string path,
        string indexName)
    {
        var pageType = SqliteBtreePageHeader.Parse(
            ReadIndexRoot(database, fileSystem, path, indexName)).PageType;
        (pageType is SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior)
            .Should().BeTrue();
    }

    private static byte[] ReadIndexRoot(
        EmbeddedDatabase database,
        IFileSystem fileSystem,
        string path,
        string indexName)
    {
        var rootPage = GetIndexRootPage(database, indexName);
        var pageSize = GetDatabasePageSize(database);
        var page = new byte[pageSize];
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        file.Read(checked((rootPage - 1) * pageSize), page).Should().Be(page.Length);
        return page;
    }

    private static void VerifyReindexedCatalog(IFileSystem fileSystem, string path)
    {
        using var verifier = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = verifier.Connect();
        ReadInteger(connection, "SELECT COUNT(*) FROM items;").Should().Be(24);
        ReadRows(connection, "PRAGMA index_list(items);")
            .Select(row => row[1].AsText())
            .Should().Contain("items_active");
    }

    private static string CreateDatabasePath(string label)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-maintenance-statement-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{label}-{Guid.NewGuid():N}.db");
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
