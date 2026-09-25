using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

// Compiler-output and execution coverage for the buffered-window lowering
// (BufferedWindowProgramBuilder) and the window-buffer opcode family it emits. These tests assert the
// bytecode shape and jump layout, run the programs through the resumable state machine to confirm real
// observable rows, and pin the runtime contracts the buffer enforces: rows are snapshotted on insert, the
// evaluator runs exactly once over the whole buffer, its result shape is validated, an empty buffer skips
// the drain loop, and Reset replays the whole pipeline from scratch.
public class BufferedWindowProgramBuilderDirectTests
{
    [Test]
    public void BuildEmitsTheIngestComputeDrainPipelineWithoutASorter()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber);

        program.RegisterCount.Should().Be(5);
        program.CursorCount.Should().Be(1);
        program.SorterCount.Should().Be(0);
        program.WindowBufferCount.Should().Be(1);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenWindowBuffer,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Column,
            VdbeOpcode.WindowBufferInsert,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.WindowBufferCompute,
            VdbeOpcode.WindowBufferData,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.WindowBufferNext,
            VdbeOpcode.CloseWindowBuffer,
            VdbeOpcode.Halt);

        // The empty-table and empty-buffer paths both land on the close block, so no resource is
        // released twice and none is left open at the halt.
        var closeAddress = program.Instructions.Count - 2;
        program.Instructions.OfType<RewindCursorInstruction>().Single()
            .EmptyTarget.Offset.Should().Be(8);
        program.Instructions.OfType<WindowBufferComputeInstruction>().Single()
            .EmptyTarget.Offset.Should().Be(closeAddress);
        program.Instructions.OfType<WindowBufferNextInstruction>().Single()
            .LoopTarget.Offset.Should().Be(9);
    }

    [Test]
    public void OrderedBuildRoutesTheComputedRecordsThroughASorterBeforeEmitting()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber,
            orderComparer: (left, right) => right[0].AsInteger().CompareTo(left[0].AsInteger()));

        program.SorterCount.Should().Be(1);
        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenWindowBuffer,
            VdbeOpcode.OpenSorter,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Column,
            VdbeOpcode.WindowBufferInsert,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.WindowBufferCompute,
            VdbeOpcode.WindowBufferData,  // gather
            VdbeOpcode.SorterInsert,
            VdbeOpcode.WindowBufferNext,
            VdbeOpcode.SorterSort,
            VdbeOpcode.SorterData,        // drain
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.SorterNext,
            VdbeOpcode.CloseSorter,
            VdbeOpcode.CloseWindowBuffer,
            VdbeOpcode.Halt);

        // The window values are computed in buffer (scan) order, then the sorter reverses the emission.
        var rows = Run(program, [[SqlValue.Integer(1), SqlValue.Integer(10)], [SqlValue.Integer(2), SqlValue.Integer(20)]]);
        rows.Select(row => row[0].AsInteger()).Should().Equal(2, 1);
        rows.Select(row => row[1].AsInteger()).Should().Equal(2, 1);
    }

    [Test]
    public void ComputedOutputsSeeTheWholeRowAndWindowRecord()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs:
            [
                BufferedWindowOutput.ForConstant(SqlValue.Text("tag")),
                BufferedWindowOutput.ForComputed(new VdbeScalarFunction
                {
                    Name = "window projection",
                    Invoke = record => SqlValue.Integer(
                        record[1].AsInteger() * 100 + record[2].AsInteger()),
                }),
            ],
            windowEvaluator: RunningRowNumber);

        var rows = Run(program, [[SqlValue.Integer(1), SqlValue.Integer(7)], [SqlValue.Integer(2), SqlValue.Integer(9)]]);
        rows.Select(row => row[0].AsText()).Should().Equal("tag", "tag");
        rows.Select(row => row[1].AsInteger()).Should().Equal(701, 902);
    }

    [Test]
    public void PredicateFiltersRowsBeforeTheyReachTheBuffer()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber,
            predicate: row => row[1].AsInteger() >= 20);

        var rows = Run(program,
            [
                [SqlValue.Integer(1), SqlValue.Integer(10)],
                [SqlValue.Integer(2), SqlValue.Integer(20)],
                [SqlValue.Integer(3), SqlValue.Integer(30)],
            ]);
        rows.Select(row => row[0].AsInteger()).Should().Equal(2, 3);
        rows.Select(row => row[1].AsInteger()).Should().Equal(1, 2);
    }

    [Test]
    public void AnEmptyScanComputesNothingAndEmitsNoRows()
    {
        var invocations = 0;
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: rows =>
            {
                invocations++;
                return RunningRowNumber(rows);
            });

        Run(program, []).Should().BeEmpty();
        // The buffer is still computed (over zero rows), so an evaluator with no input raises nothing.
        invocations.Should().Be(1);
    }

    [Test]
    public void ResetReplaysTheWholePipelineIncludingTheWindowPass()
    {
        var invocations = 0;
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: rows =>
            {
                invocations++;
                return RunningRowNumber(rows);
            });

        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(1), SqlValue.Integer(10) },
            new[] { SqlValue.Integer(2), SqlValue.Integer(20) },
        };
        using var runtime = new ResumableStatement(program, [new VdbeCursorSource(rows)]);

        Drain(runtime).Should().HaveCount(2);
        invocations.Should().Be(1);

        runtime.Reset();
        rows.Add([SqlValue.Integer(3), SqlValue.Integer(30)]);
        Drain(runtime).Should().HaveCount(3);
        invocations.Should().Be(2);
    }

    [Test]
    public void BufferedRowsAreSnapshottedSoLaterRegisterWritesCannotDisturbThem()
    {
        SqlValue[][]? observed = null;
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: rows =>
            {
                observed = [.. rows.Select(row => row.ToArray())];
                return RunningRowNumber(rows);
            });

        Run(program, [[SqlValue.Integer(1), SqlValue.Integer(10)], [SqlValue.Integer(2), SqlValue.Integer(20)]]);

        observed.Should().NotBeNull();
        observed!.Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void AWindowEvaluatorReturningTheWrongShapeFailsLoudly()
    {
        var shortTuple = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            windowCount: 2,
            outputs: [BufferedWindowOutput.ForWindow(1)],
            windowEvaluator: rows => [.. rows.Select(_ => new[] { SqlValue.Integer(1) })]);
        var wrongWidth = () => Run(shortTuple, [[SqlValue.Integer(1)]]);
        wrongWidth.Should().Throw<InvalidOperationException>()
            .WithMessage("*1-wide window tuple*2 window functions*");

        var missingRow = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: _ => []);
        var wrongCount = () => Run(missingRow, [[SqlValue.Integer(1)]]);
        wrongCount.Should().Throw<InvalidOperationException>()
            .WithMessage("*0 window tuples for 1 buffered rows*");
    }

    [Test]
    public void BuildRejectsShapesItCannotRepresent()
    {
        var noColumns = () => BufferedWindowProgramBuilder.Build(
            "t", 0, 1, [BufferedWindowOutput.ForWindow(0)], RunningRowNumber);
        noColumns.Should().Throw<ArgumentOutOfRangeException>();

        var noWindows = () => BufferedWindowProgramBuilder.Build(
            "t", 1, 0, [BufferedWindowOutput.ForColumn(0)], RunningRowNumber);
        noWindows.Should().Throw<ArgumentOutOfRangeException>();

        var noOutputs = () => BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [], RunningRowNumber);
        noOutputs.Should().Throw<ArgumentException>();

        var columnOutOfRange = () => BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForColumn(3)], RunningRowNumber);
        columnOutOfRange.Should().Throw<ArgumentException>()
            .WithMessage("*column 3*1 columns*");

        var windowOutOfRange = () => BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(2)], RunningRowNumber);
        windowOutOfRange.Should().Throw<ArgumentException>()
            .WithMessage("*window 2*1 window functions*");

        var nullEvaluator = () => BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)], null!);
        nullEvaluator.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ProgramValidationPinsTheWindowBufferResourceContract()
    {
        VdbeWindowEvaluator evaluator = RunningRowNumber;
        var buffer = new WindowBuffer(0);

        var undeclared = () => new VdbeProgram(
            1,
            0,
            [
                new OpenWindowBufferInstruction(buffer, 1, 1, evaluator),
                new HaltInstruction(),
            ]);
        undeclared.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*0 window buffers*");

        var usedBeforeOpen = () => new VdbeProgram(
            2,
            0,
            [
                new WindowBufferDataInstruction(buffer, new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ],
            windowBufferCount: 1);
        usedBeforeOpen.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*before opening it*");

        var wrongInsertWidth = () => new VdbeProgram(
            2,
            0,
            [
                new OpenWindowBufferInstruction(buffer, 1, 1, evaluator),
                new WindowBufferInsertInstruction(buffer, new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ],
            windowBufferCount: 1);
        wrongInsertWidth.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*scanned row is 1 columns wide*");

        var wrongDataWidth = () => new VdbeProgram(
            3,
            0,
            [
                new OpenWindowBufferInstruction(buffer, 1, 1, evaluator),
                new WindowBufferDataInstruction(buffer, new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ],
            windowBufferCount: 1);
        wrongDataWidth.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*row-and-window record is 2 columns wide*");

        var openedTwice = () => new VdbeProgram(
            2,
            0,
            [
                new OpenWindowBufferInstruction(buffer, 1, 1, evaluator),
                new OpenWindowBufferInstruction(buffer, 1, 1, evaluator),
                new HaltInstruction(),
            ],
            windowBufferCount: 1);
        openedTwice.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*twice*");
    }

    [Test]
    public void ExplainDescribesEveryWindowBufferOpcode()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber);

        var described = VdbeExplain.Describe(program);
        described.Should().HaveCount(program.Instructions.Count);
        string Comment(string opcode) => described.First(row => row[1].AsText() == opcode)[6].AsText();
        Comment("OpenWindowBuffer").Should().Be("open window buffer 0 (2 cols, 1 windows)");
        Comment("WindowBufferInsert").Should().Be("window buffer 0 insert r[0..1]");
        Comment("WindowBufferCompute").Should().StartWith("compute window buffer 0, goto ");
        Comment("WindowBufferData").Should().Be("r[0..2]=window buffer 0 data");
        Comment("WindowBufferNext").Should().StartWith("next window buffer 0, goto ");
        Comment("CloseWindowBuffer").Should().Be("close window buffer 0");
    }

    [Test]
    public void LimitOffsetGatingComposesOntoABufferedWindowProgram()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);
        gated.WindowBufferCount.Should().Be(1);
        gated.Instructions.Select(instruction => instruction.Opcode)
            .Should().Contain(VdbeOpcode.OffsetGate).And.Contain(VdbeOpcode.LimitGate);

        var rows = Run(gated,
            [
                [SqlValue.Integer(1), SqlValue.Integer(10)],
                [SqlValue.Integer(2), SqlValue.Integer(20)],
                [SqlValue.Integer(3), SqlValue.Integer(30)],
                [SqlValue.Integer(4), SqlValue.Integer(40)],
            ]);
        rows.Select(row => row[0].AsInteger()).Should().Equal(2, 3);
        rows.Select(row => row[1].AsInteger()).Should().Equal(2, 3);
    }

    [Test]
    public void WindowBufferSpillsScannedRowsAndComputesWithoutReloading()
    {
        const string temporaryDirectory = "window-buffer-spill-tests";
        var sampleRow = Enumerable.Range(0, 10)
            .Select(static value => SqlValue.Integer(value))
            .ToArray();
        var rowBytes = VdbeManagedFootprint.EstimateSorterRow(sampleRow);
        var rowCount = 16;
        var firstListBytes = VdbeManagedFootprint.EstimateReferenceListStorage(
            VdbeManagedFootprint.GetListCapacityForCount(0, 1));
        var infrastructure = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory);
        var tupleBytes = VdbeManagedFootprint.EstimateSorterRow([SqlValue.Integer(0)]);
        var cachedRowBytes = VdbeManagedFootprint.EstimateSorterRowFromEncodedLength(
            sampleRow.Length * (1 + sizeof(long)), sampleRow.Length);
        var budget = checked(rowBytes + firstListBytes + infrastructure
            + (tupleBytes * rowCount) + cachedRowBytes + 64);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory,
            metrics: metrics);
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: sampleRow.Length,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber);
        var source = Enumerable.Range(0, rowCount)
            .Select(value => sampleRow.Select((item, column) =>
                column == 0 ? SqlValue.Integer(value) : item).ToArray())
            .ToArray();

        using var statement = ResumableStatement.CreateWithExecutionOptions(
            program,
            options,
            [new VdbeCursorSource(source)]);
        var rows = Drain(statement);

        rows.Should().HaveCount(rowCount);
        rows.Select(static row => (row[0].AsInteger(), row[1].AsInteger()))
            .Should().Equal(Enumerable.Range(0, rowCount).Select(
                static value => ((long)value, (long)value + 1)));
        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.SpillFilesCreated.Should().Be(2);
        metrics.SpillBytesWritten.Should().BeGreaterThan(0);
        metrics.SpillBytesRead.Should().BeGreaterThan(0);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
        budget.Should().BeLessThan(checked((rowBytes * rowCount) + infrastructure));
    }

    [Test]
    public void WindowBufferComputeReadsSpilledRowsWithoutReloadingThePartition()
    {
        const string temporaryDirectory = "window-buffer-spill-compute-limit";
        var sample = new[] { SqlValue.Text(new string('z', 64)) };
        var rowBytes = VdbeManagedFootprint.EstimateSorterRow(sample);
        var infrastructure = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory);
        var windowTupleBytes = VdbeManagedFootprint.EstimateSorterRow([SqlValue.Integer(0)]);
        var budget = checked(infrastructure + (windowTupleBytes * 16) + rowBytes + 128);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory,
            metrics: metrics);
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber);
        var source = Enumerable.Range(0, 16)
            .Select(static _ => new[] { SqlValue.Text(new string('z', 64)) })
            .ToArray();

        using var statement = ResumableStatement.CreateWithExecutionOptions(
            program,
            options,
            [new VdbeCursorSource(source)]);
        var rows = Drain(statement);

        rows.Should().HaveCount(16);
        rows.Select(static row => row[0].AsInteger()).Should().Equal(Enumerable.Range(1, 16).Select(static n => (long)n));
        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
        budget.Should().BeLessThan(checked(rowBytes * 16));
    }

    [Test]
    public void SpillIndexStaysOnDiskDuringRandomAccessAndCancellationCleansUp()
    {
        const int rowCount = 1024;
        const string temporaryDirectory = "window-index-cancellation";
        var infrastructure = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory);
        var budget = infrastructure + 1024;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory, metrics: metrics);
        using var cancellation = new CancellationTokenSource();
        var visited = false;
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)],
            rows =>
            {
                using (var indexFile = fileSystem.OpenFile(
                    fileSystem.Created.Single(path => path.Contains(
                        "-window-buffer-index-", StringComparison.Ordinal)),
                    FileOpenMode.OpenExisting,
                    readOnly: true))
                {
                    indexFile.Length.Should().Be((long)rowCount * sizeof(long));
                }
                rows[rowCount - 1][0].AsInteger().Should().Be(rowCount - 1);
                rows[0][0].AsInteger().Should().Be(0);
                visited = true;
                cancellation.Cancel();
                _ = rows[1];
                return RunningRowNumber(rows);
            });
        var source = Enumerable.Range(0, rowCount)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () =>
            {
                while (statement.StepResumable(cancellation.Token) != ResumableStatementStepResult.Done)
                {
                }
            };
            run.Should().Throw<OperationCanceledException>();
        }

        visited.Should().BeTrue();
        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.SpillFilesCreated.Should().Be(2);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void TempStoreMemoryRejectsWindowRowsWithoutCreatingSpillFiles()
    {
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: 256,
            allowTemporaryFileSpill: false, metrics: metrics);
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)], RunningRowNumber);
        var source = Enumerable.Range(0, 16)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () => Drain(statement);
            run.Should().Throw<VdbeMemoryLimitExceededException>();
        }

        metrics.WindowEvaluatorMemoryUnbounded.Should().BeFalse();
        metrics.SpillFilesCreated.Should().Be(0);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [Test]
    public void SpillCacheRejectsAnOversizedDecodedRowBeforeAllocatingIt()
    {
        const string temporaryDirectory = "window-cache-limit";
        var sample = new[] { SqlValue.Text(new string('x', 64)) };
        var infrastructure = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory);
        var firstListBytes = VdbeManagedFootprint.EstimateReferenceListStorage(
            VdbeManagedFootprint.GetListCapacityForCount(0, 1));
        var budget = infrastructure + VdbeManagedFootprint.EstimateSorterRow(sample)
            + firstListBytes + 16;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory, metrics: metrics);
        var source = Enumerable.Range(0, 16)
            .Select(_ => new[] { SqlValue.Text(new string('x', 64)) })
            .ToArray();
        var evaluatorReached = false;
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)],
            rows =>
            {
                evaluatorReached = true;
                _ = rows[15];
                return RunningRowNumber(rows);
            });

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () => Drain(statement);
            run.Should().Throw<VdbeMemoryLimitExceededException>();
        }

        evaluatorReached.Should().BeTrue();
        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void IndexFileCreationFailureReleasesTheDataFileAndReservations()
    {
        var fileSystem = new FailingSpillFileSystem("-window-buffer-index-");
        var metrics = new VdbeExecutionMetrics();
        var infrastructure = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure("window-index-failure");
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: infrastructure + 512,
            temporaryDirectory: "window-index-failure", metrics: metrics);
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)], RunningRowNumber);
        var source = Enumerable.Range(0, 32)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () => Drain(statement);
            run.Should().Throw<IOException>().WithMessage("*spill creation failed*");
        }

        metrics.WindowBuffersSpilled.Should().Be(0);
        metrics.SpillFilesCreated.Should().Be(1);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void WindowResultsSpillToSequentialOutputWithoutRetainingFullDrainArray()
    {
        const int rowCount = 512;
        const string temporaryDirectory = "window-output-large";
        var inputInfrastructure = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory);
        var outputInfrastructure = VdbeManagedFootprint.EstimateWindowOutputSpillInfrastructure(temporaryDirectory);
        var budget = inputInfrastructure + outputInfrastructure + 1024;
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory, metrics: metrics);
        var source = Enumerable.Range(0, rowCount)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1,
            [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            RunningRowNumber);

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            var rows = Drain(statement);
            rows.Select(static row => (row[0].AsInteger(), row[1].AsInteger()))
                .Should().Equal(Enumerable.Range(0, rowCount)
                    .Select(static index => ((long)index, (long)index + 1)));
        }

        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowOutputsSpilled.Should().Be(1);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.SpillFilesCreated.Should().Be(3);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        budget.Should().BeLessThan(
            VdbeManagedFootprint.EstimateWindowOutputMinimum(rowCount, 1));
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void SpilledWindowOutputNextCanSkipUnreadTuple()
    {
        const string temporaryDirectory = "window-output-skip";
        var buffer = new WindowBuffer(0);
        var instructions = new List<VdbeInstruction>
        {
            new OpenWindowBufferInstruction(buffer, 1, 1, RunningRowNumber),
        };
        for (var index = 0; index < 128; index++)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), SqlValue.Integer(index)));
            instructions.Add(new WindowBufferInsertInstruction(
                buffer, new RegisterRange(new Register(0), 1)));
        }
        var compute = instructions.Count;
        instructions.Add(new WindowBufferComputeInstruction(buffer, new ProgramCounter(compute + 4)));
        instructions.Add(new WindowBufferNextInstruction(buffer, new ProgramCounter(compute + 2)));
        instructions.Add(new WindowBufferDataInstruction(buffer, new RegisterRange(new Register(0), 2)));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(1), 1)));
        instructions.Add(new CloseWindowBufferInstruction(buffer));
        instructions.Add(new HaltInstruction());
        var program = new VdbeProgram(2, 0, instructions, windowBufferCount: 1);
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            new InMemoryFileSystem(),
            sorterMemoryLimitBytes:
                VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory)
                + VdbeManagedFootprint.EstimateWindowOutputSpillInfrastructure(temporaryDirectory)
                + 1024,
            temporaryDirectory: temporaryDirectory, metrics: metrics);

        using (var statement = ResumableStatement.CreateWithExecutionOptions(program, options))
            Drain(statement).Should().ContainSingle().Subject[0].AsInteger().Should().Be(2);

        metrics.WindowOutputsSpilled.Should().Be(1);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
    }

    [Test]
    public void TempStoreMemoryRejectsLargeWindowOutputBeforeInvokingEvaluator()
    {
        const int rowCount = 128;
        var sample = new[] { SqlValue.Integer(0) };
        var budget = checked((VdbeManagedFootprint.EstimateSorterRow(sample) * rowCount)
            + VdbeManagedFootprint.EstimateReferenceListStorage(rowCount) + 512);
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: budget,
            allowTemporaryFileSpill: false, metrics: metrics);
        var evaluatorInvoked = false;
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)],
            rows =>
            {
                evaluatorInvoked = true;
                return RunningRowNumber(rows);
            });
        var source = Enumerable.Range(0, rowCount)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () => Drain(statement);
            run.Should().Throw<VdbeMemoryLimitExceededException>();
        }

        evaluatorInvoked.Should().BeFalse();
        metrics.WindowOutputsSpilled.Should().Be(0);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeFalse();
        metrics.SpillFilesCreated.Should().Be(0);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        fileSystem.Created.Should().BeEmpty();
    }

    [Test]
    public void LargeWindowValueFallsBackFromReservedOutputToSpill()
    {
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: 1400, metrics: metrics);
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)],
            rows => [.. Enumerable.Range(0, rows.Count)
                .Select(static _ => new[] { SqlValue.Text(new string('v', 128)) })]);

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options,
            [new VdbeCursorSource(
                [[SqlValue.Integer(0)], [SqlValue.Integer(1)],
                 [SqlValue.Integer(2)], [SqlValue.Integer(3)]])]))
        {
            Drain(statement).Select(static row => row[0].AsText())
                .Should().Equal(Enumerable.Repeat(new string('v', 128), 4));
        }

        metrics.WindowBuffersSpilled.Should().Be(0);
        metrics.WindowOutputsSpilled.Should().Be(1);
        metrics.SpillFilesCreated.Should().Be(1);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(1400);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void SmallWindowOutputReusesEvaluatorArrayWithoutAnotherFullArray()
    {
        SqlValue[][]? computed = null;
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)],
            rows => computed = [.. Enumerable.Range(1, rows.Count)
                .Select(static index => new[] { SqlValue.Integer(index) })]);
        var metrics = new VdbeExecutionMetrics();
        var options = new VdbeExecutionOptions(
            new InMemoryFileSystem(), sorterMemoryLimitBytes: 8192, metrics: metrics);
        using var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options,
            [new VdbeCursorSource(
                [[SqlValue.Integer(1)], [SqlValue.Integer(2)]])]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        var buffers = (Array)typeof(ResumableStatement).GetField(
            "_windowBuffers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(statement)!;
        var buffer = buffers.GetValue(0)!;
        var stored = buffer.GetType().GetField(
            "_windowValues", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(buffer);
        stored.Should().BeSameAs(computed);
        metrics.WindowOutputsSpilled.Should().Be(0);
        Drain(statement).Should().ContainSingle().Subject[0].AsInteger().Should().Be(2);
    }

    [Test]
    public void OutputFileCreationFailureReleasesInputFilesAndBudget()
    {
        const string temporaryDirectory = "window-output-failure";
        var fileSystem = new FailingSpillFileSystem("-window-output-");
        var metrics = new VdbeExecutionMetrics();
        var budget = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory)
            + VdbeManagedFootprint.EstimateWindowOutputSpillInfrastructure(temporaryDirectory)
            + 1024;
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory, metrics: metrics);
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)], RunningRowNumber);
        var source = Enumerable.Range(0, 128)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () => Drain(statement);
            run.Should().Throw<IOException>().WithMessage("*spill creation failed*");
        }

        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowOutputsSpilled.Should().Be(0);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.SpillFilesCreated.Should().Be(2);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void CancellationDuringOutputSpillReleasesPartiallyCreatedFile()
    {
        const string temporaryDirectory = "window-output-cancel";
        var fileSystem = new TrackingFileSystem();
        var metrics = new VdbeExecutionMetrics();
        var budget = VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(temporaryDirectory)
            + VdbeManagedFootprint.EstimateWindowOutputSpillInfrastructure(temporaryDirectory)
            + 1024;
        var options = new VdbeExecutionOptions(
            fileSystem, sorterMemoryLimitBytes: budget,
            temporaryDirectory: temporaryDirectory, metrics: metrics);
        using var cancellation = new CancellationTokenSource();
        var program = BufferedWindowProgramBuilder.Build(
            "t", 1, 1, [BufferedWindowOutput.ForWindow(0)],
            rows =>
            {
                var values = RunningRowNumber(rows);
                cancellation.Cancel();
                return values;
            });
        var source = Enumerable.Range(0, 128)
            .Select(static index => new[] { SqlValue.Integer(index) })
            .ToArray();

        using (var statement = ResumableStatement.CreateWithExecutionOptions(
            program, options, [new VdbeCursorSource(source)]))
        {
            Action run = () => statement.StepResumable(cancellation.Token);
            run.Should().Throw<OperationCanceledException>();
        }

        metrics.WindowBuffersSpilled.Should().Be(1);
        metrics.WindowOutputsSpilled.Should().Be(0);
        metrics.WindowEvaluatorMemoryUnbounded.Should().BeTrue();
        metrics.SpillFilesCreated.Should().Be(3);
        metrics.PeakRetainedBytes.Should().BeLessThanOrEqualTo(budget);
        metrics.CurrentRetainedBytes.Should().Be(0);
        metrics.ActiveSpillFiles.Should().Be(0);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void LimitCompletionReleasesBufferedWindowAndSorterState()
    {
        var program = BufferedWindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            windowCount: 1,
            outputs: [BufferedWindowOutput.ForColumn(0), BufferedWindowOutput.ForWindow(0)],
            windowEvaluator: RunningRowNumber,
            orderComparer: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()));
        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 1);

        using var runtime = new ResumableStatement(
            gated,
            [
                new VdbeCursorSource(
                [
                    [SqlValue.Integer(1), SqlValue.Integer(10)],
                    [SqlValue.Integer(2), SqlValue.Integer(20)],
                ]),
            ]);

        runtime.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        runtime.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        GetOpenRuntimeCount(runtime, "_sorters").Should().Be(0);
        GetOpenRuntimeCount(runtime, "_windowBuffers").Should().Be(0);
    }

    // A minimal stand-in for the evaluator's window pass: one window value per row, holding the row's
    // one-based buffer position (row_number() over the whole input).
    private static IReadOnlyList<SqlValue[]> RunningRowNumber(IReadOnlyList<SqlValue[]> rows)
    {
        var values = new SqlValue[rows.Count][];
        for (var index = 0; index < rows.Count; index++)
            values[index] = [SqlValue.Integer(index + 1)];

        return values;
    }

    private static List<SqlValue[]> Run(VdbeProgram program, IReadOnlyList<SqlValue[]> rows)
    {
        using var runtime = new ResumableStatement(program, [new VdbeCursorSource(rows)]);
        return Drain(runtime);
    }

    private static List<SqlValue[]> Drain(ResumableStatement runtime)
    {
        var emitted = new List<SqlValue[]>();
        while (true)
        {
            switch (runtime.StepResumable())
            {
                case ResumableStatementStepResult.Row:
                    emitted.Add([.. runtime.CurrentRow!]);
                    break;
                case ResumableStatementStepResult.Done:
                    return emitted;
                default:
                    throw new InvalidOperationException("The buffered window program must not yield.");
            }
        }
    }

    private sealed class TrackingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner = new InMemoryFileSystem();

        public List<string> Created { get; } = [];

        public List<string> Deleted { get; } = [];

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
            Deleted.Add(path);
            _inner.DeleteFile(path);
        }
    }

    private sealed class FailingSpillFileSystem(string failedPurpose) : IFileSystem
    {
        private readonly TrackingFileSystem _inner = new();

        public List<string> Created => _inner.Created;

        public List<string> Deleted => _inner.Deleted;

        public bool FileExists(string path) => _inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
        {
            if (mode == FileOpenMode.CreateNew
                && path.Contains(failedPurpose, StringComparison.Ordinal))
                throw new IOException("window spill creation failed");
            return _inner.OpenFile(path, mode, readOnly);
        }

        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }

    private static int GetOpenRuntimeCount(ResumableStatement runtime, string fieldName)
    {
        var field = typeof(ResumableStatement).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected ResumableStatement.{fieldName}.");
        var slots = (Array?)field.GetValue(runtime)
            ?? throw new InvalidOperationException($"Expected ResumableStatement.{fieldName} slots.");
        return slots.Cast<object?>().Count(value => value is not null);
    }
}
