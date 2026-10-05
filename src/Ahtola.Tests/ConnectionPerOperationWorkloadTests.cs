using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for the hot paths of a connection-per-operation workload over GUID-keyed
/// rowid tables (Remote Desktop Manager's SQLite data source shape): pooled reopen, equality
/// lookups on a non-integer PRIMARY KEY, multi-row imports into a uniquely indexed table, and
/// <see cref="SqliteDataReader.GetValue"/> declared-type resolution.
/// </summary>
public sealed class ConnectionPerOperationWorkloadTests
{
    private const string Schema =
        "CREATE TABLE entries(id BLOB NOT NULL PRIMARY KEY, name TEXT, data TEXT);";

    [Test]
    public void PrimaryKeyEqualityLookupSeeksTheDurableIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        var ids = CreateEntries(fileSystem, "seek.db", count: 300);

        using var database = EmbeddedDatabase.OpenFile("seek.db", fileSystem);
        using var connection = database.Connect();
        var before = database.PlannerAccessPathMetrics.IndexEqualitySeeksExecuted;

        ReadRows(connection, "SELECT name FROM entries WHERE id = ?1;", SqlValue.Blob(ids[123]))
            .Select(static row => row[0].AsText()).Should().Equal("entry-123");
        ReadRows(connection, $"SELECT name FROM entries WHERE id = X'{Convert.ToHexString(ids[7])}';")
            .Select(static row => row[0].AsText()).Should().Equal("entry-7");
        ReadRows(connection, "SELECT name FROM entries WHERE id = ?1;", SqlValue.Blob(new byte[16]))
            .Should().BeEmpty();
        ReadRows(connection, "SELECT name FROM entries WHERE id = ?1;", SqlValue.Null)
            .Should().BeEmpty();

