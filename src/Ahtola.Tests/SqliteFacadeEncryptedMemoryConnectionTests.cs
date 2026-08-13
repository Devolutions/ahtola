using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class SqliteFacadeEncryptedMemoryConnectionTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string MemoryEncryptionError = "Encryption is supported only for file-backed databases when Local Provider=Managed.";

    [TestCase("Encryption Cipher=Aes256Gcm;Encryption Key=" + Aes256Key)]
    [TestCase("EncryptionCipher=Aes256Gcm;EncryptionKey=" + Aes256Key)]
    public void SqliteFacadeRejectsEncryptedDirectMemoryDataSourceWithoutCreatingPhysicalArtifact(string encryptionOptions)
    {
        var memoryArtifact = Path.Combine(Environment.CurrentDirectory, ":memory:");
        File.Exists(memoryArtifact).Should().BeFalse();

        using var connection = new SqliteConnection(
            $"Data Source=:memory:;Local Provider=Managed;{encryptionOptions}");

        Assert.Throws<NotSupportedException>(() => connection.Open())!.Message.Should().Be(MemoryEncryptionError);

        connection.State.Should().Be(System.Data.ConnectionState.Closed);
        File.Exists(memoryArtifact).Should().BeFalse();
    }

    [Test]
    public void SqliteFacadeRejectsEncryptedMemoryModeWithoutCreatingPhysicalArtifact()
    {
        var dataSource = $"encrypted-memory-{Guid.NewGuid():N}";
        var physicalPath = Path.Combine(AppContext.BaseDirectory, dataSource);
        File.Exists(physicalPath).Should().BeFalse();

        using var connection = new SqliteConnection(
            $"Data Source={dataSource};Mode=Memory;Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={Aes256Key}");

        Assert.Throws<NotSupportedException>(() => connection.Open())!.Message.Should().Be(MemoryEncryptionError);

        connection.State.Should().Be(System.Data.ConnectionState.Closed);
        File.Exists(physicalPath).Should().BeFalse();
    }

    [Test]
    public void SqliteFacadeAllowsEncryptedFileCreateAndReopen()
    {
        var path = CreateDatabasePath();
        var connectionString =
            $"Data Source={path};Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={Aes256Key}";
        try
        {
            using (var create = new SqliteConnection(connectionString))
            {
                create.Open();
                create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                create.ExecuteNonQuery("INSERT INTO records VALUES (1, 'encrypted');");
            }

            using var reopen = new SqliteConnection(connectionString);
            reopen.Open();
            reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 1;").Should().Be("encrypted");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sqlite-facade-encrypted-memory-connection-tests");
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
