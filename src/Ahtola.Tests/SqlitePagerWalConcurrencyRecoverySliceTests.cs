using System.Diagnostics;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class SqlitePagerWalConcurrencyRecoverySliceTests
{
    [Test]
    public void RecoveryTruncationFlushFailureIsSurfacedWithoutReportingRecoverySuccess()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var header = CreateWalHeader();
        using var wal = SqliteWalFile.Create(fileSystem, "main.db-wal", header);

        wal.AppendFrame(1, CreatePage(header.PageSize, 0xA1), databaseSizeInPages: 1);
        wal.Flush();
        wal.AppendFrame(1, CreatePage(header.PageSize, 0xA2));
        wal.Flush();
        var committedLength = wal.Length - wal.FrameSize;
        var flushCountBeforeRecovery = faults.GetOperationCount(FileSystemOperation.FlushToDisk);

        faults.FailNext(FileSystemOperation.FlushToDisk);

        Assert.Throws<IOException>(() => wal.RecoverToLastCommittedFrame());

        wal.Length.Should().Be(committedLength);
        wal.ScanRecovery().Should().Be(new SqliteWalRecoveryInfo(
            LastValidFrameNumber: 1,
            LastCommittedFrameNumber: 1,
            LastCommittedDatabaseSizeInPages: 1,
            LastCommittedByteLength: committedLength,
            StopReason: SqliteWalRecoveryStopReason.EndOfFile));
        faults.GetOperationCount(FileSystemOperation.FlushToDisk).Should().Be(flushCountBeforeRecovery + 1);
    }

    [Test]
    [NonParallelizable]
    public void PhysicalPagerRetriesCrossProcessWalWriterUntilOwnerReleases()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Physical SQLite WAL shared-memory locks are only enabled on Windows.");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            var waitingPath = Path.Combine(workDirectory, "worker-waiting");
            var resultPath = Path.Combine(workDirectory, "worker-result");
            var pager = SqlitePager.Create(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                CreateWalHeader());
            using (pager)
            using (var writer = pager.BeginTransaction(targetDatabaseSizeInPages: 1))
            using (var worker = StartRetryWorker(databasePath, waitingPath, resultPath))
            {
                try
                {
                    SpinWait.SpinUntil(() => File.Exists(waitingPath), TimeSpan.FromSeconds(10))
                        .Should()
                        .BeTrue("the worker must first observe the parent WAL writer lock as busy");

                    SpinWait.SpinUntil(() => File.Exists(resultPath), TimeSpan.FromSeconds(1))
                        .Should()
                        .BeFalse("the worker must remain in its non-zero busy-timeout retry loop");
                }
                finally
                {
                    writer.Dispose();
                    pager.Dispose();
                    if (!worker.HasExited
                        && !worker.WaitForExit(TimeSpan.FromSeconds(10)))
                    {
                        worker.Kill(entireProcessTree: true);
                        Assert.Fail("The cross-process SQLite WAL retry worker did not exit after the writer lock was released.");
                    }
                }

                var output = worker.StandardOutput.ReadToEnd() + worker.StandardError.ReadToEnd();
                worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{output}");
                File.ReadAllText(resultPath).Should().Be("acquired");
            }
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessWriterRetryWorkerWaitsForWriterLock()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_SQLITE_WAL_RETRY_WORKER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        var waitingPath = Environment.GetEnvironmentVariable("TURSO_SQLITE_WAL_RETRY_WORKER_WAITING_PATH")
            ?? throw new InvalidOperationException("The retry worker is missing its waiting signal path.");
        var resultPath = Environment.GetEnvironmentVariable("TURSO_SQLITE_WAL_RETRY_WORKER_RESULT_PATH")
            ?? throw new InvalidOperationException("The retry worker is missing its result signal path.");

        try
        {
            // Writable open takes WAL_WRITE_LOCK briefly for recovery. With the
            // parent holding the writer, zero-timeout open is immediately busy —
            // signal that, then retry open until the parent releases.
            var openBusy = Assert.Throws<SqlitePagerBusyException>(() => SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                busyTimeout: TimeSpan.Zero));
            openBusy!.Operation.Should().Be(SqlitePagerLockOperation.Writer);
            File.WriteAllText(waitingPath, "waiting");

            using var pager = SqlitePager.Open(
                PhysicalFileSystem.Instance,
                databasePath,
                databasePath + "-wal",
                busyTimeout: TimeSpan.FromSeconds(5));
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 1, TimeSpan.Zero);
            transaction.Rollback();
            File.WriteAllText(resultPath, "acquired");
        }
        catch (Exception exception)
        {
            File.WriteAllText(resultPath, exception.GetType().FullName ?? exception.GetType().Name);
            throw;
        }
    }

    private static SqliteWalHeader CreateWalHeader()
        => SqliteWalHeader.Create(
            SqlitePageSize.Default,
            salt1: 0x1122_3344,
            salt2: 0x5566_7788,
            checkpointSequence: 9);

    private static byte[] CreatePage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return page;
    }

    private static Process StartRetryWorker(string databasePath, string waitingPath, string resultPath)
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
            "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqlitePagerWalConcurrencyRecoverySliceTests.CrossProcessWriterRetryWorkerWaitsForWriterLock");
        startInfo.Environment["TURSO_SQLITE_WAL_RETRY_WORKER_DATABASE_PATH"] = databasePath;
        startInfo.Environment["TURSO_SQLITE_WAL_RETRY_WORKER_WAITING_PATH"] = waitingPath;
        startInfo.Environment["TURSO_SQLITE_WAL_RETRY_WORKER_RESULT_PATH"] = resultPath;

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to start the cross-process SQLite WAL retry worker.");
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-pager-wal-retry",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