        database.PlannerAccessPathMetrics.IndexEqualitySeeksExecuted.Should().BeGreaterThanOrEqualTo(before + 3);
        database.PlannerAccessPathMetrics.IndexEqualitySeekPagesRead.Should().BeGreaterThan(0);
    }

    [Test]
    public void PrimaryKeyEqualityLookupSeesTheTransactionsOwnWrites()
    {
        var fileSystem = new InMemoryFileSystem();
        var ids = CreateEntries(fileSystem, "seek-tx.db", count: 50);
        var added = Guid.NewGuid().ToByteArray();

        using var database = EmbeddedDatabase.OpenFile("seek-tx.db", fileSystem);
        using var connection = database.Connect();
        const string lookup = "SELECT name FROM entries WHERE id = ?1;";

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO entries VALUES (?1, 'added', '');", SqlValue.Blob(added));
        Execute(connection, "UPDATE entries SET name = 'renamed' WHERE id = ?1;", SqlValue.Blob(ids[3]));
        Execute(connection, "DELETE FROM entries WHERE id = ?1;", SqlValue.Blob(ids[4]));
        ReadRows(connection, lookup, SqlValue.Blob(added)).Select(static row => row[0].AsText()).Should().Equal("added");
        ReadRows(connection, lookup, SqlValue.Blob(ids[3])).Select(static row => row[0].AsText()).Should().Equal("renamed");
        ReadRows(connection, lookup, SqlValue.Blob(ids[4])).Should().BeEmpty();
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, lookup, SqlValue.Blob(added)).Should().BeEmpty();
        ReadRows(connection, lookup, SqlValue.Blob(ids[3])).Select(static row => row[0].AsText()).Should().Equal("entry-3");
        ReadRows(connection, lookup, SqlValue.Blob(ids[4])).Select(static row => row[0].AsText()).Should().Equal("entry-4");
    }

    [Test]
    public void TriggerLookupSeesRowsWrittenEarlierInTheSameAutocommitStatement()
    {
        var fileSystem = new InMemoryFileSystem();
        _ = CreateEntries(fileSystem, "seek-trigger.db", count: 20);
        var added = Guid.NewGuid().ToByteArray();

        using var database = EmbeddedDatabase.OpenFile("seek-trigger.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE audit(name TEXT);");
        Execute(
            connection,
            "CREATE TRIGGER entries_audit AFTER INSERT ON entries BEGIN "
            + "INSERT INTO audit SELECT name FROM entries WHERE id = NEW.id; END;");

        Execute(connection, "INSERT INTO entries VALUES (?1, 'from-trigger', '');", SqlValue.Blob(added));

        ReadRows(connection, "SELECT name FROM audit;").Select(static row => row[0].AsText())
            .Should().Equal("from-trigger");
    }

    [Test]
    public void UniqueKeysStayEnforcedAcrossStatementsUpdatesDeletesAndFailedInserts()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("unique-cache.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, tag TEXT COLLATE NOCASE UNIQUE);");
        const string insert = "INSERT INTO items(code, tag) VALUES (?1, ?2);";

        Execute(connection, "BEGIN;");
        for (var index = 0; index < 200; index++)
            Execute(connection, insert, SqlValue.Text($"c{index}"), SqlValue.Text($"t{index}"));

        AssertUniqueFailure(() => Execute(connection, insert, SqlValue.Text("c10"), SqlValue.Text("new")), "items.code");
        AssertUniqueFailure(() => Execute(connection, insert, SqlValue.Text("new"), SqlValue.Text("T10")), "items.tag");

        // A multi-row INSERT that fails partway must not leave its earlier rows' keys behind.
        AssertUniqueFailure(
            () => Execute(connection, "INSERT INTO items(code, tag) VALUES ('fresh', 'x1'), ('fresh', 'x2');"),
            "items.code");
        Execute(connection, insert, SqlValue.Text("fresh"), SqlValue.Text("x1"));

        Execute(connection, "UPDATE items SET code = 'moved' WHERE code = 'c20';");
        Execute(connection, insert, SqlValue.Text("c20"), SqlValue.Text("reused-old-code"));
        AssertUniqueFailure(() => Execute(connection, insert, SqlValue.Text("moved"), SqlValue.Text("y")), "items.code");

        Execute(connection, "DELETE FROM items WHERE code = 'c30';");
        Execute(connection, insert, SqlValue.Text("c30"), SqlValue.Text("t30"));
        Execute(connection, "COMMIT;");

        AssertUniqueFailure(() => Execute(connection, insert, SqlValue.Text("c199"), SqlValue.Text("z")), "items.code");
        ReadRows(connection, "SELECT count(*) FROM items;").Single()[0].AsInteger().Should().Be(202);
    }

    [Test]
    public void EqualityUpdatesMatchUnderAffinityAndCollationLikeAFullScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, code TEXT, amount INTEGER, tag TEXT COLLATE NOCASE UNIQUE, hits INTEGER DEFAULT 0);");
        Execute(connection, "INSERT INTO items(code, amount, tag) VALUES ('5', 7, 'Alpha'), ('05', 70, 'beta'), ('6', 7, 'gamma');");

        int Update(string sql, params SqlValue[] parameters)
        {
            using var statement = connection.Prepare(sql);
            for (var index = 0; index < parameters.Length; index++)
                statement.Bind(index + 1, parameters[index]);
            statement.Step().Should().Be(StatementStepResult.Done);
            return (int)statement.RowsAffected;
        }

        Update("UPDATE items SET hits = hits + 1 WHERE code = 5;").Should().Be(1);
        Update("UPDATE items SET hits = hits + 1 WHERE amount = ?1;", SqlValue.Text("7")).Should().Be(2);
        Update("UPDATE items SET hits = hits + 1 WHERE tag = 'ALPHA';").Should().Be(1);
        Update("UPDATE items SET hits = hits + 1 WHERE tag = ?1 AND amount = 70;", SqlValue.Text("BETA")).Should().Be(1);
        Update("UPDATE items SET hits = hits + 1 WHERE code = ?1;", SqlValue.Null).Should().Be(0);

        ReadRows(connection, "SELECT tag, hits FROM items ORDER BY id;")
            .Select(static row => $"{row[0].AsText()}={row[1].AsInteger()}")
            .Should().Equal("Alpha=3", "beta=1", "gamma=1");
    }

    [Test]
    public void InPlaceUpdatesKeepSqliteUniqueConflictSemantics()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("update-conflicts.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER UNIQUE, note TEXT);");
        Execute(connection, "INSERT INTO t(v, note) VALUES (1, 'a'), (2, 'b'), (3, 'c');");

        // Rows are checked one at a time in rowid order: row 1 moving to 2 still collides with row 2.
        AssertUniqueFailure(() => Execute(connection, "UPDATE t SET v = v + 1;"), "t.v");
        Values(connection).Should().Equal("1:a", "2:b", "3:c");

        // Moving the highest key first succeeds, row by row.
        Execute(connection, "UPDATE t SET v = v + 10 WHERE v = 3;");
        Execute(connection, "UPDATE t SET v = 3 WHERE v = 2;");
        Values(connection).Should().Equal("1:a", "3:b", "13:c");

        Execute(connection, "UPDATE OR IGNORE t SET v = 3, note = 'ignored' WHERE id IN (1, 3);");
        Values(connection).Should().Equal("1:a", "3:b", "13:c");

        // OR FAIL keeps the rows updated before the failing one: row 1 moves, row 2 collides.
        var failure = () => Execute(
            connection,
            "UPDATE OR FAIL t SET v = CASE id WHEN 1 THEN 50 ELSE 13 END, note = 'failed' WHERE id IN (1, 2);");
        failure.Should().Throw<Exception>().Which.Message.Should().Contain("UNIQUE constraint failed: t.v");
        Values(connection).Should().Equal("50:failed", "3:b", "13:c");

        // The cached key sets follow every moved key.
        Execute(connection, "INSERT INTO t(v, note) VALUES (1, 'reused');");
        AssertUniqueFailure(() => Execute(connection, "INSERT INTO t(v, note) VALUES (50, 'dup');"), "t.v");
        ReadRows(connection, "PRAGMA integrity_check;").Single()[0].AsText().Should().Be("ok");
    }

    [Test]
    public void InPlaceUpdateOfAGuidKeyedRowPersistsAndReopens()
    {
        var fileSystem = new InMemoryFileSystem();
        var ids = CreateEntries(fileSystem, "update-persist.db", count: 200);
        using (var database = EmbeddedDatabase.OpenFile("update-persist.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "BEGIN;");
            Execute(connection, "UPDATE entries SET name = 'saved', data = ?2 WHERE id = ?1;", SqlValue.Blob(ids[42]), SqlValue.Text("payload"));
            Execute(connection, "COMMIT;");
            Execute(connection, "UPDATE entries SET name = 'autocommit' WHERE id = ?1;", SqlValue.Blob(ids[43]));
        }

        using var reopened = EmbeddedDatabase.OpenFile("update-persist.db", fileSystem);
        using var reader = reopened.Connect();
        ReadRows(reader, "SELECT name, data FROM entries WHERE id = ?1;", SqlValue.Blob(ids[42]))
            .Select(static row => $"{row[0].AsText()}/{row[1].AsText()}").Should().Equal("saved/payload");
        ReadRows(reader, "SELECT name FROM entries WHERE id = ?1;", SqlValue.Blob(ids[43]))
            .Select(static row => row[0].AsText()).Should().Equal("autocommit");
        ReadRows(reader, "SELECT count(*) FROM entries WHERE name LIKE 'entry-%';").Single()[0].AsInteger().Should().Be(198);
        ReadRows(reader, "PRAGMA integrity_check;").Single()[0].AsText().Should().Be("ok");
    }

    [Test]
    public void UpdatesStillEnforceForeignKeysOnTheColumnsTheyAssign()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT UNIQUE, label TEXT);");
        Execute(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_code TEXT REFERENCES parent(code), note TEXT);");
        Execute(connection, "INSERT INTO parent VALUES (1, 'p1', 'one'), (2, 'p2', 'two');");
        Execute(connection, "INSERT INTO child VALUES (10, 'p1', 'n');");

        // Neither the child column nor the referenced parent column is assigned.
        Execute(connection, "UPDATE parent SET label = 'uno' WHERE id = 1;");
        Execute(connection, "UPDATE child SET note = 'm' WHERE id = 10;");

        var orphanChild = () => Execute(connection, "UPDATE child SET parent_code = 'missing' WHERE id = 10;");
        orphanChild.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain("FOREIGN KEY constraint failed");
        var orphanParent = () => Execute(connection, "UPDATE parent SET code = 'gone' WHERE id = 1;");
        orphanParent.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain("FOREIGN KEY constraint failed");

        ReadRows(connection, "SELECT p.label, c.parent_code, c.note FROM child c JOIN parent p ON p.code = c.parent_code;")
            .Select(static row => $"{row[0].AsText()}/{row[1].AsText()}/{row[2].AsText()}")
            .Should().Equal("uno/p1/m");
    }

    private static string[] Values(EmbeddedConnection connection)
        => ReadRows(connection, "SELECT v, note FROM t ORDER BY id;")
            .Select(static row => $"{row[0].AsInteger()}:{row[1].AsText()}")
            .ToArray();

    [Test]
    public void ReaderDeclaredTypesFollowEachResultSet()
    {
        var path = CreatePath("reader-declared-types");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            var guid = Guid.NewGuid();
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText =
                    "CREATE TABLE typed(id GUID PRIMARY KEY, label TEXT);"
                    + "CREATE TABLE raw(token BLOB PRIMARY KEY, amount INTEGER);";
                setup.ExecuteNonQuery();
                setup.CommandText = "INSERT INTO typed VALUES (@id, 'a'); INSERT INTO raw VALUES (@id, 5);";
                setup.Parameters.AddWithValue("@id", guid);
                setup.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, label FROM typed; SELECT token, amount FROM raw;";
            using var reader = command.ExecuteReader();

            reader.Read().Should().BeTrue();
            reader.GetDataTypeName(0).Should().Be("GUID");
            reader.GetValue(0).Should().Be(guid);
            reader.GetDataTypeName(1).Should().Be("TEXT");

            reader.NextResult().Should().BeTrue();
            reader.Read().Should().BeTrue();
            reader.GetDataTypeName(0).Should().Be("BLOB");
            reader.GetValue(0).Should().BeOfType<byte[]>();
            reader.GetDataTypeName(1).Should().Be("INTEGER");
            reader.GetValue(1).Should().Be(5L);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PooledReopenObservesCommitsMadeByOtherConnections()
    {
        var path = CreatePath("pooled-reopen");
        try
        {
            var pooled = $"Data Source={path}";
            using (var first = new SqliteConnection(pooled))
            {
                first.Open();
                using var command = first.CreateCommand();
                command.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT); INSERT INTO notes(body) VALUES ('one');";
                command.ExecuteNonQuery();
            }

            using (var writer = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText = "INSERT INTO notes(body) VALUES ('two'); CREATE TABLE later(x INTEGER); INSERT INTO later VALUES (9);";
                command.ExecuteNonQuery();
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var reopened = new SqliteConnection(pooled);
                reopened.Open();
                using var command = reopened.CreateCommand();
                command.CommandText = "SELECT group_concat(body, ',') FROM notes;";
                command.ExecuteScalar().Should().Be("one,two");
                command.CommandText = "SELECT x FROM later;";
                command.ExecuteScalar().Should().Be(9L);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EncryptedOpenOfAForeignCiphertextDoesNotClaimAPlaintextHeader()
    {
        const string key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
        var foreign = CreatePath("foreign-ciphertext");
        var plaintext = CreatePath("plaintext");
        try
        {
            var bytes = new byte[8192];
            new Random(17).NextBytes(bytes);
            File.WriteAllBytes(foreign, bytes);
            using (var create = new SqliteConnection($"Data Source={plaintext};Pooling=False"))
            {
                create.Open();
                using var command = create.CreateCommand();
                command.CommandText = "CREATE TABLE t(x INTEGER);";
                command.ExecuteNonQuery();
            }

            string OpenFailure(string path)
            {
                using var connection = new SqliteConnection(
                    $"Data Source={path};Encryption Cipher=Aes256Gcm;Encryption Key={key};Pooling=False");
                return Assert.Catch(() => connection.Open())!.Message;
            }

            var foreignMessage = OpenFailure(foreign);
            foreignMessage.Should().Contain(AhtolaEncryptionOptions.EncryptedOrNotDatabaseMessage);
            foreignMessage.Should().NotContain("plaintext");
            OpenFailure(plaintext).Should().Contain("plaintext SQLite header");
        }
        finally
        {
            DeleteDatabase(foreign);
            DeleteDatabase(plaintext);
        }
    }

    [Test]
    public void WalLockLeasesReuseIdleHandlesOnlyWhileTheCarrierIsMapped()
    {
        if (OperatingSystem.IsMacOS())
            Assert.Ignore("macOS brokers one descriptor per carrier instead of pooling lease handles.");

        var path = CreatePath("lease-handle-pool");
        var carrier = path + "-shm";
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);";
                    command.ExecuteNonQuery();
                }

                File.Exists(carrier).Should().BeTrue();
                var locks = new SqliteWalByteRangeLock(carrier);
                locks.TryAcquireShared(200, 1, out var shared).Should().BeTrue();
                shared!.Dispose();
                PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry
                    .GetIdleLeaseHandleCountForTesting(carrier).Should().BeGreaterThan(0);

                // Every live lease still owns a distinct handle, so in-process leases conflict.
                locks.TryAcquireExclusive(201, 1, out var first).Should().BeTrue();
                locks.TryAcquireExclusive(201, 1, out var second).Should().BeFalse();
                second.Should().BeNull();
                first!.Dispose();
                locks.TryAcquireExclusive(201, 1, out var third).Should().BeTrue();
                third!.Dispose();

                using var read = connection.CreateCommand();
                read.CommandText = "SELECT count(*) FROM t;";
                read.ExecuteScalar().Should().Be(1L);
            }

            // The final mapping closed every idle handle, so the carrier is free again.
            PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry
                .GetIdleLeaseHandleCountForTesting(carrier).Should().Be(0);
            if (File.Exists(carrier))
            {
                using var exclusive = new FileStream(carrier, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ProviderConnectionsKeepCommitsInTheWalUntilTheThresholdOrClose()
    {
        var path = CreatePath("deferred-checkpoint");
        var wal = path + "-wal";
        try
        {
            using (var writer = new SqliteConnection($"Data Source={path}"))
            {
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);";
                command.ExecuteNonQuery();
                for (var index = 0; index < 5; index++)
                {
                    command.CommandText = $"INSERT INTO notes(body) VALUES ('n{index}');";
                    command.ExecuteNonQuery();
                }

                // Turso's policy: ordinary commits stay in the WAL until it passes 1000 frames.
                new FileInfo(wal).Length.Should().BeGreaterThan(SqliteWalHeader.Size);

                using var reader = new SqliteConnection($"Data Source={path};Pooling=False");
                reader.Open();
                using var count = reader.CreateCommand();
                count.CommandText = "SELECT count(*) FROM notes;";
                count.ExecuteScalar().Should().Be(5L);
            }

            // Closing the last connection checkpoints what the WAL still holds.
            SqliteConnection.ClearAllPools();
            (File.Exists(wal) ? new FileInfo(wal).Length : 0).Should().BeLessThanOrEqualTo(SqliteWalHeader.Size);
            using var reopened = new SqliteConnection($"Data Source={path};Pooling=False");
            reopened.Open();
            using var verify = reopened.CreateCommand();
            verify.CommandText = "SELECT group_concat(body, ',') FROM notes;";
            verify.ExecuteScalar().Should().Be("n0,n1,n2,n3,n4");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void DeferredCheckpointsResetTheWalOnceItPassesTheThresholdAndPeersKeepUp()
    {
        var path = CreatePath("checkpoint-threshold");
        var wal = path + "-wal";
        try
        {
            using var writerDatabase = EmbeddedDatabase.OpenFile(path);
            writerDatabase.EnableDeferredCheckpoints(frames: 8);
            using var writer = writerDatabase.Connect();
            using var readerDatabase = EmbeddedDatabase.OpenFile(path);
            using var reader = readerDatabase.Connect();
            Execute(writer, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");

            var largestWal = 0L;
            for (var index = 1; index <= 60; index++)
            {
                Execute(writer, "INSERT INTO notes(body) VALUES (?1);", SqlValue.Text($"n{index}"));
                largestWal = Math.Max(largestWal, new FileInfo(wal).Length);

                // A peer pager keeps validating the WAL across every restart the threshold causes.
                ReadRows(reader, "SELECT count(*), max(body) FROM notes WHERE id = ?1;", SqlValue.Integer(index))
                    .Single()[0].AsInteger().Should().Be(1);
            }

            var frameBytes = SqliteWalHeader.Size + 24 + 4096;
            largestWal.Should().BeLessThan(SqliteWalHeader.Size + (8 + 8) * (long)frameBytes);
            ReadRows(reader, "SELECT count(*) FROM notes;").Single()[0].AsInteger().Should().Be(60);
            ReadRows(reader, "PRAGMA integrity_check;").Single()[0].AsText().Should().Be("ok");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SpillDirectoryDefaultsToTheProcessTemporaryPathWithoutEagerLookup()
    {
        new VdbeExecutionOptions(new InMemoryFileSystem()).TemporaryDirectory
            .Should().Be(Path.GetTempPath());
        new VdbeExecutionOptions(new InMemoryFileSystem(), temporaryDirectory: "explicit-spill").TemporaryDirectory
            .Should().Be("explicit-spill");
    }

    private static List<byte[]> CreateEntries(InMemoryFileSystem fileSystem, string path, int count)
    {
        var ids = Enumerable.Range(0, count).Select(static _ => Guid.NewGuid().ToByteArray()).ToList();
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, Schema);
        Execute(connection, "BEGIN;");
        for (var index = 0; index < count; index++)
        {
            Execute(
                connection,
                "INSERT INTO entries VALUES (?1, ?2, ?3);",
                SqlValue.Blob(ids[index]),
                SqlValue.Text($"entry-{index}"),
                SqlValue.Text(new string('x', 200)));
        }

        Execute(connection, "COMMIT;");
        return ids;
    }

    private static void AssertUniqueFailure(Action action, string column)
        => action.Should().Throw<EmbeddedSqlException>()
            .Which.Message.Should().Be($"UNIQUE constraint failed: {column}");

    private static void Execute(EmbeddedConnection connection, string sql, params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql, params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }

    private static string CreatePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "connection-per-operation-workload-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
