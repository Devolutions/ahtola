using System.Text;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the browser encryption hardening pass: role tracking that
/// cannot be fooled by a database name, encrypted recovery that runs before page
/// authentication, abandoned engine temporaries that must not block an open, and
/// key material that is released on every path.
/// </summary>
[NonParallelizable]
public sealed class AhtolaBrowserEncryptedStorageHardeningTests
{
    private const string OwnedDirectory = "app/data";
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Secret = "hardening-plaintext-canary";

    [TestCase("app/data/notes-shm")]
    [TestCase("app/data/notes-wal")]
    [TestCase("app/data/notes-journal")]
    [TestCase("app/data/notes-log")]
    public async Task DatabaseNamedLikeASidecarIsStillEncrypted(string databasePath)
    {
        var store = new FakeBrowserPersistentStore();
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(databasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            await harness.Mirror.FlushPendingAsync();
        }

        store.Contains(databasePath).Should().BeTrue();
        Encoding.ASCII.GetString(store.Read(databasePath), 0, 5).Should().Be(
            "AHTLA",
            "a database whose name resembles a sidecar must never be persisted in the clear");
        foreach (var path in store.Paths)
        {
            store.Read(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(-1);
        }

        await using var reopened = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var reopenedDatabase = EmbeddedDatabase.OpenFile(databasePath, reopened.Mirror);
        using var reopenedConnection = reopenedDatabase.Connect();
        ScalarText(reopenedConnection, "SELECT body FROM notes;").Should().Be(Secret);
    }

    [Test]
    public async Task SidecarOfARealDatabaseIsStillRecognized()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        await using var harness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror))
        {
            using var connection = database.Connect();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
        }

        await harness.Mirror.FlushPendingAsync();

        var roles = new BrowserPersistedFileRoles();
        roles.RegisterDatabase(DatabasePath);
        roles.Resolve(DatabasePath + "-wal", []).Should().Be(BrowserPersistedFileRole.Wal);
        roles.Resolve(DatabasePath + "-journal", []).Should().Be(BrowserPersistedFileRole.Journal);
        roles.Resolve(DatabasePath + "-shm", []).Should().Be(BrowserPersistedFileRole.SharedMemory);
        roles.Resolve(DatabasePath + "-log", []).Should().Be(BrowserPersistedFileRole.MvccLog);
    }

    [Test]
    public void UnanchoredSidecarNamesAreTreatedAsDatabases()
    {
        var roles = new BrowserPersistedFileRoles();

        roles.Resolve("app/data/notes-shm", []).Should().Be(BrowserPersistedFileRole.Database);
        roles.Resolve("app/data/notes-log", []).Should().Be(BrowserPersistedFileRole.Database);
    }

    [Test]
    public void DatabaseMagicVetoesASidecarClassification()
    {
        var roles = new BrowserPersistedFileRoles();
        roles.RegisterDatabase("app/data/notes");

        var encryptedHeader = new byte[16];
        "AHTLA"u8.CopyTo(encryptedHeader);

        roles.Resolve("app/data/notes-shm", encryptedHeader).Should().Be(
            BrowserPersistedFileRole.Database,
            "content that starts with the AHTLA magic is a database no matter how it is named");
    }

    [TestCase("app/data/main.db.vacuum-0123456789abcdef0123456789abcdef.tmp")]
    [TestCase("app/data/main.db.vacuum-0123456789abcdef0123456789abcdef.tmp-wal")]
    [TestCase("app/data/main.db.page-size-0123456789abcdef0123456789abcdef.tmp")]
    [TestCase("app/data/main.db-log.v4-upgrade")]
    public void EngineTemporariesAreRecognized(string path)
        => BrowserPersistedFileRoles.IsTransientArtifact(path).Should().BeTrue();

    [TestCase("app/data/main.db")]
    [TestCase("app/data/main.db-wal")]
    [TestCase("app/data/notes.tmp")]
    [TestCase("app/data/main.db.vacuum-not-a-guid.tmp")]
    [TestCase("app/data/main.db.vacuum-0123456789ABCDEF0123456789ABCDEF.tmp")]
    public void RealDataIsNeverMistakenForAnEngineTemporary(string path)
        => BrowserPersistedFileRoles.IsTransientArtifact(path).Should().BeFalse();

    [Test]
    public async Task AbandonedVacuumTemporaryDoesNotBlockOpeningAHealthyDatabase()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            await harness.Mirror.FlushPendingAsync();
        }

        // A crash during VACUUM leaves a preallocated, undecryptable temporary.
        var abandoned = DatabasePath + ".vacuum-0123456789abcdef0123456789abcdef.tmp";
        store.Seed(abandoned, new byte[8192]);
        store.Seed(abandoned + "-journal", new byte[512]);

        await using var reopened = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var reopenedDatabase = EmbeddedDatabase.OpenFile(DatabasePath, reopened.Mirror);
        using var reopenedConnection = reopenedDatabase.Connect();
        ScalarText(reopenedConnection, "SELECT body FROM notes;").Should().Be(Secret);

        store.Contains(abandoned).Should().BeFalse("abandoned engine temporaries are cleaned at startup");
        store.Contains(abandoned + "-journal").Should().BeFalse();
    }

    [Test]
    public async Task HotRollbackJournalRecoversATornPageBeforeAuthentication()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            for (var index = 0; index < 24; index++)
                Execute(connection, $"INSERT INTO notes(body) VALUES ('{Secret}-{index}');");
            await harness.Mirror.FlushPendingAsync();
        }

        var (journal, pageSize) = BuildHotJournalFrom(store, DatabasePath);
        store.Seed(DatabasePath + "-journal", journal);

        // Simulate a torn page: the process died mid-write, so the encrypted page
        // no longer authenticates. The journal still holds its pre-image.
        var torn = store.Read(DatabasePath);
        torn.AsSpan(pageSize, 64).Fill(0xAB);
        store.Seed(DatabasePath, torn);

        await using var reopened = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var reopenedDatabase = EmbeddedDatabase.OpenFile(DatabasePath, reopened.Mirror);
        using var reopenedConnection = reopenedDatabase.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM notes;").Should().Be(
            24,
            "encrypted journal recovery must run before the torn page is authenticated");
    }

    [Test]
    public async Task TornPageWithNoRecoverySourceStillFailsClosed()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            await harness.Mirror.FlushPendingAsync();
        }

        var torn = store.Read(DatabasePath);
        torn.AsSpan(600, 32).Fill(0xCD);
        store.Seed(DatabasePath, torn);

        var failure = await Capture(() => BrowserHarness.CreateAsync(store, Aes256Key));
        failure.Should().BeOfType<InvalidDataException>();
        failure!.Message.Should().Contain("failed authentication");
    }

    [Test]
    public async Task ConvenienceConstructorReleasesTheKeyItCreated()
    {
        using var encryption = AhtolaBrowserEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);

        var options = new AhtolaBrowserOptions("owned/data.db", "owned", encryption: encryption);
        var retained = options.Encryption!;
        var source = new AhtolaBrowserDataSource(options);

        await source.DisposeAsync();

        // Caller-supplied options stay the caller's to dispose, and the data source
        // releases only the independent snapshot it took.
        options.Encryption.Should().NotBeNull();
        var callerCopyIsIntact = () => retained.CreateOwnedCopy();
        callerCopyIsIntact.Should().NotThrow();
        options.Dispose();
    }

    [Test]
    public async Task ConvenienceConstructorDisposesOptionsItOwns()
    {
        using var encryption = AhtolaBrowserEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);

        var source = new AhtolaBrowserDataSource("owned/data.db", "owned", encryption: encryption);
        var owned = source.Options.Encryption;
        owned.Should().NotBeNull();

        await source.DisposeAsync();

        source.Options.Encryption.Should().BeNull(
            "options created by the data source are disposed with it so their key copy is zeroed");
        var released = () => owned!.CreateOwnedCopy();
        released.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposingAnUnusedDataSourceStillReleasesKeyMaterial()
    {
        using var encryption = AhtolaBrowserEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            Aes256Key);

        var source = new AhtolaBrowserDataSource("owned/data.db", "owned", encryption: encryption);
        var owned = source.Options.Encryption!;

        await source.DisposeAsync();
        await source.DisposeAsync();

        var released = () => owned.CreateOwnedCopy();
        released.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task VacuumIntoPublishesAnEncryptedDestination()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        const string DestinationPath = "app/data/vacuumed.db";

        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using (var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror))
            {
                using var connection = database.Connect();
                Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
                for (var index = 0; index < 24; index++)
                    Execute(connection, $"INSERT INTO notes(body) VALUES ('{Secret}-{index}');");

                // The rebuilt image is written through the persisted mirror at a
                // '.vacuum-<guid>.tmp' path and then published atomically, so it has
                // to be encrypted exactly like any other database.
                Execute(connection, $"VACUUM INTO '{DestinationPath}';");
            }

            await harness.Mirror.FlushPendingAsync();
        }

        store.Contains(DestinationPath).Should().BeTrue();
        Encoding.ASCII.GetString(store.Read(DestinationPath), 0, 5).Should().Be(
            "AHTLA",
            "a published VACUUM INTO destination must be encrypted, not the plaintext temporary image");
        foreach (var path in store.Paths)
        {
            store.Read(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(
                -1,
                $"persisted file '{path}' must never contain plaintext row data");
        }

        await using var reopened = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var vacuumed = EmbeddedDatabase.OpenFile(DestinationPath, reopened.Mirror);
        using var vacuumedConnection = vacuumed.Connect();
        Scalar(vacuumedConnection, "SELECT COUNT(*) FROM notes;").Should().Be(24);
    }

    [Test]
    public async Task LiveEngineTemporaryIsEncryptedWhileItIsBeingWritten()
    {
        var store = new FakeBrowserPersistentStore();
        var temporaryPath = "app/data/main.db.vacuum-0123456789abcdef0123456789abcdef.tmp";

        await using var harness = await BrowserHarness.CreateAsync(store, Aes256Key);
        using (var database = EmbeddedDatabase.OpenFile(temporaryPath, harness.Mirror))
        {
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
        }

        await harness.Mirror.FlushPendingAsync();

        // The transient-artifact shape is a load-time concept only: while the
        // engine is writing one, it is a real database and must be encrypted.
        Encoding.ASCII.GetString(store.Read(temporaryPath), 0, 5).Should().Be("AHTLA");
        foreach (var path in store.Paths)
            store.Read(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(-1);
    }

    [Test]
    public async Task MvccJournalModeEncryptsLogicalLogAndSurvivesReopen()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        Dictionary<string, byte[]> durableImages;
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            Execute(connection, "COMMIT;");
            await harness.Mirror.FlushPendingAsync();
            durableImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in durableImages)
            store.Seed(path, image);
        var logPath = DatabasePath + "-log";
        store.Contains(logPath).Should().BeTrue();
        store.Read(logPath).Length.Should().BeGreaterThan(MvccLogicalLogFormat.LogHeaderSize);
        store.Read(logPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(-1);
        foreach (var path in store.Paths)
            store.Read(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)).Should().Be(-1);

        await using var reopened = await BrowserHarness.CreateAsync(store, Aes256Key);
        using var reopenedDatabase = EmbeddedDatabase.OpenFile(DatabasePath, reopened.Mirror);
        using var reopenedConnection = reopenedDatabase.Connect();
        ScalarText(reopenedConnection, "SELECT body FROM notes;").Should().Be(Secret);
    }

    [Test]
    public async Task MvccLogicalLogMetadataTamperingFailsAuthenticationOnLoad()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        Dictionary<string, byte[]> durableImages;
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            Execute(connection, "COMMIT;");
            await harness.Mirror.FlushPendingAsync();
            durableImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in durableImages)
            store.Seed(path, image);
        var logPath = DatabasePath + "-log";
        var log = store.Read(logPath);
        var frameOffset = MvccLogicalLogFormat.LogHeaderSize;
        log[frameOffset + 16] ^= 0x01;
        var plaintextSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
            log.AsSpan(frameOffset + 4)));
        var trailerOffset = frameOffset
                            + MvccLogicalLogFormat.TxHeaderSize
                            + MvccLogicalLogFormat.GetEncryptedPayloadSize(plaintextSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            log.AsSpan(trailerOffset),
            Crc32C.Compute(log.AsSpan(
                frameOffset,
                trailerOffset - frameOffset)));
        store.Seed(logPath, log);

        var failure = await Capture(() => BrowserHarness.CreateAsync(store, Aes256Key));
        failure.Should().BeOfType<InvalidDataException>()
            .Which.Message.Should().Contain("authentication failed");
    }

    [TestCase(-1L)]
    [TestCase(1L)]
    public async Task MvccLogicalLogPayloadLengthTamperingFailsWithoutTruncation(long delta)
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        Dictionary<string, byte[]> durableImages;
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(body TEXT);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES ('{Secret}');");
            Execute(connection, "COMMIT;");
            await harness.Mirror.FlushPendingAsync();
            durableImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in durableImages)
            store.Seed(path, image);
        var logPath = DatabasePath + "-log";
        var log = store.Read(logPath);
        var frameOffset = MvccLogicalLogFormat.LogHeaderSize;
        var originalSize = checked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
            log.AsSpan(frameOffset + 4)));
        var originalTrailerOffset = frameOffset
                                    + MvccLogicalLogFormat.TxHeaderSize
                                    + MvccLogicalLogFormat.GetEncryptedPayloadSize(
                                        checked((int)originalSize));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            log.AsSpan(frameOffset + 4),
            checked((ulong)(originalSize + delta)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            log.AsSpan(originalTrailerOffset),
            Crc32C.Compute(log.AsSpan(
                frameOffset,
                originalTrailerOffset - frameOffset)));
        store.Seed(logPath, log);

        var failure = await Capture(() => BrowserHarness.CreateAsync(store, Aes256Key));
        failure.Should().BeOfType<InvalidDataException>();
        store.Read(logPath).Should().Equal(log);
    }

    [Test]
    public async Task MvccLogicalLogGenuineTornTailKeepsPrefixAndAcceptsANewCommit()
    {
        var store = new FakeBrowserPersistentStore();
        const string DatabasePath = "app/data/main.db";
        Dictionary<string, byte[]> durableImages;
        await using (var harness = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, harness.Mirror);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);");
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES (1, '{Secret}-first');");
            Execute(connection, "COMMIT;");
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES (2, '{Secret}-torn');");
            Execute(connection, "COMMIT;");
            await harness.Mirror.FlushPendingAsync();
            durableImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in durableImages)
            store.Seed(path, image);
        var logPath = DatabasePath + "-log";
        var tornLog = store.Read(logPath);
        Array.Resize(ref tornLog, tornLog.Length - 3);
        store.Seed(logPath, tornLog);

        Dictionary<string, byte[]> recoveredImages;
        await using (var recovered = await BrowserHarness.CreateAsync(store, Aes256Key))
        {
            CountPlaintextMvccFrames(recovered.Mirror, logPath).Should().Be(1);
            using var database = EmbeddedDatabase.OpenFile(DatabasePath, recovered.Mirror);
            using var connection = database.Connect();
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, $"INSERT INTO notes VALUES (3, '{Secret}-third');");
            Execute(connection, "COMMIT;");
            await recovered.Mirror.FlushPendingAsync();
            CountPlaintextMvccFrames(recovered.Mirror, logPath).Should().Be(2);
            recoveredImages = store.Paths.ToDictionary(path => path, store.Read, StringComparer.Ordinal);
        }

        foreach (var (path, image) in recoveredImages)
            store.Seed(path, image);
        await using var reopened = await BrowserHarness.CreateAsync(store, Aes256Key);
        CountPlaintextMvccFrames(reopened.Mirror, logPath).Should().Be(2);
    }

    private static int CountPlaintextMvccFrames(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var image = new byte[checked((int)file.Length)];
        file.Read(0, image).Should().Be(image.Length);
        _ = MvccLogicalLogFormat.ValidateHeader(image);
        var position = MvccLogicalLogFormat.LogHeaderSize;
        var count = 0;
        while (position < image.Length)
        {
            var (payloadSize, _, _) = MvccLogicalLogFormat.ReadFrameHeader(
                image.AsSpan(position, MvccLogicalLogFormat.TxHeaderSize));
            var trailerOffset = position + MvccLogicalLogFormat.TxHeaderSize + payloadSize;
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    image.AsSpan(trailerOffset + sizeof(uint)))
                .Should()
                .Be(MvccLogicalLogFormat.EndMagic);
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(trailerOffset))
                .Should()
                .Be(Crc32C.Compute(image.AsSpan(
                    position,
                    MvccLogicalLogFormat.TxHeaderSize + payloadSize)));
            position = trailerOffset + MvccLogicalLogFormat.TxTrailerSize;
            count++;
        }

        position.Should().Be(image.Length);
        return count;
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static (byte[] Journal, int PageSize) BuildHotJournalFrom(
        FakeBrowserPersistentStore store,
        string databasePath)
    {
        var database = store.Read(databasePath);
        var pageSize = (database[16] << 8) | database[17];
        if (pageSize == 1)
            pageSize = 65_536;

        var pageCount = database.Length / pageSize;
        const uint ChecksumNonce = 0x5A5A_1234;
        var recordSize = pageSize + 8;
        var journal = new byte[512 + recordSize];

        // A one-record journal holding page 2's exact encrypted pre-image.
        var magic = new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 };
        magic.CopyTo(journal.AsSpan());
        WriteUInt32BigEndian(journal.AsSpan(8), 1);
        WriteUInt32BigEndian(journal.AsSpan(12), ChecksumNonce);
        WriteUInt32BigEndian(journal.AsSpan(16), (uint)pageCount);
        WriteUInt32BigEndian(journal.AsSpan(20), 512);
        WriteUInt32BigEndian(journal.AsSpan(24), (uint)pageSize);

        var page = database.AsSpan(pageSize, pageSize);
        WriteUInt32BigEndian(journal.AsSpan(512), 2);
        page.CopyTo(journal.AsSpan(512 + 4));
        WriteUInt32BigEndian(
            journal.AsSpan(512 + 4 + pageSize),
            SqliteRollbackJournalFormat.ComputeChecksum(page, ChecksumNonce));
        return (journal, pageSize);
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    private static async Task<Exception?> Capture<T>(Func<ValueTask<T>> action)
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
            string hexKey)
        {
            var persistence = new BrowserEncryptedPersistence(
                new AhtolaAsyncPageTransformer(
                    new DesktopAsyncPageCipher(
                        Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
                        Convert.FromHexString(hexKey))));
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
