using System.Text;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Storage;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// <c>PRAGMA journal_mode=mvcc</c> must fail closed for every AEGIS-encrypted
/// browser database, before header 255 is persisted and before a single
/// <c>MVTX</c> frame reaches the persistent store.
/// </summary>
/// <remarks>
/// <para>
/// Turso's logical-log frame reserves a fixed 16-byte tag plus a 12-byte nonce
/// per chunk, and that overhead is baked into every payload-size and CRC
/// computation, so the framing only fits AES-GCM. The AEGIS ciphers use 16- or
/// 32-byte nonces and Turso format version 0 defines no logical-log framing for
/// them.
/// </para>
/// <para>
/// The browser encrypts on its way to OPFS rather than through
/// <see cref="AhtolaEncryptionFileSystem"/>, so without an explicit gate the
/// core would happily switch the pager into MVCC and only discover the mismatch
/// during the asynchronous flush that follows a successful <c>COMMIT</c> —
/// after the engine reported durability. These tests pin the gate to the pragma
/// boundary and prove no MVCC artifact is ever produced.
/// </para>
/// </remarks>
[NonParallelizable]
public sealed class AhtolaBrowserAegisMvccGateTests
{
    private const string OwnedDirectory = "app/data";
    private const string DatabasePath = "app/data/main.db";
    private const string LogPath = DatabasePath + "-log";
    private const string Secret = "aegis-mvcc-gate-canary";
    private const string Key128 = "000102030405060708090A0B0C0D0E0F";
    private const string Key256 = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    public static IEnumerable<TestCaseData> AegisCiphers()
    {
        yield return new TestCaseData(StorageCipher.Aegis256, Key256, 32).SetName("{m}(Aegis256)");
        yield return new TestCaseData(StorageCipher.Aegis256X2, Key256, 32).SetName("{m}(Aegis256X2)");
        yield return new TestCaseData(StorageCipher.Aegis256X4, Key256, 32).SetName("{m}(Aegis256X4)");
        yield return new TestCaseData(StorageCipher.Aegis128L, Key128, 16).SetName("{m}(Aegis128L)");
        yield return new TestCaseData(StorageCipher.Aegis128X2, Key128, 16).SetName("{m}(Aegis128X2)");
        yield return new TestCaseData(StorageCipher.Aegis128X4, Key128, 16).SetName("{m}(Aegis128X4)");
    }

    public static IEnumerable<TestCaseData> AesCiphers()
    {
        yield return new TestCaseData(StorageCipher.Aes128Gcm, Key128).SetName("{m}(Aes128Gcm)");
        yield return new TestCaseData(StorageCipher.Aes256Gcm, Key256).SetName("{m}(Aes256Gcm)");
    }

