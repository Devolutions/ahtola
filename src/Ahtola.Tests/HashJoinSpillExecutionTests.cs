using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class HashJoinSpillExecutionTests
{
    [Test]
    public void EquiJoinSpillsWithinBudgetAndCleansEveryTemporaryFile()
    {
        const long budget = 1024;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var right = Enumerable.Range(0, 200)
            .Select(static value => Row(value, $"r{value:D3}"))
            .ToArray();
        var left = Enumerable.Range(0, 200)
            .Reverse()
            .Select(static value => Row(value, $"l{value:D3}"))
            .ToArray();
        var program = JoinProgram(left, right, VdbeJoinKind.Inner);
        var options = Options(fileSystem, metrics, budget);

        using (var statement = ResumableStatement.CreateWithExecutionOptions(program, options))
        {
            var rows = Drain(statement);
            rows.Should().HaveCount(200);
            rows.Select(static row => row[0].AsInteger())
                .Should().Equal(Enumerable.Range(0, 200).Reverse().Select(static value => (long)value));
            metrics.CurrentRetainedBytes.Should().Be(0);
            metrics.CurrentRetainedRows.Should().Be(0);
        }

        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.PeakRetainedRows.Should().BeGreaterThan(0);
        metrics.HashPartitionsCreated.Should().Be(16);
        metrics.HashPartitionScans.Should().BeGreaterThan(0);
        metrics.SpillBytesWritten.Should().BeGreaterThan(0);
        metrics.SpillBytesRead.Should().BeGreaterThan(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
        fileSystem.Created.Should().OnlyContain(path => !fileSystem.FileExists(path));
    }

    [Test]
    public void SpilledFullJoinPreservesDuplicatesNullsAndUnmatchedBuildOrder()
    {
        var metrics = new VdbeExecutionMetrics();
        var left =
            new[] { Row(1, "l1"), Row(2, "l2"), Row(4, "l4"), Row(null, "ln") };
        var right =
            new[] { Row(2, "r2a"), Row(3, "r3"), Row(2, "r2b"), Row(null, "rn") };
        var program = JoinProgram(left, right, VdbeJoinKind.Full);
        var options = Options(new InMemoryFileSystem(), metrics, memoryLimitBytes: 1024);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Drain(statement).Select(Labels).Should().Equal(
            ("l1", null),
            ("l2", "r2a"),
            ("l2", "r2b"),
            ("l4", null),
            ("ln", null),
            (null, "r3"),
            (null, "rn"));
        metrics.HashPartitionsCreated.Should().Be(16);
        metrics.HashPartitionLoads.Should().BeGreaterThan(0);
        metrics.HashPartitionFallbackScans.Should().BeGreaterThan(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
    }

    [Test]
    public void SpilledBuildLeftInnerJoinKeepsProbeThenBuildInsertionOrder()
    {
        var metrics = new VdbeExecutionMetrics();
        var left = new[] { Row(2, "l2a"), Row(1, "l1"), Row(2, "l2b") };
        var right = new[] { Row(1, "r1"), Row(2, "r2") };
        var program = JoinProgram(
            left,
            right,
            VdbeJoinKind.Inner,
            hashBuildRight: false);
        var options = Options(new InMemoryFileSystem(), metrics, memoryLimitBytes: 1024);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Drain(statement).Select(Labels).Should().Equal(
            ("l1", "r1"),
            ("l2a", "r2"),
            ("l2b", "r2"));
        metrics.HashPartitionsCreated.Should().Be(16);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(1024);
    }

    [Test]
    public void DisabledSpillFailsAtBudgetWithoutCreatingHeapBackedFiles()
    {
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var program = JoinProgram(
            [Row(1, "left")],
            Enumerable.Range(0, 20).Select(static value => Row(value, new string('x', 32))).ToArray(),
            VdbeJoinKind.Inner);
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: 128,
            temporaryDirectory: "hash-memory-only",
            allowTemporaryFileSpill: false,
            metrics: metrics);
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Assert.Throws<VdbeMemoryLimitExceededException>(() => statement.StepResumable());

        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        metrics.SpillFilesCreated.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [Test]
    public void CancellationFaultsJoinAndReleasesSpillAndReservations()
    {
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var program = JoinProgram(
            [Row(1, "left")],
            Enumerable.Range(0, 40).Select(static value => Row(value, $"r{value}")).ToArray(),
            VdbeJoinKind.Inner,
            condition: (_, _, _) =>
            {
                cancellation.Cancel();
                return true;
            });
        var options = Options(fileSystem, metrics, memoryLimitBytes: 1024);
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Assert.Throws<OperationCanceledException>(() => statement.StepResumable(cancellation.Token));

        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);

        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [TestCase(FileSystemOperation.Write, 17)]
    [TestCase(FileSystemOperation.Read, 1)]
    public void SpillIoFailureFaultsJoinAndCleansFiles(
        FileSystemOperation operation,
        long occurrence)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new TrackingFileSystem(new InMemoryFileSystem(faults));
        var metrics = new VdbeExecutionMetrics();
        var right = Enumerable.Range(0, 40).Select(static value => Row(value, $"r{value}")).ToArray();
        var program = JoinProgram([Row(1, "left")], right, VdbeJoinKind.Inner);
        var options = Options(fileSystem, metrics, memoryLimitBytes: 1024);
        faults.FailOnOccurrence(operation, occurrence);
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Assert.Throws<IOException>(() => statement.StepResumable());

        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.CurrentRetainedRows.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);

        faults.ClearScheduled();
        statement.Reset();
        Drain(statement).Should().ContainSingle();

        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void IdenticalInputsProduceIdenticalPartitionBytes()
    {
        var first = CaptureSpillPayloads();
        var second = CaptureSpillPayloads();

        first.Should().HaveSameCount(second);
        for (var index = 0; index < first.Count; index++)
            first[index].Should().Equal(second[index]);
    }

    [Test]
    public void SharedCodecRejectsTruncatedRecordsAndUnknownValueTags()
    {
        var metrics = new VdbeExecutionMetrics();
        var fileSystem = new InMemoryFileSystem();
        using (var truncated = fileSystem.OpenFile("truncated.spill", FileOpenMode.CreateNew))
        {
            var position = VdbeSpillRecordCodec.InitializeFile(
                truncated,
                VdbeSpillFileKind.SorterRun,
                metrics);
            var start = VdbeSpillRecordCodec.BeginRecord(ref position);
            VdbeSpillRecordCodec.WriteValues(
                truncated,
                ref position,
                [SqlValue.Text("payload")],
                metrics);
            VdbeSpillRecordCodec.CompleteRecord(truncated, start, position, metrics);
            truncated.SetLength(truncated.Length - 1);

            position = VdbeSpillRecordCodec.FileHeaderSize;
            Assert.Throws<EndOfStreamException>(
                () => VdbeSpillRecordCodec.ReadRecordEnd(truncated, ref position, metrics));
        }

        foreach (var reservedTag in new byte[] { 0x05, 0x13, 0x80, 0x84, 0x93, 0xFF })
        {
            using var unknownTag = fileSystem.OpenFile(
                $"unknown-tag-{reservedTag:X2}.spill",
                FileOpenMode.CreateNew);
            var unknownPosition = VdbeSpillRecordCodec.InitializeFile(
                unknownTag,
                VdbeSpillFileKind.SorterRun,
                metrics);
            var unknownStart = VdbeSpillRecordCodec.BeginRecord(ref unknownPosition);
            VdbeSpillRecordCodec.WriteByte(unknownTag, ref unknownPosition, reservedTag, metrics);
            VdbeSpillRecordCodec.CompleteRecord(
                unknownTag,
                unknownStart,
                unknownPosition,
                metrics);
            unknownPosition = VdbeSpillRecordCodec.FileHeaderSize;
            VdbeSpillRecordCodec.ReadRecordEnd(unknownTag, ref unknownPosition, metrics);

            Assert.Throws<InvalidDataException>(
                () => VdbeSpillRecordCodec.ReadValues(
                    unknownTag,
                    ref unknownPosition,
                    count: 1,
                    metrics,
                    CancellationToken.None));
        }
    }

    [Test]
    public void PredicateFailureRemainsPrimaryWhenCleanupAlsoFails()
    {
        var primary = new InvalidOperationException("predicate failed");
        var faults = new DeterministicFaultInjector();
        var fileSystem = new TrackingFileSystem(new InMemoryFileSystem(faults));
        var metrics = new VdbeExecutionMetrics();
        var program = JoinProgram(
            [Row(1, "left")],
            Enumerable.Range(0, 40).Select(static value => Row(value, $"r{value}")).ToArray(),
            VdbeJoinKind.Inner,
            condition: (_, _, _) => throw primary);
        var options = Options(fileSystem, metrics, memoryLimitBytes: 1024);
        faults.FailNext(FileSystemOperation.Delete, "cleanup failed");
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        var failure = Assert.Throws<AggregateException>(() => statement.StepResumable());

        failure!.InnerExceptions[0].Should().BeSameAs(primary);
        failure.InnerExceptions.Should().Contain(exception =>
            exception is IOException && exception.Message == "cleanup failed");
        statement.State.Should().Be(ResumableStatementState.Faulted);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Created.Should().OnlyContain(path => !fileSystem.FileExists(path));
    }

    [Test]
    public void NestedSubprogramSharesTheParentExecutionBudget()
    {
        const long budget = 1024;
        var metrics = new VdbeExecutionMetrics();
        var fileSystem = new TrackingFileSystem();
        var child = new VdbeSubprogram(JoinProgram(
            [Row(1, "left")],
            Enumerable.Range(0, 20).Select(static value => Row(value, $"r{value}")).ToArray(),
            VdbeJoinKind.Inner));
        VdbeRowComparer comparer = (_, _) => 0;
        VdbeInstruction[] parentInstructions =
        [
            new OpenSorterInstruction(new Sorter(0), comparer, 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text(new string('x', 64))),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
            new ProgramInstruction([], child),
            new CloseSorterInstruction(new Sorter(0)),
            new HaltInstruction(),
        ];
        var parent = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            parentInstructions,
            sorterCount: 1);
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            parent,
            Options(fileSystem, metrics, budget));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.HashPartitionsCreated.Should().Be(16);
        metrics.ActiveSpillFiles.Should().Be(0);
    }

    [Test]
    public void OversizedSkewBuildRecordFailsBeforeItCanBeRetainedOrSpilled()
    {
        const long budget = 2048;
        var metrics = new VdbeExecutionMetrics();
        var fileSystem = new TrackingFileSystem();
        var right = Enumerable.Range(0, 8)
            .Select(static value => Row(1, $"small-{value}"))
            .Append(Row(1, new string('x', 4096)))
            .ToArray();
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            JoinProgram([Row(1, "left")], right, VdbeJoinKind.Inner),
            Options(fileSystem, metrics, budget));

        var failure = Assert.Throws<VdbeMemoryLimitExceededException>(
            () => statement.StepResumable());

        failure!.LimitBytes.Should().Be(budget);
        failure.RequestedBytes.Should().BeGreaterThan(budget);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Created.Should().OnlyContain(path => !fileSystem.FileExists(path));
    }

    [Test]
    public void DeleteFailureLeavesHashCleanupRetryableForReset()
    {
        var faults = new DeterministicFaultInjector();
        var backing = new InMemoryFileSystem(faults);
        var fileSystem = new TrackingFileSystem(backing);
        var metrics = new VdbeExecutionMetrics();
        var right = Enumerable.Range(0, 40)
            .Select(static value => Row(value, $"r{value}"))
            .ToArray();
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            JoinProgram([Row(-1, "left")], right, VdbeJoinKind.Inner),
            Options(fileSystem, metrics, memoryLimitBytes: 1024));
        for (var occurrence = 1; occurrence <= 34; occurrence++)
            faults.FailOnOccurrence(FileSystemOperation.Delete, occurrence);

        Assert.Catch<Exception>(() => Drain(statement));

        metrics.ActiveSpillFiles.Should().Be(1);
        fileSystem.Created.Where(backing.FileExists).Should().ContainSingle();

        faults.ClearScheduled();
        statement.Reset();

        metrics.ActiveSpillFiles.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        fileSystem.Created.Should().OnlyContain(path => !backing.FileExists(path));
    }

    private static VdbeExecutionOptions Options(
        IFileSystem fileSystem,
        VdbeExecutionMetrics metrics,
        long memoryLimitBytes) =>
        new(
            fileSystem,
            sorterMemoryLimitBytes: memoryLimitBytes,
            temporaryDirectory: "hash-spill-tests",
            metrics: metrics);

    private static VdbeProgram JoinProgram(
        IReadOnlyList<SqlValue[]> left,
        IReadOnlyList<SqlValue[]> right,
        VdbeJoinKind kind,
        bool hashBuildRight = true,
        VdbeJoinCondition? condition = null)
    {
        var equiProbe = new VdbeJoinEquiProbe(Key, Key);
        var root = new VdbeJoinOperatorPlan(
            new VdbeJoinScanPlan("left", 2, new VdbeCursorSource(left)),
            new VdbeJoinScanPlan("right", 2, new VdbeCursorSource(right)),
            kind,
            condition,
            equiProbe,
            hashBuildRight);
        var plan = new VdbeJoinPlan(root, $"{kind} spill test");
        VdbeInstruction[] instructions =
        [
            new OpenJoinCursorInstruction(new Cursor(0), plan),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(8)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ColumnInstruction(new Cursor(0), 1, new Register(1)),
            new ColumnInstruction(new Cursor(0), 2, new Register(2)),
            new ColumnInstruction(new Cursor(0), 3, new Register(3)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 4)),
            new NextInstruction(new Cursor(0), new ProgramCounter(2)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        return new VdbeProgram(registerCount: 4, cursorCount: 1, instructions);
    }

    private static string? Key(VdbeJoinRow row) =>
        row.Values[0].Kind == SqlValueKind.Null
            ? null
            : "N" + row.Values[0].AsInteger();

    private static SqlValue[] Row(long? key, string label) =>
        [key.HasValue ? SqlValue.Integer(key.Value) : SqlValue.Null, SqlValue.Text(label)];

    private static (string? Left, string? Right) Labels(SqlValue[] row) =>
        (TextOrNull(row[1]), TextOrNull(row[3]));

    private static string? TextOrNull(SqlValue value) =>
        value.Kind == SqlValueKind.Null ? null : value.AsText();

    private static List<SqlValue[]> Drain(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Done)
                return rows;
            result.Should().Be(ResumableStatementStepResult.Row);
            rows.Add([.. statement.CurrentRow!]);
        }
    }

    private static IReadOnlyList<byte[]> CaptureSpillPayloads()
    {
        var fileSystem = new TrackingFileSystem(captureDeletedPayloads: true);
        var metrics = new VdbeExecutionMetrics();
        var program = JoinProgram(
            Enumerable.Range(0, 12).Select(static value => Row(value, $"l{value}")).ToArray(),
            Enumerable.Range(0, 40).Select(static value => Row(value % 12, $"r{value}")).ToArray(),
            VdbeJoinKind.Inner);
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            program,
            Options(fileSystem, metrics, memoryLimitBytes: 1024));

        Drain(statement);
        return fileSystem.DeletedPayloads;
    }

    private sealed class TrackingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner;
        private readonly bool _captureDeletedPayloads;

        public TrackingFileSystem(
            IFileSystem? inner = null,
            bool captureDeletedPayloads = false)
        {
            _inner = inner ?? new InMemoryFileSystem();
            _captureDeletedPayloads = captureDeletedPayloads;
        }

        public List<string> Created { get; } = [];

        public List<string> Deleted { get; } = [];

        public List<byte[]> DeletedPayloads { get; } = [];

        public bool FileExists(string path) => _inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            var file = _inner.OpenFile(path, mode, readOnly);
            if (mode == FileOpenMode.CreateNew)
                Created.Add(path);
            return file;
        }

        public void DeleteFile(string path)
        {
            if (_captureDeletedPayloads && _inner.FileExists(path))
                DeletedPayloads.Add(ReadAll(path));
            Deleted.Add(path);
            _inner.DeleteFile(path);
        }

        private byte[] ReadAll(string path)
        {
            using var file = _inner.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
            var bytes = new byte[checked((int)file.Length)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = file.Read(read, bytes.AsSpan(read));
                if (count <= 0)
                    throw new EndOfStreamException($"Temporary file '{path}' ended while being captured.");
                read += count;
            }

            return bytes;
        }
    }
}
