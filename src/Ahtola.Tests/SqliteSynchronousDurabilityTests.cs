using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Tests;

public sealed class SqliteSynchronousDurabilityTests
{
    private const string DatabasePath = "main.db";
    private const string WalPath = "main.db-wal";

    [TestCase(SqliteSynchronousMode.Off, 0)]
    [TestCase(SqliteSynchronousMode.Normal, 0)]
    [TestCase(SqliteSynchronousMode.Full, 1)]
    [TestCase(SqliteSynchronousMode.Extra, 1)]
    public void WalCommitUsesModeSpecificBarrier(
        SqliteSynchronousMode synchronousMode,
        int expectedFlushCount)
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateWalPager(fileSystem);
        fileSystem.ClearEvents();

        using var transaction = pager.BeginTransaction(2);
        transaction.WritePage(2, CreatePage(pager.PageSize, 0x41));
        transaction.Commit(synchronousMode);

        fileSystem.FlushPaths.Should().Equal(
            Enumerable.Repeat(WalPath, expectedFlushCount));
    }

    [TestCase(SqliteSynchronousMode.Normal)]
    [TestCase(SqliteSynchronousMode.Full)]
    [TestCase(SqliteSynchronousMode.Extra)]
    public void DurableCheckpointOrdersWalBeforeDatabaseAndWalReset(
        SqliteSynchronousMode synchronousMode)
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateWalPager(fileSystem);
        using (var transaction = pager.BeginTransaction(2))
        {
            transaction.WritePage(2, CreatePage(pager.PageSize, 0x42));
            transaction.Commit(SqliteSynchronousMode.Normal);
        }
        fileSystem.ClearEvents();

        var result = pager.CheckpointToMainStoreAndResetWal(
            synchronousMode: synchronousMode);

        result.RetainedCommittedFrameCount.Should().Be(0);
        fileSystem.FlushPaths.Should().Equal(WalPath, DatabasePath, WalPath);
    }

    [Test]
    public void OffCheckpointRetainsWalAndIssuesNoBarrier()
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateWalPager(fileSystem);
        using (var transaction = pager.BeginTransaction(2))
        {
            transaction.WritePage(2, CreatePage(pager.PageSize, 0x43));
            transaction.Commit(SqliteSynchronousMode.Off);
        }
        fileSystem.ClearEvents();

        var result = pager.CheckpointToMainStoreAndResetWal(
            synchronousMode: SqliteSynchronousMode.Off);

        result.RetainedCommittedFrameCount.Should().Be(1);
        fileSystem.FlushPaths.Should().BeEmpty();
        using var wal = SqliteWalFile.Open(fileSystem, WalPath, readOnly: true);
        wal.ScanRecovery().LastCommittedFrameNumber.Should().Be(1);
    }

    [Test]
    public void FullCommitPropagatesWalBarrierFailureBeforePublication()
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateWalPager(fileSystem);
        fileSystem.ClearEvents();
        fileSystem.FailNextFlush(WalPath);

        using var transaction = pager.BeginTransaction(2);
        transaction.WritePage(2, CreatePage(pager.PageSize, 0x44));
        Assert.Throws<IOException>(
            () => transaction.Commit(SqliteSynchronousMode.Full));

        transaction.State.Should().Be(SqlitePagerTransactionState.Faulted);
        pager.State.Should().Be(SqlitePagerState.Faulted);
    }

    [Test]
    public void NormalCommitDefersWalBarrierFailureToCheckpoint()
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateWalPager(fileSystem);
        fileSystem.ClearEvents();
        fileSystem.FailNextFlush(WalPath);
        using (var transaction = pager.BeginTransaction(2))
        {
            transaction.WritePage(2, CreatePage(pager.PageSize, 0x45));
            transaction.Commit(SqliteSynchronousMode.Normal);
        }

        pager.ReadCommittedPage(2).Should().StartWith(0x45);
        Assert.Throws<IOException>(
            () => pager.CheckpointToMainStore(
                synchronousMode: SqliteSynchronousMode.Normal));
        fileSystem.Events.Should().NotContain(
            entry => entry.Path == DatabasePath
                     && entry.Operation == FileSystemOperation.Write);
    }

    [Test]
    public async Task AsyncNormalCommitDefersBarrierAndCheckpointPreservesOrdering()
    {
        var fileSystem = new RecordingFileSystem();
        await using var pager = await AsyncSqlitePager.CreateAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            DatabasePath,
            WalPath,
            CreateWalHeader());
        fileSystem.ClearEvents();

        await using (var transaction = await pager.BeginTransactionAsync(2))
        {
            await transaction.WritePageAsync(2, CreatePage(pager.PageSize, 0x47));
            await transaction.CommitAsync(
                CancellationToken.None,
                SqliteSynchronousMode.Normal);
        }
        fileSystem.FlushPaths.Should().BeEmpty();

        _ = await pager.CheckpointToMainStoreAndResetWalAsync(
            synchronousMode: SqliteSynchronousMode.Normal);
        fileSystem.FlushPaths.Should().Equal(WalPath, DatabasePath, WalPath);
    }

    [TestCase(
        SqliteSynchronousMode.Off,
        new string[] { })]
    [TestCase(
        SqliteSynchronousMode.Normal,
        new[] { "main.db-journal", DatabasePath })]
    [TestCase(
        SqliteSynchronousMode.Full,
        new[] { "main.db-journal", "main.db-journal", DatabasePath, "main.db-journal" })]
    [TestCase(
        SqliteSynchronousMode.Extra,
        new[] { "main.db-journal", "main.db-journal", DatabasePath, "main.db-journal" })]
    public void RollbackJournalUsesModeSpecificBarrierSequence(
        SqliteSynchronousMode synchronousMode,
        string[] expectedFlushPaths)
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateRollbackPager(fileSystem);
        fileSystem.ClearEvents();
        var pageOne = pager.ReadCommittedPage(1);
        pageOne[^1] ^= 0x01;

        using var transaction = pager.BeginTransaction(1);
        transaction.WritePage(1, pageOne);
        transaction.Commit(synchronousMode);

        fileSystem.FlushPaths.Should().Equal(expectedFlushPaths);
        fileSystem.FileExists(DatabasePath + "-journal").Should().BeFalse();
    }

    [Test]
    public void FullRollbackJournalFailurePreventsDatabaseWrite()
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateRollbackPager(fileSystem);
        fileSystem.ClearEvents();
        fileSystem.FailNextFlush(DatabasePath + "-journal");
        var pageOne = pager.ReadCommittedPage(1);
        pageOne[^1] ^= 0x01;

        using var transaction = pager.BeginTransaction(1);
        transaction.WritePage(1, pageOne);
        Assert.Throws<IOException>(
            () => transaction.Commit(SqliteSynchronousMode.Full));

        fileSystem.Events.Should().NotContain(
            entry => entry.Path == DatabasePath
                     && entry.Operation == FileSystemOperation.Write);
    }

    [Test]
    public void PragmaSynchronousControlsManagedFileStoreBarriers()
    {
        var fileSystem = new RecordingFileSystem();
        using var database = EmbeddedDatabase.OpenFile("pragma.db", fileSystem);
        using var connection = database.Connect();
        fileSystem.ClearEvents();

        Execute(connection, "PRAGMA synchronous=OFF;");
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "CREATE INDEX ix_items_value ON items(value);");
        fileSystem.FlushPaths.Should().BeEmpty();

        fileSystem.ClearEvents();
        Execute(connection, "REINDEX ix_items_value;");
        fileSystem.FlushPaths.Should().BeEmpty();

        fileSystem.ClearEvents();
        Execute(connection, "PRAGMA synchronous=FULL;");
        Execute(connection, "INSERT INTO items VALUES(1, 'durable');");
        fileSystem.FlushPaths.Should().Contain(path => path == "pragma.db-wal");
        fileSystem.FlushPaths.Should().Contain(path => path == "pragma.db");
    }

    [Test]
    public void PragmaSynchronousCannotChangeInsideTransactionOrSavepoint()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "BEGIN;");
        AssertSynchronousChangeRejected(connection);
        ReadValue(connection, "PRAGMA synchronous;").Should().Be(SqlValue.Integer(2));
        Execute(connection, "ROLLBACK;");

        Execute(connection, "SAVEPOINT durability;");
        AssertSynchronousChangeRejected(connection);
        ReadValue(connection, "PRAGMA synchronous;").Should().Be(SqlValue.Integer(2));
        Execute(connection, "RELEASE durability;");

        Execute(connection, "PRAGMA synchronous=OFF;");
        ReadValue(connection, "PRAGMA synchronous;").Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void WalToDeleteForcesDurableFoldEvenWhenSynchronousIsOff()
    {
        var fileSystem = new RecordingFileSystem();
        using var pager = CreateWalPager(fileSystem);
        var page2 = CreatePage(pager.PageSize, 0x48);
        using (var transaction = pager.BeginTransaction(2))
        {
            transaction.WritePage(2, page2);
            transaction.Commit(SqliteSynchronousMode.Off);
        }
        fileSystem.ClearEvents();

        pager.SwitchJournalMode(
            SqliteJournalMode.Delete,
            synchronousMode: SqliteSynchronousMode.Off);

        pager.JournalMode.Should().Be(SqliteJournalMode.Delete);
        fileSystem.FileExists(WalPath).Should().BeFalse();
        fileSystem.FlushPaths.Should().Equal(
            WalPath,
            DatabasePath,
            WalPath,
            DatabasePath + "-journal",
            DatabasePath + "-journal",
            DatabasePath,
            DatabasePath + "-journal");
        pager.ReadCommittedPage(2).Should().Equal(page2);
    }

    [Test]
    public void FailedWalToDeleteFoldRetainsRecoveryWalAndReopensCommittedView()
    {
        var fileSystem = new RecordingFileSystem();
        var page2 = CreatePage(SqlitePageSize.Default, 0x49);
        using (var pager = CreateWalPager(fileSystem))
        {
            using (var transaction = pager.BeginTransaction(2))
            {
                transaction.WritePage(2, page2);
                transaction.Commit(SqliteSynchronousMode.Off);
            }
            fileSystem.ClearEvents();
            fileSystem.FailNextFlush(DatabasePath);

            Assert.Throws<IOException>(
                () => pager.SwitchJournalMode(
                    SqliteJournalMode.Delete,
                    synchronousMode: SqliteSynchronousMode.Off));

            pager.JournalMode.Should().Be(SqliteJournalMode.Wal);
            fileSystem.FileExists(WalPath).Should().BeTrue();
            fileSystem.FlushPaths.Should().Equal(WalPath, DatabasePath);
        }

        using var reopened = SqlitePager.Open(fileSystem, DatabasePath, WalPath);
        reopened.JournalMode.Should().Be(SqliteJournalMode.Wal);
        reopened.ReadCommittedPage(2).Should().Equal(page2);
    }

    [Test]
    public async Task BrowserMirrorReplaysOnlyBarriersRequestedByPager()
    {
        var persistent = new FakeBrowserPersistentStore();
        await using var mirror = await BrowserMirroredFileSystem.CreateAsync(
            persistent,
            "root");
        using var pager = SqlitePager.Create(
            mirror,
            "root/main.db",
            "root/main.db-wal",
            CreateWalHeader());
        await mirror.FlushPendingAsync();
        persistent.FlushPaths.Clear();

        using (var transaction = pager.BeginTransaction(2))
        {
            transaction.WritePage(2, CreatePage(pager.PageSize, 0x46));
            transaction.Commit(SqliteSynchronousMode.Normal);
        }
        _ = pager.CheckpointToMainStoreAndResetWal(
            synchronousMode: SqliteSynchronousMode.Normal);
        await mirror.FlushPendingAsync();

        persistent.FlushPaths.Should().Equal(
            "root/main.db-wal",
            "root/main.db",
            "root/main.db-wal");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static void AssertSynchronousChangeRejected(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("PRAGMA synchronous=OFF;");
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("Safety level may not be changed inside a transaction");
    }

    private static SqlitePager CreateWalPager(IFileSystem fileSystem)
        => SqlitePager.Create(
            fileSystem,
            DatabasePath,
            WalPath,
            CreateWalHeader());

    private static SqlitePager CreateRollbackPager(IFileSystem fileSystem)
        => SqlitePager.CreateRollbackJournal(
            fileSystem,
            DatabasePath,
            WalPath,
            SqliteDatabaseHeader.CreateDefault());

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x10203040,
            salt2: 0x50607080);

    private static byte[] CreatePage(int pageSize, byte value)
    {
        var page = new byte[pageSize];
        page.AsSpan().Fill(value);
        return page;
    }

    private sealed class RecordingFileSystem :
        IFileSystem,
        IStoragePathResolver
    {
        private readonly InMemoryFileSystem _inner = new();
        private string? _failedFlushPath;

        public List<(string Path, FileSystemOperation Operation)> Events { get; } = [];

        public IEnumerable<string> FlushPaths
            => Events
                .Where(entry => entry.Operation == FileSystemOperation.FlushToDisk)
                .Select(entry => entry.Path);

        public StringComparer PathComparer => StringComparer.Ordinal;

        public string GetCanonicalPath(string path) => path;

        public bool FileExists(string path) => _inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
            => new RecordingFile(this, path, _inner.OpenFile(path, mode, readOnly));

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public FileWriteStamp? GetWriteStamp(string path)
            => ((IFileSystem)_inner).GetWriteStamp(path);

        public void ClearEvents() => Events.Clear();

        public void FailNextFlush(string path) => _failedFlushPath = path;

        private sealed class RecordingFile(
            RecordingFileSystem owner,
            string path,
            IFile inner) : IFile
        {
            public long Length => inner.Length;

            public bool IsReadOnly => inner.IsReadOnly;

            public int Read(long position, Span<byte> destination)
                => inner.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source)
            {
                owner.Events.Add((path, FileSystemOperation.Write));
                inner.Write(position, source);
            }

            public void SetLength(long length)
            {
                owner.Events.Add((path, FileSystemOperation.SetLength));
                inner.SetLength(length);
            }

            public void FlushToDisk()
            {
                owner.Events.Add((path, FileSystemOperation.FlushToDisk));
                if (string.Equals(owner._failedFlushPath, path, StringComparison.Ordinal))
                {
                    owner._failedFlushPath = null;
                    throw new IOException($"Injected flush failure for '{path}'.");
                }

                inner.FlushToDisk();
            }

            public void Dispose() => inner.Dispose();
        }
    }
}
