using System.Diagnostics;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Concurrency regressions for the asynchronous pager and the asynchronous
/// statement seam. Every case pairs two participants on one physical database so
/// a lost lock, an early publication, or a blocking wait is observable rather
/// than merely theoretical.
/// </summary>
public sealed class AsyncPagerConcurrencyRegressionTests
{
    private const string DatabasePath = "main.db";
    private const string WalPath = "main.db-wal";

    [Test]
    public async Task DeleteModeCommitReportsBusyWhileAReadSnapshotIsOpen()
    {
        var storage = new InMemoryFileSystem();
        var original = CreatePage(0xA1);
        var replacement = CreatePage(0xB2);

        await using var writer = await AsyncSqlitePager.CreateRollbackJournalAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            CreateLegacyHeader());
        await CommitPageAsync(writer, replacement: original);

        await using var readerPager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);
        var snapshot = await readerPager.BeginReadAsync();
        try
        {
            (await snapshot.ReadPageAsync(2)).Should().Equal(original);

            await using (var contended = await writer.BeginTransactionAsync(2, TimeSpan.Zero))
            {
                await contended.WritePageAsync(2, replacement);
                Func<Task> commit = async () => await contended.CommitAsync();
                await commit.Should().ThrowAsync<SqlitePagerBusyException>();
                await contended.RollbackAsync();
            }

            (await snapshot.ReadPageAsync(2)).Should().Equal(original);
        }
        finally
        {
            await snapshot.DisposeAsync();
        }

        await CommitPageAsync(writer, replacement);
        (await readerPager.ReadPageAsync(2)).Should().Equal(replacement);
    }

    [Test]
    public async Task DeleteModeCommitWaitsForTheReadSnapshotItWouldOverwrite()
    {
        var storage = new InMemoryFileSystem();
        var lockManager = new SqlitePagerLockManager();
        var original = CreatePage(0xA3);
        var replacement = CreatePage(0xB4);

        await using var writer = await AsyncSqlitePager.CreateRollbackJournalAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            CreateLegacyHeader(),
            lockManager: lockManager);
        await CommitPageAsync(writer, original);

        await using var readerPager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            lockManager: lockManager);
        var snapshot = await readerPager.BeginReadAsync();
        var snapshotReleased = false;
        try
        {
            await using var contended = await writer.BeginTransactionAsync(
                2,
                Timeout.InfiniteTimeSpan);
            await contended.WritePageAsync(2, replacement);
            var commit = contended.CommitAsync().AsTask();

            // The commit registers its EXCLUSIVE intent before waiting, which is
            // exactly what shuts new readers out; poll for that instead of sleeping.
            await WaitUntilAsync(() => ExcludesNewReaders(lockManager));
            commit.IsCompleted.Should().BeFalse();
            (await snapshot.ReadPageAsync(2)).Should().Equal(original);

            await snapshot.DisposeAsync();
            snapshotReleased = true;
            await commit.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!snapshotReleased)
                await snapshot.DisposeAsync();
        }

        (await readerPager.ReadPageAsync(2)).Should().Equal(replacement);
    }

    [Test]
    public async Task WalCommitStaysInvisibleToConcurrentReadersUntilItsFlushSucceeds()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var first = CreatePage(0xC1);
        var second = CreatePage(0xC2);

        await using var writer = await AsyncSqlitePager.CreateAsync(
            writerFileSystem,
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await CommitPageAsync(writer, first);

        await using var readerPager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);
        (await readerPager.ReadPageAsync(2)).Should().Equal(first);

        byte[]? pageDuringFlush = null;
        var frameCountDuringFlush = -1L;
        writerFileSystem.BeforeFlush = async () =>
        {
            pageDuringFlush = await readerPager.ReadPageAsync(2);
            frameCountDuringFlush = readerPager.CommittedFrameCount;
        };

        await CommitPageAsync(writer, second);

        pageDuringFlush.Should().Equal(first);
        frameCountDuringFlush.Should().Be(1);
        (await readerPager.ReadPageAsync(2)).Should().Equal(second);
        readerPager.CommittedFrameCount.Should().Be(2);
    }

    [Test]
    public async Task WalCommitWhoseFlushFailsNeverBecomesVisibleToConcurrentReaders()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var committed = CreatePage(0xD1);
        var abandoned = CreatePage(0xD2);

        await using var writer = await AsyncSqlitePager.CreateAsync(
            writerFileSystem,
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await CommitPageAsync(writer, committed);

        await using var readerPager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);
        (await readerPager.ReadPageAsync(2)).Should().Equal(committed);

        writerFileSystem.FlushFailure = new IOException("injected SQLite WAL flush failure");
        await using (var failing = await writer.BeginTransactionAsync(2))
        {
            await failing.WritePageAsync(2, abandoned);
            Func<Task> commit = async () => await failing.CommitAsync();
            await commit.Should().ThrowAsync<IOException>();
        }

        // The abandoned commit is rolled out of the WAL durably rather than merely
        // hidden, so no reader — in this process or another — can adopt it.
        await using (var rawWal = await SqliteWalFile.OpenAsync(
                         AsyncFileSystemAdapter.Create(storage),
                         WalPath,
                         readOnly: true))
        {
            var recovery = await rawWal.ScanRecoveryAsync();
            recovery.LastCommittedFrameNumber.Should().Be(1);
            recovery.LastValidFrameNumber.Should().Be(1);
        }

        (await readerPager.ReadPageAsync(2)).Should().Equal(committed);
        readerPager.CommittedFrameCount.Should().Be(1);
    }

    [Test]
    public async Task WalCommitWhoseFlushFailsStaysInvisibleToLaterOpensAndReadOnlyReaders()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var committed = CreatePage(0xE1);
        var abandoned = CreatePage(0xE2);

        await using (var writer = await AsyncSqlitePager.CreateAsync(
                         writerFileSystem,
                         DatabasePath,
                         WalPath,
                         CreateWalHeader()))
        {
            await CommitPageAsync(writer, committed);

            writerFileSystem.FlushFailure = new IOException("injected SQLite WAL flush failure");
            await using var failing = await writer.BeginTransactionAsync(2);
            await failing.WritePageAsync(2, abandoned);
            Func<Task> commit = async () => await failing.CommitAsync();
            await commit.Should().ThrowAsync<IOException>();
        }

        // A read-only open cannot repair the WAL, so it must keep honoring the
        // boundary rather than adopting the abandoned commit.
        await using (var readOnly = await AsyncSqlitePager.OpenAsync(
                         AsyncFileSystemAdapter.Create(storage),
                         DatabasePath,
                         WalPath,
                         readOnly: true))
        {
            (await readOnly.ReadPageAsync(2)).Should().Equal(committed);
        }

        // A writable open owns the file, so it durably drops the hidden tail before
        // republishing; the abandoned commit can never come back afterwards.
        await using (var reopened = await AsyncSqlitePager.OpenAsync(
                         AsyncFileSystemAdapter.Create(storage),
                         DatabasePath,
                         WalPath))
        {
            (await reopened.ReadPageAsync(2)).Should().Equal(committed);
            reopened.CommittedFrameCount.Should().Be(1);
        }

        await using (var rawWal = await SqliteWalFile.OpenAsync(
                         AsyncFileSystemAdapter.Create(storage),
                         WalPath,
                         readOnly: true))
        {
            var recovery = await rawWal.ScanRecoveryAsync();
            recovery.LastCommittedFrameNumber.Should().Be(1);
            recovery.LastValidFrameNumber.Should().Be(1);
        }

        await using var final = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            readOnly: true);
        (await final.ReadPageAsync(2)).Should().Equal(committed);
    }

    [Test]
    public async Task WalCommitIgnoresCancellationRequestedAfterItsFlushBecomesDurable()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var committed = CreatePage(0xE3);
        await using var writer = await AsyncSqlitePager.CreateAsync(
            writerFileSystem,
            DatabasePath,
            WalPath,
            CreateWalHeader());
        using var cancellation = new CancellationTokenSource();
        writerFileSystem.AfterFlush = () =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        };

        await using (var transaction = await writer.BeginTransactionAsync(2))
        {
            await transaction.WritePageAsync(2, committed);
            await transaction.CommitAsync(cancellation.Token);
        }

        cancellation.IsCancellationRequested.Should().BeTrue();
        await using var reopened = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            readOnly: true);
        (await reopened.ReadPageAsync(2)).Should().Equal(committed);
        reopened.CommittedFrameCount.Should().Be(1);
    }

    [Test]
    public async Task FailedCreateDoesNotPublishAnExistingHiddenWalTail()
    {
        var storage = new InMemoryFileSystem();
        var lockManager = new SqlitePagerLockManager();
        await using (var existing = await AsyncSqlitePager.CreateAsync(
                         AsyncFileSystemAdapter.Create(storage),
                         DatabasePath,
                         WalPath,
                         CreateWalHeader(),
                         lockManager: lockManager))
        {
            await CommitPageAsync(existing, CreatePage(0xE4));
        }

        using (var writer = lockManager.EnterWriter(TimeSpan.Zero))
            writer.BeginWalPublication(lastPublishedFrameNumber: 0);
        lockManager.WalPublicationBoundary.Should().Be(0);

        Func<Task> create = async () => await AsyncSqlitePager.CreateAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            CreateWalHeader(),
            lockManager: lockManager);
        await create.Should().ThrowAsync<IOException>();
        lockManager.WalPublicationBoundary.Should().Be(0);

        await using var readOnly = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            readOnly: true,
            lockManager: lockManager);
        readOnly.CommittedFrameCount.Should().Be(0);
    }

    [Test]
    public async Task SeparateAsyncAdaptersOverOneBackendShareTheWriterLock()
    {
        var storage = new InMemoryFileSystem();
        await using var owner = await AsyncSqlitePager.CreateAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await using var peer = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);

        await using var held = await owner.BeginTransactionAsync(2, TimeSpan.Zero);
        Func<Task> contend = async () => await peer.BeginTransactionAsync(2, TimeSpan.Zero);
        await contend.Should().ThrowAsync<SqlitePagerBusyException>();
    }

    [Test]
    public async Task CaseAliasedPathsOnACaseInsensitiveBackendShareTheWriterLock()
    {
        var storage = new CaseInsensitiveFileSystem();
        await using var owner = await AsyncSqlitePager.CreateAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await using var alias = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath.ToUpperInvariant(),
            WalPath.ToUpperInvariant());

        await using var held = await owner.BeginTransactionAsync(2, TimeSpan.Zero);
        Func<Task> contend = async () => await alias.BeginTransactionAsync(2, TimeSpan.Zero);
        await contend.Should().ThrowAsync<SqlitePagerBusyException>();
    }

    [Test]
    public async Task StepAsyncSuspendsInsteadOfBlockingWhileAPeerHoldsTheWriteReservation()
    {
        using var database = new EmbeddedDatabase();
        using var owner = database.Connect();
        using var contender = database.Connect();
        ExecuteAll(owner, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER);", "BEGIN IMMEDIATE;");

        var adapter = ManagedConnectionAdapter.Wrap(contender);
        adapter.BusyTimeout = TimeSpan.FromSeconds(30);
        using var statement = adapter.Prepare("INSERT INTO t(v) VALUES (1);");

        var callDuration = Stopwatch.StartNew();
        var step = statement.StepAsync().AsTask();
        callDuration.Stop();

        // The synchronous engine would have parked this thread for the whole busy
        // timeout; the seam has to return a pending task instead.
        callDuration.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        step.IsCompleted.Should().BeFalse();

        ExecuteAll(owner, "COMMIT;");
        (await step.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(StatementStepResult.Done);

        using var count = adapter.Prepare("SELECT count(*) FROM t;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).AsInteger().Should().Be(1);
    }

    [Test]
    public async Task StepAsyncCancellationIsObservedWithoutSpendingTheBusyTimeout()
    {
        using var database = new EmbeddedDatabase();
        using var owner = database.Connect();
        using var contender = database.Connect();
        ExecuteAll(owner, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER);", "BEGIN IMMEDIATE;");

        var adapter = ManagedConnectionAdapter.Wrap(contender);
        adapter.BusyTimeout = Timeout.InfiniteTimeSpan;
        using var statement = adapter.Prepare("INSERT INTO t(v) VALUES (1);");
        using var cancellation = new CancellationTokenSource();

        var step = statement.StepAsync(cancellation.Token).AsTask();
        step.IsCompleted.Should().BeFalse();

        cancellation.Cancel();
        Func<Task> awaitCanceled = async () => await step;
        var canceled = await awaitCanceled.Should().ThrowAsync<OperationCanceledException>();
        canceled.Which.CancellationToken.Should().Be(cancellation.Token);

        ExecuteAll(owner, "COMMIT;");
    }

    [Test]
    public async Task StepAsyncPreservesBusyReportingWhenTheTimeoutIsExhausted()
    {
        using var database = new EmbeddedDatabase();
        using var owner = database.Connect();
        using var contender = database.Connect();
        ExecuteAll(owner, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER);", "BEGIN IMMEDIATE;");

        var adapter = ManagedConnectionAdapter.Wrap(contender);
        adapter.BusyTimeout = TimeSpan.FromMilliseconds(50);
        using var statement = adapter.Prepare("INSERT INTO t(v) VALUES (1);");

        Func<Task> step = async () => await statement.StepAsync();
        await step.Should().ThrowAsync<EmbeddedBusyException>();
        adapter.BusyTimeout.Should().Be(TimeSpan.FromMilliseconds(50));

        ExecuteAll(owner, "COMMIT;");
    }

    [Test]
    public async Task DurableWalCommitSurvivesAPostFlushBookkeepingFailure()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var first = CreatePage(0xF1);
        var second = CreatePage(0xF2);

        await using (var writer = await AsyncSqlitePager.CreateAsync(
                         writerFileSystem,
                         DatabasePath,
                         WalPath,
                         CreateWalHeader()))
        {
            await CommitPageAsync(writer, first);

            // The commit frame reaches durable storage and only the bookkeeping that
            // records it fails, so the transaction is committed and must stay so.
            writerFileSystem.PostFlushReadFailure =
                new IOException("injected post-flush SQLite WAL read failure");
            await using var failing = await writer.BeginTransactionAsync(2);
            await failing.WritePageAsync(2, second);
            Func<Task> commit = async () => await failing.CommitAsync();
            await commit.Should().ThrowAsync<IOException>();
        }

        await AssertDurableCommitSurvivesAsync(storage, second, expectedFrameCount: 2);
    }

    [Test]
    public async Task DurableWalCommitSurvivesCancellationRaisedAfterItsFlush()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var first = CreatePage(0xF3);
        var second = CreatePage(0xF4);

        await using (var writer = await AsyncSqlitePager.CreateAsync(
                         writerFileSystem,
                         DatabasePath,
                         WalPath,
                         CreateWalHeader()))
        {
            await CommitPageAsync(writer, first);

            using var cancellation = new CancellationTokenSource();
            writerFileSystem.AfterFlush = () =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            };

            await using var committing = await writer.BeginTransactionAsync(2);
            await committing.WritePageAsync(2, second);

            // Cancelling cannot un-commit a durable transaction, so the commit runs
            // to completion rather than abandoning the record of it.
            await committing.CommitAsync(cancellation.Token);
            committing.State.Should().Be(SqlitePagerTransactionState.Committed);
            writer.CommittedFrameCount.Should().Be(2);
        }

        await AssertDurableCommitSurvivesAsync(storage, second, expectedFrameCount: 2);
    }

    [Test]
    public async Task FailedCreateKeepsAnAbandonedWalTailHidden()
    {
        var storage = new InMemoryFileSystem();
        var writerFileSystem = new FlushInterceptingAsyncFileSystem(storage, WalPath);
        var committed = CreatePage(0xC7);
        var abandoned = CreatePage(0xC8);

        await using var writer = await AsyncSqlitePager.CreateAsync(
            writerFileSystem,
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await CommitPageAsync(writer, committed);

        await using var readerPager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath);
        (await readerPager.ReadPageAsync(2)).Should().Equal(committed);

        // Both the flush and the rollback truncation fail, so the abandoned tail is
        // still physically present and only the boundary is hiding it.
        writerFileSystem.FlushFailure = new IOException("injected SQLite WAL flush failure");
        writerFileSystem.SetLengthFailure = new IOException("injected SQLite WAL truncation failure");
        await using (var failing = await writer.BeginTransactionAsync(2))
        {
            await failing.WritePageAsync(2, abandoned);
            Func<Task> commit = async () => await failing.CommitAsync();
            await commit.Should().ThrowAsync<IOException>();
        }

        (await readerPager.ReadPageAsync(2)).Should().Equal(committed);

        // Creating over the existing database fails, and a create that never
        // produced fresh storage must not publish the tail it was hiding.
        Func<Task> create = async () => await AsyncSqlitePager.CreateAsync(
            AsyncFileSystemAdapter.Create(storage),
            DatabasePath,
            WalPath,
            CreateWalHeader());
        await create.Should().ThrowAsync<IOException>();

        (await readerPager.ReadPageAsync(2)).Should().Equal(committed);
        readerPager.CommittedFrameCount.Should().Be(1);
    }

    private static async Task AssertDurableCommitSurvivesAsync(
        IFileSystem storage,
        byte[] expectedPage,
        long expectedFrameCount)
    {
        // A writable reopen repairs whatever the boundary still hides, so a commit
        // wrongly left hidden would be truncated away here rather than retained.
        await using (var reopened = await AsyncSqlitePager.OpenAsync(
                         AsyncFileSystemAdapter.Create(storage),
                         DatabasePath,
                         WalPath))
        {
            (await reopened.ReadPageAsync(2)).Should().Equal(expectedPage);
            reopened.CommittedFrameCount.Should().Be(expectedFrameCount);
        }

        await using var rawWal = await SqliteWalFile.OpenAsync(
            AsyncFileSystemAdapter.Create(storage),
            WalPath,
            readOnly: true);
        (await rawWal.ScanRecoveryAsync()).LastCommittedFrameNumber.Should().Be(expectedFrameCount);
    }

    private static async Task CommitPageAsync(AsyncSqlitePager pager, byte[] replacement)
    {
        await using var transaction = await pager.BeginTransactionAsync(2);
        await transaction.WritePageAsync(2, replacement);
        await transaction.CommitAsync();
    }

    private static void ExecuteAll(EmbeddedConnection connection, params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var statement = connection.Prepare(sql);
            statement.Step();
        }
    }

    private static bool ExcludesNewReaders(SqlitePagerLockManager lockManager)
    {
        try
        {
            lockManager.EnterReader(TimeSpan.Zero).Dispose();
            return false;
        }
        catch (SqlitePagerBusyException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("The awaited pager lock state never arrived.");
            await Task.Yield();
        }
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x0F1E_2D3C,
            salt2: 0x4B5A_6978,
            checkpointSequence: 3);

    private static SqliteDatabaseHeader CreateLegacyHeader()
        => SqliteDatabaseHeader.CreateDefault() with
        {
            ReadVersion = SqliteFileFormatVersion.Legacy,
            WriteVersion = SqliteFileFormatVersion.Legacy,
        };

    private static byte[] CreatePage(byte fill)
    {
        var page = new byte[SqlitePageSize.Default];
        Array.Fill(page, fill);
        return page;
    }
}

