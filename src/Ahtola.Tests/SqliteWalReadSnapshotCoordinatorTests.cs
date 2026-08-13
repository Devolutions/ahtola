using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqliteWalReadSnapshotCoordinatorTests
{
    private const long FirstReadMarkLockOffset = SqliteWalIndexCheckpointInfo.LockOffset + 3;

    [Test]
    [NonParallelizable]
    public void SqliteArtifactSnapshotPinsAReadMarkAndMatchesAnIndependentWalScan()
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        using var snapshot = coordinator.BeginRead(TimeSpan.Zero);
        using var wal = OpenWalCopy(artifact.DatabasePath);

        snapshot.ReadMarkIndex.Should().BeInRange(1, SqliteWalIndexCheckpointInfo.ReadMarkCount - 1);
        snapshot.MaximumFrame.Should().BeGreaterThan(0);
        snapshot.ReadFrame(snapshot.MaximumFrame).Header.IsCommit.Should().BeTrue();

        var expectedFrames = FindLatestFramesByPage(wal, snapshot.MaximumFrame);
        foreach (var (pageNumber, expectedFrameNumber) in expectedFrames)
        {
            var expected = wal.ReadFrame(expectedFrameNumber);
            var actual = snapshot.FindFrame(pageNumber);

            actual.Should().NotBeNull();
            actual!.Header.Should().Be(expected.Header);
            actual.PageData.Should().Equal(expected.PageData);
        }

        using (var held = new CrossProcessReadMarkProbe(
                   artifact.WorkDirectory,
                   artifact.DatabasePath + "-shm",
                   snapshot.ReadMarkIndex))
        {
            held.Result.Should().Be("busy");
        }

        snapshot.Reset();

        using var released = new CrossProcessReadMarkProbe(
            artifact.WorkDirectory,
            artifact.DatabasePath + "-shm",
            readMarkIndex: 1);
        released.Result.Should().Be("acquired");
    }

    [Test]
    [NonParallelizable]
    public void SnapshotRepurposesAnUnlockedStaleReadMarkAtTheCurrentBoundary()
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var wal = OpenWalCopy(artifact.DatabasePath);
        using var mapping = new MemorySharedMemoryMapping(
            ReadAllBytesSharingWithSqlite(artifact.DatabasePath + "-shm"));
        var locks = new SqliteWalByteRangeLock(artifact.DatabasePath + "-shm");
        var index = new SqliteWalIndexSharedMemory(mapping);
        var region = index.ReadValidatedHeader(wal);
        var staleCommitFrame = FindPreviousCommitFrame(wal, region.Header.MaximumFrame);
        staleCommitFrame.Should().BeGreaterThan(0);
        using (locks.AcquireExclusive(FirstReadMarkLockOffset + 1, length: 1, TimeSpan.Zero))
            index.PublishReadMark(readMarkIndex: 1, region.Header.MaximumFrame);
        using (locks.AcquireExclusive(FirstReadMarkLockOffset + 2, length: 1, TimeSpan.Zero))
            index.PublishReadMark(readMarkIndex: 2, staleCommitFrame);

        using var heldNewestMark = new CrossProcessReadMarkProbe(
            artifact.WorkDirectory,
            artifact.DatabasePath + "-shm",
            readMarkIndex: 1);
        heldNewestMark.Result.Should().Be("acquired");

        using var coordinator = new SqliteWalReadSnapshotCoordinator(wal, index, locks);
        using var snapshot = coordinator.BeginRead(TimeSpan.Zero);

        snapshot.ReadMarkIndex.Should().Be(2);
        snapshot.MaximumFrame.Should().Be(region.Header.MaximumFrame);
    }

    [Test]
    [NonParallelizable]
    public void SnapshotIgnoresFramesAppendedByASeparateSqliteWriterProcess()
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        using var snapshot = coordinator.BeginRead(TimeSpan.Zero);
        var pinnedFrame = snapshot.ReadFrame(snapshot.MaximumFrame);

        RunSqliteWriterWorker(artifact.DatabasePath);

        using var walAfterWriter = OpenWalCopy(artifact.DatabasePath);
        var recovery = walAfterWriter.ScanRecovery();
        recovery.LastCommittedFrameNumber.Should().BeGreaterThan(snapshot.MaximumFrame);

        snapshot.ReadFrame(snapshot.MaximumFrame).Header.Should().Be(pinnedFrame.Header);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => snapshot.ReadFrame(checked(snapshot.MaximumFrame + 1)));
        foreach (var frame in FindLatestFramesByPage(walAfterWriter, snapshot.MaximumFrame))
            snapshot.FindFrame(frame.Key)!.Header.Should().Be(walAfterWriter.ReadFrame(frame.Value).Header);

        snapshot.Reset();
        using var refreshedSnapshot = coordinator.BeginRead(TimeSpan.Zero);
        refreshedSnapshot.MaximumFrame.Should().Be(checked((uint)recovery.LastCommittedFrameNumber));
    }

    [TestCase("torn")]
    [TestCase("stale")]
    [NonParallelizable]
    public void DetachedSnapshotRejectsTornAndStaleSqliteHeaders(string mutation)
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
            artifact.DatabasePath + "-shm",
            FileOpenMode.OpenExisting);

        var header = new byte[SqliteWalIndexHeader.Size];
        mapping.Read(position: 0, header);
        if (mutation == "torn")
        {
            header[40] ^= 0x01;
            mapping.Write(SqliteWalIndexHeader.Size, header);
        }
        else
        {
            var maximumFrame = ReadUInt32Native(header, offset: 16);
            maximumFrame.Should().BeGreaterThan(1);
            WriteUInt32Native(header, offset: 16, maximumFrame - 1);
            RewriteHeaderChecksum(header);
            mapping.Write(position: 0, header);
            mapping.Write(SqliteWalIndexHeader.Size, header);
        }
        mapping.MemoryBarrier();

        Assert.Throws<InvalidDataException>(() => coordinator.BeginRead(TimeSpan.Zero));
    }

    [Test]
    [NonParallelizable]
    public void ResetAndCoordinatorDisposalReleaseTheirExactReadMarks()
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        var coordinator = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        try
        {
            var resetSnapshot = coordinator.BeginRead(TimeSpan.Zero);
            var resetReadMark = resetSnapshot.ReadMarkIndex;
            resetSnapshot.Reset();

            using (var releasedAfterReset = new CrossProcessReadMarkProbe(
                       artifact.WorkDirectory,
                       artifact.DatabasePath + "-shm",
                       resetReadMark))
            {
                releasedAfterReset.Result.Should().Be("acquired");
            }

            var disposedSnapshot = coordinator.BeginRead(TimeSpan.Zero);
            var disposedReadMark = disposedSnapshot.ReadMarkIndex;
            coordinator.Dispose();

            disposedSnapshot.IsActive.Should().BeFalse();
            using var releasedAfterDispose = new CrossProcessReadMarkProbe(
                artifact.WorkDirectory,
                artifact.DatabasePath + "-shm",
                disposedReadMark);
            releasedAfterDispose.Result.Should().Be("acquired");
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    [Test]
    [NonParallelizable]
    public void FaultedSnapshotReleasesItsReadMark()
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var coordinator = SqliteWalReadSnapshotCoordinator.Open(artifact.DatabasePath);
        var snapshot = coordinator.BeginRead(TimeSpan.Zero);
        try
        {
            using (var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance).OpenSharedMemory(
                       artifact.DatabasePath + "-shm",
                       FileOpenMode.OpenExisting))
            {
                var secondHeader = new byte[SqliteWalIndexHeader.Size];
                mapping.Read(SqliteWalIndexHeader.Size, secondHeader);
                secondHeader[40] ^= 0x01;
                mapping.Write(SqliteWalIndexHeader.Size, secondHeader);
                mapping.MemoryBarrier();
            }

            Assert.Throws<InvalidDataException>(() => snapshot.FindFrame(pageNumber: 1));
            snapshot.IsActive.Should().BeFalse();

            using var releasedAfterFault = new CrossProcessReadMarkProbe(
                artifact.WorkDirectory,
                artifact.DatabasePath + "-shm",
                snapshot.ReadMarkIndex);
            releasedAfterFault.Result.Should().Be("acquired");
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [Test]
    [NonParallelizable]
    public void CanceledReadStartReleasesItsAcquiredReadMark()
    {
        RequireSnapshotSupport();
        using var artifact = SqliteWalArtifact.Create();
        using var wal = OpenWalCopy(artifact.DatabasePath);
        using var mapping = new MemorySharedMemoryMapping(
            ReadAllBytesSharingWithSqlite(artifact.DatabasePath + "-shm"));
        var locks = new SqliteWalByteRangeLock(artifact.DatabasePath + "-shm");
        var index = new SqliteWalIndexSharedMemory(mapping);
        var header = index.ReadValidatedHeader(wal);
        header.Header.MaximumFrame.Should().BeGreaterThan(0);
        using (locks.AcquireExclusive(FirstReadMarkLockOffset + 1, length: 1, TimeSpan.Zero))
            index.PublishReadMark(readMarkIndex: 1, header.Header.MaximumFrame);

        using var cancellationSource = new CancellationTokenSource();
        var cancelingMapping = new CancelOnSecondBarrierMapping(mapping, cancellationSource);
        using var coordinator = new SqliteWalReadSnapshotCoordinator(
            wal,
            new SqliteWalIndexSharedMemory(cancelingMapping),
            locks);

        Assert.Throws<OperationCanceledException>(
            () => coordinator.BeginRead(TimeSpan.Zero, cancellationSource.Token));

        using var releasedAfterCancellation = new CrossProcessReadMarkProbe(
            artifact.WorkDirectory,
            artifact.DatabasePath + "-shm",
            readMarkIndex: 1);
        releasedAfterCancellation.Result.Should().Be("acquired");
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessReadMarkProbeWorker()
    {
        var lockPath = Environment.GetEnvironmentVariable("TURSO_WAL_SNAPSHOT_LOCK_PATH");
        if (string.IsNullOrEmpty(lockPath))
            return;

        var readMarkIndex = int.Parse(
            ReadWorkerValue("TURSO_WAL_SNAPSHOT_LOCK_INDEX"),
            CultureInfo.InvariantCulture);
        var readyPath = ReadWorkerValue("TURSO_WAL_SNAPSHOT_LOCK_READY_PATH");
        var releasePath = ReadWorkerValue("TURSO_WAL_SNAPSHOT_LOCK_RELEASE_PATH");
        var resultPath = ReadWorkerValue("TURSO_WAL_SNAPSHOT_LOCK_RESULT_PATH");
        var locks = new SqliteWalByteRangeLock(lockPath);

        try
        {
            using var lease = locks.AcquireExclusive(
                FirstReadMarkLockOffset + readMarkIndex,
                length: 1,
                TimeSpan.Zero);
            File.WriteAllText(resultPath, "acquired");
            File.WriteAllText(readyPath, string.Empty);
            WaitForFile(releasePath, TimeSpan.FromSeconds(60), "The read-mark probe worker was not released.");
        }
        catch (SqliteWalByteRangeLockBusyException)
        {
            File.WriteAllText(resultPath, "busy");
            File.WriteAllText(readyPath, string.Empty);
        }
    }

    [Test]
    [Category("ProcessWorker")]
    [NonParallelizable]
    public void CrossProcessSqliteWriterWorker()
    {
        var databasePath = Environment.GetEnvironmentVariable("TURSO_WAL_SNAPSHOT_WRITER_DATABASE_PATH");
        if (string.IsNullOrEmpty(databasePath))
            return;

        using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
        connection.Open();
        Execute(connection, "PRAGMA wal_autocheckpoint=0;");
        Execute(connection, "INSERT INTO data(value) VALUES ('writer-process');");
    }

    private static Dictionary<uint, uint> FindLatestFramesByPage(SqliteWalFile wal, uint maximumFrame)
    {
        var frames = new Dictionary<uint, uint>();
        for (uint frameNumber = 1; frameNumber <= maximumFrame; frameNumber++)
            frames[wal.ReadFrame(frameNumber).Header.PageNumber] = frameNumber;
        return frames;
    }

    private static uint FindPreviousCommitFrame(SqliteWalFile wal, uint maximumFrame)
    {
        for (var frameNumber = maximumFrame - 1; frameNumber > 0; frameNumber--)
        {
            if (wal.ReadFrame(frameNumber).Header.IsCommit)
                return frameNumber;
        }

        return 0;
    }

    private static SqliteWalFile OpenWalCopy(string databasePath)
    {
        var fileSystem = new InMemoryFileSystem();
        using (var walCopy = fileSystem.OpenFile("main.db-wal", FileOpenMode.CreateNew))
            walCopy.Write(position: 0, ReadAllBytesSharingWithSqlite(databasePath + "-wal"));
        return SqliteWalFile.Open(fileSystem, "main.db-wal", readOnly: true);
    }

    private static byte[] ReadAllBytesSharingWithSqlite(string path)
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

    private static void RunSqliteWriterWorker(string databasePath)
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
            "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalReadSnapshotCoordinatorTests."
            + nameof(CrossProcessSqliteWriterWorker));
        startInfo.Environment["TURSO_WAL_SNAPSHOT_WRITER_DATABASE_PATH"] = databasePath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the SQLite WAL writer worker.");
        var output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"writer output:{Environment.NewLine}{output}");
    }

    private static uint ReadUInt32Native(byte[] source, int offset)
        => SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)))
            : BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset, sizeof(uint)));

    private static void WriteUInt32Native(byte[] destination, int offset, uint value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, sizeof(uint)), value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), value);
    }

    private static void RewriteHeaderChecksum(byte[] header)
    {
        var checksumByteOrder = SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian
            ? SqliteWalChecksumByteOrder.LittleEndian
            : SqliteWalChecksumByteOrder.BigEndian;
        var checksum = SqliteWalChecksum.Calculate(header.AsSpan(0, 40), checksumByteOrder);
        WriteUInt32Native(header, offset: 40, checksum.First);
        WriteUInt32Native(header, offset: 44, checksum.Second);
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string ReadWorkerValue(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"The read-mark probe worker is missing '{name}'.");

    private static void WaitForFile(string path, TimeSpan timeout, string failureMessage, Process? worker = null, Func<string>? output = null)
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

    private static bool SupportsSnapshotCoordinator
        => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.Is64BitProcess) || OperatingSystem.IsMacOS();

    private static void RequireSnapshotSupport()
    {
        if (!SupportsSnapshotCoordinator)
        {
            Assert.Ignore(
                "Detached SQLite WAL read snapshots are supported only on Windows, 64-bit Linux, and macOS.");
        }
    }

    private sealed class SqliteWalArtifact : IDisposable
    {
        private SqliteWalArtifact(string workDirectory, string databasePath, SqliteConnection connection)
        {
            WorkDirectory = workDirectory;
            DatabasePath = databasePath;
            Connection = connection;
        }

        internal string WorkDirectory { get; }

        internal string DatabasePath { get; }

        private SqliteConnection Connection { get; }

        internal static SqliteWalArtifact Create()
        {
            var workDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "sqlite-wal-read-snapshot",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var databasePath = Path.Combine(workDirectory, "main.db");
            var connection = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False");
            try
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
                return new SqliteWalArtifact(workDirectory, databasePath, connection);
            }
            catch
            {
                connection.Dispose();
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            Connection.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(WorkDirectory))
                Directory.Delete(WorkDirectory, recursive: true);
        }
    }

    private sealed class CrossProcessReadMarkProbe : IDisposable
    {
        private readonly Process _worker;
        private readonly string _releasePath;
        private readonly string _resultPath;
        private readonly StringBuilder _output = new();
        private bool _released;

        internal CrossProcessReadMarkProbe(string workDirectory, string lockPath, int readMarkIndex)
        {
            var token = Guid.NewGuid().ToString("N");
            var readyPath = Path.Combine(workDirectory, $"wal-snapshot-lock-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"wal-snapshot-lock-release-{token}");
            _resultPath = Path.Combine(workDirectory, $"wal-snapshot-lock-result-{token}");
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
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalReadSnapshotCoordinatorTests."
                + nameof(CrossProcessReadMarkProbeWorker));
            startInfo.Environment["TURSO_WAL_SNAPSHOT_LOCK_PATH"] = lockPath;
            startInfo.Environment["TURSO_WAL_SNAPSHOT_LOCK_INDEX"] = readMarkIndex.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["TURSO_WAL_SNAPSHOT_LOCK_READY_PATH"] = readyPath;
            startInfo.Environment["TURSO_WAL_SNAPSHOT_LOCK_RELEASE_PATH"] = _releasePath;
            startInfo.Environment["TURSO_WAL_SNAPSHOT_LOCK_RESULT_PATH"] = _resultPath;

            _worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the SQLite WAL read-mark probe worker.");
            _worker.OutputDataReceived += AppendOutput;
            _worker.ErrorDataReceived += AppendOutput;
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();
            WaitForFile(
                readyPath,
                TimeSpan.FromSeconds(60),
                "The SQLite WAL read-mark probe worker did not report readiness.",
                _worker,
                DrainOutput);
        }

        internal string Result => File.ReadAllText(_resultPath);

        public void Dispose()
        {
            try
            {
                if (!_released)
                {
                    File.WriteAllText(_releasePath, string.Empty);
                    _released = true;
                }

                if (!_worker.WaitForExit(TimeSpan.FromSeconds(60)))
                {
                    _worker.Kill(entireProcessTree: true);
                    Assert.Fail(
                        "The SQLite WAL read-mark probe worker did not exit within 60 seconds:"
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

    private sealed class CancelOnSecondBarrierMapping : ISqliteWalSharedMemoryMapping
    {
        private readonly ISqliteWalSharedMemoryMapping _inner;
        private readonly CancellationTokenSource _cancellationSource;
        private int _barrierCount;

        internal CancelOnSecondBarrierMapping(
            ISqliteWalSharedMemoryMapping inner,
            CancellationTokenSource cancellationSource)
        {
            _inner = inner;
            _cancellationSource = cancellationSource;
        }

        public long Length => _inner.Length;

        public bool IsReadOnly => _inner.IsReadOnly;

        public void Read(long position, Span<byte> destination) => _inner.Read(position, destination);

        public void Write(long position, ReadOnlySpan<byte> source) => _inner.Write(position, source);

        public void MemoryBarrier()
        {
            _inner.MemoryBarrier();
            if (Interlocked.Increment(ref _barrierCount) == 2)
                _cancellationSource.Cancel();
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class MemorySharedMemoryMapping : ISqliteWalSharedMemoryMapping
    {
        private readonly object _gate = new();
        private byte[] _bytes;
        private bool _disposed;

        internal MemorySharedMemoryMapping(byte[] bytes)
        {
            _bytes = bytes.ToArray();
        }

        public long Length
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _bytes.Length;
                }
            }
        }

        public bool IsReadOnly => false;

        public void Read(long position, Span<byte> destination)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ValidateRange(position, destination.Length);
                _bytes.AsSpan(checked((int)position), destination.Length).CopyTo(destination);
            }
        }

        public void Write(long position, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (position < 0 || position > int.MaxValue - source.Length)
                    throw new ArgumentOutOfRangeException(nameof(position));

                var end = checked((int)position + source.Length);
                if (end > _bytes.Length)
                    Array.Resize(ref _bytes, end);
                source.CopyTo(_bytes.AsSpan(checked((int)position), source.Length));
            }
        }

        public void MemoryBarrier()
        {
            lock (_gate)
                ThrowIfDisposed();
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
        }

        private void ValidateRange(long position, int length)
        {
            if (position < 0 || position > _bytes.Length || length > _bytes.Length - position)
                throw new ArgumentOutOfRangeException(nameof(position));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MemorySharedMemoryMapping));
        }
    }
}
