using System.Text;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// Proves that the browser package's asynchronous AHTLA encryption produces and
/// consumes exactly the bytes the desktop engine writes, so an encrypted OPFS
/// database is a plain Ahtola encrypted database rather than a browser container.
/// </summary>
[NonParallelizable]
public sealed class AhtolaBrowserEncryptedStorageTests
{
    private const string OwnedDirectory = "app/data";
    private const string DatabasePath = "app/data/browser.db";
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Aes128Key = "000102030405060708090A0B0C0D0E0F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";
    private const string Secret = "browser-plaintext-canary";

    [TestCase(Aes128Key, Core.Storage.AhtolaEncryptionCipher.Aes128Gcm)]
    [TestCase(Aes256Key, Core.Storage.AhtolaEncryptionCipher.Aes256Gcm)]
    public async Task AsyncPageTransformerRoundTripsThroughDesktopPageEncryption(
        string hexKey,
        Core.Storage.AhtolaEncryptionCipher cipher)
    {
        const int PageSize = 4096;
        var key = Convert.FromHexString(hexKey);
        await using var transformer = new AhtolaAsyncPageTransformer(new DesktopAsyncPageCipher(cipher, key));
        using var options = new AhtolaEncryptionOptions(cipher, key);
        using var desktop = options.CreatePageEncryption(PageSize);

        foreach (var pageNumber in new uint[] { 1, 2, 37 })
        {
            var plaintext = CreatePlaintextPage(PageSize, pageNumber);

            var browserEncrypted = await transformer.EncryptPageAsync(plaintext, pageNumber, default);
            desktop.DecryptPage(browserEncrypted, pageNumber).Should().Equal(
                plaintext,
                "the desktop engine must be able to decrypt a page the browser wrote");

            var desktopEncrypted = desktop.EncryptPage(plaintext, pageNumber);
            (await transformer.DecryptPageAsync(desktopEncrypted, pageNumber, default)).Should().Equal(
                plaintext,
                "the browser must be able to decrypt a page the desktop engine wrote");

            if (pageNumber == 1)
            {
                Encoding.ASCII.GetString(browserEncrypted, 0, 5).Should().Be("AHTLA");
                browserEncrypted[5].Should().Be(0);
                browserEncrypted[6].Should().Be((byte)cipher);
                browserEncrypted.AsSpan(16, 84).ToArray().Should().Equal(
                    plaintext.AsSpan(16, 84).ToArray(),
                    "the visible SQLite header tail is copied verbatim and authenticated");
            }
        }
    }

