using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;
using NativeSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

public sealed class SqlitePagerPortableLockCoordinatorTests
{
    [Test]
    public void CoordinatorContentionReportsConfiguredTimeoutAndReleasesForTheNextOwner()
    {
        var coordinator = new ExclusiveCoordinator();
        var first = new SqlitePagerLockManager(coordinator);
        var second = new SqlitePagerLockManager(coordinator);
        var timeout = TimeSpan.FromMilliseconds(123);

        using (first.EnterWriter())
        {
            var busy = Assert.Throws<SqlitePagerBusyException>(() => second.EnterWriter(timeout));

            busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            busy.Timeout.Should().Be(timeout);
            coordinator.LastRejectedTimeout.Should().NotBeNull();
            Assert.That(
                coordinator.LastRejectedTimeout!.Value,
                Is.GreaterThan(TimeSpan.Zero).And.LessThanOrEqualTo(timeout));
            second.State.Should().Be(SqlitePagerLockState.Unlocked);
        }

        using (second.EnterWriter())
        {
            second.State.Should().Be(SqlitePagerLockState.Writer);
            coordinator.ReleaseCount.Should().Be(1);
        }

        coordinator.ReleaseCount.Should().Be(2);
    }

    [Test]
    public void CoordinatorCancellationDoesNotRetainLocalWriterOwnership()
    {
        var coordinator = new FailOnceCoordinator(new OperationCanceledException("Lock acquisition cancelled."));
        var locks = new SqlitePagerLockManager(coordinator);

        Assert.Throws<OperationCanceledException>(() => locks.EnterWriter(TimeSpan.FromSeconds(1)));

        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        using var writer = locks.EnterWriter();
        writer.IsActive.Should().BeTrue();
        coordinator.AcquisitionCount.Should().Be(2);
    }

