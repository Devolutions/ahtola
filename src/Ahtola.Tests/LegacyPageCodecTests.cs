using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using Ahtola.Data.Sqlite.Codecs;

namespace Ahtola.Tests;

/// <summary>
/// The legacy password page codecs read and write the files the native engines wrote: the
/// fixtures come from System.Data.SQLite (plain and RC4 <c>Password</c>) and from
/// wxSQLite3/sqlite3secure (AES-128-CBC), each with an Items table of 60 rows.
/// </summary>
[NonParallelizable]
public sealed class LegacyPageCodecTests
{
    private const string FixturePassword = "Fixture-Pässwörd1";
    private const long FixtureRowCount = 60;

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ahtola-legacy-codecs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestCase("system-data-sqlite-rc4.db", LegacyPageCodecFormat.SystemDataSQLiteRc4)]
    [TestCase("wxsqlite3-aes128.db", LegacyPageCodecFormat.WxSQLite3Aes128)]
    public void PasswordKeywordOpensAFileTheNativeEngineEncrypted(string fixture, LegacyPageCodecFormat format)
    {
        var path = CopyFixture(fixture);
        LegacyPageCodecs.DetectFile(path, FixturePassword).Should().Be(format);

        using var connection = Open(path, $"Password={FixturePassword}");
        connection.SelectedPageCodec.Should().BeOfType(format == LegacyPageCodecFormat.SystemDataSQLiteRc4
            ? typeof(SystemDataSQLiteRc4PageCodec)
            : typeof(WxSQLite3Aes128PageCodec));
        Scalar(connection, "PRAGMA integrity_check;").Should().Be("ok");
        Scalar(connection, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount);
    }

    [Test]
    public void APlainSystemDataSqliteFileNeedsNoCodec()
    {
        var path = CopyFixture("system-data-sqlite-plain.db");
        LegacyPageCodecs.DetectFile(path, FixturePassword).Should().Be(LegacyPageCodecFormat.None);

        using var connection = Open(path, string.Empty);
        connection.SelectedPageCodec.Should().BeNull();
        Scalar(connection, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount);
    }

    [TestCase("system-data-sqlite-rc4.db")]
    [TestCase("wxsqlite3-aes128.db")]
    public void AWrongPasswordFailsAsNotADatabase(string fixture)
    {
        var path = CopyFixture(fixture);
        LegacyPageCodecs.DetectFile(path, "wrong").Should().BeNull();

        var failure = Assert.Throws<SqliteException>(() => Open(path, "Password=wrong").Dispose())!;
        failure.SqliteErrorCode.Should().Be(26);
        failure.Message.Should().Contain("file is encrypted or is not a database");

        using var codec = new WxSQLite3Aes128PageCodec("wrong");
        using var explicitCodec = new SqliteConnection($"Data Source={path};Pooling=False") { PageCodec = codec };
        Assert.Throws<SqliteException>(() => explicitCodec.Open())!.SqliteErrorCode.Should().Be(26);
    }

    [TestCase("system-data-sqlite-rc4.db", LegacyPageCodecFormat.SystemDataSQLiteRc4)]
    [TestCase("wxsqlite3-aes128.db", LegacyPageCodecFormat.WxSQLite3Aes128)]
    public void WritesKeepTheFileInItsFormat(string fixture, LegacyPageCodecFormat format)
    {
        var path = CopyFixture(fixture);
        using (var connection = Open(path, $"Password={FixturePassword}"))
        {
            using var transaction = connection.BeginTransaction();
            for (var i = 0; i < 200; i++)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO Items(ID, Name, Data) VALUES ($id, $name, randomblob(400));";
                insert.Parameters.AddWithValue("$id", Guid.NewGuid());
                insert.Parameters.AddWithValue("$name", "new-" + i);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        SqliteConnection.ClearAllPools();
        LegacyPageCodecs.DetectFile(path, FixturePassword).Should().Be(format);
        using var reopened = Open(path, $"Password={FixturePassword}");
        Scalar(reopened, "PRAGMA integrity_check;").Should().Be("ok");
        Scalar(reopened, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount + 200);
    }

    [TestCase("system-data-sqlite-rc4.db", LegacyPageCodecFormat.SystemDataSQLiteRc4)]
    [TestCase("wxsqlite3-aes128.db", LegacyPageCodecFormat.WxSQLite3Aes128)]
    public void ChangePageCodecRekeysAndDecryptsInPlace(string fixture, LegacyPageCodecFormat format)
    {
        var path = CopyFixture(fixture);
        using (var connection = Open(path, $"Password={FixturePassword}"))
        {
            connection.ChangePageCodec(LegacyPageCodecs.Create(format, "Another password"));
            Scalar(connection, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount);
        }

        LegacyPageCodecs.DetectFile(path, "Another password").Should().Be(format);
        LegacyPageCodecs.DetectFile(path, FixturePassword).Should().BeNull();
        File.Exists(path + ".ahtola-rekey").Should().BeFalse();

        using (var connection = Open(path, "Password=Another password"))
        {
            connection.ChangePageCodec(null);
            connection.SelectedPageCodec.Should().BeNull();
            Scalar(connection, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount);
        }

        LegacyPageCodecs.DetectFile(path, FixturePassword).Should().Be(LegacyPageCodecFormat.None);
        using var native = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        native.Open();
        using var count = native.CreateCommand();
        count.CommandText = "SELECT count(*) FROM Items;";
        count.ExecuteScalar().Should().Be(FixtureRowCount);
    }

    [Test]
    public void ChangePageCodecFailsBusyWhileAnotherConnectionIsOpen()
    {
        var path = CopyFixture("system-data-sqlite-plain.db");
        using var other = Open(path, string.Empty);
        Scalar(other, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount);
        using var connection = Open(path, string.Empty);

        Assert.Throws<SqliteException>(() => connection.ChangePageCodec(new SystemDataSQLiteRc4PageCodec("x")))!
            .SqliteErrorCode.Should().Be(5);
    }

    [Test]
    public void AnEmptyFileWithAPasswordBecomesAnEncryptedDatabase()
    {
        // System.Data.SQLite's CreateFile() makes a zero-length file before the first open.
        var path = Path.Combine(_directory, "new.db");
        File.WriteAllBytes(path, []);
        using (var connection = Open(path, $"Password={FixturePassword}"))
            Execute(connection, "CREATE TABLE Items(ID guid NOT NULL PRIMARY KEY, Name varchar(50));");

        SqliteConnection.ClearAllPools();
        LegacyPageCodecs.DetectFile(path, FixturePassword).Should().Be(LegacyPageCodecFormat.SystemDataSQLiteRc4);
    }

    [Test]
    public void CandidatesPickTheCodecThatMatchesTheFile()
    {
        var path = CopyFixture("wxsqlite3-aes128.db");
        var rc4 = LegacyPageCodecs.Create(LegacyPageCodecFormat.SystemDataSQLiteRc4, FixturePassword);
        using var aes = new WxSQLite3Aes128PageCodec(FixturePassword);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False")
        {
            PageCodecCandidates = [rc4, aes],
        };
        connection.Open();

        connection.SelectedPageCodec.Should().BeSameAs(aes);
        Scalar(connection, "SELECT count(*) FROM Items;").Should().Be(FixtureRowCount);
    }

    [Test]
    public void CodecsReportWhetherAHeaderIsTheirs()
    {
        var rc4File = File.ReadAllBytes(FixturePath("system-data-sqlite-rc4.db")).AsSpan(0, 32).ToArray();
        var aesFile = File.ReadAllBytes(FixturePath("wxsqlite3-aes128.db")).AsSpan(0, 32).ToArray();
        using var aes = new WxSQLite3Aes128PageCodec(FixturePassword);
        IPageCodec rc4 = new SystemDataSQLiteRc4PageCodec(FixturePassword);

        rc4.MatchesHeader(rc4File).Should().BeTrue();
        rc4.MatchesHeader(aesFile).Should().BeFalse();
        ((IPageCodec)aes).MatchesHeader(aesFile).Should().BeTrue();
        ((IPageCodec)aes).MatchesHeader(rc4File).Should().BeFalse();
        rc4.MatchesHeader(rc4File.AsSpan(0, 4)).Should().BeNull();
    }

    private static string FixturePath(string fixture)
        => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "LegacyCodecs", fixture);

    private string CopyFixture(string fixture)
    {
        var path = Path.Combine(_directory, fixture);
        File.Copy(FixturePath(fixture), path);
        return path;
    }

    private static SqliteConnection Open(string path, string extra)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;{extra}");
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
