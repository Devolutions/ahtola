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