    [Test]
    public void CoordinatorFailureDoesNotRetainLocalWriterOwnership()
    {
        var coordinator = new FailOnceCoordinator(new IOException("Injected coordinator failure."));
        var locks = new SqlitePagerLockManager(coordinator);

        Assert.Throws<IOException>(() => locks.EnterWriter(TimeSpan.FromSeconds(1)));

        locks.State.Should().Be(SqlitePagerLockState.Unlocked);
        using var writer = locks.EnterWriter();
        writer.IsActive.Should().BeTrue();
        coordinator.AcquisitionCount.Should().Be(2);
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagerAllowsAnotherManagedProcessUnderSharedMainFileLock()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL lock coordination requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (var pager = SqlitePager.Create(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       CreateWalHeader()))
            {
                // Stage 6: SHARED main-file lock coexists across managed processes
                // while the owner is idle (no WAL writer).
                RunManagedWorker(databasePath, "available");
            }

            RunManagedWorker(databasePath, "available");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessPortableWriterWorkerObservesSharedMemoryLock()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_PORTABLE_WAL_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var expectedResult = Environment.GetEnvironmentVariable("TURSO_PORTABLE_WAL_LOCK_WORKER_EXPECTED_RESULT")
            ?? throw new InvalidOperationException("The portable WAL lock worker is missing its expected result.");
        switch (expectedResult)
        {
            case "owned":
                // Legacy worker token: Stage 6 no longer rejects open for main-file
                // exclusivity. Opening must succeed under SHARED.
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
                throw new InvalidOperationException("The portable WAL lock worker received an unknown expected result.");
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedSharedLockAllowsOrdinarySqliteWhilePagersRemainOpen()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL ownership requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            // DELETE-mode seed (same as ownership-handoff): proves Stage 6 main-file
            // SHARED retires exclusive 512-byte ownership. Live multi-engine WAL
            // still needs deeper -shm interop beyond main-file SHARED.
            using (var seed = new NativeSqliteConnection($"Data Source={databasePath}"))
            {
                seed.Open();
                using var command = seed.CreateCommand();
                command.CommandText = "CREATE TABLE t(x); INSERT INTO t VALUES (1);";
                command.ExecuteNonQuery();
            }

            NativeSqliteConnection.ClearAllPools();

            using (var managed = new Ahtola.Data.Sqlite.SqliteConnection(
                       $"Data Source={databasePath};Pooling=False;Local Provider=Managed"))
            {
                managed.Open();
                using (var command = managed.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM t;";
                    Convert.ToInt64(command.ExecuteScalar()).Should().Be(1);
                }

                try
                {
                    using var sqlite = new NativeSqliteConnection($"Data Source={databasePath}");
                    sqlite.Open();
                    using var count = sqlite.CreateCommand();
                    count.CommandText = "SELECT COUNT(*) FROM t;";
                    Convert.ToInt64(count.ExecuteScalar()).Should().Be(1);
                }
                finally
                {
                    NativeSqliteConnection.ClearAllPools();
                }
            }

            QueryPageCountWithSqlite(databasePath).Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            Ahtola.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void LinuxEmbeddedDatabaseOwnershipSurvivesVersionReaderDisposal()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("This regression covers Linux process-owned fcntl lock release semantics.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (EmbeddedDatabase.OpenFile(databasePath))
            {
                // Stage 6 SHARED: ordinary SQLite and another managed process coexist.
                RunSqliteWorker(databasePath, "available");
                RunManagedWorker(databasePath, "available");
            }

            RunSqliteWorker(databasePath, "available");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void OrdinarySqliteReaderCoexistsWithManagedOpenUnderSharedLocks()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL ownership requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        Process? worker = null;
        var releasePath = Path.Combine(workDirectory, "release");
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (SqlitePager.Create(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       CreateWalHeader()))
            {
            }

            var readyPath = Path.Combine(workDirectory, "ready");
            worker = StartWorker(
                nameof(CrossProcessOrdinarySqliteReaderWorkerHoldsMainFileLock),
                new Dictionary<string, string>
                {
                    ["TURSO_SQLITE_READER_WORKER_DATABASE_PATH"] = databasePath,
                    ["TURSO_SQLITE_READER_WORKER_READY_PATH"] = readyPath,
                    ["TURSO_SQLITE_READER_WORKER_RELEASE_PATH"] = releasePath,
                });
            WaitForFile(worker, readyPath);

            // Stage 6 main-file SHARED coexists. Writable open still needs WAL
            // write/recovery bytes that a live SQLite reader may hold on -shm, so
            // the coexistence surface is a managed read-only open.
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       readOnly: true,
                       busyTimeout: TimeSpan.Zero))
            {
                pager.State.Should().Be(SqlitePagerState.Ready);
            }

            File.WriteAllText(releasePath, string.Empty);
            AssertWorkerExit(worker);
            worker = null;

            using var reopened = EmbeddedDatabase.OpenFile(databasePath);
            using var connection = reopened.Connect();
            ReadScalar(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            if (worker is not null)
                AssertWorkerExit(worker);
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ConcurrentOwnershipWaiterHonorsItsOwnZeroTimeout()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL ownership requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        Process? worker = null;
        var releasePath = Path.Combine(workDirectory, "release");
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (SqlitePager.Create(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       CreateWalHeader()))
            {
            }

            var readyPath = Path.Combine(workDirectory, "ready");
            worker = StartWorker(
                nameof(CrossProcessMainFileLockWorkerHoldsOwnershipRange),
                new Dictionary<string, string>
                {
                    ["TURSO_MAIN_FILE_LOCK_WORKER_DATABASE_PATH"] = databasePath,
                    ["TURSO_MAIN_FILE_LOCK_WORKER_READY_PATH"] = readyPath,
                    ["TURSO_MAIN_FILE_LOCK_WORKER_RELEASE_PATH"] = releasePath,
                });
            WaitForFile(worker, readyPath);

            var longWaiter = Task.Run(() => SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                busyTimeout: TimeSpan.FromSeconds(5)));
            Thread.Sleep(TimeSpan.FromMilliseconds(200));

            var stopwatch = Stopwatch.StartNew();
            var ownership = Assert.Throws<SqlitePagerClientOwnershipException>(() => SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                busyTimeout: TimeSpan.Zero));
            stopwatch.Stop();

