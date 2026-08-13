using AwesomeAssertions;
using System.Reflection;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedIncrementalBlobBoundaryTests
{
    [Test]
    public void ManagedBlobUsesTheCoreAdapterAndPersistsBoundedWrites()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'010203');");

        GetPrivateField(connection, "_database").Should().BeNull();
        ((object?)connection.Handle).Should().BeNull();

        using var blob = new SqliteBlob(
            connection,
            tableName: "data",
            columnName: "value",
            rowid: 1);
        blob.Length.Should().Be(3);

        var read = new byte[4];
        blob.Read(read, 1, 2).Should().Be(2);
        read.Should().Equal(0, 1, 2, 0);
        blob.Seek(-1, SeekOrigin.Current).Should().Be(1);

        var source = new byte[] { 9, 8 };
        blob.Write(source, 0, source.Length);
        source[0] = 0;

        blob.Position = 0;
        blob.Read(read, 0, 3).Should().Be(3);
        read.Take(3).Should().Equal(1, 9, 8);
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1, 9, 8);
    }

    [Test]
    public void ManagedBlobMapsOpenAndInvalidationFailuresWithoutChangingStoredValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery(
            "CREATE TABLE data(value BLOB, text_value TEXT);"
            + "INSERT INTO data(rowid, value, text_value) VALUES (1, X'0102', 'text');");

        var missingTable = Assert.Throws<SqliteException>(() => new SqliteBlob(connection, "missing", "value", 1));
        missingTable!.SqliteErrorCode.Should().Be(1);
        missingTable.Message.Should().Contain("no such table: missing");

        var missing = Assert.Throws<SqliteException>(() => new SqliteBlob(connection, "data", "value", 2));
        missing!.SqliteErrorCode.Should().Be(1);
        missing.Message.Should().Contain("no such rowid: 2");

        var nonBlob = Assert.Throws<SqliteException>(() => new SqliteBlob(connection, "data", "text_value", 1));
        nonBlob!.SqliteErrorCode.Should().Be(1);
        connection.ExecuteScalar<string>("SELECT text_value FROM data WHERE rowid = 1;").Should().Be("text");

        using var blob = new SqliteBlob(connection, "data", "value", 1);
        connection.ExecuteNonQuery("DELETE FROM data WHERE rowid = 1;");

        var aborted = Assert.Throws<SqliteException>(() => blob.Write([3], 0, 1));
        aborted!.SqliteErrorCode.Should().Be(4);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);
    }

    [Test]
    public void ManagedBlobDatabaseNameOverloadSupportsMainAndAttachments()
    {
        var mainPath = CreateDatabasePath();
        var attachmentPath = CreateDatabasePath();
        try
        {
            using var connection = new SqliteConnection($"Data Source={mainPath};Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'0102');");
            connection.ExecuteNonQuery(
                $"ATTACH DATABASE '{attachmentPath}' AS aux;"
                + "CREATE TABLE aux.data(value BLOB);"
                + "INSERT INTO aux.data(rowid, value) VALUES (1, X'0405');");

            using (var blob = new SqliteBlob(connection, "main", "data", "value", 1))
            {
                blob.Position = 1;
                blob.Write([3], 0, 1);
            }

            using (var blob = new SqliteBlob(connection, "aux", "data", "value", 1))
            {
                blob.Position = 1;
                blob.Write([6], 0, 1);
            }

            connection.ExecuteScalar<byte[]>("SELECT value FROM main.data WHERE rowid = 1;").Should().Equal(1, 3);
            connection.ExecuteScalar<byte[]>("SELECT value FROM aux.data WHERE rowid = 1;").Should().Equal(4, 6);
        }
        finally
        {
            DeleteDatabase(mainPath);
            DeleteDatabase(attachmentPath);
        }
    }

    [Test]
    public void ManagedBlobRejectsWithoutRowidTablesWithoutChangingStoredValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery(
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value BLOB) WITHOUT ROWID;"
            + "INSERT INTO data VALUES (1, X'0102');");

        var error = Assert.Throws<SqliteException>(() => new SqliteBlob(connection, "data", "value", 1));

        error!.SqliteErrorCode.Should().Be(1);
        error.Message.Should().Be(
            Data.Sqlite.Properties.Resources.SqliteNativeError(1, "cannot open table without rowid: data"));
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE id = 1;").Should().Equal(1, 2);
    }

    [Test]
    public void ManagedBlobInvalidatesWhenAnotherColumnOfItsRowChanges()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery(
            "CREATE TABLE data(value BLOB, revision INTEGER);"
            + "INSERT INTO data(rowid, value, revision) VALUES (1, X'0102', 0);");
        using var blob = new SqliteBlob(connection, "data", "value", 1);

        connection.ExecuteNonQuery("UPDATE data SET revision = 1 WHERE rowid = 1;");

        var aborted = Assert.Throws<SqliteException>(() =>
        {
            blob.Read(new byte[1], 0, 1).Should().Be(1);
        });
        aborted!.SqliteErrorCode.Should().Be(4);
        connection.ExecuteScalar<long>("SELECT revision FROM data WHERE rowid = 1;").Should().Be(1);
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1, 2);
    }

    [Test]
    public void ManagedBlobInvalidatesWhenItsRowIsNoOpUpdated()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery(
            "CREATE TABLE data(value BLOB, revision INTEGER);"
            + "INSERT INTO data(rowid, value, revision) VALUES (1, X'0102', 0);");
        using var blob = new SqliteBlob(connection, "data", "value", 1);

        connection.ExecuteNonQuery("UPDATE data SET revision = revision WHERE rowid = 1;");

        var aborted = Assert.Throws<SqliteException>(() =>
        {
            blob.Read(new byte[1], 0, 1).Should().Be(1);
        });
        aborted!.SqliteErrorCode.Should().Be(4);
        connection.ExecuteScalar<long>("SELECT revision FROM data WHERE rowid = 1;").Should().Be(0);
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1, 2);
    }

    [Test]
    public void ManagedBlobRemainsUsableWhenAnotherRowIsNoOpUpdated()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery(
            "CREATE TABLE data(value BLOB, revision INTEGER);"
            + "INSERT INTO data(rowid, value, revision) VALUES (1, X'0102', 0);"
            + "INSERT INTO data(rowid, value, revision) VALUES (2, X'0304', 0);");
        using var blob = new SqliteBlob(connection, "data", "value", 1);

        connection.ExecuteNonQuery("UPDATE data SET revision = revision WHERE rowid = 2;");

        var value = new byte[1];
        blob.Read(value, 0, value.Length).Should().Be(1);
        value.Should().Equal(1);
    }

    [Test]
    public void ManagedBlobWriteRejectsUpdateTriggersWithoutRunningThem()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE data(value BLOB);
            CREATE TABLE audit(value TEXT);
            INSERT INTO data(rowid, value) VALUES (1, X'0102');
            CREATE TRIGGER data_audit AFTER UPDATE ON data
            BEGIN
                INSERT INTO audit VALUES ('updated');
            END;
            """);
        using var blob = new SqliteBlob(connection, "data", "value", 1);

        var error = Assert.Throws<SqliteException>(() => blob.Write([3], 0, 1));

        error!.SqliteErrorCode.Should().Be(1);
        error.Message.Should().Be(
            Ahtola.Data.Sqlite.Properties.Resources.SqliteNativeError(
                1,
                "cannot write to an incremental blob on a table with UPDATE triggers"));
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1, 2);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(0);
    }

    [Test]
    public void ManagedReadOnlyBlobReadsTablesWithUpdateTriggers()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("""
            CREATE TABLE data(value BLOB);
            CREATE TABLE audit(value TEXT);
            INSERT INTO data(rowid, value) VALUES (1, X'0102');
            CREATE TRIGGER data_audit AFTER UPDATE ON data
            BEGIN
                INSERT INTO audit VALUES ('updated');
            END;
            """);
        using var blob = new SqliteBlob(connection, "data", "value", 1, readOnly: true);

        var value = new byte[2];
        blob.Read(value, 0, value.Length).Should().Be(value.Length);

        blob.CanWrite.Should().BeFalse();
        value.Should().Equal(1, 2);
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1, 2);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit;").Should().Be(0);
    }

    [Test]
    public void ManagedBlobParticipatesInTransactionsAndHonorsReadOnlyBlobs()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'0102');");

        using (var transaction = connection.BeginTransaction())
        {
            using var blob = new SqliteBlob(connection, "data", "value", 1);
            blob.Write([3], 0, 1);
            connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(3, 2);
            transaction.Rollback();
        }

        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1, 2);

        using var readOnlyBlob = new SqliteBlob(connection, "data", "value", 1, readOnly: true);
        readOnlyBlob.CanWrite.Should().BeFalse();
        Assert.Throws<NotSupportedException>(() => readOnlyBlob.Write([3], 0, 1))!
            .Message.Should().Be(Data.Sqlite.Properties.Resources.WriteNotSupported);
    }

    [Test]
    public void ManagedBlobDisposalClosesTheStreamingAdapter()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'01');");

        var blob = new SqliteBlob(connection, "data", "value", 1);
        blob.Dispose();

        blob.CanRead.Should().BeFalse();
        Assert.Throws<ObjectDisposedException>(() => _ = blob.Length);
    }

    [Test]
    public async Task ManagedBlobAsyncDisposalRejectsZeroLengthWritesWithoutNativeFallback()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'01');");

        GetPrivateField(connection, "_database").Should().BeNull();
        var blob = new SqliteBlob(connection, "data", "value", 1);
        await blob.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => blob.Write([], 0, 0));
        connection.ExecuteScalar<byte[]>("SELECT value FROM data WHERE rowid = 1;").Should().Equal(1);
    }

    [Test]
    public void ClosingManagedConnectionDisposesOpenIncrementalBlobs()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'01');");

        using var blob = new SqliteBlob(connection, "data", "value", 1);

        connection.Close();

        blob.CanRead.Should().BeFalse();
        blob.CanSeek.Should().BeFalse();
        blob.CanWrite.Should().BeFalse();
        Assert.Throws<ObjectDisposedException>(() => _ = blob.Length);
        Assert.Throws<ObjectDisposedException>(() => blob.Position = 0);
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected {instance.GetType().Name}.{fieldName}.");
        return field.GetValue(instance);
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-incremental-blob-boundary-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
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
