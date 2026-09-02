using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// End-to-end coverage for LimitOffsetProgramBuilder, the lowering that gates an already-compiled
// result-streaming program with OFFSET/LIMIT counters. Every gated program is executed through the
// resumable state machine, so the tests assert real emitted output as well as the lowered opcode shape.
// The transform composes with any program that emits through unconditional ResultRow opcodes (scans,
// sorted scans, UNION ALL) and rejects the conditional compound emitters, mirroring the evaluator's
// OFFSET-then-LIMIT semantics exactly, including conditional compound emitters.
public class LimitOffsetProgramBuilderTests
{
    private static readonly VdbeRowEquality ByteExactRows = (left, right) =>
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].Equals(right[index]))
                return false;
        }

        return true;
    };

    [Test]
    public void AppliesLimitToAScanProgramAndCapsRows()
    {
        var (program, source) = ScanProgram(10, 20, 30, 40, 50);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 2);

        Integers(Run(gated, source)).Should().Equal(10, 20);
        Opcodes(gated).Should().Contain(VdbeOpcode.LimitGate);
        Opcodes(gated).Should().NotContain(VdbeOpcode.OffsetGate);
    }

    [Test]
    public void AppliesOffsetToAScanProgramAndSkipsLeadingRows()
    {
        var (program, source) = ScanProgram(10, 20, 30, 40, 50);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 2, limit: null);

        Integers(Run(gated, source)).Should().Equal(30, 40, 50);
        Opcodes(gated).Should().Contain(VdbeOpcode.OffsetGate);
        Opcodes(gated).Should().NotContain(VdbeOpcode.LimitGate);
    }

    [Test]
    public void AppliesLimitAndOffsetWithOffsetGateBeforeLimitGate()
    {
        var (program, source) = ScanProgram(10, 20, 30, 40, 50);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);

        // OFFSET skips 10, LIMIT then emits 20 and 30 — the skipped row is not charged against LIMIT.
        Integers(Run(gated, source)).Should().Equal(20, 30);

        var opcodes = Opcodes(gated);
        var offsetIndex = opcodes.IndexOf(VdbeOpcode.OffsetGate);
        var limitIndex = opcodes.IndexOf(VdbeOpcode.LimitGate);
        offsetIndex.Should().BeGreaterThanOrEqualTo(0);
        limitIndex.Should().Be(offsetIndex + 1);
    }

    [Test]
    public void LimitZeroEmitsNothing()
    {
        var (program, source) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 0);

        Run(gated, source).Should().BeEmpty();
        Opcodes(gated).Should().Contain(VdbeOpcode.LimitGate);
    }

    [Test]
    public void OffsetBeyondRowCountEmitsNothing()
    {
        var (program, source) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 99, limit: null);

        Run(gated, source).Should().BeEmpty();
    }

    [Test]
    public void NegativeLimitIsUnboundedAndReturnsTheProgramUnchanged()
    {
        var (program, source) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: -1);

        // A negative limit is unbounded, so with no offset the lowering is a faithful no-op: same instance.
        gated.Should().BeSameAs(program);
        Integers(Run(gated, source)).Should().Equal(10, 20, 30);
    }

    [Test]
    public void NegativeOffsetSkipsNothingAndReturnsTheProgramUnchanged()
    {
        var (program, source) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: -5, limit: null);

        gated.Should().BeSameAs(program);
        Integers(Run(gated, source)).Should().Equal(10, 20, 30);
    }

    [Test]
    public void NoOffsetAndUnboundedLimitReturnsTheProgramUnchanged()
    {
        var (program, _) = ScanProgram(10, 20, 30);

        LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: null).Should().BeSameAs(program);
    }

    [Test]
    public void ClampedOffsetIsAppliedWhenPairedWithALimit()
    {
        var (program, source) = ScanProgram(10, 20, 30);

        // A negative offset clamps to zero but the non-negative limit still gates the stream.
        var gated = LimitOffsetProgramBuilder.Apply(program, offset: -3, limit: 2);

        Integers(Run(gated, source)).Should().Equal(10, 20);
        Opcodes(gated).Should().NotContain(VdbeOpcode.OffsetGate);
        Opcodes(gated).Should().Contain(VdbeOpcode.LimitGate);
    }

    [Test]
    public void AppliesLimitToASortedScanPreservingOrder()
    {
        var program = SortedScanProgram(30, 10, 20, 40);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 2);

        // The sorter still orders ascending; LIMIT then keeps the two smallest.
        Integers(Run(gated)).Should().Equal(10, 20);
        gated.SorterCount.Should().Be(program.SorterCount);
    }

    [Test]
    public void AppliesOffsetAndLimitToASortedScan()
    {
        var program = SortedScanProgram(30, 10, 20, 40, 50);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);

        // Ascending order is 10,20,30,40,50; skip 10, then take 20 and 30.
        Integers(Run(gated)).Should().Equal(20, 30);
    }

    [Test]
    public void AppliesLimitAcrossAUnionAllConcatenatedStream()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ScanTerm("a", 1, 2, 3), ScanTerm("b", 4, 5)]);

        var gated = LimitOffsetProgramBuilder.Apply(compound, offset: 1, limit: 3);

        // The shared counters span the concatenated 1,2,3,4,5 stream: skip 1, then take 2,3,4.
        Integers(RunCompound(gated)).Should().Equal(2, 3, 4);
        gated.CursorSources.Should().HaveCount(compound.CursorSources.Count);
    }

    [Test]
    public void AppliesLimitToAConstantProjectionWithASingleResultRow()
    {
        var program = ConstantProjectionProgram(42);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 1);

        Integers(Run(gated)).Should().Equal(42);
    }

    [Test]
    public void OffsetSkippingTheOnlyRowOfAConstantProjectionEmitsNothing()
    {
        // The single ResultRow is the last instruction before Halt, so the OffsetGate's skip target is the
        // Halt itself: skipping the only candidate ends the program with no output.
        var program = ConstantProjectionProgram(42);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: null);

        Run(gated).Should().BeEmpty();
        var offsetGate = gated.Instructions.OfType<OffsetGateInstruction>().Single();
        gated.Instructions[offsetGate.SkipTarget.Offset].Should().BeOfType<HaltInstruction>();
    }

    [Test]
    public void AppliesLimitToAUnionDistinctProgram()
    {
        var compound = CompoundProgramBuilder.BuildUnionDistinct(
            [ScanTerm("a", 1, 2), ScanTerm("b", 2, 3)],
            ByteExactRows);

        var gated = LimitOffsetProgramBuilder.Apply(compound, offset: 0, limit: 1);

        Integers(RunCompound(gated)).Should().Equal(1);
        gated.Program.Instructions.Should().Contain(instruction => instruction is LimitGateInstruction);
    }

    [Test]
    public void AppliesLimitToAnIntersectProgram()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1, 2, 3), ScanTerm("b", 2, 3)],
            ByteExactRows);

        var gated = LimitOffsetProgramBuilder.Apply(compound, offset: 0, limit: 1);

        Integers(RunCompound(gated)).Should().Equal(2);
        gated.Program.Instructions.Should().Contain(instruction => instruction is LimitGateInstruction);
    }

    [Test]
    public void RejectsAnAlreadyGatedProgram()
    {
        var (program, _) = ScanProgram(10, 20, 30);
        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 2);

        Assert.Throws<StatementCompilationException>(
            () => LimitOffsetProgramBuilder.Apply(gated, offset: 0, limit: 1));
    }

    [Test]
    public void RejectsAProgramThatEmitsNoResultRows()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        Assert.Throws<StatementCompilationException>(
            () => LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 1));
    }

    [Test]
    public void OffsetGateSkipTargetsTheLoopAdvanceInstruction()
    {
        var (program, _) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: null);

        var offsetGate = gated.Instructions.OfType<OffsetGateInstruction>().Single();
        gated.Instructions[offsetGate.SkipTarget.Offset].Should().BeOfType<NextInstruction>();
    }

    [Test]
    public void LimitGateDoneTargetsTheTerminatingHalt()
    {
        var (program, _) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 1);

        var limitGate = gated.Instructions.OfType<LimitGateInstruction>().Single();
        limitGate.DoneTarget.Offset.Should().Be(gated.Instructions.Count - 1);
        gated.Instructions[^1].Should().BeOfType<HaltInstruction>();
    }

    [Test]
    public void ReplaysAGatedScanAfterReset()
    {
        var (program, source) = ScanProgram(10, 20, 30, 40, 50);
        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);
        using var statement = new ResumableStatement(gated, [source]);

        Integers(DrainRows(statement)).Should().Equal(20, 30);

        statement.Reset();

        Integers(DrainRows(statement)).Should().Equal(20, 30);
    }

    [Test]
    public void GatedProgramIsRenderableByExplain()
    {
        var (program, _) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);
        var rendered = VdbeExplain.Describe(gated);

        rendered.Should().HaveCount(gated.Instructions.Count);
    }

    [Test]
    public void PreservesTheRegisterFileByAppendingCounters()
    {
        var (program, _) = ScanProgram(10, 20, 30);

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);

        // Two new counter registers are appended after the program's existing register file.
        gated.RegisterCount.Should().Be(program.RegisterCount + 2);
    }

    [Test]
    public void ApplyResultOrderResortsEmittedRows()
    {
        var (program, source) = ScanProgram(10, 20, 30);
        VdbeRowComparer descending = (left, right) => right[0].AsInteger().CompareTo(left[0].AsInteger());

        var ordered = LimitOffsetProgramBuilder.ApplyResultOrder(program, descending);

        Integers(Run(ordered, source)).Should().Equal(30, 20, 10);
        ordered.SorterCount.Should().Be(program.SorterCount + 1);
        Opcodes(ordered).Should().Contain(VdbeOpcode.SorterInsert);
        Opcodes(ordered).Should().Contain(VdbeOpcode.SorterSort);
        ordered.Instructions[^1].Should().BeOfType<HaltInstruction>();
    }

    [Test]
    public void ApplyResultOrderThenLimitGatesTheSortedStream()
    {
        var (program, source) = ScanProgram(10, 20, 30, 40);
        VdbeRowComparer descending = (left, right) => right[0].AsInteger().CompareTo(left[0].AsInteger());

        var ordered = LimitOffsetProgramBuilder.ApplyResultOrder(program, descending);
        var gated = LimitOffsetProgramBuilder.Apply(ordered, offset: 1, limit: 2);

        Integers(Run(gated, source)).Should().Equal(30, 20);
    }

    [Test]
    public void RejectsANullProgram()
    {
        Assert.Throws<ArgumentNullException>(
            () => LimitOffsetProgramBuilder.Apply((VdbeProgram)null!, offset: 0, limit: 1));
    }

    [Test]
    public void RejectsANullCompoundTerm()
    {
        Assert.Throws<ArgumentNullException>(
            () => LimitOffsetProgramBuilder.Apply((CompoundTerm)null!, offset: 0, limit: 1));
    }

    // A single-column scan over the supplied integer rows: OpenReadCursor / Rewind / Column / ResultRow /
    // Next / CloseCursor / Halt, plus the cursor source that feeds it.
    private static (VdbeProgram Program, VdbeCursorSource Source) ScanProgram(params long[] values)
    {
        var source = new VdbeCursorSource(values.Select(value => new[] { SqlValue.Integer(value) }).ToList());
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), "t", 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(5)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(2)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        return (new VdbeProgram(registerCount: 1, cursorCount: 1, instructions), source);
    }

    // A cursor-less sorted scan that materializes the values into an ascending sorter and drains them:
    // OpenSorter / (LoadConstant, SorterInsert)* / SorterSort / SorterData / ResultRow / SorterNext /
    // CloseSorter / Halt.
    private static VdbeProgram SortedScanProgram(params long[] values)
    {
        VdbeRowComparer ascending = (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger());

        var instructions = new List<VdbeInstruction>
        {
            new OpenSorterInstruction(new Sorter(0), ascending, 1),
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

    // A single-column table scan term over the supplied integer rows, for CompoundProgramBuilder.
    private static CompoundTerm ScanTerm(string table, params long[] values)
    {
        var rows = values.Select(value => new[] { SqlValue.Integer(value) }).ToList();
        var source = new VdbeCursorSource(rows);
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), table, 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(5)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(2)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        return new CompoundTerm(new VdbeProgram(1, cursorCount: 1, instructions), [source]);
    }

    // A source-less constant projection: loads a value and emits it as one result row, then halts.
    private static VdbeProgram ConstantProjectionProgram(long value)
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(value)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        return new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);
    }

    private static List<VdbeOpcode> Opcodes(VdbeProgram program)
        => program.Instructions.Select(instruction => instruction.Opcode).ToList();

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> Run(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> Run(VdbeProgram program, VdbeCursorSource source)
    {
        using var statement = new ResumableStatement(program, [source]);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> RunCompound(CompoundTerm compound)
    {
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                rows.Add([.. statement.CurrentRow!]);
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
