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
            statement.Step().Should().Be(StatementStepResult.Done);
        }
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