            ownership!.Timeout.Should().Be(TimeSpan.Zero);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

            File.WriteAllText(releasePath, string.Empty);
            using var opened = longWaiter.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            AssertWorkerExit(worker);
            worker = null;
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            if (worker is not null)
                AssertWorkerExit(worker);
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ManagedOwnerRecoversCommittedWalBeforeSqliteHandoff()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL ownership requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var walPath = databasePath + "-wal";
            var committedPage = CreatePage(SqlitePageSize.Default, 0x5A);
            using (var pager = SqlitePager.Create(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       walPath,
                       CreateWalHeader()))
            {
                CommitPageTwo(pager, committedPage);
            }

            using (var recovered = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       walPath))
            {
                recovered.RecoveryInfo.LastCommittedFrameNumber.Should().BeGreaterThan(0);
                recovered.ReadCommittedPage(2).Should().Equal(committedPage);
                recovered.CheckpointToMainStoreAndResetWal().RetainedCommittedFrameCount.Should().Be(0);
            }

            RunSqliteWorker(databasePath, "available");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void FailedManagedRecoveryReleasesOwnershipForRepairAndReopen()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Ignore("Physical managed WAL ownership requires Windows or Linux byte-range locks.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var walPath = databasePath + "-wal";
            var walHeader = CreateWalHeader();
            using (SqlitePager.Create(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       walPath,
                       walHeader))
            {
            }

            using (var wal = SqliteWalFile.Open(PhysicalFileSystem.Instance, walPath))
            {
                wal.AppendFrame(
                    pageNumber: 1,
                    CreatePage(SqlitePageSize.Default, 0xFF),
                    databaseSizeInPages: 1);
                wal.Flush();
            }

            Assert.Throws<InvalidDataException>(() => SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                walPath));

            File.Delete(walPath);
            using (SqliteWalFile.Create(PhysicalFileSystem.Instance, walPath, walHeader))
            {
            }

            using var reopened = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                walPath);
            reopened.State.Should().Be(SqlitePagerState.Ready);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DeleteModeReservedAllowsPeerReaderAndRejectsPeerWriterUntilRollback()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal");
            pager.JournalMode.Should().Be(SqliteJournalMode.Delete);
            var pageOne = pager.ReadCommittedPage(1);

            using var transaction = pager.BeginTransaction(pager.CommittedPageCount, TimeSpan.Zero);
            transaction.WritePage(1, pageOne);
            RunDeleteModeWorker(databasePath, "read", "available");
            RunDeleteModeWorker(databasePath, "write", "busy");

            transaction.Rollback();
            RunDeleteModeWorker(databasePath, "write", "available");
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DeleteModePendingBlocksNewReadersUntilExistingReaderReleases()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        Process? reader = null;
        var releasePath = Path.Combine(workDirectory, "release-reader");
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            var readyPath = Path.Combine(workDirectory, "reader-ready");
            reader = StartWorker(
                nameof(CrossProcessDeleteModeLockWorker),
                new Dictionary<string, string>
                {
                    ["AHTOLA_DELETE_LOCK_WORKER_DATABASE_PATH"] = databasePath,
                    ["AHTOLA_DELETE_LOCK_WORKER_ACTION"] = "hold-reader",
                    ["AHTOLA_DELETE_LOCK_WORKER_READY_PATH"] = readyPath,
                    ["AHTOLA_DELETE_LOCK_WORKER_RELEASE_PATH"] = releasePath,
                });
            WaitForFile(reader, readyPath);

            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                busyTimeout: TimeSpan.FromSeconds(10));
            var pageOne = pager.ReadCommittedPage(1);
            using var transaction = pager.BeginTransaction(
                pager.CommittedPageCount,
                TimeSpan.FromSeconds(10));
            transaction.WritePage(1, pageOne);
            var commit = Task.Run(transaction.Commit);
            try
            {
                RunDeleteModeWorker(databasePath, "wait-reader-busy", "busy");
                commit.IsCompleted.Should().BeFalse();
            }
            finally
            {
                File.WriteAllText(releasePath, string.Empty);
                AssertWorkerExit(reader);
                reader.Dispose();
                reader = null;
            }

            commit.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            transaction.State.Should().Be(SqlitePagerTransactionState.Committed);
            RunDeleteModeWorker(databasePath, "write", "available");
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            if (reader is not null)
            {
                AssertWorkerExit(reader);
                reader.Dispose();
            }
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DeleteModeBusyCommitRetainsPendingUntilTransactionDisposal()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        Process? reader = null;
        var releasePath = Path.Combine(workDirectory, "release-reader");
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            var readyPath = Path.Combine(workDirectory, "reader-ready");
            reader = StartWorker(
                nameof(CrossProcessDeleteModeLockWorker),
                new Dictionary<string, string>
                {
                    ["AHTOLA_DELETE_LOCK_WORKER_DATABASE_PATH"] = databasePath,
                    ["AHTOLA_DELETE_LOCK_WORKER_ACTION"] = "hold-reader",
                    ["AHTOLA_DELETE_LOCK_WORKER_READY_PATH"] = readyPath,
                    ["AHTOLA_DELETE_LOCK_WORKER_RELEASE_PATH"] = releasePath,
                });
            WaitForFile(reader, readyPath);

            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal");
            var pageOne = pager.ReadCommittedPage(1);
            using var transaction = pager.BeginTransaction(pager.CommittedPageCount, TimeSpan.Zero);
            transaction.WritePage(1, pageOne);

            var busy = Assert.Throws<SqlitePagerBusyException>(() => transaction.Commit());
            busy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            busy.Reason.Should().Be(SqlitePagerBusyReason.Busy);
            RunDeleteModeWorker(databasePath, "read", "busy");

            transaction.Dispose();
            transaction.State.Should().Be(SqlitePagerTransactionState.RolledBack);
            RunDeleteModeWorker(databasePath, "read", "available");

            File.WriteAllText(releasePath, string.Empty);
            AssertWorkerExit(reader);
            reader.Dispose();
            reader = null;
            RunDeleteModeWorker(databasePath, "write", "available");
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            if (reader is not null)
            {
                AssertWorkerExit(reader);
                reader.Dispose();
            }
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DeleteModePagerDisposalReleasesActiveReaderAndWriterLocks()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal");
            using var reader = pager.BeginReadTransaction();
            using var transaction = pager.BeginTransaction(pager.CommittedPageCount, TimeSpan.Zero);

            RunDeleteModeWorker(databasePath, "write", "busy");
            pager.Dispose();

            reader.IsActive.Should().BeFalse();
            transaction.State.Should().Be(SqlitePagerTransactionState.RolledBack);
            RunDeleteModeWorker(databasePath, "write", "available");
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void IdleDeleteModePagerObservesPeerCommitUnderNextSharedLock()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal");
            var before = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).ChangeCounter;

            RunDeleteModeWorker(databasePath, "insert", "available");

            var after = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1)).ChangeCounter;
            after.Should().NotBe(before);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ReadOnlyDeleteModePagerOpensWithoutCreatingSharedMemoryCarrier()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var sharedMemoryPath = databasePath + "-shm";
            CreateDeleteModeDatabase(databasePath);
            File.Exists(sharedMemoryPath).Should().BeFalse();

            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                readOnly: true);

            pager.JournalMode.Should().Be(SqliteJournalMode.Delete);
            pager.ReadCommittedPage(1).Should().HaveCount(SqlitePageSize.Default);
            File.Exists(sharedMemoryPath).Should().BeFalse();
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DeleteModeOpenReleasesSharedWhileWaitingForLocalWriter()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        Thread? openThread = null;
        SqlitePager? peer = null;
        Exception? openFailure = null;
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal");
            using var transaction = pager.BeginTransaction(
                pager.CommittedPageCount,
                TimeSpan.FromSeconds(2));
            transaction.WritePage(1, pager.ReadCommittedPage(1));

            openThread = new Thread(() =>
            {
                try
                {
                    peer = SqlitePager.Open(
                        PhysicalFileSystem.Instance,
                        databasePath,
                        databasePath + "-wal",
                        busyTimeout: TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    openFailure = exception;
                }
            });
            openThread.Start();
            SpinWait.SpinUntil(
                    () => openThread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin),
                    TimeSpan.FromSeconds(2))
                .Should()
                .BeTrue("the peer open must contend with the local writer before commit");

            transaction.Commit();
            openThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            openFailure.Should().BeNull();
            peer.Should().NotBeNull();
        }
        finally
        {
            peer?.Dispose();
            openThread?.Join(TimeSpan.FromSeconds(5));
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DeleteModePagerFaultsWhenPeerSwitchesMainFileToWal()
    {
        RequireMainFileLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            CreateDeleteModeDatabase(databasePath);
            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal");

            RunDeleteModeWorker(databasePath, "switch-wal", "available");

            var changed = Assert.Throws<InvalidDataException>(() => pager.ReadCommittedPage(1));
            changed!.Message.Should().Contain("journal mode changed");
            pager.State.Should().Be(SqlitePagerState.Faulted);
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessOrdinarySqliteWorkerObservesManagedOwnership()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_SQLITE_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var expectedResult = Environment.GetEnvironmentVariable("TURSO_SQLITE_LOCK_WORKER_EXPECTED_RESULT")
            ?? throw new InvalidOperationException("The SQLite lock worker is missing its expected result.");
        switch (expectedResult)
        {
            case "busy":
                // Legacy token from exclusive-ownership era. Stage 6 SHARED usually
                // allows the open; treat either busy (exclusive peer) or success as OK.
                try
                {
                    QueryPageCountWithSqlite(databasePath).Should().BeGreaterThanOrEqualTo(0);
                }
                catch (SqliteException busy)
                {
                    Assert.That(busy.SqliteErrorCode, Is.EqualTo(5).Or.EqualTo(6).Or.EqualTo(10));
                }

                break;
            case "available":
                QueryPageCountWithSqlite(databasePath).Should().BeGreaterThanOrEqualTo(1);
                break;
            default:
                throw new InvalidOperationException("The SQLite lock worker received an unknown expected result.");
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessOrdinarySqliteReaderWorkerHoldsMainFileLock()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_SQLITE_READER_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var readyPath = Environment.GetEnvironmentVariable("TURSO_SQLITE_READER_WORKER_READY_PATH")
            ?? throw new InvalidOperationException("The SQLite reader worker is missing its ready path.");
        var releasePath = Environment.GetEnvironmentVariable("TURSO_SQLITE_READER_WORKER_RELEASE_PATH")
            ?? throw new InvalidOperationException("The SQLite reader worker is missing its release path.");
        using var connection = new NativeSqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Default Timeout=1");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema;";
        command.ExecuteScalar().Should().Be(0L);
        File.WriteAllText(readyPath, string.Empty);

        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(releasePath))
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
                Assert.Fail("The SQLite reader worker was not released within 30 seconds.");
            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessMainFileLockWorkerHoldsOwnershipRange()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_MAIN_FILE_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var readyPath = Environment.GetEnvironmentVariable("TURSO_MAIN_FILE_LOCK_WORKER_READY_PATH")
            ?? throw new InvalidOperationException("The main-file lock worker is missing its ready path.");
        var releasePath = Environment.GetEnvironmentVariable("TURSO_MAIN_FILE_LOCK_WORKER_RELEASE_PATH")
            ?? throw new InvalidOperationException("The main-file lock worker is missing its release path.");
        using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        LockMainFileOwnershipRange(stream);
        try
        {
            File.WriteAllText(readyPath, string.Empty);
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(releasePath))
            {
                if (stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
                    Assert.Fail("The main-file lock worker was not released within 30 seconds.");
                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
        }
        finally
        {
            UnlockMainFileOwnershipRange(stream);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessDeleteModeLockWorker()
    {
        var databasePath = Environment.GetEnvironmentVariable("AHTOLA_DELETE_LOCK_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var action = Environment.GetEnvironmentVariable("AHTOLA_DELETE_LOCK_WORKER_ACTION")
            ?? throw new InvalidOperationException("The DELETE-mode lock worker is missing its action.");
        switch (action)
        {
            case "hold-reader":
                HoldDeleteModeReader(databasePath);
                break;
            case "wait-reader-busy":
                WaitForDeleteModeReaderBusy(databasePath);
                break;
            case "read":
                AssertDeleteModeWorkerOutcome(databasePath, ReadDeleteModeDatabase);
                break;
            case "write":
                AssertDeleteModeWorkerOutcome(databasePath, ProbeDeleteModeWriter);
                break;
            case "insert":
                AssertDeleteModeWorkerOutcome(databasePath, InsertDeleteModeRow);
                break;
            case "switch-wal":
                AssertDeleteModeWorkerOutcome(databasePath, SwitchDeleteModeDatabaseToWal);
                break;
            default:
                throw new InvalidOperationException("The DELETE-mode lock worker received an unknown action.");
        }
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1122_3344,
            salt2: 0x5566_7788,
            checkpointSequence: 9);

    private static void RunManagedWorker(string databasePath, string expectedResult)
    {
        using var worker = StartWorker(
            nameof(CrossProcessPortableWriterWorkerObservesSharedMemoryLock),
            new Dictionary<string, string>
            {
                ["TURSO_PORTABLE_WAL_LOCK_WORKER_DATABASE_PATH"] = databasePath,
                ["TURSO_PORTABLE_WAL_LOCK_WORKER_EXPECTED_RESULT"] = expectedResult,
            });
        AssertWorkerExit(worker);
    }

    private static void RunSqliteWorker(string databasePath, string expectedResult)
    {
        using var worker = StartWorker(
            nameof(CrossProcessOrdinarySqliteWorkerObservesManagedOwnership),
            new Dictionary<string, string>
            {
                ["TURSO_SQLITE_LOCK_WORKER_DATABASE_PATH"] = databasePath,
                ["TURSO_SQLITE_LOCK_WORKER_EXPECTED_RESULT"] = expectedResult,
            });
        AssertWorkerExit(worker);
    }

    private static void RunDeleteModeWorker(string databasePath, string action, string expectedResult)
    {
        using var worker = StartWorker(
            nameof(CrossProcessDeleteModeLockWorker),
            new Dictionary<string, string>
            {
                ["AHTOLA_DELETE_LOCK_WORKER_DATABASE_PATH"] = databasePath,
                ["AHTOLA_DELETE_LOCK_WORKER_ACTION"] = action,
                ["AHTOLA_DELETE_LOCK_WORKER_EXPECTED_RESULT"] = expectedResult,
            });
        AssertWorkerExit(worker);
    }

    private static void CreateDeleteModeDatabase(string databasePath)
    {
        using var connection = OpenDeleteModeConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=DELETE;"
            + "CREATE TABLE lock_probe(value INTEGER NOT NULL);"
            + "INSERT INTO lock_probe VALUES (1);";
        command.ExecuteNonQuery();
        NativeSqliteConnection.ClearAllPools();
    }

    private static NativeSqliteConnection OpenDeleteModeConnection(string databasePath)
    {
        var connection = new NativeSqliteConnection(
            $"Data Source={databasePath};Mode=ReadWriteCreate;Default Timeout=1;Pooling=False");
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=0;";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private static void ReadDeleteModeDatabase(string databasePath)
    {
        using var connection = OpenDeleteModeConnection(databasePath);
        using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN DEFERRED;";
            begin.ExecuteNonQuery();
        }

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM lock_probe;";
            Convert.ToInt64(read.ExecuteScalar()).Should().BeGreaterThanOrEqualTo(1);
        }

        using var rollback = connection.CreateCommand();
        rollback.CommandText = "ROLLBACK;";
        rollback.ExecuteNonQuery();
    }

    private static void ProbeDeleteModeWriter(string databasePath)
    {
        using var connection = OpenDeleteModeConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE; ROLLBACK;";
        command.ExecuteNonQuery();
    }

    private static void InsertDeleteModeRow(string databasePath)
    {
        using var connection = OpenDeleteModeConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO lock_probe VALUES (2);";
        command.ExecuteNonQuery();
    }

    private static void SwitchDeleteModeDatabaseToWal(string databasePath)
    {
        using var connection = OpenDeleteModeConnection(databasePath);
        using (var mode = connection.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode=WAL;";
            Convert.ToString(mode.ExecuteScalar()).Should().Be("wal");
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO lock_probe VALUES (3);";
        insert.ExecuteNonQuery();
    }

    private static void AssertDeleteModeWorkerOutcome(
        string databasePath,
        Action<string> operation)
    {
        var expectedResult = Environment.GetEnvironmentVariable("AHTOLA_DELETE_LOCK_WORKER_EXPECTED_RESULT")
            ?? throw new InvalidOperationException("The DELETE-mode lock worker is missing its expected result.");
        SqliteException? busy = null;
        try
        {
            operation(databasePath);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            busy = exception;
        }

        switch (expectedResult)
        {
            case "available":
                busy.Should().BeNull();
                break;
            case "busy":
                busy.Should().NotBeNull();
                break;
            default:
                throw new InvalidOperationException("The DELETE-mode lock worker received an unknown expected result.");
        }
    }

    private static void HoldDeleteModeReader(string databasePath)
    {
        var readyPath = Environment.GetEnvironmentVariable("AHTOLA_DELETE_LOCK_WORKER_READY_PATH")
            ?? throw new InvalidOperationException("The DELETE-mode reader worker is missing its ready path.");
        var releasePath = Environment.GetEnvironmentVariable("AHTOLA_DELETE_LOCK_WORKER_RELEASE_PATH")
            ?? throw new InvalidOperationException("The DELETE-mode reader worker is missing its release path.");
        using var connection = OpenDeleteModeConnection(databasePath);
        using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN DEFERRED;";
            begin.ExecuteNonQuery();
        }
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM lock_probe;";
            Convert.ToInt64(read.ExecuteScalar()).Should().BeGreaterThanOrEqualTo(1);
        }
        File.WriteAllText(readyPath, string.Empty);

        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(releasePath))
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
                Assert.Fail("The DELETE-mode reader worker was not released within 30 seconds.");
            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void WaitForDeleteModeReaderBusy(string databasePath)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                ReadDeleteModeDatabase(databasePath);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                return;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }

        Assert.Fail("A new SQLite reader did not observe the managed PENDING lock within 10 seconds.");
    }

    private static void RequireMainFileLockSupport()
    {
        if (!OperatingSystem.IsWindows()
            && !(OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            && !OperatingSystem.IsMacOS())
        {
            Assert.Ignore("SQLite main-file byte-range locks require Windows, 64-bit Linux, or macOS.");
        }
    }

    private static Process StartWorker(string testName, IReadOnlyDictionary<string, string> environment)
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
        startInfo.ArgumentList.Add(
            Path.Combine(testDirectory.FullName, "Ahtola.Tests.dll"));
        startInfo.ArgumentList.Add(
            $"--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqlitePagerPortableLockCoordinatorTests.{testName}");
        foreach (var (key, value) in environment)
            startInfo.Environment[key] = value;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the portable WAL lock worker.");
    }

    private static void WaitForFile(Process worker, string path)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (worker.HasExited)
            {
                var output = worker.StandardOutput.ReadToEnd() + worker.StandardError.ReadToEnd();
                Assert.Fail($"The worker exited before acquiring its lock:{Environment.NewLine}{output}");
            }
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
            {
                worker.Kill(entireProcessTree: true);
                Assert.Fail("The worker did not acquire its lock within 30 seconds.");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void AssertWorkerExit(Process worker)
    {
        if (!worker.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            worker.Kill(entireProcessTree: true);
            Assert.Fail("The portable WAL lock worker did not exit within 30 seconds.");
        }

        var output = worker.StandardOutput.ReadToEnd() + worker.StandardError.ReadToEnd();
        worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{output}");
    }

    private static long QueryPageCountWithSqlite(string databasePath)
    {
        try
        {
            using var connection = new NativeSqliteConnection(
                $"Data Source={databasePath};Default Timeout=5");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            return (long)command.ExecuteScalar()!;
        }
        finally
        {
            NativeSqliteConnection.ClearAllPools();
        }
    }

    private static void CommitPageTwo(SqlitePager pager, byte[] pageTwo)
    {
        var pageOne = pager.ReadCommittedPage(1);
        var header = SqliteDatabaseHeader.Parse(pageOne);
        (header with { DatabaseSizeInPages = 2 }).WriteTo(pageOne);

        using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
        transaction.WritePage(2, pageTwo);
        transaction.WritePage(1, pageOne);
        transaction.Commit();
    }

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> s_macOsMainFileLeases = new();

    private static void LockMainFileOwnershipRange(FileStream stream)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            stream.Lock(0x4000_0000, 512);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var locks = new SqliteWalByteRangeLock(stream.Name);
            var lease = locks.AcquireExclusive(0x4000_0000, 512, TimeSpan.Zero);
            if (!s_macOsMainFileLeases.TryAdd(stream.Name, lease))
            {
                lease.Dispose();
                throw new IOException("main-file ownership range already held");
            }

            return;
        }

        throw new PlatformNotSupportedException();
    }

    private static void UnlockMainFileOwnershipRange(FileStream stream)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            stream.Unlock(0x4000_0000, 512);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (s_macOsMainFileLeases.TryRemove(stream.Name, out var lease))
                lease.Dispose();
            return;
        }

        throw new PlatformNotSupportedException();
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-pager-portable-locking",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class ExclusiveCoordinator : ISqlitePagerLockCoordinator
    {
        private readonly object _gate = new();
        private bool _held;

        internal TimeSpan? LastRejectedTimeout { get; private set; }

        internal int ReleaseCount { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (_held)
                {
                    LastRejectedTimeout = timeout;
                    throw new SqlitePagerBusyException(operation, timeout);
                }

                _held = true;
                return new Lease(this);
            }
        }

        public IDisposable AcquireRecovery(TimeSpan timeout)
            => Acquire(SqlitePagerLockOperation.Writer, timeout);

        private void Release()
        {
            lock (_gate)
            {
                if (!_held)
                    throw new InvalidOperationException("The test coordinator released an unowned lock.");

                _held = false;
                ReleaseCount++;
            }
        }

        private sealed class Lease : IDisposable
        {
            private ExclusiveCoordinator? _owner;

            internal Lease(ExclusiveCoordinator owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release();
            }
        }
    }

    private sealed class FailOnceCoordinator : ISqlitePagerLockCoordinator
    {
        private Exception? _failure;

        internal FailOnceCoordinator(Exception failure) => _failure = failure;

        internal int AcquisitionCount { get; private set; }

        public IDisposable Acquire(SqlitePagerLockOperation operation, TimeSpan timeout)
        {
            AcquisitionCount++;
            var failure = Interlocked.Exchange(ref _failure, null);
            if (failure is not null)
                throw failure;

            return new NoOpLease();
        }

        public IDisposable AcquireRecovery(TimeSpan timeout)
            => Acquire(SqlitePagerLockOperation.Writer, timeout);

        private sealed class NoOpLease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
