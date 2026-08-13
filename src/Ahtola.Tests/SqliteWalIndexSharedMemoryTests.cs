using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class SqliteWalIndexSharedMemoryTests
{
    [Test]
    public void PublishHeaderUsesSqlitesSecondCopyBarrierFirstCopyOrdering()
    {
        using var wal = CreateWal((PageNumber: 1, DatabasePageCount: 1));
        var header = CreateIndexHeader(wal);
        using var mapping = new MemorySharedMemoryMapping(SqliteWalIndexLayout.BlockSize);
        var index = new SqliteWalIndexSharedMemory(mapping);

        index.PublishHeader(header, wal);

        mapping.Operations.Should().Equal(
            $"write:{SqliteWalIndexHeader.Size}",
            "barrier",
            "write:0");
        mapping.ReadBytes(0, SqliteWalIndexHeader.Size).Should().Equal(header.ToArray());
        mapping.ReadBytes(SqliteWalIndexHeader.Size, SqliteWalIndexHeader.Size).Should().Equal(header.ToArray());
    }

    [Test]
    public void CheckpointProgressCanPublishItsSelectedBoundaryAfterAWriterAdvancesTheHeader()
    {
        using var wal = CreateWal((PageNumber: 1, DatabasePageCount: 1));
        var selectedHeader = CreateIndexHeader(wal);
        using var mapping = new MemorySharedMemoryMapping(SqliteWalIndexLayout.BlockSize);
        var index = new SqliteWalIndexSharedMemory(mapping);
        index.PublishHeader(selectedHeader, wal);

        var secondFrameNumber = wal.AppendFrame(
            pageNumber: 2,
            pageData: new byte[wal.PageSize],
            databaseSizeInPages: 2);
        wal.Flush();
        var secondFrame = wal.ReadFrame(secondFrameNumber).Header;
        var advancedHeader = selectedHeader.WithCommittedFrames(
            maximumFrame: checked(selectedHeader.MaximumFrame + 1),
            databasePageCount: 2,
            secondFrame.Checksum1,
            secondFrame.Checksum2);
        mapping.Write(SqliteWalIndexHeader.Size, advancedHeader.ToArray());
        mapping.MemoryBarrier();

        index.PublishBackfillAttemptedFrameCount(
            selectedHeader,
            attemptedFrameCount: 1,
            wal: wal);
        index.PublishBackfilledFrameCount(
            selectedHeader,
            backfilledFrameCount: 1,
            wal: wal);

        mapping.Write(position: 0, advancedHeader.ToArray());
        mapping.MemoryBarrier();
        var region = index.ReadValidatedHeader(wal);
        region.Header.MaximumFrame.Should().Be(advancedHeader.MaximumFrame);
        region.CheckpointInfo.BackfillAttemptedFrameCount.Should().Be(1);
        region.CheckpointInfo.BackfilledFrameCount.Should().Be(1);
    }

    [TestCase(512)]
    [TestCase(4_096)]
    [NonParallelizable]
    public void SqliteProducedWalIndexLookupsMatchAnIndependentWalScan(int pageSize)
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (var connection = new SqliteConnection(
                       $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False"))
            {
                connection.Open();
                Execute(connection, $"PRAGMA page_size={pageSize};");
                Execute(connection, "VACUUM;");
                Execute(connection, "PRAGMA journal_mode=WAL;");
                Execute(connection, "PRAGMA wal_autocheckpoint=0;");
                Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
                Execute(connection, "INSERT INTO data(value) VALUES ('one'), ('two'), ('three');");
                Execute(connection, "UPDATE data SET value = 'two-updated' WHERE id = 2;");
                Execute(connection, "CREATE INDEX data_value ON data(value);");

                using var wal = OpenWalCopy(databasePath);
                using var mapping = new MemorySharedMemoryMapping(ReadAllBytesSharingWithSqlite(databasePath + "-shm"));
                var index = new SqliteWalIndexSharedMemory(mapping);
                var region = index.ReadValidatedHeader(wal);
                var expectedFrames = FindLatestFramesByPage(wal, region.Header.MaximumFrame);

                wal.PageSize.Should().Be(pageSize);
                expectedFrames.Should().NotBeEmpty();
                foreach (var (pageNumber, expectedFrame) in expectedFrames)
                    index.FindFrame(wal, pageNumber).Should().Be(expectedFrame);

                index.FindFrame(wal, uint.MaxValue).Should().BeNull();
            }
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    [NonParallelizable]
    public void SqliteProducedWalIndexLookupCrossesTheFirstHashBlockBoundary()
    {
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (var connection = new SqliteConnection(
                       $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False"))
            {
                connection.Open();
                Execute(connection, "PRAGMA page_size=512;");
                Execute(connection, "VACUUM;");
                Execute(connection, "PRAGMA journal_mode=WAL;");
                Execute(connection, "PRAGMA wal_autocheckpoint=0;");
                Execute(connection, "PRAGMA synchronous=OFF;");
                Execute(connection, "CREATE TABLE data(value TEXT NOT NULL);");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO data VALUES ($value);";
                    var value = command.CreateParameter();
                    value.ParameterName = "$value";
                    command.Parameters.Add(value);
                    for (var row = 0; row <= SqliteWalIndexLayout.FirstBlockFrameCapacity; row++)
                    {
                        value.Value = row.ToString();
                        command.ExecuteNonQuery();
                    }
                }

                using var wal = OpenWalCopy(databasePath);
                using var mapping = new MemorySharedMemoryMapping(ReadAllBytesSharingWithSqlite(databasePath + "-shm"));
                var index = new SqliteWalIndexSharedMemory(mapping);
                var region = index.ReadValidatedHeader(wal);
                var lastFrame = wal.ReadFrame(region.Header.MaximumFrame).Header;

                region.Header.MaximumFrame.Should().BeGreaterThan(SqliteWalIndexLayout.FirstBlockFrameCapacity);
                mapping.Length.Should().BeGreaterThanOrEqualTo(SqliteWalIndexLayout.BlockSize * 2L);
                index.FindFrame(wal, lastFrame.PageNumber).Should().Be(region.Header.MaximumFrame);
            }
        }
        finally
        {
            DeleteWorkDirectory(workDirectory);
        }
    }

    [Test]
    public void StableHeaderReadRetriesADualHeaderPublicationInProgress()
    {
        using var wal = CreateWal((PageNumber: 1, DatabasePageCount: 1));
        var current = CreateIndexHeader(wal, changeCounter: 1).ToArray();
        var replacement = current.ToArray();
        WriteUInt32Native(replacement, offset: 8, value: 2);
        RewriteHeaderChecksum(replacement);
        var source = CreateHeaderRegion(current, replacement);
        using var mapping = new MemorySharedMemoryMapping(source);
        var rewroteFirstCopy = false;
        mapping.OnBarrier = () =>
        {
            if (rewroteFirstCopy)
                return;

            mapping.Write(position: 0, replacement);
            rewroteFirstCopy = true;
        };
        var index = new SqliteWalIndexSharedMemory(mapping);

        var region = index.ReadValidatedHeader(wal);

        region.Header.ChangeCounter.Should().Be(2);
        rewroteFirstCopy.Should().BeTrue();
        mapping.Operations.Count(static operation => operation == "barrier").Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void LookupRejectsAnOutOfBoundsHashReference()
    {
        using var wal = CreateWal((PageNumber: 7, DatabasePageCount: 7));
        var header = CreateIndexHeader(wal);
        var source = CreateHeaderRegion(header.ToArray(), header.ToArray());
        var hashSlot = (int)(unchecked(7U * 383U) & (SqliteWalIndexLayout.HashSlotCount - 1));
        WriteUInt16Native(
            source,
            checked((int)SqliteWalIndexLayout.GetHashSlotOffset(blockIndex: 0, hashSlotIndex: hashSlot)),
            value: checked((ushort)(SqliteWalIndexLayout.FirstBlockFrameCapacity + 1)));
        using var mapping = new MemorySharedMemoryMapping(source);
        var index = new SqliteWalIndexSharedMemory(mapping);

        Assert.Throws<InvalidDataException>(() => index.FindFrame(wal, pageNumber: 7));
    }

    [Test]
    public void LookupRejectsAHashEntryWhoseFrameDoesNotContainTheRequestedPage()
    {
        using var wal = CreateWal((PageNumber: 7, DatabasePageCount: 7));
        var header = CreateIndexHeader(wal);
        var source = CreateHeaderRegion(header.ToArray(), header.ToArray());
        const uint requestedPageNumber = 99;
        var hashSlot = (int)(unchecked(requestedPageNumber * 383U)
                             & (SqliteWalIndexLayout.HashSlotCount - 1));
        WriteUInt16Native(
            source,
            checked((int)SqliteWalIndexLayout.GetHashSlotOffset(blockIndex: 0, hashSlotIndex: hashSlot)),
            value: 1);
        WriteUInt32Native(
            source,
            checked((int)SqliteWalIndexLayout.GetPageNumberOffset(frameNumber: 1)),
            requestedPageNumber);
        using var mapping = new MemorySharedMemoryMapping(source);
        var index = new SqliteWalIndexSharedMemory(mapping);

        Assert.Throws<InvalidDataException>(() => index.FindFrame(wal, requestedPageNumber));
    }

    [Test]
    public void StableHeaderWithStaleCommittedFrameBoundaryIsRejected()
    {
        using var wal = CreateWal(
            (PageNumber: 1, DatabasePageCount: 1),
            (PageNumber: 2, DatabasePageCount: 2));
        var staleHeader = CreateIndexHeader(wal).ToArray();
        var firstFrame = wal.ReadFrame(frameNumber: 1).Header;
        WriteUInt32Native(staleHeader, offset: 16, value: 1);
        WriteUInt32Native(staleHeader, offset: 20, value: 1);
        WriteUInt32Native(staleHeader, offset: 24, firstFrame.Checksum1);
        WriteUInt32Native(staleHeader, offset: 28, firstFrame.Checksum2);
        RewriteHeaderChecksum(staleHeader);
        using var mapping = new MemorySharedMemoryMapping(CreateHeaderRegion(staleHeader, staleHeader));
        var index = new SqliteWalIndexSharedMemory(mapping);

        Assert.Throws<InvalidDataException>(() => index.ReadValidatedHeader(wal));
    }

    [TestCase("torn")]
    [TestCase("corrupt")]
    [NonParallelizable]
    public void CrossProcessMalformedPublicationIsRejectedFailClosed(string mutation)
    {
        RequirePhysicalMappingSupport();
        var workDirectory = CreateWorkDirectory();
        try
        {
            var databasePath = Path.Combine(workDirectory, "main.db");
            using (var connection = new SqliteConnection(
                       $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False"))
            {
                connection.Open();
                Execute(connection, "PRAGMA journal_mode=WAL;");
                Execute(connection, "PRAGMA wal_autocheckpoint=0;");
                Execute(connection, "CREATE TABLE data(value TEXT NOT NULL);");
                Execute(connection, "INSERT INTO data VALUES ('one');");

                using var wal = OpenWalCopy(databasePath);
                var fileSystem = (ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance;
                using var mapping = fileSystem.OpenSharedMemory(
                    databasePath + "-shm",
                    FileOpenMode.OpenExisting,
                    readOnly: true);
                using var worker = new CrossProcessHeaderMutationWorker(
                    workDirectory,
                    databasePath + "-shm",
                    mutation);
                var index = new SqliteWalIndexSharedMemory(mapping);

                Assert.Throws<InvalidDataException>(() => index.ReadValidatedHeader(wal));
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
    public void CrossProcessMalformedPublicationWorker()
    {
        var sharedMemoryPath = Environment.GetEnvironmentVariable("TURSO_WAL_INDEX_WORKER_PATH");
        if (string.IsNullOrEmpty(sharedMemoryPath))
            return;

        var mutation = Environment.GetEnvironmentVariable("TURSO_WAL_INDEX_WORKER_MUTATION")
            ?? throw new InvalidOperationException("The WAL-index worker is missing its mutation type.");
        var readyPath = Environment.GetEnvironmentVariable("TURSO_WAL_INDEX_WORKER_READY_PATH")
            ?? throw new InvalidOperationException("The WAL-index worker is missing its ready path.");
        var releasePath = Environment.GetEnvironmentVariable("TURSO_WAL_INDEX_WORKER_RELEASE_PATH")
            ?? throw new InvalidOperationException("The WAL-index worker is missing its release path.");

        using var mapping = ((ISqliteWalSharedMemoryFileSystem)PhysicalFileSystem.Instance)
            .OpenSharedMemory(sharedMemoryPath, FileOpenMode.OpenExisting);
        var header = new byte[SqliteWalIndexHeader.Size];
        mapping.Read(position: 0, header);
        switch (mutation)
        {
            case "torn":
                WriteUInt32Native(header, offset: 8, ReadUInt32Native(header, offset: 8) + 1);
                RewriteHeaderChecksum(header);
                mapping.Write(SqliteWalIndexHeader.Size, header);
                mapping.MemoryBarrier();
                break;
            case "corrupt":
                header[40] ^= 0x01;
                mapping.Write(SqliteWalIndexHeader.Size, header);
                mapping.MemoryBarrier();
                mapping.Write(position: 0, header);
                mapping.MemoryBarrier();
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mutation),
                    mutation,
                    "Unknown WAL-index worker mutation.");
        }

        File.WriteAllText(readyPath, string.Empty);
        WaitForFile(releasePath, TimeSpan.FromSeconds(60), "The WAL-index worker was not released.");
    }

    private static SqliteWalFile CreateWal(params (uint PageNumber, uint DatabasePageCount)[] frames)
    {
        var fileSystem = new InMemoryFileSystem();
        var wal = SqliteWalFile.Create(
            fileSystem,
            "main.db-wal",
            SqliteWalHeader.Create(
                pageSize: 512,
                salt1: 0x1122_3344,
                salt2: 0x5566_7788));
        foreach (var (pageNumber, databasePageCount) in frames)
        {
            wal.AppendFrame(
                pageNumber,
                new byte[wal.PageSize],
                databaseSizeInPages: databasePageCount);
        }

        return wal;
    }

    private static SqliteWalIndexHeader CreateIndexHeader(SqliteWalFile wal, uint changeCounter = 1)
    {
        var recovery = wal.ScanRecovery();
        if (recovery.LastCommittedFrameNumber <= 0 || recovery.LastCommittedFrameNumber > uint.MaxValue)
            throw new InvalidOperationException("The test WAL must contain a committed frame representable by the WAL-index.");

        var frame = wal.ReadFrame(recovery.LastCommittedFrameNumber).Header;
        var bytes = new byte[SqliteWalIndexHeader.Size];
        WriteUInt32Native(bytes, offset: 0, SqliteWalIndexHeader.CurrentFormatVersion);
        WriteUInt32Native(bytes, offset: 8, changeCounter);
        bytes[12] = 1;
        bytes[13] = wal.Header.ChecksumByteOrder == SqliteWalChecksumByteOrder.BigEndian ? (byte)1 : (byte)0;
        WriteUInt16Native(bytes, offset: 14, checked((ushort)wal.PageSize));
        WriteUInt32Native(bytes, offset: 16, checked((uint)recovery.LastCommittedFrameNumber));
        WriteUInt32Native(bytes, offset: 20, recovery.LastCommittedDatabaseSizeInPages);
        WriteUInt32Native(bytes, offset: 24, frame.Checksum1);
        WriteUInt32Native(bytes, offset: 28, frame.Checksum2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, sizeof(uint)), wal.Header.Salt1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, sizeof(uint)), wal.Header.Salt2);
        RewriteHeaderChecksum(bytes);
        return SqliteWalIndexHeader.Parse(bytes);
    }

    private static Dictionary<uint, uint> FindLatestFramesByPage(SqliteWalFile wal, uint maximumFrame)
    {
        var frames = new Dictionary<uint, uint>();
        for (uint frame = 1; frame <= maximumFrame; frame++)
            frames[wal.ReadFrame(frame).Header.PageNumber] = frame;

        return frames;
    }

    private static byte[] CreateHeaderRegion(byte[] firstHeader, byte[] secondHeader)
    {
        var source = new byte[SqliteWalIndexLayout.BlockSize];
        firstHeader.CopyTo(source, 0);
        secondHeader.CopyTo(source, SqliteWalIndexHeader.Size);
        return source;
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

    private static void WriteUInt16Native(byte[] destination, int offset, ushort value)
    {
        if (SqliteWalIndexHeader.NativeByteOrder == SqliteWalIndexByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, sizeof(ushort)), value);
        else
            BinaryPrimitives.WriteUInt16BigEndian(destination.AsSpan(offset, sizeof(ushort)), value);
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
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

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "sqlite-wal-index-shared-memory",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
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

    private sealed class MemorySharedMemoryMapping : ISqliteWalSharedMemoryMapping
    {
        private readonly object _gate = new();
        private byte[] _bytes;
        private bool _disposed;

        internal MemorySharedMemoryMapping(int length)
            : this(new byte[length])
        {
        }

        internal MemorySharedMemoryMapping(byte[] bytes)
        {
            _bytes = bytes.ToArray();
        }

        internal List<string> Operations { get; } = [];

        internal Action? OnBarrier { get; set; }

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
                Operations.Add($"write:{position}");
            }
        }

        public void MemoryBarrier()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                Operations.Add("barrier");
                OnBarrier?.Invoke();
            }
        }

        public byte[] ReadBytes(int position, int length)
        {
            var result = new byte[length];
            Read(position, result);
            return result;
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

    private sealed class CrossProcessHeaderMutationWorker : IDisposable
    {
        private readonly Process _process;
        private readonly string _releasePath;
        private readonly StringBuilder _output = new();
        private bool _released;

        internal CrossProcessHeaderMutationWorker(string workDirectory, string sharedMemoryPath, string mutation)
        {
            var token = Guid.NewGuid().ToString("N");
            var readyPath = Path.Combine(workDirectory, $"wal-index-ready-{token}");
            _releasePath = Path.Combine(workDirectory, $"wal-index-release-{token}");
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
                "--TestCaseFilter:FullyQualifiedName=Ahtola.Tests.SqliteWalIndexSharedMemoryTests."
                + nameof(CrossProcessMalformedPublicationWorker));
            startInfo.Environment["TURSO_WAL_INDEX_WORKER_PATH"] = sharedMemoryPath;
            startInfo.Environment["TURSO_WAL_INDEX_WORKER_MUTATION"] = mutation;
            startInfo.Environment["TURSO_WAL_INDEX_WORKER_READY_PATH"] = readyPath;
            startInfo.Environment["TURSO_WAL_INDEX_WORKER_RELEASE_PATH"] = _releasePath;

            _process = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("Failed to start the WAL-index mutation worker.");
            _process.OutputDataReceived += AppendOutput;
            _process.ErrorDataReceived += AppendOutput;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            WaitForFile(
                readyPath,
                TimeSpan.FromSeconds(60),
                "The WAL-index mutation worker did not publish its mutation.",
                _process,
                DrainOutput);
        }

        public void Dispose()
        {
            if (!_released)
            {
                File.WriteAllText(_releasePath, string.Empty);
                _released = true;
            }

            if (!_process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                _process.Kill(entireProcessTree: true);
                Assert.Fail(
                    "The WAL-index mutation worker did not exit within 60 seconds:"
                    + Environment.NewLine
                    + DrainOutput());
            }

            _process.WaitForExit();
            _process.ExitCode.Should().Be(0, $"worker output:{Environment.NewLine}{DrainOutput()}");
            _process.Dispose();
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
}