    /// <summary>
    /// The pragma throws, names the cipher and its nonce width, and leaves the
    /// database in its previous journal mode with no logical log on disk.
    /// </summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public async Task AegisRejectsMvccJournalModeAtThePragmaBoundary(
        StorageCipher cipher,
        string hexKey,
        int nonceSize)
    {
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory);
        using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE notes(body TEXT);");
        Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
        var before = ScalarText(connection, "PRAGMA journal_mode;");

        var failure = Assert.Throws<NotSupportedException>(
            () => Execute(connection, "PRAGMA journal_mode=mvcc;"))!;

        failure.Message.Should().Contain(cipher.ToString());
        failure.Message.Should().Contain($"{nonceSize}-byte nonce");
        failure.Message.Should().Contain("supports only the AES-GCM");

        ScalarText(connection, "PRAGMA journal_mode;").Should().Be(
            before,
            "a refused mode switch must not move the pager");
        database.IsMvccEnabled.Should().BeFalse();

        await harness.Mirror.FlushPendingAsync();
        store.Contains(LogPath).Should().BeFalse("no logical log may be created for a refused mode");
    }

    /// <summary>
    /// The alternate spellings the parser accepts must be refused identically,
    /// and so must a repeat attempt after the first refusal.
    /// </summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public async Task AegisRejectsEveryMvccSpellingAndStaysUsable(
        StorageCipher cipher,
        string hexKey,
        int nonceSize)
    {
        _ = nonceSize;
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory);
        using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE notes(body TEXT);");

        foreach (var spelling in new[] { "mvcc", "MVCC", "experimental_mvcc" })
        {
            Assert.Throws<NotSupportedException>(
                () => Execute(connection, $"PRAGMA journal_mode={spelling};"))!
                .Message.Should().Contain("supports only the AES-GCM");
            database.IsMvccEnabled.Should().BeFalse();
        }

        // The connection must still be fully usable after a refused switch.
        Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
        ScalarText(connection, "SELECT body FROM notes;").Should().Be(Secret);
    }

    /// <summary>
    /// A refused switch must not cost durability: the ordinary WAL commit that
    /// follows it still persists, encrypted, and survives a reopen.
    /// </summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public async Task AegisCommitsAndReopensAfterTheRefusedSwitch(
        StorageCipher cipher,
        string hexKey,
        int nonceSize)
    {
        _ = nonceSize;
        var store = new FakeBrowserPersistentStore();
        Dictionary<string, byte[]> durableImages;
        await using (var harness = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            Assert.Throws<NotSupportedException>(
                () => Execute(connection, "PRAGMA journal_mode=mvcc;"));

            Execute(connection, "BEGIN;");
            Execute(connection, $"INSERT INTO notes VALUES (1, '{Secret}');");
            Execute(connection, "COMMIT;");
            await harness.Mirror.FlushPendingAsync();
            durableImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in durableImages)
            store.Seed(path, image);

        store.Contains(LogPath).Should().BeFalse();
        Encoding.ASCII.GetString(store.Read(DatabasePath), 0, 5).Should().Be("AHTLA");
        foreach (var path in store.Paths)
            store.Read(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(-1);

        await using var reopened = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory);
        using var reopenedDatabase = EmbeddedDatabase.OpenFile(DatabasePath, reopened.Mirror);
        using var reopenedConnection = reopenedDatabase.Connect();
        ScalarText(reopenedConnection, "SELECT body FROM notes;").Should().Be(Secret);
        ScalarText(reopenedConnection, "PRAGMA journal_mode;").Should().NotBe("mvcc");

        // The refusal is a property of the cipher, not of one connection.
        Assert.Throws<NotSupportedException>(
            () => Execute(reopenedConnection, "PRAGMA journal_mode=mvcc;"));
    }

    /// <summary>
    /// The AES controls prove the gate is cipher-specific rather than a blanket
    /// browser restriction: MVCC still opens, commits, and reopens.
    /// </summary>
    [TestCaseSource(nameof(AesCiphers))]
    public async Task AesGcmStillEnablesMvccAndSurvivesReopen(StorageCipher cipher, string hexKey)
    {
        var store = new FakeBrowserPersistentStore();
        Dictionary<string, byte[]> durableImages;
        await using (var harness = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            ScalarText(connection, "PRAGMA journal_mode=mvcc;").Should().Be("mvcc");
            database.IsMvccEnabled.Should().BeTrue();

            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            Execute(connection, "COMMIT;");
            await harness.Mirror.FlushPendingAsync();
            durableImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in durableImages)
            store.Seed(path, image);

        store.Contains(LogPath).Should().BeTrue();
        store.Read(LogPath).Length.Should().BeGreaterThan(MvccLogicalLogFormat.LogHeaderSize);
        foreach (var path in store.Paths)
            store.Read(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(-1);

        await using var reopened = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory);
        using var reopenedDatabase = EmbeddedDatabase.OpenFile(DatabasePath, reopened.Mirror);
        using var reopenedConnection = reopenedDatabase.Connect();
        ScalarText(reopenedConnection, "SELECT body FROM notes;").Should().Be(Secret);
        ScalarText(reopenedConnection, "PRAGMA journal_mode;").Should().Be("mvcc");
    }

    /// <summary>
    /// The mirror answers the gate directly, without I/O, so the core can ask at
    /// the pragma boundary. An unencrypted mirror imposes no restriction.
    /// </summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public async Task MirrorReportsTheCipherSpecificReason(
        StorageCipher cipher,
        string hexKey,
        int nonceSize)
    {
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory);

        MvccLogicalLog.DescribeMvccUnsupportedReason(harness.Mirror)
            .Should().Contain($"{nonceSize}-byte nonce");
        MvccLogicalLog.DescribeMvccUnsupportedCipher(cipher).Should().Contain(cipher.ToString());
    }

    [TestCaseSource(nameof(AesCiphers))]
    public async Task MirrorImposesNoRestrictionForAesGcm(StorageCipher cipher, string hexKey)
    {
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserCipherHarness.CreateAsync(
            store,
            cipher,
            hexKey,
            OwnedDirectory);

        MvccLogicalLog.DescribeMvccUnsupportedReason(harness.Mirror).Should().BeNull();
        MvccLogicalLog.DescribeMvccUnsupportedCipher(cipher).Should().BeNull();
    }

    /// <summary>
    /// The desktop engine refuses the same ciphers at the same boundary, so a
    /// database can never be switched into a mode only one of the two can leave.
    /// </summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public void DesktopEncryptionFileSystemRefusesMvccAtThePragmaBoundary(
        StorageCipher cipher,
        string hexKey,
        int nonceSize)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "aegis-mvcc-gate-tests");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, $"main-{Guid.NewGuid():N}.db");
        try
        {
            using var options = new AhtolaEncryptionOptions(cipher, Convert.FromHexString(hexKey));
            using var fileSystem = new AhtolaEncryptionFileSystem(new PhysicalFileSystem(), options);
            using (var database = EmbeddedDatabase.OpenFile(databasePath, fileSystem))
            {
                using var connection = database.Connect();
                Execute(connection, "CREATE TABLE notes(body TEXT);");

                Assert.Throws<NotSupportedException>(
                        () => Execute(connection, "PRAGMA journal_mode=mvcc;"))!
                    .Message.Should().Contain($"{nonceSize}-byte nonce");
                database.IsMvccEnabled.Should().BeFalse();
            }

            File.Exists(databasePath + "-log").Should().BeFalse();
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", "-log" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static string ScalarText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }
}
