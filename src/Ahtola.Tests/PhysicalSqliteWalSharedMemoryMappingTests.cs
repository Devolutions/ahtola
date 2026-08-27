using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class PhysicalSqliteWalSharedMemoryMappingTests
{
    [Test]
    [NonParallelizable]
    public void WritableMappingGrowsAndFlushesMappedBytes()
    {
        RequirePhysicalMappingSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance)
                .OpenSharedMemory(path, FileOpenMode.CreateNew);
            try
            {
                mapping.Length.Should().Be(0);
                mapping.Write(3, [0x21, 0x43, 0x65]);
                mapping.MemoryBarrier();

                mapping.Length.Should().Be(6);
                var mappedBytes = new byte[6];
                mapping.Read(0, mappedBytes);
                mappedBytes.Should().Equal(0, 0, 0, 0x21, 0x43, 0x65);
            }
            finally
            {
                mapping.Dispose();
            }

            File.ReadAllBytes(path).Should().Equal(0, 0, 0, 0x21, 0x43, 0x65);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ReadOnlyMappingRejectsWritesAndNeverCreatesAMissingFile()
    {
        RequirePhysicalMappingSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            File.WriteAllBytes(path, [0x12, 0x34]);
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;

            using (var mapping = fileSystem.OpenSharedMemory(path, FileOpenMode.OpenExisting, readOnly: true))
            {
                mapping.IsReadOnly.Should().BeTrue();
                Assert.Throws<InvalidOperationException>(() => mapping.Write(0, [0x56]));
                var bytes = new byte[2];
                mapping.Read(0, bytes);
                bytes.Should().Equal(0x12, 0x34);
            }

            var missingPath = Path.Combine(workDirectory, "missing.db-shm");
            Assert.Throws<FileNotFoundException>(
                () => fileSystem.OpenSharedMemory(missingPath, FileOpenMode.OpenOrCreate, readOnly: true));
            File.Exists(missingPath).Should().BeFalse();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void MappingRejectsOutOfRangeAccessAndUseAfterDisposal()
    {
        RequirePhysicalMappingSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance)
                .OpenSharedMemory(path, FileOpenMode.CreateNew);
            mapping.Write(0, [0x01, 0x02]);

            Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Read(-1, new byte[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Read(2, new byte[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Read(1, new byte[2]));
            Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Write(long.MaxValue, [0x03]));

            mapping.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = mapping.Length);
            Assert.Throws<ObjectDisposedException>(() => mapping.Read(0, new byte[1]));
            Assert.Throws<ObjectDisposedException>(() => mapping.MemoryBarrier());
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void CrossProcessMappingObservesPublishedMappedBytes()
    {
        RequirePhysicalMappingSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            var expected = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
            using var writer = fileSystem.OpenSharedMemory(path, FileOpenMode.CreateNew);
            writer.Write(0, new byte[expected.Length]);
            writer.MemoryBarrier();

            using var observer = new CrossProcessMappingObserver(workDirectory, path, expected.Length);
            writer.Write(0, expected);
            writer.MemoryBarrier();

            observer.ReadPublishedBytes().Should().Equal(expected);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void CleanupRequiresExclusiveDmsAfterCrossProcessMappingLeaves()
    {
        RequirePhysicalMappingSupport();
        if (OperatingSystem.IsMacOS())
            Assert.Ignore("Darwin deliberately prohibits last-owner SHM unlink.");
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
            var mapping = (PhysicalSqliteWalSharedMemoryMapping)fileSystem.OpenSharedMemory(
                path,
                FileOpenMode.CreateNew);
            mapping.Write(0, [0x5A]);

            using (var peer = new CrossProcessMappingObserver(workDirectory, path, byteCount: 1))
            {
                mapping.DisposeAndTryDeleteIfLast().Should().BeFalse();
                File.Exists(path).Should().BeTrue(
                    "the peer's shared DMS lease prevents final-owner cleanup");
                peer.ReadPublishedBytes().Should().Equal(0x5A);
            }

            var final = (PhysicalSqliteWalSharedMemoryMapping)fileSystem.OpenSharedMemory(
                path,
                FileOpenMode.OpenExisting);
            final.DisposeAndTryDeleteIfLast().Should().BeTrue();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void MacOSTransientMappingsReuseBrokeredDescriptorAndFinalOwnerNeverUnlinks()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("This characterizes Darwin's process-scoped F_SETLK behavior.");
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
            var first = fileSystem.OpenSharedMemory(path, FileOpenMode.CreateNew);
            var second = (PhysicalSqliteWalSharedMemoryMapping)fileSystem.OpenSharedMemory(
                path,
                FileOpenMode.OpenExisting);
            first.Dispose();

            for (var index = 0; index < 256; index++)
            {
                using var transient = fileSystem.OpenSharedMemory(path, FileOpenMode.OpenExisting);
            }
            PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry
                .GetBrokeredHandleCountForTesting(path)
                .Should().Be(1, "transient mappings reuse the carrier descriptor on Darwin");
            var byteRangeLocks = new SqliteWalByteRangeLock(path);
            for (var index = 0; index < 256; index++)
            {
                using var transientLock = byteRangeLocks.AcquireExclusive(
                    offset: 120,
                    length: 1,
                    timeout: TimeSpan.Zero);
            }
            RunCleanupProbeWorker(workDirectory, path).Should().BeFalse(
                "transient mappings and lock leases must not release the process DMS lease");
            second.DisposeAndTryDeleteIfLast().Should().BeFalse(
                "same-process foreign SQLite users cannot be excluded on Darwin");
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void MacOSReadOnlyMappingBrokersWritableReadMarkLocksUntilFinalRelease()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("This characterizes Darwin's process-scoped F_SETLK behavior.");
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            File.WriteAllBytes(path, new byte[SqliteWalIndexLayout.BlockSize]);
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
            using var mapping = fileSystem.OpenSharedMemory(
                path,
                FileOpenMode.OpenExisting,
                readOnly: true);
            var locks = new SqliteWalByteRangeLock(path);
            using var retainedReadMark = locks.AcquireShared(
                offset: 123,
                length: 1,
                timeout: TimeSpan.Zero);

            using (locks.AcquireExclusiveWritable(
                       offset: 124,
                       length: 1,
                       timeout: TimeSpan.Zero,
                       cancellationToken: CancellationToken.None))
            {
            }

            PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry
                .GetBrokeredHandleCountForTesting(path)
                .Should().Be(2, "the read-only mapping lazily adds one registry-owned writable descriptor");
            RunCleanupProbeWorker(workDirectory, path).Should().BeFalse(
                "disposing the writable read-mark lease must preserve DMS");
            RunCleanupProbeWorker(workDirectory, path, offset: 123).Should().BeFalse(
                "disposing the writable read-mark lease must preserve unrelated process locks");
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void MacOSFinalMappingReleaseWaitsForBorrowedCheckpointAndReadMarkLeases()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("This characterizes Darwin's process-scoped F_SETLK behavior.");
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            File.WriteAllBytes(path, new byte[SqliteWalIndexLayout.BlockSize]);
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
            using var mapping = fileSystem.OpenSharedMemory(
                path,
                FileOpenMode.OpenExisting,
                readOnly: true);
            var locks = new SqliteWalByteRangeLock(path);
            using var checkpoint = locks.AcquireExclusiveWritable(
                offset: 121,
                length: 1,
                timeout: TimeSpan.Zero,
                cancellationToken: CancellationToken.None);
            using var readMark = locks.AcquireExclusiveWritable(
                offset: 124,
                length: 1,
                timeout: TimeSpan.Zero,
                cancellationToken: CancellationToken.None);

            mapping.Dispose();

            PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry
                .GetBrokeredHandleCountForTesting(path)
                .Should().Be(2, "borrowed leases keep both broker descriptors alive");
            RunCleanupProbeWorker(workDirectory, path).Should().BeFalse(
                "DMS remains held after the final mapping leaves while leases survive");
            RunCleanupProbeWorker(workDirectory, path, offset: 121).Should().BeFalse();
            RunCleanupProbeWorker(workDirectory, path, offset: 124).Should().BeFalse();

            checkpoint.Dispose();
            RunCleanupProbeWorker(workDirectory, path).Should().BeFalse(
                "the remaining read-mark lease still owns the registry lifetime");
            readMark.Dispose();

            RunCleanupProbeWorker(workDirectory, path).Should().BeTrue(
                "the registry releases DMS only after mappings and borrowed leases reach zero");
            PhysicalSqliteWalSharedMemoryMapping.SqliteWalSharedMemoryLifecycleRegistry
                .GetBrokeredHandleCountForTesting(path)
                .Should().Be(0);
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void ConcurrentFinalCleanupAndOpenNeverReturnsADeletedCarrier()
    {
        RequirePhysicalMappingSupport();
        if (OperatingSystem.IsMacOS())
            Assert.Ignore("Darwin deliberately prohibits last-owner SHM unlink.");
        var workDirectory = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDirectory, "main.db-shm");
            var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
            for (var iteration = 0; iteration < 128; iteration++)
            {
                var closing = (PhysicalSqliteWalSharedMemoryMapping)fileSystem.OpenSharedMemory(
                    path,
                    FileOpenMode.OpenOrCreate);
                PhysicalSqliteWalSharedMemoryMapping? opened = null;
                var start = new ManualResetEventSlim();
                var closeTask = Task.Run(() =>
                {
                    start.Wait();
                    closing.DisposeAndTryDeleteIfLast();
                });
                var openTask = Task.Run(() =>
                {
                    start.Wait();
                    opened = (PhysicalSqliteWalSharedMemoryMapping)fileSystem.OpenSharedMemory(
                        path,
                        FileOpenMode.OpenOrCreate);
                });

                start.Set();
                Task.WaitAll(closeTask, openTask);
                try
                {
                    File.Exists(path).Should().BeTrue();
                    SqliteWalSharedMemoryCarrierIdentity.FromPath(path)
                        .Should().Be(opened!.CarrierIdentity);
                }
                finally
                {
                    opened?.DisposeAndTryDeleteIfLast();
                    start.Dispose();
                }
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
    public void CrossProcessMappingWorkerObservesPublishedBytes()
    {
        var path = Environment.GetEnvironmentVariable("TURSO_SHM_MAPPING_WORKER_PATH");
        if (string.IsNullOrEmpty(path))
            return;

        var readyPath = Environment.GetEnvironmentVariable("TURSO_SHM_MAPPING_WORKER_READY_PATH")
            ?? throw new InvalidOperationException("The shared-memory mapping worker is missing its ready path.");
        var releasePath = Environment.GetEnvironmentVariable("TURSO_SHM_MAPPING_WORKER_RELEASE_PATH")
            ?? throw new InvalidOperationException("The shared-memory mapping worker is missing its release path.");
        var resultPath = Environment.GetEnvironmentVariable("TURSO_SHM_MAPPING_WORKER_RESULT_PATH")
            ?? throw new InvalidOperationException("The shared-memory mapping worker is missing its result path.");
        var byteCountText = Environment.GetEnvironmentVariable("TURSO_SHM_MAPPING_WORKER_BYTE_COUNT")
            ?? throw new InvalidOperationException("The shared-memory mapping worker is missing its byte count.");
        var byteCount = int.Parse(byteCountText);

        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance)
            .OpenSharedMemory(path, FileOpenMode.OpenExisting, readOnly: true);
        File.WriteAllText(readyPath, string.Empty);
        WaitForFile(releasePath, TimeSpan.FromSeconds(60), "The shared-memory mapping worker was not released.");

        mapping.MemoryBarrier();
        var bytes = new byte[byteCount];
        mapping.Read(0, bytes);
        File.WriteAllText(resultPath, Convert.ToHexString(bytes));
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessCleanupProbeWorker()
    {
        var path = Environment.GetEnvironmentVariable("TURSO_SHM_CLEANUP_WORKER_PATH");
        if (string.IsNullOrEmpty(path))
            return;
        var resultPath = Environment.GetEnvironmentVariable("TURSO_SHM_CLEANUP_WORKER_RESULT_PATH")
            ?? throw new InvalidOperationException("The cleanup probe worker is missing its result path.");

        var offsetText = Environment.GetEnvironmentVariable("TURSO_SHM_CLEANUP_WORKER_OFFSET");
        var offset = string.IsNullOrEmpty(offsetText)
            ? PhysicalSqliteWalSharedMemoryMapping.DeadManSwitchLockOffset
            : long.Parse(offsetText, System.Globalization.CultureInfo.InvariantCulture);
        var locks = new SqliteWalByteRangeLock(path);
        var acquired = locks.TryAcquireExclusive(
            offset,
            length: 1,
            out var lease);
        lease?.Dispose();
        File.WriteAllText(resultPath, acquired ? "acquired" : "blocked");
    }

    [Test]
    public void PhysicalMappingFailsClosedOnUnsupportedPlatforms()
    {
        if (SupportsPhysicalMapping)
            return;

        var path = Path.Combine(Path.GetTempPath(), $"Ahtola-shm-unsupported-{Guid.NewGuid():N}");
        try
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance)
                    .OpenSharedMemory(path, FileOpenMode.OpenOrCreate));
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class CrossProcessMappingObserver : IDisposable
    {
        private readonly Process _worker;
        private readonly string _releasePath;
        private readonly string _resultPath;
        private readonly StringBuilder _output = new();
        private bool _released;

        internal CrossProcessMappingObserver(string workDirectory, string path, int byteCount)
        {
            var token = Guid.NewGuid().ToString("N");
            var readyPath = Path.Combine(workDirectory, $"shm-mapping-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"shm-mapping-release-{token}");
            _resultPath = Path.Combine(workDirectory, $"shm-mapping-result-{token}");
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
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.PhysicalSqliteWalSharedMemoryMappingTests."
                + nameof(CrossProcessMappingWorkerObservesPublishedBytes));
            startInfo.Environment["TURSO_SHM_MAPPING_WORKER_PATH"] = path;
            startInfo.Environment["TURSO_SHM_MAPPING_WORKER_READY_PATH"] = readyPath;
            startInfo.Environment["TURSO_SHM_MAPPING_WORKER_RELEASE_PATH"] = _releasePath;
            startInfo.Environment["TURSO_SHM_MAPPING_WORKER_RESULT_PATH"] = _resultPath;
            startInfo.Environment["TURSO_SHM_MAPPING_WORKER_BYTE_COUNT"] = byteCount.ToString();

            _worker = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start the shared-memory mapping worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();

            WaitForFile(
                readyPath,
                TimeSpan.FromSeconds(60),
                "The shared-memory mapping worker did not open its mapping.",
                _worker,
                DrainOutput);
        }

        internal byte[] ReadPublishedBytes()
        {
            ReleaseWorker();
            WaitForWorkerExit();
            return Convert.FromHexString(File.ReadAllText(_resultPath));
        }

        public void Dispose()
        {
            try
            {
                ReleaseWorker();
                WaitForWorkerExit();
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

        private void WaitForWorkerExit()
        {
            if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                _worker.Kill(entireProcessTree: true);
                Assert.Fail(
                    "The shared-memory mapping worker did not exit within 60 seconds:"
                    + Environment.NewLine
                    + DrainOutput());
            }

            _worker.WaitForExit();
            _worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{DrainOutput()}");
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

    private static bool RunCleanupProbeWorker(
        string workDirectory,
        string path,
        long? offset = null)
    {
        var resultPath = Path.Combine(workDirectory, $"shm-cleanup-result-{Guid.NewGuid():N}");
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
            "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.PhysicalSqliteWalSharedMemoryMappingTests."
            + nameof(CrossProcessCleanupProbeWorker));
        startInfo.Environment["TURSO_SHM_CLEANUP_WORKER_PATH"] = path;
        startInfo.Environment["TURSO_SHM_CLEANUP_WORKER_RESULT_PATH"] = resultPath;
        if (offset is not null)
        {
            startInfo.Environment["TURSO_SHM_CLEANUP_WORKER_OFFSET"] =
                offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using var worker = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("Failed to start the SHM cleanup probe worker.");
        if (!worker.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            worker.Kill(entireProcessTree: true);
            Assert.Fail("The SHM cleanup probe worker did not exit within 60 seconds.");
        }

        var output = worker.StandardOutput.ReadToEnd() + worker.StandardError.ReadToEnd();
        worker.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{output}");
        return File.ReadAllText(resultPath) == "acquired";
    }

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

    private static bool SupportsPhysicalMapping
        => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.Is64BitProcess) || OperatingSystem.IsMacOS();

    private static void RequirePhysicalMappingSupport()
    {
        if (!SupportsPhysicalMapping)
        {
            Assert.Ignore(
                "Physical SQLite shared-memory mappings are supported only on Windows, 64-bit Linux, and macOS.");
        }
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "physical-sqlite-wal-shm-mapping",
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