    [Test]
    public async Task BrowserWrittenDatabaseOpensWithDesktopEncryption()
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            Execute(connection, $"INSERT INTO notes(body) VALUES ('{Secret}');");
            await harness.Mirror.FlushPendingAsync();
        }

        store.Contains(DatabasePath).Should().BeTrue();
        AssertPersistedBytesAreEncrypted(store);

        var desktop = new InMemoryFileSystem();
        CopyInto(desktop, store);
        using var encryptionOptions = AhtolaEncryptionOptions.FromHex(
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        using var encrypted = new AhtolaEncryptionFileSystem(desktop, encryptionOptions);
        using var reopened = EmbeddedDatabase.OpenFile(DatabasePath, encrypted);
        using var desktopConnection = reopened.Connect();
        ScalarText(desktopConnection, "SELECT body FROM notes;").Should().Be(Secret);
    }

    [Test]
    public async Task DesktopWrittenDatabaseOpensInBrowser()
    {
        var desktop = new InMemoryFileSystem();
        using (var encryptionOptions = AhtolaEncryptionOptions.FromHex(
                   Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
                   Aes256Key))
        using (var encrypted = new AhtolaEncryptionFileSystem(desktop, encryptionOptions))
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, encrypted))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            Execute(connection, $"INSERT INTO notes(body) VALUES ('{Secret}');");
        }

        var store = new FakeBrowserPersistentStore();
        CopyInto(store, desktop, DatabasePath, DatabasePath + "-wal", DatabasePath + "-journal");

        await using var harness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var browserDatabase = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
        using var browserConnection = browserDatabase.Connect();
        ScalarText(browserConnection, "SELECT body FROM notes;").Should().Be(Secret);
    }

    [Test]
    public async Task BrowserDatabaseSurvivesReopenAcrossSessions()
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            for (var index = 0; index < 64; index++)
                Execute(connection, $"INSERT INTO notes(body) VALUES ('{Secret}-{index}');");
            await harness.Mirror.FlushPendingAsync();
        }

        AssertPersistedBytesAreEncrypted(store);

        await using var reopenedHarness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var reopened = EmbeddedDatabase.OpenFile(DatabasePath, reopenedHarness.Mirror);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM notes;").Should().Be(64);
        ScalarText(reopenedConnection, "SELECT body FROM notes WHERE id = 64;").Should().Be($"{Secret}-63");
    }

    [Test]
    public async Task EncryptedBrowserDatabaseReservesTwentyEightBytesFromCreation()
    {
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror))
        {
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE t(x INTEGER);");
        }

        await harness.Mirror.FlushPendingAsync();

        var plaintextHeader = new byte[SqliteDatabaseHeader.Size];
        using (var file = harness.Mirror.OpenFile(DatabasePath, FileOpenMode.OpenExisting, readOnly: true))
            file.Read(0, plaintextHeader);
        SqliteDatabaseHeader.Parse(plaintextHeader).ReservedSpace.Should().Be(28);

        var persisted = store.Read(DatabasePath);
        persisted[20].Should().Be(28, "the encrypted image keeps the reserved-space field visible");
        Encoding.ASCII.GetString(persisted, 0, 5).Should().Be("AHTLA");
    }

    [Test]
    public async Task WrongKeyFailsClosedWhenLoadingEncryptedBrowserStorage()
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE t(x INTEGER);");
            await harness.Mirror.FlushPendingAsync();
        }

        var failure = await ThrowsAsync(() => BrowserHarness.CreateAsync(store, WrongAes256Key));
        failure.Should().BeOfType<InvalidDataException>();
        failure!.Message.Should().Contain("failed authentication");
    }

    [Test]
    public async Task CipherMismatchFailsClosedWithoutFallback()
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE t(x INTEGER);");
            await harness.Mirror.FlushPendingAsync();
        }

        var failure = await ThrowsAsync(() => BrowserHarness.CreateAsync(
            store,
            Aes128Key,
            Core.Storage.AhtolaEncryptionCipher.Aes128Gcm));
        failure.Should().BeOfType<InvalidDataException>();
        failure!.Message.Should().Contain("cipher fallback is not permitted");
    }

    [Test]
    public async Task TamperedEncryptedPageFailsClosed()
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE t(x INTEGER);");
            Execute(connection, "INSERT INTO t VALUES (5);");
            await harness.Mirror.FlushPendingAsync();
        }

        var tampered = store.Read(DatabasePath);
        tampered[600] ^= 0xFF;
        store.Seed(DatabasePath, tampered);

        var failure = await ThrowsAsync(() => BrowserHarness.CreateAsync(store, Aes256Key));
        failure.Should().BeOfType<InvalidDataException>();
        failure!.Message.Should().Contain("failed authentication");
    }

    [Test]
    public async Task PlaintextDatabaseIsRejectedWhenEncryptionIsConfigured()
    {
        var store = new FakeBrowserPersistentStore();
        var plaintext = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, plaintext))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(x INTEGER);");
        }

        CopyInto(store, plaintext, DatabasePath);

        var failure = await ThrowsAsync(() => BrowserHarness.CreateAsync(store, Aes256Key));
        failure.Should().BeOfType<InvalidDataException>();
        failure!.Message.Should().Contain(AhtolaPasswordEncryption.EncryptedOrNotDatabaseMessage);
    }

    [Test]
    public async Task RollbackJournalRecordsArePersistedEncryptedAndRecoverable()
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            for (var index = 0; index < 40; index++)
                Execute(connection, $"INSERT INTO notes(body) VALUES ('{Secret}-{index}');");
            await harness.Mirror.FlushPendingAsync();
        }

        AssertPersistedBytesAreEncrypted(store);

        var desktop = new InMemoryFileSystem();
        CopyInto(desktop, store);
        using var encryptionOptions = AhtolaEncryptionOptions.FromHex(
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        using var encrypted = new AhtolaEncryptionFileSystem(desktop, encryptionOptions);
        using var reopened = EmbeddedDatabase.OpenFile(DatabasePath, encrypted);
        using var desktopConnection = reopened.Connect();
        Scalar(desktopConnection, "SELECT COUNT(*) FROM notes;").Should().Be(40);
    }

    [Test]
    public async Task AttachedEncryptedDatabaseRoundTripsThroughPersistentStore()
    {
        var store = new FakeBrowserPersistentStore();
        const string AttachedPath = "app/data/attached.db";
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE main_notes(body TEXT);");
            Execute(connection, $"INSERT INTO main_notes VALUES ('{Secret}-main');");
            Execute(connection, $"ATTACH DATABASE '{AttachedPath}' AS side;");
            Execute(connection, "CREATE TABLE side.side_notes(body TEXT);");
            Execute(connection, $"INSERT INTO side.side_notes VALUES ('{Secret}-side');");
            Execute(connection, "DETACH DATABASE side;");
            await harness.Mirror.FlushPendingAsync();
        }

        store.Contains(AttachedPath).Should().BeTrue();
        AssertPersistedBytesAreEncrypted(store);
        Encoding.ASCII.GetString(store.Read(AttachedPath), 0, 5).Should().Be("AHTLA");

        var desktop = new InMemoryFileSystem();
        CopyInto(desktop, store);
        using var encryptionOptions = AhtolaEncryptionOptions.FromHex(
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        using var encrypted = new AhtolaEncryptionFileSystem(desktop, encryptionOptions);
        using var reopened = EmbeddedDatabase.OpenFile(AttachedPath, encrypted);
        using var desktopConnection = reopened.Connect();
        ScalarText(desktopConnection, "SELECT body FROM side_notes;").Should().Be($"{Secret}-side");
    }

    [Test]
    public async Task CommittedWalFramesArePersistedEncryptedAndReadableOnDesktop()
    {
        const int PageSize = 4096;
        var walPath = DatabasePath + "-wal";
        var store = new FakeBrowserPersistentStore();
        var pages = new[]
        {
            CreatePlaintextPage(PageSize, 2),
            CreatePlaintextPage(PageSize, 3),
            CreatePlaintextPage(PageSize, 7),
        };

        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            var header = SqliteWalHeader.Create(PageSize, salt1: 0x1234_5678, salt2: 0x9ABC_DEF0);
            using (var wal = SqliteWalFile.Create(harness.Mirror, walPath, header))
            {
                wal.AppendFrame(2, pages[0]);
                wal.AppendFrame(3, pages[1]);
                wal.AppendFrame(7, pages[2], databaseSizeInPages: 7);
            }

            await harness.Mirror.FlushPendingAsync();
        }

        var persisted = store.Read(walPath);
        persisted.Length.Should().Be(SqliteWalHeader.Size + (3 * (SqliteWalFrameHeader.Size + PageSize)));
        AssertWalChainIsValid(persisted);
        persisted.AsSpan(SqliteWalFrameHeader.Size + SqliteWalHeader.Size, PageSize).ToArray()
            .Should().NotEqual(pages[0], "WAL frame bodies must be stored encrypted");

        var desktop = new InMemoryFileSystem();
        CopyInto(desktop, store);
        using var encryptionOptions = AhtolaEncryptionOptions.FromHex(
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        using var encryptedWal = SqliteWalFile.Open(
            desktop,
            walPath,
            readOnly: true,
            encryption: encryptionOptions);
        encryptedWal.ReadFrame(1).PageData.Should().Equal(pages[0]);
        encryptedWal.ReadFrame(2).PageData.Should().Equal(pages[1]);
        encryptedWal.ReadFrame(3).PageData.Should().Equal(pages[2]);
        encryptedWal.ReadFrame(3).Header.DatabaseSizeInPages.Should().Be(7);
    }

    [Test]
    public async Task DesktopRetainedWalOpensInBrowser()
    {
        const int PageSize = 4096;
        var walPath = DatabasePath + "-wal";
        var desktop = new InMemoryFileSystem();
        var pages = new[]
        {
            CreatePlaintextPage(PageSize, 2),
            CreatePlaintextPage(PageSize, 5),
        };

        using (var encryptionOptions = AhtolaEncryptionOptions.FromHex(
                   Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
                   Aes256Key))
        {
            var header = SqliteWalHeader.Create(PageSize, salt1: 0x0BAD_F00D, salt2: 0x0000_1234, checkpointSequence: 3);
            using var wal = SqliteWalFile.Create(desktop, walPath, header, encryption: encryptionOptions);
            wal.AppendFrame(2, pages[0]);
            wal.AppendFrame(5, pages[1], databaseSizeInPages: 5);
        }

        var store = new FakeBrowserPersistentStore();
        CopyInto(store, desktop, walPath);

        await using var harness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var plaintextWal = SqliteWalFile.Open(harness.Mirror, walPath, readOnly: true);
        plaintextWal.ReadFrame(1).PageData.Should().Equal(
            pages[0],
            "the browser must decrypt a desktop-written WAL into plaintext frames");
        plaintextWal.ReadFrame(2).PageData.Should().Equal(pages[1]);
        plaintextWal.ReadFrame(2).Header.DatabaseSizeInPages.Should().Be(5);
    }

    [Test]
    public async Task BrowserAppendsToADesktopWrittenWalWithoutBreakingItsChain()
    {
        const int PageSize = 4096;
        var walPath = DatabasePath + "-wal";
        var desktop = new InMemoryFileSystem();
        var first = CreatePlaintextPage(PageSize, 2);
        var appended = CreatePlaintextPage(PageSize, 9);

        using (var encryptionOptions = AhtolaEncryptionOptions.FromHex(
                   Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
                   Aes256Key))
        {
            var header = SqliteWalHeader.Create(PageSize, salt1: 7, salt2: 11, checkpointSequence: 1);
            using var wal = SqliteWalFile.Create(desktop, walPath, header, encryption: encryptionOptions);
            wal.AppendFrame(2, first, databaseSizeInPages: 2);
        }

        var store = new FakeBrowserPersistentStore();
        CopyInto(store, desktop, walPath);

        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using (var wal = SqliteWalFile.Open(harness.Mirror, walPath))
                wal.AppendFrame(9, appended, databaseSizeInPages: 9);
            await harness.Mirror.FlushPendingAsync();
        }

        AssertWalChainIsValid(store.Read(walPath));

        var verification = new InMemoryFileSystem();
        CopyInto(verification, store);
        using var verifyOptions = AhtolaEncryptionOptions.FromHex(
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);
        using var verified = SqliteWalFile.Open(
            verification,
            walPath,
            readOnly: true,
            encryption: verifyOptions);
        verified.ReadFrame(1).PageData.Should().Equal(first);
        verified.ReadFrame(2).PageData.Should().Equal(
            appended,
            "a frame the browser appended must extend the desktop chain byte-compatibly");
    }

    [Test]
    public async Task PageCipherIsReleasedWhenPersistenceIsDisposed()
    {
        var cipher = new DesktopAsyncPageCipher(
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            Convert.FromHexString(Aes256Key));
        var persistence = new BrowserEncryptedPersistence(new AhtolaAsyncPageTransformer(cipher));

        cipher.IsReleased.Should().BeFalse();
        await persistence.DisposeAsync();
        cipher.IsReleased.Should().BeTrue();
    }

    [Test]
    public async Task CancellingAFlushLeavesPendingMutationsReplayable()
    {
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE notes(body TEXT);");
        Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var failure = await ThrowsAsync(() => harness.Mirror.FlushPendingAsync(cancelled.Token));
        failure.Should().BeAssignableTo<OperationCanceledException>();

        harness.Mirror.HasPendingMutations.Should().BeTrue(
            "a cancelled flush must keep the unreplayed mutations queued instead of losing them");

        await harness.Mirror.FlushPendingAsync();
        harness.Mirror.HasPendingMutations.Should().BeFalse();
        AssertPersistedBytesAreEncrypted(store);
    }

    private static byte[] CreatePlaintextPage(int pageSize, uint pageNumber)
    {
        var page = new byte[pageSize];
        if (pageNumber == 1)
        {
            "SQLite format 3\0"u8.CopyTo(page);
            page[16] = (byte)(pageSize >> 8);
            page[17] = (byte)pageSize;
            page[18] = 2;
            page[19] = 2;
            page[20] = 28;
        }

        // Leave the trailing reserved bytes zero: the engine guarantees this and
        // the encrypted layout stores its tag and nonce there.
        for (var index = pageNumber == 1 ? 100 : 0; index < pageSize - 28; index++)
            page[index] = (byte)((index * 31) + pageNumber);
        return page;
    }

    private static void AssertWalChainIsValid(byte[] wal)
    {
        var header = SqliteWalHeader.Parse(wal.AsSpan(0, SqliteWalHeader.Size));
        var frameSize = SqliteWalFrameHeader.Size + header.PageSize;
        var running = (header.Checksum1, header.Checksum2);
        var frames = 0;
        for (var offset = SqliteWalHeader.Size; offset + frameSize <= wal.Length; offset += frameSize)
        {
            var frameHeader = SqliteWalFrameHeader.Parse(wal.AsSpan(offset, SqliteWalFrameHeader.Size));
            var (First, Second) = SqliteWalChecksum.Calculate(
                wal.AsSpan(offset, 8),
                header.ChecksumByteOrder,
                running.Item1,
                running.Item2);
            running = SqliteWalChecksum.Calculate(
                wal.AsSpan(offset + SqliteWalFrameHeader.Size, header.PageSize),
                header.ChecksumByteOrder,
                First,
                Second);
            running.Should().Be(
                (frameHeader.Checksum1, frameHeader.Checksum2),
                "encrypted WAL frame checksums must be computed over the encrypted page image");
            frames++;
        }

        frames.Should().BeGreaterThan(0);
    }

    private static void AssertPersistedBytesAreEncrypted(FakeBrowserPersistentStore store)
    {
        var needle = Encoding.UTF8.GetBytes(Secret);
        foreach (var path in store.Paths)
        {
            if (path.EndsWith("-shm", StringComparison.Ordinal))
                continue;
            store.Read(path).AsSpan().IndexOf(needle).Should().Be(
                -1,
                $"persisted file '{path}' must never contain plaintext row data");
        }
    }

    private static void CopyInto(InMemoryFileSystem destination, FakeBrowserPersistentStore source)
    {
        foreach (var path in source.Paths.ToArray())
        {
            var content = source.Read(path);
            using var file = destination.OpenFile(path, FileOpenMode.OpenOrCreate);
            file.SetLength(content.Length);
            if (content.Length != 0)
                file.Write(0, content);
        }
    }

    private static void CopyInto(
        FakeBrowserPersistentStore destination,
        InMemoryFileSystem source,
        params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!source.FileExists(path))
                continue;
            using var file = source.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
            var content = new byte[file.Length];
            if (content.Length != 0)
                file.Read(0, content);
            destination.Seed(path, content);
        }
    }

    private static async Task<Exception?> ThrowsAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> ThrowsAsync<T>(Func<ValueTask<T>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string ScalarText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private sealed class BrowserHarness : IAsyncDisposable
    {
        private readonly BrowserEncryptedPersistence _persistence;

        private BrowserHarness(BrowserMirroredFileSystem mirror, BrowserEncryptedPersistence persistence)
        {
            Mirror = mirror;
            _persistence = persistence;
        }

        public BrowserMirroredFileSystem Mirror { get; }

        public static async ValueTask<BrowserHarness> CreateAsync(
            FakeBrowserPersistentStore store,
            string hexKey,
            Core.Storage.AhtolaEncryptionCipher cipher = Core.Storage.AhtolaEncryptionCipher.Aes256Gcm)
        {
            var persistence = new BrowserEncryptedPersistence(
                new AhtolaAsyncPageTransformer(
                    new DesktopAsyncPageCipher(cipher, Convert.FromHexString(hexKey))));
            try
            {
                var mirror = await BrowserMirroredFileSystem.CreateAsync(
                    store,
                    OwnedDirectory,
                    ownsPersistent: false,
                    encryption: persistence);
                return new BrowserHarness(mirror, persistence);
            }
            catch
            {
                await persistence.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Mirror.DisposeAsync();
            }
            finally
            {
                await _persistence.DisposeAsync();
            }
        }
    }
}

