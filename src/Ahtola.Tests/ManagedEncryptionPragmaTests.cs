using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// <c>PRAGMA hexkey</c> / <c>PRAGMA cipher</c> and ATTACH URI <c>cipher</c>/<c>hexkey</c>
/// options (Turso <c>PragmaName::EncryptionKey</c>/<c>EncryptionCipher</c> and
/// <c>OpenOptions</c>). Both used to be accepted as silent no-ops, which left a database the
/// caller believed was encrypted stored in plaintext.
/// </summary>
public sealed class ManagedEncryptionPragmaTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Aegis128Key = "0F0E0D0C0B0A09080706050403020100";

    [Test]
    public void PragmaHexKeyAndCipherReportAnUnencryptedSession()
    {
        using var database = EmbeddedDatabase.OpenFile("pragma-plain.db", new InMemoryFileSystem());
        using var connection = database.Connect();

        ReadRows(connection, "PRAGMA hexkey;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("encryption key is not set for this session"));
        ReadRows(connection, "PRAGMA cipher;").Should().BeEmpty();
    }

    [Test]
    public void PragmaHexKeyAndCipherReportAnEncryptedSessionWithoutEchoingTheKey()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        using var database = EmbeddedDatabase.OpenFile("pragma-encrypted.db", fileSystem);
        using var connection = database.Connect();

        var hexKey = ReadRows(connection, "PRAGMA hexkey;");
        hexKey.Should().ContainSingle().Which.Should().Equal(SqlValue.Text("encryption key is set for this session"));
        ReadRows(connection, "PRAGMA cipher;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("aes256gcm"));
        ReadRows(connection, "PRAGMA temp.cipher;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("aes256gcm"));
    }

    [Test]
    public void PragmaHexKeyAssignmentFailsInsteadOfLeavingThePlaintextFileUnencrypted()
    {
        var inner = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("pragma-assign.db", inner))
        using (var connection = database.Connect())
        {
            var key = () => Execute(connection, $"PRAGMA hexkey = '{Aes256Key}';");
            key.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot enable encryption on an open managed connection*");
            var cipher = () => Execute(connection, "PRAGMA cipher = 'aegis256';");
            cipher.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot enable encryption on an open managed connection*");

            Execute(connection, "CREATE TABLE items(value TEXT);");
        }

        // The file is still plaintext, and nothing claimed otherwise.
        using var reopened = EmbeddedDatabase.OpenFile("pragma-assign.db", inner);
        using var reader = reopened.Connect();
        ReadRows(reader, "SELECT count(*) FROM items;").Should().ContainSingle();
    }

    [Test]
    public void PragmaEncryptionAssignmentValidatesValuesAndRefusesToResetAnEncryptedSession()
    {
        using (var plain = EmbeddedDatabase.OpenFile("pragma-validate.db", new InMemoryFileSystem()))
        using (var connection = plain.Connect())
        {
            var unknown = () => Execute(connection, "PRAGMA cipher = 'rot13';");
            unknown.Should().Throw<EmbeddedSqlException>().WithMessage("Unknown cipher name: rot13");
            var notHex = () => Execute(connection, "PRAGMA hexkey = 'zz';");
            notHex.Should().Throw<EmbeddedSqlException>().WithMessage("Invalid hex string*");
            var shortKey = () => Execute(connection, "PRAGMA hexkey = '0011';");
            shortKey.Should().Throw<EmbeddedSqlException>()
                .WithMessage("Hex string must decode to exactly 16 or 32 bytes, got 2");
        }

        using var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        using var database = EmbeddedDatabase.OpenFile("pragma-reset.db", fileSystem);
        using var encrypted = database.Connect();
        var reset = () => Execute(encrypted, $"PRAGMA hexkey = '{Aes256Key}';");
        reset.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot reset encryption attributes if already set in the session");
        var resetCipher = () => Execute(encrypted, "PRAGMA cipher = 'aes-256-gcm';");
        resetCipher.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot reset encryption attributes if already set in the session");
    }

    [Test]
    public void SqliteFacadeReportsEncryptionPragmaColumns()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"pragma-facade-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new Ahtola.Data.Sqlite.SqliteConnection(
                $"Data Source={path};Local Provider=Managed;Pooling=False;"
                + $"Encryption Cipher=aegis128l;Encryption Key={Aegis128Key}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cipher;";
            using (var reader = command.ExecuteReader())
            {
                reader.GetName(0).Should().Be("cipher");
                reader.Read().Should().BeTrue();
                reader.GetString(0).Should().Be("aegis128l");
            }

            command.CommandText = "PRAGMA hexkey;";
            using (var reader = command.ExecuteReader())
            {
                reader.GetName(0).Should().Be("hexkey");
                reader.Read().Should().BeTrue();
                reader.GetString(0).Should().Be("encryption key is set for this session");
            }
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void AttachUriCipherAndHexKeyEncryptTheAttachedDatabase()
    {
        var inner = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("attach-uri-main.db", inner))
        using (var connection = main.Connect())
        {
            Execute(connection, $"ATTACH DATABASE 'file:attach-uri-aux.db?cipher=aegis256&hexkey={Aes256Key}' AS aux;");
            Execute(connection, "CREATE TABLE aux.items(value TEXT);");
            Execute(connection, "INSERT INTO aux.items VALUES ('secret');");
            Execute(connection, "DETACH aux;");

            Execute(connection, $"ATTACH DATABASE 'file:attach-uri-aux.db?cipher=aegis256&hexkey={Aes256Key}' AS aux;");
            ReadRows(connection, "SELECT value FROM aux.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("secret"));
            Execute(connection, "DETACH aux;");
        }

        // Without the key the attached file is not a readable plaintext database.
        var plainOpen = () =>
        {
            using var plain = EmbeddedDatabase.OpenFile("attach-uri-aux.db", inner);
            using var plainConnection = plain.Connect();
            ReadRows(plainConnection, "SELECT value FROM items;");
        };
        plainOpen.Should().Throw<Exception>();

        using var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aegis256, Aes256Key);
        using var keyed = new AhtolaEncryptionFileSystem(inner, encryption);
        using var reopened = EmbeddedDatabase.OpenFile("attach-uri-aux.db", keyed);
        using var reader = reopened.Connect();
        ReadRows(reader, "SELECT value FROM items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("secret"));
    }

    [Test]
    public void AttachUriEncryptionOptionsMustBeCompleteAndValid()
    {
        using var main = EmbeddedDatabase.OpenFile("attach-uri-errors.db", new InMemoryFileSystem());
        using var connection = main.Connect();

        var cipherOnly = () => Execute(connection, "ATTACH DATABASE 'file:aux-a.db?cipher=aegis256' AS aux;");
        cipherOnly.Should().Throw<EmbeddedSqlException>().WithMessage("hexkey is required when cipher is provided");
        var keyOnly = () => Execute(connection, $"ATTACH DATABASE 'file:aux-b.db?hexkey={Aes256Key}' AS aux;");
        keyOnly.Should().Throw<EmbeddedSqlException>().WithMessage("cipher is required when hexkey is provided");
        var unknown = () => Execute(connection, $"ATTACH DATABASE 'file:aux-c.db?cipher=rot13&hexkey={Aes256Key}' AS aux;");
        unknown.Should().Throw<EmbeddedSqlException>().WithMessage("Unknown cipher name: rot13");
        var both = () => Execute(
            connection,
            $"ATTACH DATABASE 'file:aux-d.db?cipher=aegis256&hexkey={Aes256Key}' AS aux KEY '{Aes256Key}';");
        both.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot combine KEY with URI cipher/hexkey*");
        var memory = () => Execute(connection, $"ATTACH DATABASE 'file::memory:?cipher=aegis256&hexkey={Aes256Key}' AS aux;");
        memory.Should().Throw<EmbeddedSqlException>().WithMessage("*not supported for in-memory attachments*");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
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

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
