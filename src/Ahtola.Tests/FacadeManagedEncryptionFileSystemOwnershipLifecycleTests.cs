using System.Data;
using System.Reflection;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class FacadeManagedEncryptionFileSystemOwnershipLifecycleTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public void SqliteFacadeDisposesItsEncryptionSnapshotAfterIdempotentClose()
    {
        var path = CreateDatabasePath("facade-close");
        try
        {
            using var connection = new SqliteConnection(CreateConnectionString(path, Aes256Key));
            connection.Open();

            var fileSystem = GetOwnedEncryptionFileSystem(connection);
            _ = fileSystem.Encryption;

            connection.Close();
            connection.Close();

            connection.State.Should().Be(ConnectionState.Closed);
            Assert.Throws<ObjectDisposedException>(() => _ = fileSystem.Encryption);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SqliteFacadeFailedEncryptedOpenDoesNotRetainAnEncryptionFileSystem()
    {
        var path = CreateDatabasePath("facade-failed-open");
        try
        {
            using (var create = new SqliteConnection(CreateConnectionString(path, Aes256Key)))
            {
                create.Open();
            }

            using var connection = new SqliteConnection(CreateConnectionString(path, WrongAes256Key));
            Assert.Throws<InvalidDataException>(() => connection.Open())!.Message.Should().Contain("failed authentication");

            connection.State.Should().Be(ConnectionState.Closed);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();
            connection.Close();
            connection.Close();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void AhtolaConnectionDisposesItsEncryptionSnapshotAfterIdempotentClose()
    {
        var path = CreateDatabasePath("provider-close");
        try
        {
            using var connection = new global::Ahtola.AhtolaConnection(CreateConnectionString(path, Aes256Key));
            connection.Open();

            var fileSystem = GetOwnedEncryptionFileSystem(connection);
            _ = fileSystem.Encryption;

            connection.Close();
            connection.Close();

            connection.State.Should().Be(ConnectionState.Closed);
            Assert.Throws<ObjectDisposedException>(() => _ = fileSystem.Encryption);
            GetOwnedEncryptionFileSystemOrNull(connection).Should().BeNull();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static string CreateConnectionString(string path, string key)
        => $"Data Source={path};Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={key}";

    private static AhtolaEncryptionFileSystem GetOwnedEncryptionFileSystem(object connection)
        => GetOwnedEncryptionFileSystemOrNull(connection)
           ?? throw new AssertionException("The managed connection did not retain its encryption file system after opening.");

    private static AhtolaEncryptionFileSystem? GetOwnedEncryptionFileSystemOrNull(object connection)
    {
        var field = connection.GetType().GetField(
            "_managedEncryptionFileSystem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("managed encrypted opens retain their facade-created file system until shutdown");
        return (AhtolaEncryptionFileSystem?)field!.GetValue(connection);
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "facade-managed-encryption-file-system-ownership-lifecycle-tests");
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
