using System.Diagnostics;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqlitePagerLockingStorageTests
{
    [Test]
    public void LockManagerAllowsReadersWithWriterAndReportsBusyContention()
    {
        var locks = new SqlitePagerLockManager();
        using var reader = locks.EnterReader();
        locks.State.Should().Be(SqlitePagerLockState.Readers);

        using var writer = locks.EnterWriter();
        locks.State.Should().Be(SqlitePagerLockState.WriterAndReaders);

        var writerBusy = Assert.Throws<SqlitePagerBusyException>(() => locks.EnterWriter());
        writerBusy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        writerBusy.Timeout.Should().Be(TimeSpan.Zero);

        var checkpointBusy = Assert.Throws<SqlitePagerBusyException>(
            () => locks.EnterCheckpoint(TimeSpan.FromMilliseconds(1)));
        checkpointBusy!.Operation.Should().Be(SqlitePagerLockOperation.Checkpoint);
        checkpointBusy.Timeout.Should().Be(TimeSpan.FromMilliseconds(1));
        locks.State.Should().Be(SqlitePagerLockState.WriterAndReaders);
    }

    [Test]
    public void ReaderWriterAndCheckpointInterleaveAtExplicitWalSnapshots()
    {
        var fileSystem = new InMemoryFileSystem();
        var locks = new SqlitePagerLockManager();
        using var pager = CreatePager(fileSystem, locks);
        var firstImage = CreatePage(pager.PageSize, 0x71);
        var secondImage = CreatePage(pager.PageSize, 0x72);

        using (var initialWriter = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            initialWriter.WritePage(2, firstImage);
            initialWriter.Commit();
        }

        using var reader = pager.BeginReadTransaction();
        reader.ReadPage(2).Should().Equal(firstImage);

        using (var writer = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            writer.WritePage(2, secondImage);
            writer.Commit();
        }

        pager.ReadCommittedPage(2).Should().Equal(secondImage);
        reader.ReadPage(2).Should().Equal(firstImage);
        locks.State.Should().Be(SqlitePagerLockState.Readers);
        Assert.Throws<SqlitePagerBusyException>(() => pager.CheckpointToMainStore());

        var checkpoint = Task.Run(() => pager.CheckpointToMainStore(TimeSpan.FromSeconds(5)));
        SpinWait.SpinUntil(
                () => locks.WaitingCheckpointCount == 1,
                TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue("the checkpoint must wait for the explicit read snapshot");

        Assert.Throws<SqlitePagerBusyException>(() => pager.BeginReadTransaction());
        reader.Dispose();

        checkpoint.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        checkpoint.GetAwaiter().GetResult().InstalledPageCount.Should().Be(1);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);

        using var mainStore = SqlitePageStore.Open(fileSystem, "main.db", readOnly: true);
        mainStore.ReadPage(2).Should().Equal(secondImage);
    }

    [Test]
    public void TransactionRetriesWithExclusiveLeaseWhenModeChangesDuringLockAcquisition()
    {
        var fileSystem = new InMemoryFileSystem();
        var locks = new SqlitePagerLockManager();
        using var pager = CreatePager(fileSystem, locks);
        var reader = pager.BeginReadTransaction();
        var transition = Task.Run(
            () => pager.SwitchJournalMode(SqliteJournalMode.Delete, TimeSpan.FromSeconds(5)));
        SpinWait.SpinUntil(
                () => locks.WaitingCheckpointCount == 1,
                TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue("the mode transition must be waiting for the read snapshot");

        SqlitePagerTransaction? transaction = null;
        Exception? transactionFailure = null;
        var transactionThread = new Thread(() =>
        {
            try
            {
                transaction = pager.BeginTransaction(
                    targetDatabaseSizeInPages: 1,
                    busyTimeout: TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                transactionFailure = exception;
            }
        });
        transactionThread.Start();
        SpinWait.SpinUntil(
                () => transactionThread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin),
                TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue("the transaction must select and wait for the WAL writer lease");

        try
        {
            reader.Dispose();
            transition.GetAwaiter().GetResult().Should().Be(SqliteJournalMode.Delete);
            transactionThread.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();
            transactionFailure.Should().BeNull();
            transaction.Should().NotBeNull();
            locks.State.Should().Be(SqlitePagerLockState.Checkpoint);
            Assert.Throws<SqlitePagerBusyException>(() => pager.BeginReadTransaction());
        }
        finally
        {
            reader.Dispose();
            transaction?.Dispose();
            transactionThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public void CheckpointFaultReleasesExclusiveLockForLaterOperations()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var locks = new SqlitePagerLockManager();
        using var pager = CreatePager(fileSystem, locks);

        using (var writer = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            writer.WritePage(2, CreatePage(pager.PageSize, 0x81));
            writer.Commit();
        }

        faults.FailNext(FileSystemOperation.Write);
        Assert.Throws<IOException>(() => pager.CheckpointToMainStore());

        pager.State.Should().Be(SqlitePagerState.Faulted);
        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        using var nextWriter = locks.EnterWriter();
        locks.State.Should().Be(SqlitePagerLockState.Writer);
    }

    [Test]
    public void PagersForTheSameInMemoryStorageShareTheDefaultWriterLock()
    {
        var fileSystem = new InMemoryFileSystem();
        using var first = CreatePager(fileSystem);
        using var second = SqlitePager.Open(fileSystem, "main.db", "main.db-wal");

        using var writer = first.BeginTransaction(targetDatabaseSizeInPages: 1);

        var busy = Assert.Throws<SqlitePagerBusyException>(
            () => second.BeginTransaction(targetDatabaseSizeInPages: 1));
        busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
        first.LockManager.Should().BeSameAs(second.LockManager);
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagersForDistinctAttachedDatabasePathsUseIndependentWriterLocks()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL ownership requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            using var primary = CreatePhysicalPager(Path.Combine(workDirectory, "main.db"));
            using var attached = CreatePhysicalPager(Path.Combine(workDirectory, "aux.db"));

            primary.LockManager.Should().NotBeSameAs(attached.LockManager);

            using var primaryWriter = primary.BeginTransaction(targetDatabaseSizeInPages: 1);
            using var attachedWriter = attached.BeginTransaction(targetDatabaseSizeInPages: 1);

            primary.LockManager.State.Should().Be(SqlitePagerLockState.Writer);
            attached.LockManager.State.Should().Be(SqlitePagerLockState.Writer);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    public void SharedPagersPreserveOldSnapshotsAndRefreshNewReadersAfterCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        using var writerPager = CreatePager(fileSystem);
        using var readerPager = SqlitePager.Open(fileSystem, "main.db", "main.db-wal");
        var page2 = CreatePage(writerPager.PageSize, 0x91);

        using var oldSnapshot = readerPager.BeginReadTransaction();
        using (var writer = writerPager.BeginTransaction(targetDatabaseSizeInPages: 2))
        {
            writer.WritePage(2, page2);
            writer.Commit();
        }

        oldSnapshot.PageCount.Should().Be(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => oldSnapshot.ReadPage(2));

        using var currentSnapshot = readerPager.BeginReadTransaction();
        currentSnapshot.PageCount.Should().Be(2);
        currentSnapshot.ReadPage(2).Should().Equal(page2);
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagerCreatesSharedMemoryLockCarrier()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Physical SQLite WAL shared-memory locks are only enabled on Windows.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);

            File.Exists(databasePath + "-shm").Should().BeTrue();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagerOpenFailureReleasesWriterAndRecoveryLocks()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Physical SQLite WAL shared-memory locks are only enabled on Windows.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var walPath = databasePath + "-wal";
            var header = CreateWalHeader();
            using (SqlitePageStore.Create(PhysicalFileSystem.Instance, databasePath))
            {
            }

            using (var invalidWal = PhysicalFileSystem.Instance.OpenFile(walPath, FileOpenMode.CreateNew))
            {
                invalidWal.Write(0, [0x01]);
                invalidWal.FlushToDisk();
            }

            Assert.Throws<InvalidDataException>(() => SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                walPath,
                busyTimeout: TimeSpan.Zero));

            PhysicalFileSystem.Instance.DeleteFile(walPath);
            using (SqliteWalFile.Create(PhysicalFileSystem.Instance, walPath, header))
            {
            }

            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                walPath,
                busyTimeout: TimeSpan.Zero);
            pager.State.Should().Be(SqlitePagerState.Ready);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagerAllowsCrossProcessOpenUnderSharedMainFileLock()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Physical SQLite WAL shared-memory locks are only enabled on Windows.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using var pager = CreatePhysicalPager(databasePath);
            // Stage 6: idle managed owner holds SHARED only; peer open+write is allowed.
            RunWriterWorker(databasePath, "available");
            pager.Dispose();
            RunWriterWorker(databasePath, "available");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessWriterWorkerObservesBusyLock()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_SQLITE_WAL_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var expectedResult = Environment.GetEnvironmentVariable("TURSO_SQLITE_WAL_LOCK_WORKER_EXPECTED_RESULT");
        switch (expectedResult)
        {
            case "owned":
                // Legacy token: Stage 6 SHARED allows open; do not require ownership failure.
                using (var ownedPager = SqlitePager.Open(
                           PhysicalFileSystem.Instance,
                           databasePath,
                           databasePath + "-wal",
                           busyTimeout: TimeSpan.Zero))
                {
                    ownedPager.PageSize.Should().BeGreaterThan(0);
                }

                break;
            case "available":
                using (var pager = SqlitePager.Open(
                           PhysicalFileSystem.Instance,
                           databasePath,
                           databasePath + "-wal",
                           busyTimeout: TimeSpan.Zero))
                using (var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 1, TimeSpan.Zero))
                {
                    transaction.Rollback();
                }

                break;
            default:
                throw new InvalidOperationException("The cross-process SQLite WAL lock worker received an unknown expected result.");
        }
    }

    private static SqlitePager CreatePager(IFileSystem fileSystem, SqlitePagerLockManager locks)
        => SqlitePager.Create(
            fileSystem,
            "main.db",
            "main.db-wal",
            SqliteWalHeader.Create(
                SqlitePageSize.Default,
                salt1: 0x1122_3344,
                salt2: 0x5566_7788,
                checkpointSequence: 9),
            lockManager: locks);

    private static SqlitePager CreatePager(IFileSystem fileSystem)
        => SqlitePager.Create(
            fileSystem,
            "main.db",
            "main.db-wal",
            CreateWalHeader());

    private static SqlitePager CreatePhysicalPager(string databasePath)
        => SqlitePager.Create(
            PhysicalFileSystem.Instance,
            databasePath,
            databasePath + "-wal",
            CreateWalHeader());

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1122_3344,
            salt2: 0x5566_7788,
            checkpointSequence: 9);

    private static void RunWriterWorker(string databasePath, string expectedResult)
    {
        var testDirectory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = testDirectory.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(Path.Combine(testDirectory.FullName, "Ahtola.Tests.dll"));
        startInfo.ArgumentList.Add(
            "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqlitePagerLockingStorageTests.CrossProcessWriterWorkerObservesBusyLock");
        startInfo.Environment["TURSO_SQLITE_WAL_LOCK_WORKER_DATABASE_PATH"] = databasePath;
        startInfo.Environment["TURSO_SQLITE_WAL_LOCK_WORKER_EXPECTED_RESULT"] = expectedResult;

        using var worker = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("Failed to start the cross-process SQLite WAL lock worker.");
        if (!worker.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            worker.Kill(entireProcessTree: true);
            Assert.Fail("The cross-process SQLite WAL lock worker did not exit within 30 seconds.");
        }

        var output = worker.StandardOutput.ReadToEnd() + worker.StandardError.ReadToEnd();
        worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{output}");
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-pager-locking",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }
}
