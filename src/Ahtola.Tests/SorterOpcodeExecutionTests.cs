using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

// Opcode-level coverage for the sorter family. Programs are built by hand from the
// public Execution contract and run through the resumable state machine, so the
// tests exercise the interpreter and validator directly rather than any wiring.
public class SorterOpcodeExecutionTests
{
    private static readonly VdbeRowComparer AscendingFirstColumn =
        (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger());

    private static readonly VdbeRowComparer DescendingFirstColumn =
        (left, right) => right[0].AsInteger().CompareTo(left[0].AsInteger());

    // Compares the first column only, leaving equal-key rows to the sorter's stable order.
    private static readonly VdbeRowComparer AscendingFirstColumnStable =
        (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger());

    [Test]
    public void SorterOrdersInsertedRowsAndSnapshotsRegisterValues()
    {
        // r0 is overwritten between inserts, so ordered output proves records are copied.
        var program = SingleColumnSorterProgram(AscendingFirstColumn, 3, 1, 2);

        var rows = RunToCompletion(program);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2));
        rows[2].Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void SorterHonorsDescendingComparer()
    {
        var program = SingleColumnSorterProgram(DescendingFirstColumn, 3, 1, 2);

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger()).Should().Equal(3, 2, 1);
    }

    [Test]
    public void EmptySorterDrainsToNoRowsThroughTheEmptyTarget()
    {
        // 0 OpenSorter / 1 SorterSort -> 5 / 2 SorterData / 3 ResultRow / 4 SorterNext -> 2
        // 5 CloseSorter / 6 Halt. The empty target must skip the whole drain loop.
        VdbeInstruction[] instructions =
        [
            new OpenSorterInstruction(new Sorter(0), AscendingFirstColumn, 1),
            new SorterSortInstruction(new Sorter(0), new ProgramCounter(5)),
            new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new SorterNextInstruction(new Sorter(0), new ProgramCounter(2)),
            new CloseSorterInstruction(new Sorter(0)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, sorterCount: 1);

        RunToCompletion(program).Should().BeEmpty();
    }

    [Test]
    public void SorterKeepsInsertionOrderForEqualKeys()
    {
        // key/tag rows compared on the key only; equal keys must preserve insertion order.
        VdbeInstruction[] instructions =
        [
            new OpenSorterInstruction(new Sorter(0), AscendingFirstColumnStable, 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(0)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("c")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("d")),
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new SorterSortInstruction(new Sorter(0), new ProgramCounter(17)),
            new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new SorterNextInstruction(new Sorter(0), new ProgramCounter(14)),
            new CloseSorterInstruction(new Sorter(0)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, sorterCount: 1);

        var rows = RunToCompletion(program);

        rows.Select(row => row[1].AsText()).Should().Equal("c", "a", "b", "d");
        rows.Select(row => row[0].AsInteger()).Should().Equal(0, 1, 1, 1);
    }

    [Test]
    public void SorterObservesCancellationRequestedByANonThrowingComparer()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisonRequestedCancellation = false;
        VdbeRowComparer comparer = (left, right) =>
        {
            if (!comparisonRequestedCancellation)
            {
                comparisonRequestedCancellation = true;
                cancellation.Cancel();
            }

            return left[0].AsInteger().CompareTo(right[0].AsInteger());
        };
        var program = SingleColumnSorterProgram(comparer, 3, 1, 2);
        using var statement = new ResumableStatement(program);

        Assert.Throws<OperationCanceledException>(
            () => statement.StepResumable(cancellation.Token));
        statement.State.Should().Be(ResumableStatementState.Ready);

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
    }

    [Test]
    public void ResetReplaysASorterProgramFromTheStart()
    {
        var program = SingleColumnSorterProgram(AscendingFirstColumn, 5, 4);

        using var statement = new ResumableStatement(program);
        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(4, 5);

        statement.Reset();

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(4, 5);
    }

    // --- Spill path coverage (BufferRowCapacity > 0) -------------------------------------
    //
    // These tests force the external merge-sort path by setting a tiny buffer capacity
    // so the sorter flushes sorted runs to the temp file and drains via the lazy k-way
    // merge. A capacity of 2 with 10 inserts produces multiple runs; capacity 1 flushes
    // every row as its own run (maximally exercises the merge heap).

    [Test]
    public void SorterSpillsToRunsAndMergesCorrectly()
    {
        // Capacity 2 / 10 rows -> 5 runs (2,2,2,2,2). Merged output must be fully sorted.
        var program = SingleColumnSpillSorterProgram(AscendingFirstColumn, bufferRowCapacity: 2, 9, 1, 3, 5, 5, 4, 6, 2, 1, 3);

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger())
            .Should().Equal(1, 1, 2, 3, 3, 4, 5, 5, 6, 9);
    }

    [Test]
    public void SpilledSorterKeepsInsertionOrderForEqualKeys()
    {
        // Equal-key corpus with a distinguishable second column so stability is
        // observable: each first-column value appears twice, inserted in ascending
        // second-column order. A stable spilled sort must preserve that order across runs.
        var program = TwoColumnStableSorterProgram(
            AscendingFirstColumn,
            bufferRowCapacity: 2,
            // (first, second) pairs — duplicates in the first column with monotonic seconds.
            (1, 10), (1, 11), (2, 20), (2, 21), (3, 30), (3, 31), (4, 40), (4, 41));

        var rows = RunToCompletion(program);

        rows.Select(row => (first: row[0].AsInteger(), second: row[1].AsInteger()))
            .Should().Equal(
                (1, 10), (1, 11), (2, 20), (2, 21), (3, 30), (3, 31), (4, 40), (4, 41));
    }

    [Test]
    public void SpilledSorterHonorsDescendingComparer()
    {
        var program = SingleColumnSpillSorterProgram(DescendingFirstColumn, bufferRowCapacity: 2, 3, 1, 9, 5, 2, 6, 5, 4, 1, 3);

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger())
            .Should().Equal(9, 6, 5, 5, 4, 3, 3, 2, 1, 1);
    }

    [Test]
    public void SpilledSorterObservesCancellation()
    {
        // The first comparison during a run sort requests cancellation; the spilled
        // sort path must surface OperationCanceledException and leave the statement Ready.
        using var cancellation = new CancellationTokenSource();
        var comparisonRequestedCancellation = false;
        VdbeRowComparer comparer = (left, right) =>
        {
            if (!comparisonRequestedCancellation)
            {
                comparisonRequestedCancellation = true;
                cancellation.Cancel();
            }

            return left[0].AsInteger().CompareTo(right[0].AsInteger());
        };
        var program = SingleColumnSpillSorterProgram(comparer, bufferRowCapacity: 2, 3, 1, 2);
        using var statement = new ResumableStatement(program);

        Assert.Throws<OperationCanceledException>(
            () => statement.StepResumable(cancellation.Token));
        statement.State.Should().Be(ResumableStatementState.Ready);

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
    }

    [Test]
    public void EmptySpilledSorterDrainsToNoRows()
    {
        // Capacity is set but no rows are ever inserted: Sort should report empty and
        // jump straight to the empty target. Guards the empty-spill early return.
        var program = SingleColumnSpillSorterProgram(AscendingFirstColumn, bufferRowCapacity: 2);

        var rows = RunToCompletion(program);

        rows.Should().BeEmpty();
    }

    [Test]
    public void SpilledSorterRoundTripsTextBlobAndRealValues()
    {
        // Capacity 1 -> every row is its own run, so the merge heap serializes and
        // deserializes every value kind through the temp file. A zero comparer makes the
        // stable sort preserve insertion order, so the output is deterministic and the
        // assertion checks the codec round-tripped each kind + the JSON subtype intact.
        var program = MixedKindSorterProgram(bufferRowCapacity: 1);

        var rows = RunToCompletion(program);

        rows.Should().HaveCount(6);
        rows[0].Should().Equal(SqlValue.Text("hello"));
        rows[1].Should().Equal(SqlValue.Blob([0x00, 0xFF, 0x10]));
        rows[2].Should().Equal(SqlValue.Integer(7));
        rows[3].Should().Equal(SqlValue.Null);
        rows[4].Should().Equal(SqlValue.Real(1.25));
        rows[5][0].Kind.Should().Be(SqlValueKind.Text);
        rows[5][0].AsText().Should().Be("{\"k\":1}");
        rows[5][0].IsJson.Should().BeTrue();
    }

    [Test]
    public void ResetReplaysASpilledSorterProgram()
    {
        // After Reset the sorter is reopened and re-inserted from the bytecode, so a
        // spilled program must produce identical output on the second drain. Guards
        // against a leaked temp file or stale merge state surviving Reset.
        var program = SingleColumnSpillSorterProgram(AscendingFirstColumn, bufferRowCapacity: 2, 5, 4, 1, 3, 2);

        using var statement = new ResumableStatement(program);
        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3, 4, 5);

        statement.Reset();

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public void SorterUsesConfiguredByteBudgetAndCleansUpIFileSystemRuns()
    {
        var fileSystem = new TrackingFileSystem();
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: 1,
            temporaryDirectory: "sorter-byte-budget");
        var program = SingleColumnSorterProgram(AscendingFirstColumn, 3, 1, 2);

        using (var statement = ResumableStatement.CreateWithExecutionOptions(program, options))
        {
            DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
        }

        fileSystem.Created.Should().NotBeEmpty();
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
        fileSystem.Created.Should().OnlyContain(path => !fileSystem.FileExists(path));
    }

    [Test]
    public void SorterConsolidatesManyRunsWithinConfiguredMergeFanIn()
    {
        var options = new VdbeExecutionOptions(
            new InMemoryFileSystem(),
            sorterMemoryLimitBytes: 1024,
            temporaryDirectory: "sorter-fan-in",
            sorterMergeFanIn: 2);
        var values = Enumerable.Range(1, 40).Select(static value => (long)(41 - value)).ToArray();
        var program = SingleColumnSpillSorterProgram(AscendingFirstColumn, 1, values);

        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(Enumerable.Range(1, 40).Select(static value => (long)value));
    }

    [Test]
    public void SpillWriteFailureDeletesTheAllocatedIFileSystemRunOnDispose()
    {
        var faults = new DeterministicFaultInjector();
        var backing = new InMemoryFileSystem(faults);
        var fileSystem = new TrackingFileSystem(backing);
        faults.FailNextAfter(FileSystemOperation.Open, FileSystemOperation.Write);
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: 1,
            temporaryDirectory: "sorter-write-fault");
        var program = SingleColumnSorterProgram(AscendingFirstColumn, 2, 1);
        var statement = ResumableStatement.CreateWithExecutionOptions(program, options);

        Assert.Throws<IOException>(() => statement.StepResumable());
        faults.ClearScheduled();
        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2);
        statement.Dispose();

        fileSystem.Created.Should().NotBeEmpty();
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
        fileSystem.Created.Should().OnlyContain(path => !fileSystem.FileExists(path));
    }

    [Test]
    public void SpillCancellationDeletesTheAllocatedIFileSystemRunOnDispose()
    {
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new TrackingFileSystem();
        VdbeRowComparer comparer = (left, right) =>
        {
            cancellation.Cancel();
            return left[0].AsInteger().CompareTo(right[0].AsInteger());
        };
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: 1024,
            temporaryDirectory: "sorter-cancellation");
        var statement = ResumableStatement.CreateWithExecutionOptions(
            SingleColumnSpillSorterProgram(comparer, 2, 3, 2, 1),
            options);

        Assert.Throws<OperationCanceledException>(() => statement.StepResumable(cancellation.Token));
        statement.Dispose();

        fileSystem.Created.Should().HaveCount(1);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void SpillReadFailureDeletesTheAllocatedIFileSystemRunOnDispose()
    {
        var faults = new DeterministicFaultInjector();
        var backing = new InMemoryFileSystem(faults);
        var fileSystem = new TrackingFileSystem(backing);
        faults.FailNext(FileSystemOperation.Read);
        var options = new VdbeExecutionOptions(
            fileSystem,
            sorterMemoryLimitBytes: 1,
            temporaryDirectory: "sorter-read-fault");
        var statement = ResumableStatement.CreateWithExecutionOptions(
            SingleColumnSorterProgram(AscendingFirstColumn, 3, 1, 2),
            options);

        Assert.Throws<IOException>(() => statement.StepResumable());
        faults.ClearScheduled();
        DrainRows(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
        statement.Dispose();

        fileSystem.Created.Should().HaveCount(1);
        fileSystem.Deleted.Should().BeEquivalentTo(fileSystem.Created);
    }

    [Test]
    public void ValidationRejectsMalformedSorterBytecode()
    {
        var comparer = AscendingFirstColumn;

        // Sorter index beyond the declared count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new OpenSorterInstruction(new Sorter(0), comparer, 1), new HaltInstruction()],
            sorterCount: 0));

        // Used before being opened.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            sorterCount: 1));

        // Opened twice.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new OpenSorterInstruction(new Sorter(0), comparer, 1),
                new OpenSorterInstruction(new Sorter(0), comparer, 1),
                new HaltInstruction(),
            ],
            sorterCount: 1));

        // Null comparer.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new OpenSorterInstruction(new Sorter(0), null!, 1), new HaltInstruction()],
            sorterCount: 1));

        // Non-positive column count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new OpenSorterInstruction(new Sorter(0), comparer, 0), new HaltInstruction()],
            sorterCount: 1));

        // Record width does not match the sorter's column count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new OpenSorterInstruction(new Sorter(0), comparer, 2),
                new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            sorterCount: 1));

        // Closed before being opened.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new CloseSorterInstruction(new Sorter(0)), new HaltInstruction()],
            sorterCount: 1));

        // Jump target outside the program.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new OpenSorterInstruction(new Sorter(0), comparer, 1),
                new SorterSortInstruction(new Sorter(0), new ProgramCounter(99)),
                new HaltInstruction(),
            ],
            sorterCount: 1));
    }

    [Test]
    public void ConstructorRejectsNegativeSorterCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new HaltInstruction()],
            sorterCount: -1));
    }

    [Test]
    public void SorterHandleRejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sorter(-1));
    }

    // Builds: OpenSorter / (LoadConstant, SorterInsert)* / SorterSort / drain loop / CloseSorter / Halt
    private static VdbeProgram SingleColumnSorterProgram(VdbeRowComparer comparer, params long[] values)
        => SingleColumnSpillSorterProgram(comparer, bufferRowCapacity: 0, values);

    // Same shape as SingleColumnSorterProgram but opts into the spill path via a positive
    // buffer capacity. Kept as a distinct name (not an overload) so call sites with integer
    // literals stay unambiguous — `SingleColumnSorterProgram(comparer, 3, 1, 2)` must always
    // mean values [3,1,2], never capacity=3.
    private static VdbeProgram SingleColumnSpillSorterProgram(VdbeRowComparer comparer, int bufferRowCapacity, params long[] values)
    {
        var instructions = new List<VdbeInstruction>
        {
            new OpenSorterInstruction(new Sorter(0), comparer, 1, bufferRowCapacity),
        };

        foreach (var value in values)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), SqlValue.Integer(value)));
            instructions.Add(new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)));
        }

        var sortAddr = instructions.Count;
        var drainLoop = sortAddr + 1;
        var drainDone = drainLoop + 3;

        instructions.Add(new SorterSortInstruction(new Sorter(0), new ProgramCounter(drainDone)));
        instructions.Add(new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), 1)));
        instructions.Add(new SorterNextInstruction(new Sorter(0), new ProgramCounter(drainLoop)));
        instructions.Add(new CloseSorterInstruction(new Sorter(0)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, sorterCount: 1);
    }

    // Two-column variant for stability checks: the first column is the sort key, the
    // second column carries an insertion-order tag so a stable sort is observable.
    private static VdbeProgram TwoColumnStableSorterProgram(
        VdbeRowComparer comparer,
        int bufferRowCapacity,
        params (long First, long Second)[] rows)
    {
        var instructions = new List<VdbeInstruction>
        {
            new OpenSorterInstruction(new Sorter(0), comparer, 2, bufferRowCapacity),
        };

        foreach (var (first, second) in rows)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), SqlValue.Integer(first)));
            instructions.Add(new LoadConstantInstruction(new Register(1), SqlValue.Integer(second)));
            instructions.Add(new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)));
        }

        var sortAddr = instructions.Count;
        var drainLoop = sortAddr + 1;
        var drainDone = drainLoop + 3;

        instructions.Add(new SorterSortInstruction(new Sorter(0), new ProgramCounter(drainDone)));
        instructions.Add(new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 2)));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), 2)));
        instructions.Add(new SorterNextInstruction(new Sorter(0), new ProgramCounter(drainLoop)));
        instructions.Add(new CloseSorterInstruction(new Sorter(0)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, sorterCount: 1);
    }

    // One-column sorter loaded with mixed value kinds (Text/Blob/Integer/Null/Real/JSON).
    // Uses a zero comparer so the stable sort preserves insertion order; capacity 1 still
    // forces every row through the spill codec, which is what this program exists to test.
    private static VdbeProgram MixedKindSorterProgram(int bufferRowCapacity)
    {
        var values = new SqlValue[]
        {
            SqlValue.Text("hello"),
            SqlValue.Blob([0x00, 0xFF, 0x10]),
            SqlValue.Integer(7),
            SqlValue.Null,
            SqlValue.Real(1.25),
            SqlValue.JsonText("{\"k\":1}"),
        };

        VdbeRowComparer stableZero = (_, _) => 0;

        var instructions = new List<VdbeInstruction>
        {
            new OpenSorterInstruction(new Sorter(0), stableZero, 1, bufferRowCapacity),
        };

        foreach (var value in values)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), value));
            instructions.Add(new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)));
        }

        var sortAddr = instructions.Count;
        var drainLoop = sortAddr + 1;
        var drainDone = drainLoop + 3;

        instructions.Add(new SorterSortInstruction(new Sorter(0), new ProgramCounter(drainDone)));
        instructions.Add(new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 1)));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), 1)));
        instructions.Add(new SorterNextInstruction(new Sorter(0), new ProgramCounter(drainLoop)));
        instructions.Add(new CloseSorterInstruction(new Sorter(0)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, sorterCount: 1);
    }

    private sealed class TrackingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner;

        public TrackingFileSystem(IFileSystem? inner = null) => _inner = inner ?? new InMemoryFileSystem();

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

    private static List<SqlValue[]> RunToCompletion(VdbeProgram program, params VdbeCursorSource[] cursorSources)
    {
        using var statement = new ResumableStatement(
            program,
            cursorSources.Length == 0 ? null : cursorSources);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
            {
                rows.Add([.. statement.CurrentRow!]);
            }
            else if (result == ResumableStatementStepResult.Done)
            {
                break;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected step result {result}.");
            }
        }

        return rows;
    }
}
