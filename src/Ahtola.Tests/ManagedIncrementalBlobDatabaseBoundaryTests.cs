using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedIncrementalBlobDatabaseBoundaryTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void ManagedBlobReadsWritesAndInvalidatesRowsInNamedAttachedDatabases()
    {
        var mainPath = CreateDatabasePath("attached-main");
        var attachedPath = CreateDatabasePath("attached-data");
        try
        {
            using var connection = OpenManaged(mainPath);
            Attach(connection, attachedPath, "aux");
            connection.ExecuteNonQuery(
                "CREATE TABLE aux.data(value BLOB, revision INTEGER);"
                + "INSERT INTO aux.data(rowid, value, revision) VALUES (1, X'010203', 0);");

            using var blob = new SqliteBlob(
                connection,
                databaseName: "AuX",
                tableName: "data",
                columnName: "value",
                rowid: 1);
            var value = new byte[3];
            blob.Read(value, 0, value.Length).Should().Be(value.Length);
            value.Should().Equal(1, 2, 3);

            blob.Position = 1;
            blob.Write([9, 8], 0, 2);
            connection.ExecuteScalar<byte[]>("SELECT value FROM aux.data WHERE rowid = 1;")
                .Should().Equal(1, 9, 8);

            connection.ExecuteNonQuery("UPDATE aux.data SET revision = 1 WHERE rowid = 1;");
            var invalidated = Assert.Throws<SqliteException>(() => blob.ReadByte());
            invalidated!.SqliteErrorCode.Should().Be(4);
            connection.ExecuteScalar<byte[]>("SELECT value FROM aux.data WHERE rowid = 1;")
                .Should().Equal(1, 9, 8);
        }
        finally
        {
            DeleteDatabase(mainPath);
            DeleteDatabase(attachedPath);
        }
    }

    [Test]
    public void ManagedAttachedBlobBlocksDetachButAllowsTransactionsWithoutChangingData()
    {
        var mainPath = CreateDatabasePath("attached-lifecycle-main");
        var attachedPath = CreateDatabasePath("attached-lifecycle-data");
        try
        {
            using var connection = OpenManaged(mainPath);
            Attach(connection, attachedPath, "aux");
            connection.ExecuteNonQuery(
                "CREATE TABLE aux.data(value BLOB);"
                + "INSERT INTO aux.data(rowid, value) VALUES (1, X'0102');");

            var blob = new SqliteBlob(connection, "aux", "data", "value", 1);
            var detach = Assert.Throws<SqliteException>(() => connection.ExecuteNonQuery("DETACH aux;"));
            detach!.Message.Should().Contain("database is locked");
            using (var transaction = connection.BeginTransaction())
            {
                connection.ExecuteScalar<byte[]>("SELECT value FROM aux.data WHERE rowid = 1;")
                    .Should().Equal(1, 2);
                transaction.Commit();
            }

            blob.Dispose();
            connection.ExecuteNonQuery("DETACH aux;");
            Assert.Throws<SqliteException>(() =>
                new SqliteBlob(connection, "aux", "data", "value", 1))!.Message
                .Should().Contain("no such database: aux");
        }
        finally
        {
            DeleteDatabase(mainPath);
            DeleteDatabase(attachedPath);
        }
    }

    [Test]
    public void ManagedBlobRejectsWithoutRowidAndResizeFailuresAtomically()
    {
        using var connection = OpenManaged(":memory:");
        connection.ExecuteNonQuery(
            "CREATE TABLE keyed(id INTEGER PRIMARY KEY, value BLOB) WITHOUT ROWID;"
            + "INSERT INTO keyed VALUES (1, X'0102');"
            + "CREATE TABLE data(value BLOB);"
            + "INSERT INTO data(rowid, value) VALUES (1, X'0304');");

        var withoutRowid = Assert.Throws<SqliteException>(() =>
            new SqliteBlob(connection, "main", "keyed", "value", 1));
        withoutRowid!.SqliteErrorCode.Should().Be(1);
        withoutRowid.Message.Should().Contain("cannot open table without rowid: keyed");
        connection.ExecuteScalar<byte[]>("SELECT value FROM keyed WHERE id = 1;").Should().Equal(1, 2);

        using var blob = new SqliteBlob(connection, "main", "data", "value", 1);
        blob.Position = blob.Length;
        Assert.Throws<NotSupportedException>(() => blob.Write([5], 0, 1))!.Message
            .Should().Be(Data.Sqlite.Properties.Resources.ResizeNotSupported);
        Assert.Throws<NotSupportedException>(() => blob.SetLength(3))!.Message
            .Should().Be(Data.Sqlite.Properties.Resources.ResizeNotSupported);
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(3, 4);
    }

    [Test]
    public void ManagedAttachedBlobRejectsUpdateTriggersWithoutChangingData()
    {
        var mainPath = CreateDatabasePath("attached-trigger-main");
        var attachedPath = CreateDatabasePath("attached-trigger-data");
        try
        {
            using (var seed = OpenManaged(attachedPath))
            {
                seed.ExecuteNonQuery("""
                    CREATE TABLE data(value BLOB);
                    CREATE TABLE audit(value TEXT);
                    INSERT INTO data(rowid, value) VALUES (1, X'0102');
                    CREATE TRIGGER data_audit AFTER UPDATE ON data
                    BEGIN
                        INSERT INTO audit VALUES ('updated');
                    END;
                    """);
            }

            using var connection = OpenManaged(mainPath);
            Attach(connection, attachedPath, "aux");
            using var blob = new SqliteBlob(connection, "aux", "data", "value", 1);

            var error = Assert.Throws<SqliteException>(() => blob.Write([3], 0, 1));

            error!.SqliteErrorCode.Should().Be(1);
            error.Message.Should().Contain("cannot write to an incremental blob on a table with UPDATE triggers");
            connection.ExecuteScalar<byte[]>("SELECT value FROM aux.data WHERE rowid = 1;")
                .Should().Equal(1, 2);
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM aux.audit;").Should().Be(0);
        }
        finally
        {
            DeleteDatabase(mainPath);
            DeleteDatabase(attachedPath);
        }
    }

    [Test]
    public void CoreManagedBlobEntryPointUsesNamedDatabasesAndExplicitWithoutRowidFailure()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-core-main.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(connection, "ATTACH DATABASE 'blob-core-attached.db' AS aux;");
        Execute(
            connection,
            "CREATE TABLE aux.data(value BLOB);"
            + "INSERT INTO aux.data(rowid, value) VALUES (1, X'0102');"
            + "CREATE TABLE aux.keyed(id INTEGER PRIMARY KEY, value BLOB) WITHOUT ROWID;"
            + "INSERT INTO aux.keyed VALUES (1, X'0304');");

        using (var blob = connection.OpenBlob("aux", "data", "value", 1))
        {
            Span<byte> value = stackalloc byte[2];
            blob.Read(0, value).Should().Be(2);
            value.ToArray().Should().Equal(1, 2);
            blob.Write(1, [9]);
        }

        ReadBlob(connection, "SELECT value FROM aux.data WHERE rowid = 1;").Should().Equal(1, 9);
        Assert.Throws<ManagedBlobException>(() =>
            connection.OpenBlob("aux", "keyed", "value", 1))!.Message
            .Should().Be("cannot open table without rowid: keyed");
    }

    [Test]
    public void CoreReadOnlyBlobStreamsOverflowRangesWithoutValueSizedOpenAllocation()
    {
        const int payloadLength = 1024 * 1024;
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-read.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        var payload = Enumerable.Range(0, payloadLength)
            .Select(static index => unchecked((byte)(index * 31)))
            .ToArray();
        Execute(connection, "CREATE TABLE data(value BLOB);");
        ExecuteBound(
            connection,
            "INSERT INTO data(rowid, value) VALUES (1, $value);",
            SqlValue.Blob(payload));
        Execute(connection, "INSERT INTO data(rowid, value) VALUES (2, X'01');");

        using (var warmup = connection.OpenBlob("main", "data", "value", 2, readOnly: true))
        {
            Span<byte> one = stackalloc byte[1];
            warmup.Read(0, one).Should().Be(1);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using var blob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Span<byte> actual = stackalloc byte[96];

        blob.Length.Should().Be(payloadLength);
        blob.Read(4070, actual).Should().Be(actual.Length);
        actual.ToArray().Should().Equal(payload.AsSpan(4070, actual.Length).ToArray());
        allocated.Should().BeLessThan(
            payloadLength / 4,
            "opening a page-native handle must not snapshot or copy the complete BLOB");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CoreReadOnlyBlobUsesTransactionVisibleRowsWithoutCopyingTheBlob(bool concurrent)
    {
        const int payloadLength = 64 * 1024;
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            concurrent ? "blob-page-native-mvcc.db" : "blob-page-native-classic.db",
            fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        var baseline = Enumerable.Repeat((byte)0x11, payloadLength).ToArray();
        var updated = Enumerable.Range(0, payloadLength)
            .Select(static index => unchecked((byte)(index * 13)))
            .ToArray();
        Execute(connection, "CREATE TABLE data(value BLOB);");
        ExecuteBound(
            connection,
            "INSERT INTO data(rowid, value) VALUES (1, $value);",
            SqlValue.Blob(baseline));
        if (concurrent)
            Execute(connection, "PRAGMA journal_mode = mvcc;");
        Execute(connection, concurrent ? "BEGIN CONCURRENT;" : "BEGIN;");

        try
        {
            using (var baseBlob = connection.OpenBlob("main", "data", "value", 1, readOnly: true))
            {
                Span<byte> actual = stackalloc byte[32];
                baseBlob.Read(4090, actual).Should().Be(actual.Length);
                actual.ToArray().Should().Equal(baseline.AsSpan(4090, actual.Length).ToArray());
            }

            ExecuteBound(
                connection,
                "UPDATE data SET value = $value WHERE rowid = 1;",
                SqlValue.Blob(updated));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            using var overlayBlob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Span<byte> overlayActual = stackalloc byte[48];

            overlayBlob.Read(8180, overlayActual).Should().Be(overlayActual.Length);
            overlayActual.ToArray().Should().Equal(updated.AsSpan(8180, overlayActual.Length).ToArray());
            allocated.Should().BeLessThan(
                payloadLength / 2,
                "transaction overlays already own an immutable SqlValue and need no full-value snapshot");
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }

    [Test]
    public void CoreReadOnlyBlobMapsLogicalColumnsPastVirtualGeneratedColumns()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-generated.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(prefix BLOB, computed BLOB GENERATED ALWAYS AS (X'AA') VIRTUAL, value BLOB);"
            + "INSERT INTO data(rowid, prefix, value) VALUES (1, X'0102', X'030405');");

        using var blob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);
        Span<byte> actual = stackalloc byte[3];

        blob.Read(0, actual).Should().Be(3);
        actual.ToArray().Should().Equal(3, 4, 5);
        Assert.Throws<ManagedBlobException>(() =>
            connection.OpenBlob("main", "data", "computed", 1, readOnly: true))!.Message
            .Should().Be("cannot open generated column: computed");
    }

    [Test]
    public void CorePageNativeReadOnlyBlobExpiresOnlyWhenItsOwnRowChanges()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-expiry.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(value BLOB, revision INTEGER);"
            + "INSERT INTO data(rowid, value, revision) VALUES (1, X'0102', 0);"
            + "INSERT INTO data(rowid, value, revision) VALUES (2, X'0304', 0);");
        using var blob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);

        Execute(connection, "UPDATE data SET revision = 1 WHERE rowid = 2;");
        Span<byte> actual = stackalloc byte[2];
        blob.Read(0, actual).Should().Be(2);
        actual.ToArray().Should().Equal(1, 2);

        Execute(connection, "UPDATE data SET revision = 1 WHERE rowid = 1;");
        var expired = Assert.Throws<ManagedBlobException>(() => blob.Read(0, new byte[1]));
        expired!.ErrorCode.Should().Be(4);
    }

    [Test]
    public void CorePageNativeReadOnlyBlobSurvivesPeerCatalogRefresh()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-refresh.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(value BLOB);"
            + "INSERT INTO data(rowid, value) VALUES (1, X'0102');"
            + "INSERT INTO data(rowid, value) VALUES (2, X'0304');");
        using var blob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);

        using (var peerDatabase = EmbeddedDatabase.OpenFile("blob-page-native-refresh.db", fileSystem))
        using (var peer = peerDatabase.Connect())
        using (var peerAdapter = ManagedDatabaseAdapter.FromConnection(peer))
            Execute(peerAdapter.Connection, "UPDATE data SET value = X'0506' WHERE rowid = 2;");

        ReadBlob(connection, "SELECT value FROM data WHERE rowid = 2;").Should().Equal(5, 6);
        Span<byte> actual = stackalloc byte[2];
        blob.Read(0, actual).Should().Be(2);
        actual.ToArray().Should().Equal(1, 2);
    }

    [Test]
    public void CorePageNativeReadOnlyBlobOpenAdoptsPeerCatalogChanges()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-open-refresh.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(value BLOB);"
            + "INSERT INTO data(rowid, value) VALUES (1, X'0102');");

        using (var peerDatabase = EmbeddedDatabase.OpenFile("blob-page-native-open-refresh.db", fileSystem))
        using (var peer = peerDatabase.Connect())
        using (var peerAdapter = ManagedDatabaseAdapter.FromConnection(peer))
            Execute(peerAdapter.Connection, "INSERT INTO data(rowid, value) VALUES (2, X'030405');");

        using (var blob = connection.OpenBlob("main", "data", "value", 2, readOnly: true))
        {
            Span<byte> actual = stackalloc byte[3];
            blob.Read(0, actual).Should().Be(3);
            actual.ToArray().Should().Equal(3, 4, 5);
        }

        using (var peerDatabase = EmbeddedDatabase.OpenFile("blob-page-native-open-refresh.db", fileSystem))
        using (var peer = peerDatabase.Connect())
        using (var peerAdapter = ManagedDatabaseAdapter.FromConnection(peer))
            Execute(peerAdapter.Connection, "DROP TABLE data;");

        Assert.Throws<ManagedBlobException>(() =>
            connection.OpenBlob("main", "data", "value", 1, readOnly: true))!.Message
            .Should().Be("no such table: data");
    }

    [Test]
    public void CorePageNativeReadOnlyBlobRejectsTransactionLocalSchemaChanges()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-schema-change.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(prefix BLOB, value BLOB);"
            + "INSERT INTO data(rowid, prefix, value) VALUES (1, X'01', X'020304');");
        using var existingBlob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);
        Execute(connection, "BEGIN; ALTER TABLE data DROP COLUMN prefix;");

        try
        {
            Assert.Throws<ManagedBlobException>(() => existingBlob.Read(0, new byte[1]))!.ErrorCode
                .Should().Be(4);
            Assert.Throws<ManagedBlobException>(() =>
                connection.OpenBlob("main", "data", "value", 1, readOnly: true))!.Message
                .Should().Contain("unavailable after schema changes");
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CorePageNativeReadOnlyBlobExpiresWhenSavepointRollbackRewindsItsValue(bool concurrent)
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            concurrent ? "blob-page-native-rollback-mvcc.db" : "blob-page-native-rollback-classic.db",
            fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(value BLOB);"
            + "INSERT INTO data(rowid, value) VALUES (1, X'0102');");
        if (concurrent)
            Execute(connection, "PRAGMA journal_mode = mvcc;");
        Execute(connection, concurrent ? "BEGIN CONCURRENT; SAVEPOINT before_update;" : "BEGIN; SAVEPOINT before_update;");

        try
        {
            Execute(connection, "UPDATE data SET value = X'03040506' WHERE rowid = 1;");
            using var blob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);
            blob.Length.Should().Be(4);

            Execute(connection, "ROLLBACK TO before_update;");

            var expired = Assert.Throws<ManagedBlobException>(() => _ = blob.Length);
            expired!.ErrorCode.Should().Be(4);
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }

    [Test]
    public void CorePageNativeReadOnlyBlobSurvivesUnaffectedRollbackAndCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("blob-page-native-transaction-lifetime.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(
            connection,
            "CREATE TABLE data(value BLOB);"
            + "INSERT INTO data(rowid, value) VALUES (1, X'0102');"
            + "INSERT INTO data(rowid, value) VALUES (2, X'0304');"
            + "BEGIN;");
        using var blob = connection.OpenBlob("main", "data", "value", 1, readOnly: true);

        Execute(
            connection,
            "SAVEPOINT other_row;"
            + "UPDATE data SET value = X'0506' WHERE rowid = 2;"
            + "ROLLBACK TO other_row;");
        Span<byte> actual = stackalloc byte[2];
        blob.Read(0, actual).Should().Be(2);
        actual.ToArray().Should().Equal(1, 2);

        Execute(connection, "COMMIT;");
        blob.Read(0, actual).Should().Be(2);
        actual.ToArray().Should().Equal(1, 2);
    }

    [Test]
    public void ManagedAttachedBlobPersistsAcrossPlaintextAndEncryptedReopen()
    {
        VerifyAttachedReopen(encrypted: false);
        VerifyAttachedReopen(encrypted: true);
    }

    private static void VerifyAttachedReopen(bool encrypted)
    {
        var suffix = encrypted ? "encrypted" : "plaintext";
        var mainPath = CreateDatabasePath($"reopen-{suffix}-main");
        var attachedPath = CreateDatabasePath($"reopen-{suffix}-data");
        try
        {
            using (var create = OpenManaged(mainPath, encrypted))
            {
                Attach(create, attachedPath, "aux");
                create.ExecuteNonQuery(
                    "CREATE TABLE aux.data(value BLOB);"
                    + "INSERT INTO aux.data(rowid, value) VALUES (1, X'010203');");
                using var blob = new SqliteBlob(create, "aux", "data", "value", 1);
                blob.Position = 1;
                blob.Write([7], 0, 1);
            }

            if (encrypted)
                File.ReadAllBytes(attachedPath).AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());

            using var reopen = OpenManaged(mainPath, encrypted);
            Attach(reopen, attachedPath, "aux");
            using var reopenedBlob = new SqliteBlob(reopen, "aux", "data", "value", 1, readOnly: true);
            var value = new byte[3];
            reopenedBlob.Read(value, 0, value.Length).Should().Be(value.Length);
            value.Should().Equal(1, 7, 3);
        }
        finally
        {
            DeleteDatabase(mainPath);
            DeleteDatabase(attachedPath);
        }
    }

    private static SqliteConnection OpenManaged(string path, bool encrypted = false)
    {
        var encryption = encrypted
            ? $";Encryption Cipher=Aes256Gcm;Encryption Key={Aes256Key}"
            : string.Empty;
        var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed{encryption}");
        connection.Open();
        return connection;
    }

    private static void Attach(SqliteConnection connection, string path, string name)
        => connection.ExecuteNonQuery($"ATTACH DATABASE '{path.Replace("'", "''", StringComparison.Ordinal)}' AS \"{name}\";");

    private static void Execute(IManagedConnectionAdapter connection, string sql)
    {
        foreach (var statementSql in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var statement = connection.Prepare(statementSql + ";");
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        }
    }

    private static void ExecuteBound(
        IManagedConnectionAdapter connection,
        string sql,
        SqlValue value)
    {
        using var statement = connection.Prepare(sql);
        statement.Bind(statement.GetParameterIndex("$value"), value);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static byte[] ReadBlob(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsBlob().ToArray();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-incremental-blob-database-boundary-tests");
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
