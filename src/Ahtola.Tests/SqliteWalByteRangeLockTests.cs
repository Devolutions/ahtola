using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqliteWalByteRangeLockTests
{
    private const long LockOffset = 120;

    [Test]
    public void PlatformMatrixDocumentsSupportedHosts()
    {
        // Windows + 64-bit Linux (OFD) + macOS (POSIX F_SETLK). Fail closed elsewhere.
        var supported =
            OperatingSystem.IsWindows()
            || (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            || OperatingSystem.IsMacOS();
        SupportsByteRangeLocks.Should().Be(supported);

        if (!supported)
            return;

        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            var locks = new SqliteWalByteRangeLock(lockPath);
            using var lease = locks.AcquireShared(LockOffset, length: 1, TimeSpan.Zero);
            lease.Should().NotBeNull();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    public void WindowsWritableLeaseCanAccessItsOwnLockedRange()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True);
        const long pendingByte = 0x4000_0000;
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = CreateLockCarrier(workDirectory);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                stream.SetLength(pendingByte + 512);

            var locks = new SqliteWalByteRangeLock(path);
            using var lease = locks.AcquireExclusiveWritable(
                pendingByte,
                length: 512,
                TimeSpan.Zero,
                CancellationToken.None);
            byte[] expected = [0x41, 0x48, 0x54, 0x4f, 0x4c, 0x41];
            lease.Write(expected, pendingByte);
            lease.FlushToDisk();
            var actual = new byte[expected.Length];

            lease.Read(actual, pendingByte).Should().Be(expected.Length);
            actual.Should().Equal(expected);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void IndependentProcessesCanShareTheSameRange()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            var locks = new SqliteWalByteRangeLock(lockPath);
            using var localLease = locks.AcquireShared(LockOffset, length: 1, TimeSpan.Zero);
            using var worker = new CrossProcessLockWorker(
                workDirectory,
                lockPath,
                LockOffset,
                length: 1,
                SqliteWalByteRangeLockMode.Shared,
                TimeSpan.Zero);

            worker.Result.Should().Be("acquired");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void IndependentExclusiveLocksConflict()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            var locks = new SqliteWalByteRangeLock(lockPath);
            using var localLease = locks.AcquireExclusive(LockOffset, length: 1, TimeSpan.Zero);
            using var worker = new CrossProcessLockWorker(
                workDirectory,
                lockPath,
                LockOffset,
                length: 1,
                SqliteWalByteRangeLockMode.Exclusive,
                TimeSpan.Zero);

            worker.Result.Should().Be("busy");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void SharedAndExclusiveLocksConflictInBothDirections()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            var locks = new SqliteWalByteRangeLock(lockPath);
            using (locks.AcquireShared(LockOffset, length: 1, TimeSpan.Zero))
            using (var exclusiveWorker = new CrossProcessLockWorker(
                       workDirectory,
                       lockPath,
                       LockOffset,
                       length: 1,
                       SqliteWalByteRangeLockMode.Exclusive,
                       TimeSpan.Zero))
            {
                exclusiveWorker.Result.Should().Be("busy");
            }

            using (locks.AcquireExclusive(LockOffset, length: 1, TimeSpan.Zero))
            using (var sharedWorker = new CrossProcessLockWorker(
                       workDirectory,
                       lockPath,
                       LockOffset,
                       length: 1,
                       SqliteWalByteRangeLockMode.Shared,
                       TimeSpan.Zero))
            {
                sharedWorker.Result.Should().Be("busy");
            }
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void IndependentRangesDoNotCollide()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            var locks = new SqliteWalByteRangeLock(lockPath);
            using var localLease = locks.AcquireExclusive(LockOffset, length: 1, TimeSpan.Zero);
            using var worker = new CrossProcessLockWorker(
                workDirectory,
                lockPath,
                LockOffset + 1,
                length: 1,
                SqliteWalByteRangeLockMode.Exclusive,
                TimeSpan.Zero);

            worker.Result.Should().Be("acquired");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void DisposingLeaseReleasesTheRangeForAnotherProcess()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            var locks = new SqliteWalByteRangeLock(lockPath);
            var localLease = locks.AcquireExclusive(LockOffset, length: 1, TimeSpan.Zero);
            try
            {
                using var blockedWorker = new CrossProcessLockWorker(
                    workDirectory,
                    lockPath,
                    LockOffset,
                    length: 1,
                    SqliteWalByteRangeLockMode.Exclusive,
                    TimeSpan.Zero);
                blockedWorker.Result.Should().Be("busy");
            }
            finally
            {
                localLease.Dispose();
            }

            using var releasedWorker = new CrossProcessLockWorker(
                workDirectory,
                lockPath,
                LockOffset,
                length: 1,
                SqliteWalByteRangeLockMode.Exclusive,
                TimeSpan.Zero);
            releasedWorker.Result.Should().Be("acquired");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void NonBlockingAndTimedAcquisitionReportBusy()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var lockPath = CreateLockCarrier(workDirectory);
            using var worker = new CrossProcessLockWorker(
                workDirectory,
                lockPath,
                LockOffset,
                length: 1,
                SqliteWalByteRangeLockMode.Exclusive,
                TimeSpan.Zero);
            worker.Result.Should().Be("acquired");

            var locks = new SqliteWalByteRangeLock(lockPath);
            locks.TryAcquireShared(LockOffset, length: 1, out var lease).Should().BeFalse();
            lease.Should().BeNull();

            var timeout = TimeSpan.FromMilliseconds(50);
            var busy = Assert.Throws<SqliteWalByteRangeLockBusyException>(
                () => locks.AcquireShared(LockOffset, length: 1, timeout));
            busy!.LockFilePath.Should().Be(Path.GetFullPath(lockPath));
            busy.Offset.Should().Be(LockOffset);
            busy.Length.Should().Be(1);
            busy.Mode.Should().Be(SqliteWalByteRangeLockMode.Shared);
            busy.Timeout.Should().Be(timeout);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    public void LockRangesMustBeFiniteAndNonEmpty()
    {
        RequireByteRangeLockSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var locks = new SqliteWalByteRangeLock(CreateLockCarrier(workDirectory));

            Assert.Throws<ArgumentOutOfRangeException>(() => locks.TryAcquireShared(-1, length: 1, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => locks.TryAcquireShared(0, length: 0, out _));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => locks.TryAcquireExclusive(long.MaxValue, length: 1, out _));
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    public void LockPrimitiveFailsClosedOnUnsupportedPlatforms()
    {
        if (SupportsByteRangeLocks)
            return;

        var path = Path.Combine(Path.GetTempPath(), $"Ahtola-wal-lock-unsupported-{Guid.NewGuid():N}");
        try
        {
            Assert.Throws<PlatformNotSupportedException>(() => new SqliteWalByteRangeLock(path));
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessByteRangeLockWorker()
    {
        var lockPath = Environment.GetEnvironmentVariable("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_PATH");
        if (string.IsNullOrEmpty(lockPath))
            return;

        var offset = ReadWorkerLong("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_OFFSET");
        var length = ReadWorkerLong("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_LENGTH");
        var mode = ReadWorkerMode();
        var timeout = TimeSpan.FromTicks(ReadWorkerLong("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_TIMEOUT_TICKS"));
        var readyPath = ReadWorkerValue("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_READY_PATH");
        var releasePath = ReadWorkerValue("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_RELEASE_PATH");
        var resultPath = ReadWorkerValue("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_RESULT_PATH");
        var locks = new SqliteWalByteRangeLock(lockPath);

        try
        {
            using var lease = locks.Acquire(offset, length, mode, timeout);
            File.WriteAllText(resultPath, "acquired");
            File.WriteAllText(readyPath, string.Empty);
            WaitForFile(releasePath, TimeSpan.FromSeconds(60), "The byte-range lock worker was not released.");
        }
        catch (SqliteWalByteRangeLockBusyException)
        {
            File.WriteAllText(resultPath, "busy");
            File.WriteAllText(readyPath, string.Empty);
        }
    }

    private sealed class CrossProcessLockWorker : IDisposable
    {
        private readonly Process _worker;
        private readonly string _releasePath;
        private readonly string _resultPath;
        private readonly StringBuilder _output = new();
        private bool _released;

        internal CrossProcessLockWorker(
            string workDirectory,
            string lockPath,
            long offset,
            long length,
            SqliteWalByteRangeLockMode mode,
            TimeSpan timeout)
        {
            var token = Guid.NewGuid().ToString("N");
            var readyPath = Path.Combine(workDirectory, $"wal-lock-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"wal-lock-release-{token}");
            _resultPath = Path.Combine(workDirectory, $"wal-lock-result-{token}");
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
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalByteRangeLockTests."
                + nameof(CrossProcessByteRangeLockWorker));
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_PATH"] = lockPath;
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_OFFSET"] = offset.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_LENGTH"] = length.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_MODE"] = mode.ToString();
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_TIMEOUT_TICKS"] = timeout.Ticks.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_READY_PATH"] = readyPath;
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_RELEASE_PATH"] = _releasePath;
            startInfo.Environment["TURSO_WAL_BYTE_RANGE_LOCK_WORKER_RESULT_PATH"] = _resultPath;

            _worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the SQLite WAL byte-range lock worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();

            WaitForFile(
                readyPath,
                TimeSpan.FromSeconds(60),
                "The SQLite WAL byte-range lock worker did not report readiness.",
                _worker,
                DrainOutput);
        }

        internal string Result => File.ReadAllText(_resultPath);

        public void Dispose()
        {
            try
            {
                ReleaseWorker();
                if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
                {
                    _worker.Kill(entireProcessTree: true);
                    Assert.Fail(
                        "The SQLite WAL byte-range lock worker did not exit within 60 seconds:"
                        + Environment.NewLine
                        + DrainOutput());
                }

                _worker.WaitForExit();
                _worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{DrainOutput()}");
            }
            finally
            {
                _worker.Dispose();
            }
        }

        private void ReleaseWorker()
        {
            if (_released)
                return;

            File.WriteAllText(_releasePath, string.Empty);
            _released = true;
        }

        private void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;

            lock (_output)
            {
                _output.AppendLine(args.Data);
            }
        }

        private string DrainOutput()
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private static long ReadWorkerLong(string name)
        => long.Parse(ReadWorkerValue(name), CultureInfo.InvariantCulture);

    private static SqliteWalByteRangeLockMode ReadWorkerMode()
    {
        var value = ReadWorkerValue("TURSO_WAL_BYTE_RANGE_LOCK_WORKER_MODE");
        if (Enum.TryParse<SqliteWalByteRangeLockMode>(value, ignoreCase: false, out var mode))
            return mode;

        throw new InvalidOperationException($"The byte-range lock worker received unknown mode '{value}'.");
    }

    private static string ReadWorkerValue(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"The byte-range lock worker is missing '{name}'.");

    private static void WaitForFile(
        string path,
        TimeSpan timeout,
        string failureMessage,
        Process? worker = null,
        Func<string>? output = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (worker?.HasExited == true)
            {
                worker.WaitForExit();
                Assert.Fail($"{failureMessage}{Environment.NewLine}{output?.Invoke()}");
            }
            if (stopwatch.Elapsed >= timeout)
            {
                worker?.Kill(entireProcessTree: true);
                Assert.Fail($"{failureMessage}{Environment.NewLine}{output?.Invoke()}");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private static bool SupportsByteRangeLocks
        => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.Is64BitProcess) || OperatingSystem.IsMacOS();

    private static void RequireByteRangeLockSupport()
    {
        if (!SupportsByteRangeLocks)
        {
            Assert.Ignore(
                "SQLite WAL byte-range locks are supported only on Windows, 64-bit Linux, and macOS.");
        }
    }

    private static string CreateLockCarrier(string workDirectory)
    {
        var path = Path.Combine(workDirectory, "main.db-shm");
        File.WriteAllBytes(path, []);
        return path;
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-wal-byte-range-lock",
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
