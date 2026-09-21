using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedEncryptedFileOpenContractTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public void ManagedProviderCreatesAndReopensAnEncryptedFileWithTheExactHexKey()
    {
        var path = CreateDatabasePath("raw-key");
        try
        {
            using (var create = new global::Ahtola.AhtolaConnection(ConnectionString(path, Aes256Key)))
            {
                create.Open();
                create.ExecuteNonQuery("CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
                create.ExecuteNonQuery("INSERT INTO records VALUES (7, 'encrypted');");
            }

            File.ReadAllBytes(path).AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());

            using (var reopen = new SqliteConnection(ConnectionString(path, Aes256Key)))
            {
                reopen.Open();
                reopen.ExecuteScalar<string>("SELECT value FROM records WHERE id = 7;").Should().Be("encrypted");
            }

            var wrongKeyFailure = Assert.Throws<InvalidDataException>(() =>
            {
                using var wrongKey = new SqliteConnection(ConnectionString(path, WrongAes256Key));
                wrongKey.Open();
            });
            wrongKeyFailure!.Message.Should().Contain("failed authentication");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedProviderRequiresAHexadecimalKeyWithAnExplicitCipher()
    {
        using var connection = new global::Ahtola.AhtolaConnection(
            "Data Source=:memory:;Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key=not-hex");

        Assert.Throws<ArgumentException>(() => connection.Open())!.Message
            .Should().Contain("hexadecimal");
    }

    [Test]
    public void PasswordKeywordsAreRejected()
    {
        var password = () => new SqliteConnectionStringBuilder("Data Source=app.db;Password=secret");
        var passwordScheme = () => new global::Ahtola.AhtolaConnectionStringBuilder(
            "Data Source=app.db;Password Scheme=Ahtola.Password.v1");

        password.Should().Throw<ArgumentException>();
        passwordScheme.Should().Throw<ArgumentException>();
    }

    private static string ConnectionString(string path, string key)
        => $"Data Source={path};Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={key};Pooling=False";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-encrypted-file-open-contract-tests");
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
