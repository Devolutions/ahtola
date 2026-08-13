using System.Data;
using System.Data.Common;
using System.Reflection;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class FacadeEncryptionFileSystemSnapshotOwnershipRegressionTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [TestCase("sqlite")]
    [TestCase("Ahtola")]
    public void FacadeOwnedEncryptionSnapshotsAreReleasedByIdempotentClose(string facade)
    {
        var path = CreateDatabasePath($"{facade}-close");
        try
        {
            using var connection = CreateConnection(facade, CreateConnectionString(path, Aes256Key));
            connection.Open();
            Execute(connection, "CREATE TABLE entries(value INTEGER);");

            var firstSnapshot = GetOwnedEncryptionFileSystem(connection);
            _ = firstSnapshot.Encryption;

            connection.Close();
            connection.Close();

            connection.State.Should().Be(ConnectionState.Closed);
            Assert.Throws<ObjectDisposedException>(() => _ = firstSnapshot.Encryption);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();

            connection.Open();
            var secondSnapshot = GetOwnedEncryptionFileSystem(connection);
            secondSnapshot.Should().NotBeSameAs(firstSnapshot);

            connection.Close();

            Assert.Throws<ObjectDisposedException>(() => _ = secondSnapshot.Encryption);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase("sqlite")]
    [TestCase("Ahtola")]
    public void FacadeFailedEncryptedOpenReleasesSnapshotWithoutChangingDiagnostics(string facade)
    {
        var path = CreateDatabasePath($"{facade}-failed-open");
        try
        {
            using (var create = CreateConnection(facade, CreateConnectionString(path, Aes256Key)))
            {
                create.Open();
                Execute(create, "CREATE TABLE entries(value INTEGER);");
                Execute(create, "INSERT INTO entries VALUES (7);");
            }

            using var connection = CreateConnection(facade, CreateConnectionString(path, WrongAes256Key));
            Assert.Throws<InvalidDataException>(() => connection.Open())!
                .Message.Should().Contain("failed authentication");

            connection.State.Should().Be(ConnectionState.Closed);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();
            connection.Close();
            connection.Close();

            connection.ConnectionString = CreateConnectionString(path, Aes256Key);
            connection.Open();
            Scalar(connection, "SELECT value FROM entries;").Should().Be(7L);

            var snapshot = GetOwnedEncryptionFileSystem(connection);
            connection.Close();

            Assert.Throws<ObjectDisposedException>(() => _ = snapshot.Encryption);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static DbConnection CreateConnection(string facade, string connectionString)
        => facade switch
        {
            "sqlite" => new SqliteConnection(connectionString),
            "Ahtola" => new global::Ahtola.AhtolaConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(facade)),
        };

    private static string CreateConnectionString(string path, string key)
        => $"Data Source={path};Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={key}";

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static AhtolaEncryptionFileSystem GetOwnedEncryptionFileSystem(DbConnection connection)
        => GetOwnedEncryptionFileSystemOrNull(connection)
           ?? throw new AssertionException("The managed connection did not retain its encryption file system after opening.");

    private static AhtolaEncryptionFileSystem? GetOwnedEncryptionFileSystemOrNull(DbConnection connection)
    {
        var field = connection.GetType().GetField(
            "_managedEncryptionFileSystem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("managed encrypted opens retain their facade-created file system until shutdown");
        return (AhtolaEncryptionFileSystem?)field!.GetValue(connection);
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "facade-encryption-file-system-snapshot-ownership-regression-tests");
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
