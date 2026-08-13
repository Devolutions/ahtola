using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqliteWalProcessIsolationHarnessTests
{
    private const long WriteLockOffset = SqliteWalIndexCheckpointInfo.LockOffset;
    private const long CheckpointLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 1;

    [Test]
    [NonParallelizable]
    public void ProcessIsolatedReaderAndCheckpointerRespectSnapshotAndReleaseItForHandoff()
    {
        RequireCoordinatorSupport();
        using var artifact = DetachedSqliteWalArtifact.Create();
        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
        {
            using var reader = StartWorker(artifact, WorkerOperation.HoldReadSnapshot);
            var snapshotFrame = uint.Parse(reader.ReadResult(), CultureInfo.InvariantCulture);
            var write = ReadCommittedWritePages(artifact.DatabasePath);
            coordinator.Commit([write.First], write.DatabasePageCount, TimeSpan.Zero);
            var passive = coordinator.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero);
            passive.IsBusy.Should().BeTrue();
            passive.SafeFrame.Should().Be(snapshotFrame);

            Assert.Throws<SqliteWalByteRangeLockBusyException>(
                () => coordinator.Checkpoint(SqliteWalCheckpointMode.Full, TimeSpan.Zero));

            reader.ReleaseAndWait();

            var full = coordinator.Checkpoint(SqliteWalCheckpointMode.Full, TimeSpan.Zero);
            full.IsBusy.Should().BeFalse();
            full.BackfilledFrameCount.Should().Be(full.MaximumFrame);
            coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();
        }

        AssertSqliteCanReopen(artifact.DatabasePath);
    }

    [Test]
    [NonParallelizable]
    public void ProcessIsolatedWriterContentionAndCancellationReleaseTheWriterLease()
    {
        RequireCoordinatorSupport();
        using var artifact = DetachedSqliteWalArtifact.Create();
        var write = ReadCommittedWritePages(artifact.DatabasePath);
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath);
        using var writerBlocker = StartWorker(
            artifact,
            WorkerOperation.HoldByteRangeLock,
            lockOffset: WriteLockOffset);

        Assert.Throws<SqliteWalByteRangeLockBusyException>(
            () => coordinator.Commit(
                [write.First],
                write.DatabasePageCount,
                TimeSpan.Zero));
        Assert.Throws<SqliteWalByteRangeLockBusyException>(
            () => coordinator.Checkpoint(SqliteWalCheckpointMode.Full, TimeSpan.Zero));

        using var cancellation = new CancellationTokenSource();
        var waitingCommit = Task.Run(
            () => coordinator.Commit(
                [write.First],
                write.DatabasePageCount,
                Timeout.InfiniteTimeSpan,
                cancellation.Token));
        SpinWait.SpinUntil(
                () => waitingCommit.Status == TaskStatus.Running || waitingCommit.IsCompleted,
                TimeSpan.FromSeconds(5))
            .Should()
            .BeTrue("the canceled commit must begin while the separate process owns the writer byte");
        waitingCommit.IsCompleted.Should().BeFalse();

        cancellation.Cancel();
        SpinWait.SpinUntil(() => waitingCommit.IsCompleted, TimeSpan.FromSeconds(5))
            .Should()
            .BeTrue("cancellation must interrupt a detached writer wait");
        Assert.Throws<OperationCanceledException>(() => waitingCommit.GetAwaiter().GetResult());

        writerBlocker.ReleaseAndWait();
        using (var writerProbe = StartWorker(
                   artifact,
                   WorkerOperation.HoldByteRangeLock,
                   lockOffset: WriteLockOffset,
                   holdLease: false))
        {
            writerProbe.WaitForExit();
            writerProbe.ReadResult().Should().Be("acquired");
        }

        coordinator.Commit([write.First], write.DatabasePageCount, TimeSpan.Zero)
            .MaximumFrame
            .Should()
            .BeGreaterThan(0);
    }

    [Test]
    [NonParallelizable]
    public void AbruptWriterTerminationBeforeIndexPublicationRepairsOrFailsClosed()
    {
        RequireCoordinatorSupport();
        using var artifact = DetachedSqliteWalArtifact.Create();
        var before = ReadCommittedWritePages(artifact.DatabasePath);

        using (var writer = StartWorker(artifact, WorkerOperation.CrashAfterFirstFrameAppend))
            writer.Abort();

        using (var tail = OpenWalCopy(artifact.DatabasePath))
        {
            var recovery = tail.ScanRecovery();
            recovery.LastValidFrameNumber.Should().Be(before.LastCommittedFrameNumber + 1);
            recovery.LastCommittedFrameNumber.Should().Be(before.LastCommittedFrameNumber);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidDataException>(
                () => SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath));
            return;
        }

        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
        {
            coordinator.Recover(TimeSpan.Zero).LastCommittedFrameNumber
                .Should()
                .Be(before.LastCommittedFrameNumber);
            coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();
        }

        AssertSqliteCanReopen(artifact.DatabasePath);
    }

    [TestCase(CheckpointWindow.AfterAttemptPublication)]
    [TestCase(CheckpointWindow.AfterMainStoreFlush)]
    [NonParallelizable]
    public void AbruptCheckpointInterruptionRebuildsTransientProgressBeforeHandoff(
        CheckpointWindow window)
    {
        RequireCoordinatorSupport();
        using var artifact = DetachedSqliteWalArtifact.Create();

        using (var checkpoint = StartWorker(
                   artifact,
                   WorkerOperation.PauseCheckpoint,
                   checkpointWindow: window))
        {
            checkpoint.Abort();
        }

        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
        using (var wal = OpenWalCopy(artifact.DatabasePath))
        using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                   artifact.DatabasePath + "-shm",
                   FileOpenMode.OpenExisting,
                   readOnly: true))
        {
            var rebuilt = new SqliteWalIndexSharedMemory(mapping).ReadValidatedHeader(wal);
            rebuilt.CheckpointInfo.BackfilledFrameCount.Should().Be(0);
            rebuilt.CheckpointInfo.BackfillAttemptedFrameCount.Should().Be(0);

            var retry = coordinator.Checkpoint(SqliteWalCheckpointMode.Passive, TimeSpan.Zero);
            retry.BackfilledFrameCount.Should().Be(retry.MaximumFrame);
            coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();
        }

        AssertSqliteCanReopen(artifact.DatabasePath);
    }

    [TestCase(TailMutation.TornIndex)]
    [TestCase(TailMutation.TornFrame)]
    [TestCase(TailMutation.CorruptFrame)]
    [TestCase(TailMutation.UncommittedFrame)]
    [NonParallelizable]
    public void FreshProcessClassifiesTornCorruptAndUncommittedArtifacts(TailMutation mutation)
    {
        RequireCoordinatorSupport();
        using var artifact = DetachedSqliteWalArtifact.Create();
        var before = ReadCommittedWritePages(artifact.DatabasePath);

        switch (mutation)
        {
            case TailMutation.TornIndex:
                TearSecondIndexHeader(artifact.DatabasePath);
                break;
            case TailMutation.TornFrame:
                AppendTornFrame(artifact.DatabasePath, before.LastCommittedFrameNumber);
                break;
            case TailMutation.CorruptFrame:
                CorruptFirstFrameChecksum(artifact.DatabasePath);
                break;
            case TailMutation.UncommittedFrame:
                AppendValidUncommittedFrame(
                    artifact.DatabasePath,
                    before.LastCommittedFrameNumber);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown WAL tail mutation.");
        }

        using var worker = StartWorker(artifact, WorkerOperation.ReopenAndReport);
        worker.WaitForExit();
        var result = worker.ReadResult();

        if (mutation == TailMutation.TornIndex)
        {
            result.Should().Be("success");
            using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
                coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();
            AssertSqliteCanReopen(artifact.DatabasePath);
            return;
        }

        if (mutation == TailMutation.CorruptFrame)
        {
            result.Should().Be(nameof(InvalidDataException));
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            result.Should().Be(nameof(InvalidDataException));
            return;
        }

        result.Should().Be("success");
        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
            coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();
        AssertSqliteCanReopen(artifact.DatabasePath);
    }

    [Test]
    [NonParallelizable]
    public void ProcessIsolatedCarrierReplacementRepairsOnWindowsAndFailsClosedOnLinux()
    {
        RequireCoordinatorSupport();
        using var artifact = DetachedSqliteWalArtifact.Create();
        var before = ReadCommittedWritePages(artifact.DatabasePath);
        var sharedMemoryPath = artifact.DatabasePath + "-shm";
        var replacementPath = sharedMemoryPath + ".replacement";

        using var recovery = StartWorker(artifact, WorkerOperation.RecoverAfterCarrierReplacement);
        AppendValidUncommittedFrame(
            artifact.DatabasePath,
            before.LastCommittedFrameNumber);
        recovery.SignalGo();
        recovery.WaitForPause();

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Move(sharedMemoryPath, replacementPath));
        }
        else
        {
            File.Move(sharedMemoryPath, replacementPath);
            File.Copy(replacementPath, sharedMemoryPath);
        }

        recovery.ReleaseAndWait();
        if (!OperatingSystem.IsWindows())
        {
            recovery.ReadResult().Should().Be(nameof(InvalidDataException));
            using var tail = OpenWalCopy(artifact.DatabasePath);
            tail.ScanRecovery().LastValidFrameNumber.Should().Be(before.LastCommittedFrameNumber + 1);
            return;
        }

        recovery.ReadResult().Should().Be("recovered");
        using (var coordinator = SqliteWalWriterCheckpointCoordinator.Open(artifact.DatabasePath))
            coordinator.Checkpoint(SqliteWalCheckpointMode.Truncate, TimeSpan.Zero).ResetWal.Should().BeTrue();
        AssertSqliteCanReopen(artifact.DatabasePath);
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void ProcessWorker()
    {
        var operation = Environment.GetEnvironmentVariable("TURSO_WAL_PROCESS_HARNESS_OPERATION");
        if (string.IsNullOrEmpty(operation))
            return;

        var context = WorkerContext.Read();
        try
        {
            switch (operation)
            {
                case WorkerOperation.HoldReadSnapshot:
                    HoldReadSnapshot(context);
                    break;
                case WorkerOperation.HoldByteRangeLock:
                    HoldByteRangeLock(context);
                    break;
                case WorkerOperation.CrashAfterFirstFrameAppend:
                    CrashAfterFirstFrameAppend(context);
                    break;
                case WorkerOperation.PauseCheckpoint:
                    PauseCheckpoint(context);
                    break;
                case WorkerOperation.ReopenAndReport:
                    ReopenAndReport(context);
                    break;
                case WorkerOperation.RecoverAfterCarrierReplacement:
                    RecoverAfterCarrierReplacement(context);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown WAL process harness operation '{operation}'.");
            }
        }
        catch (Exception exception)
        {
            if (!File.Exists(context.ResultPath))
                File.WriteAllText(context.ResultPath, $"fatal:{exception.GetType().Name}");
            throw;
        }
    }

    private static void HoldReadSnapshot(WorkerContext context)
    {
        using var coordinator = SqliteWalReadSnapshotCoordinator.Open(context.DatabasePath);
        using var snapshot = coordinator.BeginRead(TimeSpan.Zero);
        File.WriteAllText(context.ResultPath, snapshot.MaximumFrame.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(context.ReadyPath, string.Empty);
        WaitForFile(context.ReleasePath, WorkerTimeout, "The read-snapshot worker was not released.");
    }

    private static void HoldByteRangeLock(WorkerContext context)
    {
        if (context.LockOffset is null)
            throw new InvalidOperationException("The byte-range lock worker is missing its requested lock offset.");

        var locks = new SqliteWalByteRangeLock(context.DatabasePath + "-shm");
        try
        {
            using var lease = locks.AcquireExclusive(context.LockOffset.Value, length: 1, TimeSpan.Zero);
            File.WriteAllText(context.ResultPath, "acquired");
            File.WriteAllText(context.ReadyPath, string.Empty);
            if (context.HoldLease)
                WaitForFile(context.ReleasePath, WorkerTimeout, "The byte-range lock worker was not released.");
        }
        catch (SqliteWalByteRangeLockBusyException)
        {
            File.WriteAllText(context.ResultPath, "busy");
            File.WriteAllText(context.ReadyPath, string.Empty);
        }
    }

    private static void CrashAfterFirstFrameAppend(WorkerContext context)
    {
        var write = ReadCommittedWritePages(context.DatabasePath);
        SqliteWalWriterCheckpointCoordinator.AfterDetachedWalFrameAppendForTesting = () =>
        {
            File.WriteAllText(context.ReadyPath, string.Empty);
            WaitForFile(context.ReleasePath, WorkerTimeout, "The crash writer worker was not released.");
        };

        try
        {
            using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(context.DatabasePath);
            coordinator.Commit(
                [write.First, write.Second],
                write.DatabasePageCount,
                Timeout.InfiniteTimeSpan);
            File.WriteAllText(context.ResultPath, "completed");
        }
        finally
        {
            SqliteWalWriterCheckpointCoordinator.AfterDetachedWalFrameAppendForTesting = null;
        }
    }

    private static void PauseCheckpoint(WorkerContext context)
    {
        if (context.CheckpointWindow is null)
            throw new InvalidOperationException("The checkpoint worker is missing its interruption window.");

        Action pause = () =>
        {
            File.WriteAllText(context.ReadyPath, string.Empty);
            WaitForFile(context.ReleasePath, WorkerTimeout, "The checkpoint worker was not released.");
        };

        if (context.CheckpointWindow == CheckpointWindow.AfterAttemptPublication)
            SqliteWalWriterCheckpointCoordinator.AfterDetachedBackfillAttemptPublicationForTesting = pause;
        else
            SqliteWalWriterCheckpointCoordinator.AfterDetachedMainStoreBackfillForTesting = pause;

        try
        {
            using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(context.DatabasePath);
            coordinator.Checkpoint(SqliteWalCheckpointMode.Passive, Timeout.InfiniteTimeSpan);
            File.WriteAllText(context.ResultPath, "completed");
        }
        finally
        {
            SqliteWalWriterCheckpointCoordinator.AfterDetachedBackfillAttemptPublicationForTesting = null;
            SqliteWalWriterCheckpointCoordinator.AfterDetachedMainStoreBackfillForTesting = null;
        }
    }

    private static void ReopenAndReport(WorkerContext context)
    {
        try
        {
            using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(context.DatabasePath);
            File.WriteAllText(context.ResultPath, "success");
        }
        catch (Exception exception)
        {
            File.WriteAllText(context.ResultPath, exception.GetType().Name);
        }
        finally
        {
            File.WriteAllText(context.ReadyPath, string.Empty);
        }
    }

    private static void RecoverAfterCarrierReplacement(WorkerContext context)
    {
        using var coordinator = SqliteWalWriterCheckpointCoordinator.Open(context.DatabasePath);
        SqliteWalWriterCheckpointCoordinator.BeforeDetachedTailRepairForTesting = () =>
        {
            File.WriteAllText(context.PausePath, string.Empty);
            WaitForFile(context.ReleasePath, WorkerTimeout, "The carrier-recovery worker was not released.");
        };

        try
        {
            File.WriteAllText(context.ReadyPath, string.Empty);
            WaitForFile(context.GoPath, WorkerTimeout, "The carrier-recovery worker was not started.");
            coordinator.Recover(Timeout.InfiniteTimeSpan);
            File.WriteAllText(context.ResultPath, "recovered");
        }
        catch (Exception exception)
        {
            File.WriteAllText(context.ResultPath, exception.GetType().Name);
        }
        finally
        {
            SqliteWalWriterCheckpointCoordinator.BeforeDetachedTailRepairForTesting = null;
        }
    }

    private static WorkerProcess StartWorker(
        DetachedSqliteWalArtifact artifact,
        string operation,
        long? lockOffset = null,
        bool holdLease = true,
        CheckpointWindow? checkpointWindow = null)
        => new(artifact.WorkDirectory, artifact.DatabasePath, operation, lockOffset, holdLease, checkpointWindow);

    private static CommittedWritePages ReadCommittedWritePages(string databasePath)
    {
        using var wal = OpenWalCopy(databasePath);
        var recovery = wal.ScanRecovery();
        var maximumFrame = checked((uint)recovery.LastCommittedFrameNumber);
        var first = wal.ReadFrame(maximumFrame);
        SqliteWalFrame? second = null;
        for (var frameNumber = 1U; frameNumber <= maximumFrame; frameNumber++)
        {
            var candidate = wal.ReadFrame(frameNumber);
            if (candidate.Header.PageNumber != first.Header.PageNumber)
            {
                second = candidate;
                break;
            }
        }

        second.Should().NotBeNull("the SQLite-generated artifact must contain two distinct database pages");
        return new CommittedWritePages(
            new SqliteWalWritePage(first.Header.PageNumber, first.PageData.ToArray()),
            new SqliteWalWritePage(second!.Header.PageNumber, second.PageData.ToArray()),
            checked((uint)recovery.LastCommittedDatabaseSizeInPages),
            maximumFrame);
    }

    private static SqliteWalFile OpenWalCopy(string databasePath)
    {
        var fileSystem = new InMemoryFileSystem();
        using (var copy = fileSystem.OpenFile("main.db-wal", FileOpenMode.CreateNew))
            copy.Write(position: 0, ReadAllBytes(databasePath + "-wal"));
        return SqliteWalFile.Open(fileSystem, "main.db-wal", readOnly: true);
    }

    private static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException($"SQLite test artifact '{path}' is too large to snapshot.");

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void AppendValidUncommittedFrame(
        string databasePath,
        uint lastCommittedFrameNumber)
    {
        var frame = CreateUncommittedFrame(databasePath, lastCommittedFrameNumber);
        AppendWalBytes(databasePath, frame);
    }

    private static void AppendTornFrame(string databasePath, uint lastCommittedFrameNumber)
    {
        var frame = CreateUncommittedFrame(databasePath, lastCommittedFrameNumber);
        AppendWalBytes(databasePath, frame[..^1]);
    }

    private static byte[] CreateUncommittedFrame(string databasePath, uint lastCommittedFrameNumber)
    {
        using var sourceWal = OpenWalCopy(databasePath);
        var committedFrame = sourceWal.ReadFrame(lastCommittedFrameNumber);
        var page = committedFrame.PageData.ToArray();
        page[^1] ^= 0x7B;
        var frame = new byte[SqliteWalFrameHeader.Size + sourceWal.PageSize];
        var frameHeader = new SqliteWalFrameHeader(
            committedFrame.Header.PageNumber,
            0,
            sourceWal.Header.Salt1,
            sourceWal.Header.Salt2,
            0,
            0);
        frameHeader.WriteTo(frame.AsSpan(0, SqliteWalFrameHeader.Size));
        page.CopyTo(frame, SqliteWalFrameHeader.Size);
        var initialChecksum = SqliteWalChecksum.Calculate(
            frame.AsSpan(0, 8),
            sourceWal.Header.ChecksumByteOrder,
            committedFrame.Header.Checksum1,
            committedFrame.Header.Checksum2);
        var checksum = SqliteWalChecksum.Calculate(
            frame.AsSpan(SqliteWalFrameHeader.Size),
            sourceWal.Header.ChecksumByteOrder,
            initialChecksum.First,
            initialChecksum.Second);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, sizeof(uint)), checksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, sizeof(uint)), checksum.Second);
        return frame;
    }

    private static void AppendWalBytes(string databasePath, byte[] frame)
    {
        using var stream = new FileStream(
            databasePath + "-wal",
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = stream.Length;
        stream.Write(frame);
        stream.Flush(flushToDisk: true);
    }

    private static void TearSecondIndexHeader(string databasePath)
    {
        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            databasePath + "-shm",
            FileOpenMode.OpenExisting);
        var header = new byte[SqliteWalIndexHeader.Size];
        mapping.Read(SqliteWalIndexHeader.Size, header);
        header[40] ^= 0x01;
        mapping.Write(SqliteWalIndexHeader.Size, header);
        mapping.MemoryBarrier();
    }

    private static void CorruptFirstFrameChecksum(string databasePath)
    {
        using var stream = new FileStream(
            databasePath + "-wal",
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = SqliteWalHeader.Size + 16;
        var checksum = stream.ReadByte();
        checksum.Should().NotBe(-1);
        stream.Position = SqliteWalHeader.Size + 16;
        stream.WriteByte(unchecked((byte)(checksum ^ 0x01)));
        stream.Flush(flushToDisk: true);
    }

    private static void AssertSqliteCanReopen(string databasePath)
    {
            // After abrupt worker kills and managed SHM unmap, Windows can briefly surface
            // SQLITE_BUSY/LOCKED/IOERR while the kernel releases byte-range locks and
            // section objects. Retry only those transient codes; real corruption fails closed.
            const int sqliteBusy = 5;
            const int sqliteLocked = 6;
            const int sqliteIoErr = 10;
            const int maxAttempts = 8;
            Exception? lastFailure = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                SqliteConnection.ClearAllPools();
                // Drop any leftover finalizers holding -shm/-wal handles from the prior attempt.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                try
                {
                    using var connection = new SqliteConnection(
                        $"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
                    connection.Open();

                    ExecuteScalar(connection, "PRAGMA integrity_check;").Should().Be("ok");
                    ExecuteScalar(connection, "SELECT count(*) FROM data;").Should().Be("3");
                    return;
                }
                catch (SqliteException exception)
                    when (exception.SqliteErrorCode is sqliteBusy or sqliteLocked or sqliteIoErr
                          && attempt < maxAttempts)
                {
                    lastFailure = exception;
                    Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt * attempt));
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                }
            }

            throw new IOException(
                $"Stock SQLite could not reopen the recovered database after {maxAttempts} attempts.",
                lastFailure);
        }

    private static string ExecuteScalar(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException($"SQLite command '{commandText}' returned null.");
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void WaitForFile(string path, TimeSpan timeout, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= timeout)
                Assert.Fail(failureMessage);

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private static bool SupportsCoordinator
        => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.Is64BitProcess) || OperatingSystem.IsMacOS();

    private static void RequireCoordinatorSupport()
    {
        if (!SupportsCoordinator)
        {
            Assert.Ignore(
                "Detached SQLite WAL writer/checkpoint coordination is supported only on Windows, 64-bit Linux, and macOS.");
        }
    }

    private sealed record CommittedWritePages(
        SqliteWalWritePage First,
        SqliteWalWritePage Second,
        uint DatabasePageCount,
        uint LastCommittedFrameNumber);

    public enum CheckpointWindow
    {
        AfterAttemptPublication,
        AfterMainStoreFlush,
    }

    public enum TailMutation
    {
        TornIndex,
        TornFrame,
        CorruptFrame,
        UncommittedFrame,
    }

    private static class WorkerOperation
    {
        internal const string HoldReadSnapshot = "hold-read-snapshot";
        internal const string HoldByteRangeLock = "hold-byte-range-lock";
        internal const string CrashAfterFirstFrameAppend = "crash-after-first-frame-append";
        internal const string PauseCheckpoint = "pause-checkpoint";
        internal const string ReopenAndReport = "reopen-and-report";
        internal const string RecoverAfterCarrierReplacement = "recover-after-carrier-replacement";
    }

    private sealed class DetachedSqliteWalArtifact : IDisposable
    {
        private DetachedSqliteWalArtifact(string workDirectory, string databasePath)
        {
            WorkDirectory = workDirectory;
            DatabasePath = databasePath;
        }

        internal string WorkDirectory { get; }

        internal string DatabasePath { get; }

        internal static DetachedSqliteWalArtifact Create()
        {
            var workDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "sqlite-wal-process-isolation",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var sourcePath = Path.Combine(workDirectory, "sqlite-source.db");
            var databasePath = Path.Combine(workDirectory, "detached.db");
            try
            {
                using (var connection = new SqliteConnection(
                           $"Data Source={sourcePath};Mode=ReadWriteCreate;Pooling=False"))
                {
                    connection.Open();
                    Execute(connection, "PRAGMA page_size=512;");
                    Execute(connection, "VACUUM;");
                    Execute(connection, "PRAGMA journal_mode=WAL;");
                    Execute(connection, "PRAGMA wal_autocheckpoint=0;");
                    Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
                    Execute(connection, "INSERT INTO data(value) VALUES ('one'), ('two'), ('three');");
                    Execute(connection, "UPDATE data SET value = 'two-updated' WHERE id = 2;");
                    Execute(connection, "CREATE INDEX data_value ON data(value);");

                    File.Copy(sourcePath, databasePath);
                    File.Copy(sourcePath + "-wal", databasePath + "-wal");
                    File.Copy(sourcePath + "-shm", databasePath + "-shm");
                }

                SqliteConnection.ClearAllPools();
                return new DetachedSqliteWalArtifact(workDirectory, databasePath);
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(WorkDirectory))
                Directory.Delete(WorkDirectory, recursive: true);
        }
    }

    private sealed class WorkerContext
    {
        private WorkerContext(
            string databasePath,
            string readyPath,
            string releasePath,
            string goPath,
            string pausePath,
            string resultPath,
            long? lockOffset,
            bool holdLease,
            CheckpointWindow? checkpointWindow)
        {
            DatabasePath = databasePath;
            ReadyPath = readyPath;
            ReleasePath = releasePath;
            GoPath = goPath;
            PausePath = pausePath;
            ResultPath = resultPath;
            LockOffset = lockOffset;
            HoldLease = holdLease;
            CheckpointWindow = checkpointWindow;
        }

        internal string DatabasePath { get; }

        internal string ReadyPath { get; }

        internal string ReleasePath { get; }

        internal string GoPath { get; }

        internal string PausePath { get; }

        internal string ResultPath { get; }

        internal long? LockOffset { get; }

        internal bool HoldLease { get; }

        internal CheckpointWindow? CheckpointWindow { get; }

        internal static WorkerContext Read()
        {
            var lockOffsetText = Environment.GetEnvironmentVariable("TURSO_WAL_PROCESS_HARNESS_LOCK_OFFSET");
            var checkpointWindowText = Environment.GetEnvironmentVariable("TURSO_WAL_PROCESS_HARNESS_CHECKPOINT_WINDOW");
            return new WorkerContext(
                ReadValue("TURSO_WAL_PROCESS_HARNESS_DATABASE_PATH"),
                ReadValue("TURSO_WAL_PROCESS_HARNESS_READY_PATH"),
                ReadValue("TURSO_WAL_PROCESS_HARNESS_RELEASE_PATH"),
                ReadValue("TURSO_WAL_PROCESS_HARNESS_GO_PATH"),
                ReadValue("TURSO_WAL_PROCESS_HARNESS_PAUSE_PATH"),
                ReadValue("TURSO_WAL_PROCESS_HARNESS_RESULT_PATH"),
                string.IsNullOrEmpty(lockOffsetText)
                    ? null
                    : long.Parse(lockOffsetText, CultureInfo.InvariantCulture),
                bool.TryParse(
                    Environment.GetEnvironmentVariable("TURSO_WAL_PROCESS_HARNESS_HOLD_LEASE"),
                    out var holdLease)
                && holdLease,
                string.IsNullOrEmpty(checkpointWindowText)
                    ? null
                    : Enum.Parse<CheckpointWindow>(checkpointWindowText, ignoreCase: false));
        }

        private static string ReadValue(string name)
            => Environment.GetEnvironmentVariable(name)
               ?? throw new InvalidOperationException($"The WAL process harness worker is missing '{name}'.");
    }

    private sealed class WorkerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly string _readyPath;
        private readonly string _releasePath;
        private readonly string _goPath;
        private readonly string _pausePath;
        private readonly string _resultPath;
        private readonly StringBuilder _output = new();
        private bool _completed;
        private bool _released;

        internal WorkerProcess(
            string workDirectory,
            string databasePath,
            string operation,
            long? lockOffset,
            bool holdLease,
            CheckpointWindow? checkpointWindow)
        {
            var token = Guid.NewGuid().ToString("N");
            _readyPath = Path.Combine(workDirectory, $"wal-process-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"wal-process-release-{token}");
            _goPath = Path.Combine(workDirectory, $"wal-process-go-{token}");
            _pausePath = Path.Combine(workDirectory, $"wal-process-pause-{token}");
            _resultPath = Path.Combine(workDirectory, $"wal-process-result-{token}");

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
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalProcessIsolationHarnessTests."
                + nameof(ProcessWorker));
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_OPERATION"] = operation;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_DATABASE_PATH"] = databasePath;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_READY_PATH"] = _readyPath;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_RELEASE_PATH"] = _releasePath;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_GO_PATH"] = _goPath;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_PAUSE_PATH"] = _pausePath;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_RESULT_PATH"] = _resultPath;
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_LOCK_OFFSET"] =
                lockOffset?.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_HOLD_LEASE"] =
                holdLease.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_PROCESS_HARNESS_CHECKPOINT_WINDOW"] =
                checkpointWindow?.ToString();

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the WAL process harness worker.");
            _process.OutputDataReceived += AppendOutput;
            _process.ErrorDataReceived += AppendOutput;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            WaitForSignal(_readyPath, "The WAL process harness worker did not report readiness.");
        }

        internal string ReadResult()
        {
            WaitForSignal(_resultPath, "The WAL process harness worker did not report a result.");
            return File.ReadAllText(_resultPath);
        }

        internal void SignalGo()
            => File.WriteAllText(_goPath, string.Empty);

        internal void WaitForPause()
            => WaitForSignal(_pausePath, "The WAL process harness worker did not reach its pause point.");

        internal void ReleaseAndWait()
        {
            if (!_released)
            {
                File.WriteAllText(_releasePath, string.Empty);
                _released = true;
            }

            WaitForExit();
        }

        internal void Abort()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            if (!_process.WaitForExit(WorkerTimeout))
                Assert.Fail($"The WAL process harness worker did not terminate:{Environment.NewLine}{DrainOutput()}");
                    // Drain async stdout/stderr callbacks after the kill so Dispose is quiet.
                    _process.WaitForExit();
                    _completed = true;
                    // Windows can retain byte-range locks and section objects briefly after the
                    // process image exits; give the kernel a moment before the parent reopens.
                    if (OperatingSystem.IsWindows())
                        Thread.Sleep(TimeSpan.FromMilliseconds(50));
                }

        internal void WaitForExit()
        {
            if (_completed)
                return;

            if (!_process.WaitForExit(WorkerTimeout))
            {
                _process.Kill(entireProcessTree: true);
                Assert.Fail($"The WAL process harness worker did not exit:{Environment.NewLine}{DrainOutput()}");
            }

            _process.WaitForExit();
            _process.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{DrainOutput()}");
            _completed = true;
        }

        public void Dispose()
        {
            try
            {
                if (!_completed)
                    ReleaseAndWait();
            }
            finally
            {
                _process.Dispose();
            }
        }

        private void WaitForSignal(string path, string failureMessage)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(path))
            {
                if (_process.HasExited)
                {
                    _process.WaitForExit();
                    Assert.Fail($"{failureMessage}{Environment.NewLine}{DrainOutput()}");
                }
                if (stopwatch.Elapsed >= WorkerTimeout)
                {
                    _process.Kill(entireProcessTree: true);
                    Assert.Fail($"{failureMessage}{Environment.NewLine}{DrainOutput()}");
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;

            lock (_output)
                _output.AppendLine(args.Data);
        }

        private string DrainOutput()
        {
            lock (_output)
                return _output.ToString();
        }
    }

    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(60);
}
