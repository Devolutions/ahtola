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
        const long budget = 8192;
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
        var options = Options(new InMemoryFileSystem(), metrics, memoryLimitBytes: 8192);

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
        // At least one partition could not be fully loaded under this tight budget and had
        // to be answered by a fallback tier - either the lightweight key index (preferred,
        // cheaper) or, failing that, a raw scan. Which one engages is an implementation
        // detail of the adaptive partition/probe handling; that some fallback tier engaged
        // at all is the invariant this scenario is exercising.
        (metrics.HashPartitionIndexBuilds + metrics.HashPartitionFallbackScans)
            .Should().BeGreaterThan(0);
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
        var options = Options(new InMemoryFileSystem(), metrics, memoryLimitBytes: 8192);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Drain(statement).Select(Labels).Should().Equal(
            ("l1", "r1"),
            ("l2a", "r2"),
            ("l2b", "r2"));
        metrics.HashPartitionsCreated.Should().Be(16);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(8192);
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
        var options = Options(fileSystem, metrics, memoryLimitBytes: 8192);
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
        var options = Options(fileSystem, metrics, memoryLimitBytes: 8192);
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
            var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
                unknownTag,
                ref unknownPosition,
                metrics);

            Assert.Throws<InvalidDataException>(
                () => VdbeSpillRecordCodec.ReadValues(
                    unknownTag,
                    ref unknownPosition,
                    count: 1,
                    recordEnd,
                    metrics,
                    CancellationToken.None));
        }

        using var unknownBoolean = fileSystem.OpenFile(
            "unknown-boolean.spill",
            FileOpenMode.CreateNew);
        var booleanPosition = VdbeSpillRecordCodec.InitializeFile(
            unknownBoolean,
            VdbeSpillFileKind.HashMatchMap,
            metrics);
        VdbeSpillRecordCodec.WriteByte(unknownBoolean, ref booleanPosition, 2, metrics);
        booleanPosition = VdbeSpillRecordCodec.FileHeaderSize;

        Assert.Throws<InvalidDataException>(
            () => VdbeSpillRecordCodec.ReadBoolean(
                    unknownBoolean,
                    ref booleanPosition,
                    metrics,
                    "hash match-map"));
    }

    [TestCase(0x03)]
    [TestCase(0x04)]
    public void SharedCodecRejectsPayloadThatCrossesItsRecordBoundary(byte valueTag)
    {
        const int payloadLength = 64 * 1024;
        var metrics = new VdbeExecutionMetrics();
        var fileSystem = new InMemoryFileSystem();
        using var file = fileSystem.OpenFile(
            $"cross-record-{valueTag:X2}.spill",
            FileOpenMode.CreateNew);
        var position = VdbeSpillRecordCodec.InitializeFile(
            file,
            VdbeSpillFileKind.SorterRun,
            metrics);
        var recordStart = VdbeSpillRecordCodec.BeginRecord(ref position);
        VdbeSpillRecordCodec.WriteByte(file, ref position, valueTag, metrics);
        VdbeSpillRecordCodec.WriteInt32(file, ref position, payloadLength, metrics);
        VdbeSpillRecordCodec.CompleteRecord(file, recordStart, position, metrics);
        file.SetLength(checked(position + payloadLength));

        position = VdbeSpillRecordCodec.FileHeaderSize;
        var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(file, ref position, metrics);

        Assert.Throws<InvalidDataException>(
            () => VdbeSpillRecordCodec.ReadValues(
                file,
                ref position,
                count: 1,
                recordEnd,
                metrics,
                CancellationToken.None));
        position.Should().Be(recordEnd);
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
        var options = Options(fileSystem, metrics, memoryLimitBytes: 8192);
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
        const long budget = 8192;
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
        const long budget = 8192;
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
    public void HashSpillRejectsBudgetBelowFixedInfrastructureBeforeCreatingFiles()
    {
        const string temporaryDirectory = "hash-spill-tests";
        var infrastructureBytes = VdbeManagedFootprint.EstimateHashSpillInfrastructure(
            temporaryDirectory,
            partitionCount: 16,
            trackUnmatchedBuild: false);
        var budget = infrastructureBytes - 1;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            JoinProgram(
                [Row(-1, "left")],
                Enumerable.Range(0, 100).Select(static value => Row(value, $"r{value}")).ToArray(),
                VdbeJoinKind.Inner),
            Options(fileSystem, metrics, budget));

        var failure = Assert.Throws<VdbeMemoryLimitExceededException>(
            () => statement.StepResumable());

        failure!.LimitBytes.Should().Be(budget);
        failure.RequestedBytes.Should().Be(infrastructureBytes);
        metrics.SpillFilesCreated.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [Test]
    public void EmptyHashBuildSucceedsWhenInfrastructureReservationUsesTheWholeBudget()
    {
        const string temporaryDirectory = "hash-spill-tests";
        var budget = VdbeManagedFootprint.EstimateHashSpillInfrastructure(
            temporaryDirectory,
            partitionCount: 16,
            trackUnmatchedBuild: false);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            JoinProgram([Row(1, "left")], [], VdbeJoinKind.Inner),
            Options(fileSystem, metrics, budget));

        Drain(statement).Should().BeEmpty();

        metrics.PeakRetainedBytes.Should().Be(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.SpillFilesCreated.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [TestCase(VdbeJoinKind.Right, 0)]
    [TestCase(VdbeJoinKind.Full, 1)]
    public void EmptyUnmatchedHashBuildSucceedsWhenInfrastructureUsesTheWholeBudget(
        VdbeJoinKind kind,
        int expectedRowCount)
    {
        const string temporaryDirectory = "hash-spill-tests";
        var budget = VdbeManagedFootprint.EstimateHashSpillInfrastructure(
            temporaryDirectory,
            partitionCount: 16,
            trackUnmatchedBuild: true);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            JoinProgram([Row(1, "left")], [], kind),
            Options(fileSystem, metrics, budget));

        var rows = Drain(statement);

        rows.Should().HaveCount(expectedRowCount);
        if (kind == VdbeJoinKind.Full)
            Labels(rows[0]).Should().Be(("left", null));
        metrics.PeakRetainedBytes.Should().Be(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.SpillFilesCreated.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [Test]
    public void HashConstructionDeleteFailureRemainsRetryableForReset()
    {
        var faults = new DeterministicFaultInjector();
        var backing = new InMemoryFileSystem(faults);
        var fileSystem = new TrackingFileSystem(backing);
        var metrics = new VdbeExecutionMetrics();
        faults.FailOnOccurrence(
            FileSystemOperation.Write,
            occurrence: 3,
            message: "construction failed");
        for (var occurrence = 1; occurrence <= 64; occurrence++)
            faults.FailOnOccurrence(FileSystemOperation.Delete, occurrence);
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            JoinProgram(
                [Row(1, "left")],
                Enumerable.Range(0, 40).Select(static value => Row(value, $"r{value}")).ToArray(),
                VdbeJoinKind.Inner),
            Options(fileSystem, metrics, memoryLimitBytes: 8192));

        Assert.Catch<Exception>(() => statement.StepResumable());

        metrics.ActiveSpillFiles.Should().BeGreaterThan(0);
        metrics.CurrentRetainedBytes.Should().BeGreaterThan(0);
        fileSystem.Created.Where(backing.FileExists).Should().NotBeEmpty();

        faults.ClearScheduled();
        statement.Reset();

        metrics.ActiveSpillFiles.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        fileSystem.Created.Should().OnlyContain(path => !backing.FileExists(path));
        Drain(statement).Should().ContainSingle();
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
            Options(fileSystem, metrics, memoryLimitBytes: 8192));
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

    [Test]
    public void SmallInMemoryJoinNeverEngagesAdaptivePartitioningCounters()
    {
        // Documents the unchanged path: a join small enough to stay entirely in the
        // in-memory HashBuildBuffer never spills, so none of the new spill-partition
        // residency/split/index machinery is touched at all.
        var metrics = new VdbeExecutionMetrics();
        var program = JoinProgram(
            [Row(1, "l1"), Row(2, "l2")],
            [Row(1, "r1"), Row(2, "r2")],
            VdbeJoinKind.Inner);
        var options = Options(new InMemoryFileSystem(), metrics, memoryLimitBytes: 1024 * 1024);
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Drain(statement).Should().HaveCount(2);

        metrics.HashPartitionsCreated.Should().Be(0);
        metrics.HashPartitionLoads.Should().Be(0);
        metrics.HashPartitionFallbackScans.Should().Be(0);
        metrics.HashPartitionResidencyReuses.Should().Be(0);
        metrics.HashPartitionEvictions.Should().Be(0);
        metrics.HashPartitionSplits.Should().Be(0);
        metrics.HashPartitionIndexBuilds.Should().Be(0);
        metrics.HashPartitionIndexSeeks.Should().Be(0);
    }

    [Test]
    public void OversizedPartitionSplitsWithoutBreakingCorrectness()
    {
        // 2000 large-payload build rows spread over the fixed 16 top-level partitions make
        // every partition too large to fully load under a modest budget, forcing the
        // lazy adaptive split (turso-src/core/vdbe/hash_table.rs
        // choose_partition_count/MIN_PARTITIONS/MAX_PARTITIONS) rather than the old
        // pathological "rescan the whole partition on every probe" fallback.
        const long budget = 32768;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var payload = new string('x', 300);
        var right = Enumerable.Range(0, 2000)
            .Select(value => Row(value, payload))
            .ToArray();
        var probedKeys = new[] { 0, 500, 1000, 1500, 1999 };
        var left = probedKeys.Select(value => Row(value, $"l{value}")).ToArray();
        var program = JoinProgram(left, right, VdbeJoinKind.Inner);
        var options = Options(fileSystem, metrics, budget);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);
        var rows = Drain(statement);

        rows.Select(Labels).Should().Equal(
            probedKeys.Select(value => ((string?)$"l{value}", (string?)payload)));
        metrics.HashPartitionSplits.Should().BeGreaterThan(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void InterleavedProbesBetweenTwoDistinctPartitionsAvoidReloadThrashing()
    {
        // "N3" and "N1" are precomputed (via the same StableHash/GetPartition the compiled
        // hash join runtime uses) to land in two different top-level partitions, so this
        // test deterministically exercises cross-partition interleaving instead of guessing
        // at runtime bucketing. The padding rows (from other partitions) exist purely to
        // force the build side to spill under the given budget.
        const long budget = 16384;
        var metrics = new VdbeExecutionMetrics();
        var padding = new[]
        {
            205, 208, 211, 213, 215, 227, 228, 231, 233, 237, 241, 248, 251, 255, 257, 258,
            263, 273, 275, 277, 280, 282, 284, 294, 299, 304, 309, 310, 312, 314, 326, 329,
            330, 332, 336, 340, 349, 350, 354, 356, 359, 362, 372, 374, 376, 381, 383, 385,
            395, 398, 403, 413, 415, 417, 421, 428, 431, 435, 437, 438,
        };
        var right = new[] { Row(3, "r3"), Row(1, "r1") }
            .Concat(padding.Select(value => Row(value, $"pad{value}")))
            .ToArray();
        const int cycles = 50;
        var left = Enumerable.Range(0, cycles)
            .SelectMany(i => new[] { Row(3, $"p{i}a"), Row(1, $"p{i}b") })
            .ToArray();
        var program = JoinProgram(left, right, VdbeJoinKind.Inner);
        var options = Options(new InMemoryFileSystem(), metrics, budget);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);
        var rows = Drain(statement);

        var expected = Enumerable.Range(0, cycles)
            .SelectMany(i => new (string? Left, string? Right)[]
            {
                ($"p{i}a", "r3"),
                ($"p{i}b", "r1"),
            });
        rows.Select(Labels).Should().Equal(expected);

        // Only 2 distinct partitions are ever probed; a bounded residency cache that can
        // hold both at once should load each at most once across all 100 probes, not once
        // per probe as the old single-slot "reload whenever the hash changes" design did.
        metrics.HashPartitionLoads.Should().BeLessThanOrEqualTo(2);
        metrics.HashPartitionResidencyReuses.Should().BeGreaterThan(0);
        metrics.HashPartitionFallbackScans.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
    }

    [Test]
    public void CyclicProbesExceedingResidentCapacityStillAvoidRawRescans()
    {
        // Documents a known, bounded limitation rather than an eliminated one: no
        // recency-based cache, however designed, can avoid reloading when a cyclic working
        // set genuinely exceeds its capacity. Here, 80 build rows spread naturally over all
        // 16 top-level partitions (~5 each) are probed round-robin across 8 distinct
        // partitions, under a budget deliberately sized so the 8 corresponding lightweight
        // key indexes cannot all stay resident at once (each index alone is comfortably
        // affordable; several at once are not - see the budget/margin comment below). What
        // the added index tier changes is the *cost* of each reload: instead of the old
        // per-probe full-partition rescan, a miss here still only pays for a cheap
        // header-only index rebuild (turso-src/core/vdbe/hash_table.rs's grace-style
        // adaptive handling caps unnecessary partition reloads/rescans; it does not claim
        // to make a genuinely over-capacity cyclic pattern free).
        const long budget = 10240;
        var metrics = new VdbeExecutionMetrics();
        var payload = new string('y', 40);
        var right = Enumerable.Range(0, 80)
            .Select(value => Row(value, $"r{value}-{payload}"))
            .ToArray();
        // Precomputed (via the same StableHash/GetPartition the runtime uses) to be 8
        // pairwise-distinct top-level partitions among keys 0..79.
        var representative = new[] { 3, 20, 1, 8, 7, 24, 5, 26 };
        const int cycles = 5;
        var left = Enumerable.Range(0, cycles)
            .SelectMany(_ => representative.Select(key => Row(key, $"p{key}")))
            .ToArray();
        var program = JoinProgram(left, right, VdbeJoinKind.Inner);
        var options = Options(new InMemoryFileSystem(), metrics, budget);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);
        var rows = Drain(statement);

        var expected = Enumerable.Range(0, cycles)
            .SelectMany(_ => representative.Select(key =>
                ((string?)$"p{key}", (string?)$"r{key}-{payload}")));
        rows.Select(Labels).Should().Equal(expected);

        metrics.HashPartitionSplits.Should().Be(0);
        metrics.HashPartitionsCreated.Should().Be(16);
        // The working set (8 distinct partitions) exceeds what fits at once, so repeated
        // index rebuilds are expected (thrashing is not eliminated): more rebuilds than one
        // per distinct partition would mean at least one had to be re-established.
        metrics.HashPartitionIndexBuilds.Should().BeGreaterThan(representative.Length);
        // ...but every one of those reloads must be cheap header-only index rebuilds, never
        // the old full-partition raw rescan.
        metrics.HashPartitionFallbackScans.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
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
            Options(fileSystem, metrics, memoryLimitBytes: 8192));

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
