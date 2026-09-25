using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;
using AwesomeAssertions;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

#pragma warning disable CA1416

public sealed class AhtolaBrowserEncryptedBoundedScanTests
{
    private const string Path = "encrypted-bounded.db";
    private const string Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongKey = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public async Task DesktopEncryptedDatabaseStreamsAcrossReopen()
    {
        var storage = new InMemoryFileSystem();
        using (var options = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Key))
        using (var encrypted = new AhtolaEncryptionFileSystem(storage, options))
        using (var database = EmbeddedDatabase.OpenFile(Path, encrypted))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, "INSERT INTO items VALUES (1, 'first'), (2, 'second');");
        }

        for (var reopen = 0; reopen < 2; reopen++)
        {
            await using var bounded = await OpenAsync(storage);
            await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT id, label FROM items ORDER BY id");
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsInteger().Should().Be(1);
            reader.GetValue(1).AsText().Should().Be("first");
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsInteger().Should().Be(2);
            reader.GetValue(1).AsText().Should().Be("second");
            (await reader.ReadAsync()).Should().BeFalse();
        }
    }

    [Test]
    public async Task CompositeIntegerKeySeekReadsEncryptedWithoutRowidPages()
    {
        var storage = new InMemoryFileSystem();
        using (var options = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Key))
        using (var encrypted = new AhtolaEncryptionFileSystem(storage, options))
        using (var database = EmbeddedDatabase.OpenFile(Path, encrypted))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(tenant INTEGER, seq INTEGER, label TEXT, PRIMARY KEY(tenant, seq)) WITHOUT ROWID;");
            Execute(connection, "BEGIN;");
            for (var tenant = 1; tenant <= 2; tenant++)
            {
                for (var seq = 1; seq <= 80; seq++)
                    Execute(connection, $"INSERT INTO items VALUES ({tenant}, {seq}, 'item-{tenant}-{seq}');");
            }
            Execute(connection, "COMMIT;");
        }

        await using var bounded = await OpenAsync(storage, pageBudget: 8);
        await using (var reader = await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items WHERE seq = 79 AND tenant = 2"))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsText().Should().Be("item-2-79");
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using var missing = await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items WHERE tenant = 2 AND seq = 81");
        (await missing.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task ReadsChangedPageFromAuthenticatedWalRatherThanStaleMainFile()
    {
        var storage = CreateWalDatabase();

        using (var wal = SqliteWalFile.Open(storage, Path + "-wal"))
            wal.ScanRecovery().LastCommittedFrameNumber.Should().BeGreaterThan(0);
        await using var bounded = await OpenAsync(storage);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).AsText().Should().Be("new");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task BrowserEncryptedPersistenceIsReadableWithoutWholeImageLoading()
    {
        const string directory = "owned";
        const string browserPath = "owned/browser.db";
        var persisted = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserCipherHarness.CreateAsync(
                         persisted, StorageCipher.Aes256Gcm, Key, directory))
        {
            using var database = EmbeddedDatabase.OpenFile(browserPath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, "INSERT INTO items VALUES (3, 'browser-written');");
            await harness.Mirror.FlushPendingAsync();
        }

        var storage = new InMemoryFileSystem();
        foreach (var path in persisted.Paths)
        {
            using var file = storage.OpenFile(path, FileOpenMode.CreateNew);
            file.Write(0, persisted.Read(path));
        }

        await using var bounded = await OpenAsync(storage, path: browserPath);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).AsText().Should().Be("browser-written");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task WrongKeyCipherAndAuthenticatedHeaderTamperFailClosed()
    {
        var storage = CreateDatabase();
        var wrongKey = async () => await OpenAsync(storage, WrongKey);
        (await wrongKey.Should().ThrowAsync<InvalidDataException>()).Which.Message
            .Should().Contain("authentication");

        var wrongCipher = async () => await OpenAsync(storage, Key, StorageCipher.Aegis256);
        await wrongCipher.Should().ThrowAsync<InvalidDataException>();

        using (var file = storage.OpenFile(Path, FileOpenMode.OpenExisting))
        {
            var byteAtOffset = new byte[1];
            file.Read(50, byteAtOffset).Should().Be(1);
            file.Write(50, [(byte)(byteAtOffset[0] ^ 0x20)]);
        }
        var tampered = async () => await OpenAsync(storage);
        (await tampered.Should().ThrowAsync<InvalidDataException>()).Which.Message
            .Should().Contain("authentication");
    }

    [Test]
    public async Task ManagedAegisCipherReadsDesktopEncryptedPages()
    {
        var storage = CreateDatabase(StorageCipher.Aegis256);
        await using var bounded = await OpenAsync(storage, cipher: StorageCipher.Aegis256);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).AsText().Should().Be("first");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task ChecksumValidWalFrameWithInvalidPageTagFailsAuthentication()
    {
        var storage = CreateWalDatabase();
        SqliteWalHeader header;
        SqliteWalFrame frame;
        using (var wal = SqliteWalFile.Open(storage, Path + "-wal"))
        {
            header = wal.Header;
            frame = wal.ReadFrame(1);
        }
        frame.PageData[^28] ^= 0x40;
        storage.DeleteFile(Path + "-wal");
        using (var wal = SqliteWalFile.Create(storage, Path + "-wal", header))
        {
            wal.AppendFrame(frame.Header.PageNumber, frame.PageData, frame.Header.DatabaseSizeInPages);
            wal.Flush();
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
        }

        await using var bounded = await OpenAsync(storage);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        var read = async () => await reader.ReadAsync();
        (await read.Should().ThrowAsync<InvalidDataException>()).Which.Message
            .Should().Contain("authentication");
    }

    [Test]
    public async Task ChangedWalFrameAfterOpenIsRejectedBeforePageDecryption()
    {
        var storage = CreateWalDatabase();
        await using var bounded = await OpenAsync(storage);
        using (var file = storage.OpenFile(Path + "-wal", FileOpenMode.OpenExisting))
        {
            var byteAtOffset = new byte[1];
            var offset = SqliteWalHeader.Size + SqliteWalFrameHeader.Size + 100;
            file.Read(offset, byteAtOffset).Should().Be(1);
            file.Write(offset, [(byte)(byteAtOffset[0] ^ 0x80)]);
        }

        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        var read = async () => await reader.ReadAsync();
        (await read.Should().ThrowAsync<InvalidDataException>()).Which.Message
            .Should().Contain("checksum");
    }

    [Test]
    public async Task OverflowScanKeepsPageBudgetAndCancellation()
    {
        var storage = new InMemoryFileSystem();
        using (var options = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Key))
        using (var encrypted = new AhtolaEncryptionFileSystem(storage, options))
        using (var database = EmbeddedDatabase.OpenFile(Path, encrypted))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, $"INSERT INTO items VALUES (1, '{new string('x', 9000)}');");
        }

        await using var bounded = await OpenAsync(storage, pageBudget: 1);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        var overBudget = async () => await reader.ReadAsync();
        await overBudget.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var openCancelled = async () => await OpenAsync(storage, cancellationToken: cancelled.Token);
        await openCancelled.Should().ThrowAsync<OperationCanceledException>();

        await using var cancelledReader = await bounded.ExecuteBoundedScanAsync("SELECT id FROM items");
        var readCancelled = async () => await cancelledReader.ReadAsync(cancelled.Token);
        await readCancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RejectsMissingWalAndInvalidFrameChecksumBeforeDecrypting()
    {
        var storage = CreateDatabase();
        using (var file = storage.OpenFile(Path, FileOpenMode.OpenExisting))
        {
            var header = new byte[SqlitePageSize.Default];
            file.Read(0, header).Should().Be(header.Length);
            using var encryption = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Key);
            using var cipher = encryption.CreatePageEncryption(SqlitePageSize.Default);
            var plaintext = cipher.DecryptPage(header, 1);
            var changed = SqliteDatabaseHeader.Parse(plaintext) with
            {
                ReadVersion = SqliteFileFormatVersion.Wal,
                WriteVersion = SqliteFileFormatVersion.Wal,
                VersionValidFor = 0,
            };
            changed.WriteTo(plaintext);
            file.Write(0, cipher.EncryptPage(plaintext, 1));
        }
        var missingWal = async () => await OpenAsync(storage);
        (await missingWal.Should().ThrowAsync<InvalidDataException>()).Which.Message
            .Should().Contain("WAL");

        var walStorage = CreateWalDatabase();
        using (var file = walStorage.OpenFile(Path + "-wal", FileOpenMode.OpenExisting))
        {
            var byteAtOffset = new byte[1];
            file.Read(SqliteWalHeader.Size + SqliteWalFrameHeader.Size + 100, byteAtOffset).Should().Be(1);
            file.Write(SqliteWalHeader.Size + SqliteWalFrameHeader.Size + 100,
                [(byte)(byteAtOffset[0] ^ 0x40)]);
        }
        var corruptWal = async () => await OpenAsync(walStorage);
        await corruptWal.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task RejectsNonemptyRollbackJournalAndUnsupportedSqlShape()
    {
        var storage = CreateDatabase();
        await using (var bounded = await OpenAsync(storage))
        {
            var unsupported = async () => await bounded.ExecuteBoundedScanAsync(
                "SELECT label FROM items WHERE label = 'first'");
            await unsupported.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();
        }

        using (var journal = storage.OpenFile(Path + "-journal", FileOpenMode.OpenOrCreate))
            journal.Write(0, [0xA7]);
        var hotJournal = async () => await OpenAsync(storage);
        (await hotJournal.Should().ThrowAsync<InvalidDataException>()).Which.Message
            .Should().Contain("rollback journal");
    }

    [Test]
    public async Task OpensAuthoritativeWalDatabaseWithTruncatedEmptyWal()
    {
        var storage = CreateWalDatabase();
        using (var wal = storage.OpenFile(Path + "-wal", FileOpenMode.OpenExisting))
            wal.SetLength(0);
        await using var bounded = await OpenAsync(storage);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).AsText().Should().Be("old");
    }

    [Test]
    public async Task CommittedWalLocationsRespectMetadataBudgetAcrossReopens()
    {
        var storage = CreateWalWithDistinctPages(3, commitEachFrame: true);
        using (var wal = SqliteWalFile.Open(storage, Path + "-wal"))
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(3);

        var overBudget = async () => await OpenAsync(storage, pageBudget: 2);
        (await overBudget.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>())
            .Which.Message.Should().Contain("PageBudget of 2");

        for (var reopen = 0; reopen < 2; reopen++)
        {
            await using var bounded = await OpenAsync(storage, pageBudget: 3);
            await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT label FROM items");
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsText().Should().Be("new");
            (await reader.ReadAsync()).Should().BeFalse();
        }
    }

    [Test]
    public async Task UncommittedWalLocationsRespectMetadataBudgetBeforeTailRejection()
    {
        var storage = CreateWalWithDistinctPages(4, commitEachFrame: false);
        using (var wal = SqliteWalFile.Open(storage, Path + "-wal"))
        {
            wal.ScanRecovery().LastValidFrameNumber.Should().Be(4);
            wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(0);
        }

        var overBudget = async () => await OpenAsync(storage, pageBudget: 2);
        (await overBudget.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>())
            .Which.Message.Should().Contain("PageBudget of 2");

        var tail = async () => await OpenAsync(storage, pageBudget: 4);
        (await tail.Should().ThrowAsync<InvalidDataException>())
            .Which.Message.Should().Contain("uncommitted tail");
    }

    [Test]
    public async Task CancellationDuringWalLocationScanReleasesHandlesAndAllowsReopen()
    {
        var storage = CreateWalWithDistinctPages(4, commitEachFrame: true);
        var controller = new DeterministicAsyncIoController(forceYield: true);
        var fileSystem = DeterministicAsyncFileSystem.Create(
            AsyncFileSystemAdapter.Create(storage), controller);
        using var cancellation = new CancellationTokenSource();
        var cipher = new DesktopAsyncPageCipher(StorageCipher.Aes256Gcm, Convert.FromHexString(Key));
        var opening = AhtolaBrowserBoundedConnection.OpenAsync(
            fileSystem, ownsFileSystem: false, Path, pageBudget: 4, cancellation.Token,
            new AhtolaAsyncPageTransformer(cipher)).AsTask();

        var released = 0;
        while (!opening.IsCompleted)
        {
            if (controller.PendingYieldCount == 0)
            {
                await Task.Yield();
                continue;
            }

            var pending = controller.PendingOperations[0];
            if (pending.Type == FileSystemOperation.Read
                && pending.Path == Path + "-wal"
                && pending.PathOccurrence >= 2)
            {
                cancellation.Cancel();
                break;
            }
            controller.Release(pending.Id);
            if (++released > 30)
                throw new InvalidOperationException("The encrypted WAL scan did not reach a frame read.");
        }

        while (!opening.IsCompleted)
        {
            if (controller.PendingYieldCount > 0)
                controller.ReleaseNext();
            else
                await Task.Yield();
        }

        var canceled = async () => await opening;
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        cipher.IsReleased.Should().BeTrue();

        await using var reopened = await OpenAsync(storage, pageBudget: 4);
        await using var reader = await reopened.ExecuteBoundedScanAsync("SELECT label FROM items");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).AsText().Should().Be("new");
    }

    private static InMemoryFileSystem CreateWalWithDistinctPages(int frameCount, bool commitEachFrame)
    {
        var storage = CreateWalDatabase();
        using var encryption = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Key);
        using var store = SqlitePageStore.Open(storage, Path, encryption: encryption);
        var walPage = store.ReadPage(2);
        var old = walPage.AsSpan().IndexOf("old"u8);
        old.Should().BeGreaterThanOrEqualTo(0);
        "new"u8.CopyTo(walPage.AsSpan(old));
        var mainPage = store.ReadPage(2);
        for (var pageNumber = 3U; pageNumber <= (uint)frameCount + 1; pageNumber++)
            store.WritePage(pageNumber, mainPage);

        storage.DeleteFile(Path + "-wal");
        using (var wal = SqliteWalFile.Create(
                   storage, Path + "-wal",
                   SqliteWalHeader.Create(store.PageSize, salt1: 17, salt2: 19),
                   encryption))
        {
            for (var index = 0; index < frameCount; index++)
                wal.AppendFrame((uint)index + 2, walPage, commitEachFrame ? store.PageCount : 0);
            wal.Flush();
        }
        return storage;
    }

    private static InMemoryFileSystem CreateWalDatabase()
    {
        var storage = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Key);
        using (var encrypted = new AhtolaEncryptionFileSystem(storage, encryption))
        using (var database = EmbeddedDatabase.OpenFile(Path, encrypted))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, "INSERT INTO items VALUES (1, 'old');");
        }

        using var store = SqlitePageStore.Open(storage, Path, encryption: encryption);
        var pageOne = store.ReadPage(1);
        (store.Header with
        {
            ReadVersion = SqliteFileFormatVersion.Wal,
            WriteVersion = SqliteFileFormatVersion.Wal,
        }).WriteTo(pageOne);
        store.WritePage(1, pageOne);
        var newPage = store.ReadPage(2);
        var old = newPage.AsSpan().IndexOf("old"u8);
        old.Should().BeGreaterThanOrEqualTo(0);
        "new"u8.CopyTo(newPage.AsSpan(old));
        storage.DeleteFile(Path + "-wal");
        using (var wal = SqliteWalFile.Create(
                   storage,
                   Path + "-wal",
                   SqliteWalHeader.Create(store.PageSize, salt1: 11, salt2: 13),
                   encryption))
        {
            wal.AppendFrame(2, newPage, store.PageCount);
            wal.Flush();
        }
        return storage;
    }

    private static InMemoryFileSystem CreateDatabase(StorageCipher cipher = StorageCipher.Aes256Gcm)
    {
        var storage = new InMemoryFileSystem();
        using var options = AhtolaEncryptionOptions.FromHex(cipher, Key);
        using var encrypted = new AhtolaEncryptionFileSystem(storage, options);
        using var database = EmbeddedDatabase.OpenFile(Path, encrypted);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
        Execute(connection, "INSERT INTO items VALUES (1, 'first');");
        return storage;
    }

    private static async ValueTask<AhtolaBrowserBoundedConnection> OpenAsync(
        InMemoryFileSystem storage,
        string key = Key,
        StorageCipher cipher = StorageCipher.Aes256Gcm,
        int pageBudget = 16,
        CancellationToken cancellationToken = default,
        string path = Path)
    {
        var secret = Convert.FromHexString(key);
        IAhtolaAsyncPageCipher pageCipher = cipher is StorageCipher.Aes128Gcm or StorageCipher.Aes256Gcm
            ? new DesktopAsyncPageCipher(cipher, secret)
            : new AhtolaManagedAegisPageCipher(cipher, secret);
        return await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            ownsFileSystem: false,
            path,
            pageBudget,
            cancellationToken,
            new AhtolaAsyncPageTransformer(pageCipher));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }
}

#pragma warning restore CA1416
