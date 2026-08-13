using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class AhtolaEncryptionOptionsOwnershipBoundaryTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public void FileSystemOwnsEncryptionSnapshotAfterSuccessfulAndFailedOpens()
    {
        var fileSystem = new InMemoryFileSystem();

        using var validOptions = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var validFileSystem = new AhtolaEncryptionFileSystem(fileSystem, validOptions);
        using (var database = EmbeddedDatabase.OpenFile("ownership-success.db", validFileSystem))
        {
            validOptions.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => new AhtolaEncryptionFileSystem(fileSystem, validOptions));

            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE entries(value INTEGER);");
            Execute(connection, "INSERT INTO entries VALUES (7);");
            Scalar(connection, "SELECT value FROM entries;").Should().Be(7);
        }

        using (var reopened = EmbeddedDatabase.OpenFile("ownership-success.db", validFileSystem))
        using (var connection = reopened.Connect())
        {
            Scalar(connection, "SELECT value FROM entries;").Should().Be(7);
            Execute(connection, "INSERT INTO entries VALUES (8);");
        }

        using var invalidOptions = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, WrongAes256Key);
        using var invalidFileSystem = new AhtolaEncryptionFileSystem(fileSystem, invalidOptions);
        Assert.Throws<InvalidDataException>(
            () => EmbeddedDatabase.OpenFile("ownership-success.db", invalidFileSystem))!
            .Message.Should().Contain("failed authentication");
        invalidOptions.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => new AhtolaEncryptionFileSystem(fileSystem, invalidOptions));

        using (var database = EmbeddedDatabase.OpenFile("ownership-failure.db", invalidFileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entries(value INTEGER);");
            Execute(connection, "INSERT INTO entries VALUES (11);");
            Scalar(connection, "SELECT value FROM entries;").Should().Be(11);
        }

        using (var reopened = EmbeddedDatabase.OpenFile("ownership-failure.db", invalidFileSystem))
        using (var connection = reopened.Connect())
        {
            Scalar(connection, "SELECT value FROM entries;").Should().Be(11);
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }
}