/// <summary>
/// Wraps a synchronous backend and lets a test observe or fail the durable flush
/// of one specific file. It reports the same backing store as a plain adapter so
/// pagers built on either facade still resolve to one pager lock scope.
/// </summary>
internal sealed class FlushInterceptingAsyncFileSystem :
    IAsyncFileSystem,
    IStoragePathResolver,
    IAsyncFileSystemBacking
{
    private readonly IFileSystem _backing;
    private readonly IAsyncFileSystem _inner;
    private readonly string _interceptedPath;

    internal FlushInterceptingAsyncFileSystem(IFileSystem backing, string interceptedPath)
    {
        _backing = backing;
        _inner = AsyncFileSystemAdapter.Create(backing);
        _interceptedPath = interceptedPath;
    }

    internal Func<ValueTask>? BeforeFlush { get; set; }

    internal Func<ValueTask>? AfterFlush { get; set; }

    internal Exception? FlushFailure { get; set; }

    internal Exception? SetLengthFailure { get; set; }

    internal Exception? PostFlushReadFailure { get; set; }

    private Exception? ArmedReadFailure { get; set; }

    public IFileSystem BackingFileSystem => _backing;

    public StringComparer PathComparer => ((IStoragePathResolver)_inner).PathComparer;

    public string GetCanonicalPath(string path) => ((IStoragePathResolver)_inner).GetCanonicalPath(path);

    public ValueTask<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        => _inner.FileExistsAsync(path, cancellationToken);

    public async ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        var file = await _inner.OpenFileAsync(path, mode, readOnly, cancellationToken).ConfigureAwait(false);
        return string.Equals(path, _interceptedPath, StringComparison.Ordinal)
            ? new InterceptedFile(file, this)
            : file;
    }

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        => _inner.DeleteFileAsync(path, cancellationToken);

    public ValueTask<FileWriteStamp?> GetWriteStampAsync(
        string path,
        CancellationToken cancellationToken = default)
        => _inner.GetWriteStampAsync(path, cancellationToken);

    private sealed class InterceptedFile(IAsyncFile inner, FlushInterceptingAsyncFileSystem owner) : IAsyncFile
    {
        public bool IsReadOnly => inner.IsReadOnly;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
            => inner.GetLengthAsync(cancellationToken);

        public ValueTask<int> ReadAsync(
            long position,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            var armed = owner.ArmedReadFailure;
            if (armed is not null)
            {
                owner.ArmedReadFailure = null;
                throw armed;
            }

            return inner.ReadAsync(position, destination, cancellationToken);
        }

        public ValueTask WriteAsync(
            long position,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
            => inner.WriteAsync(position, source, cancellationToken);

        public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
        {
            var failure = owner.SetLengthFailure;
            if (failure is not null)
            {
                owner.SetLengthFailure = null;
                throw failure;
            }

            return inner.SetLengthAsync(length, cancellationToken);
        }

        public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
        {
            var observe = owner.BeforeFlush;
            if (observe is not null)
            {
                owner.BeforeFlush = null;
                await observe().ConfigureAwait(false);
            }

            var failure = owner.FlushFailure;
            if (failure is not null)
            {
                owner.FlushFailure = null;
                throw failure;
            }

            await inner.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

            owner.ArmedReadFailure = owner.PostFlushReadFailure;
            owner.PostFlushReadFailure = null;
            var after = owner.AfterFlush;
            if (after is not null)
            {
                owner.AfterFlush = null;
                await after().ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>
/// Models a case-insensitive volume: canonical paths keep their original casing,
/// exactly like <c>Path.GetFullPath</c> on Windows, while lookups and comparison
/// ignore case.
/// </summary>
internal sealed class CaseInsensitiveFileSystem : IFileSystem, IStoragePathResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _storedSpellings = new(StringComparer.OrdinalIgnoreCase);
    private readonly InMemoryFileSystem _inner = new();

    public StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    public string GetCanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return path;
    }

    public bool FileExists(string path) => _inner.FileExists(Resolve(path));

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        => _inner.OpenFile(Resolve(path), mode, readOnly);

    public void DeleteFile(string path) => _inner.DeleteFile(Resolve(path));

    public FileWriteStamp? GetWriteStamp(string path)
        => ((IFileSystem)_inner).GetWriteStamp(Resolve(path));


    private string Resolve(string path)
    {
        lock (_gate)
        {
            if (_storedSpellings.TryGetValue(path, out var stored))
                return stored;

            _storedSpellings[path] = path;
            return path;
        }
    }
}
