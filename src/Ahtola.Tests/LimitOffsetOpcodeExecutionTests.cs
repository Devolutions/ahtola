using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the LIMIT/OFFSET gate family (OffsetGate, LimitGate). Programs are built by
// hand from the public Execution contract and run through the resumable state machine, so these tests
// exercise the interpreter and validator directly rather than any builder or SQL wiring. The gate
// semantics mirror the tree-walking evaluator: OFFSET skips leading candidates without counting them
// against LIMIT, LIMIT caps the surviving rows, and LIMIT 0 emits nothing.
public class LimitOffsetOpcodeExecutionTests
{
    [Test]
    public void LimitGateCapsEmittedRows()
    {
        RunGatedScan(offset: null, limit: 2, 10, 20, 30, 40, 50).Should().Equal(10, 20);
    }

    [Test]
    public void LimitGateZeroEmitsNothing()
    {
        RunGatedScan(offset: null, limit: 0, 10, 20, 30).Should().BeEmpty();
    }

    [Test]
    public void LimitGateLargerThanRowCountEmitsEveryRow()
    {
        RunGatedScan(offset: null, limit: 100, 10, 20, 30).Should().Equal(10, 20, 30);
    }

    [Test]
    public void OffsetGateSkipsLeadingRows()
    {
        RunGatedScan(offset: 2, limit: null, 10, 20, 30, 40, 50).Should().Equal(30, 40, 50);
    }

    [Test]
    public void OffsetGateBeyondRowCountEmitsNothing()
    {
        RunGatedScan(offset: 10, limit: null, 10, 20, 30).Should().BeEmpty();
    }

    [Test]
    public void OffsetThenLimitAppliesOffsetBeforeLimit()
    {
        // OFFSET 1 skips the first row (10); LIMIT 2 then emits the next two (20, 30). The skipped row
        // must not be counted against LIMIT, so the output is exactly [20, 30] rather than [20].
        RunGatedScan(offset: 1, limit: 2, 10, 20, 30, 40, 50).Should().Equal(20, 30);
    }

    [Test]
    public void OffsetConsumesRowsWithoutSpendingTheLimitAllowance()
    {
        // With only three rows and OFFSET 2 + LIMIT 2, the offset consumes 10 and 20, leaving just 30 to
        // pass the limit gate. The limit allowance is never charged for the skipped rows.
        RunGatedScan(offset: 2, limit: 2, 10, 20, 30).Should().Equal(30);
    }

    [Test]
    public void LimitGateStopsImmediatelyWhenCounterIsNotAPositiveInteger()
    {
        // A counter that is not a positive integer is treated as an exhausted allowance: the gate jumps to
        // the done target on the first candidate and no row is emitted. This is the defensive branch that
        // backs LIMIT 0 and guards against a mis-seeded counter.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(1), SqlValue.Null),
            new OpenReadCursorInstruction(new Cursor(0), "t", 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new LimitGateInstruction(new Register(1), new ProgramCounter(8)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(3)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);

        Integers(Run(program, Source(10, 20))).Should().BeEmpty();
    }

    [Test]
    public void OffsetGateDoesNotSkipWhenCounterIsNotAPositiveInteger()
    {
        // A non-positive-integer offset counter skips nothing, so every row falls through to be emitted.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(1), SqlValue.Null),
            new OpenReadCursorInstruction(new Cursor(0), "t", 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new OffsetGateInstruction(new Register(1), new ProgramCounter(6)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(3)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);

        Integers(Run(program, Source(10, 20))).Should().Equal(10, 20);
    }

    [Test]
    public void ResetReplaysAGatedProgramFromTheStart()
    {
        var program = GatedScan(offset: 1, limit: 2);
        using var statement = new ResumableStatement(program, [Source(10, 20, 30, 40, 50)]);

        Integers(DrainRows(statement)).Should().Equal(20, 30);

        statement.Reset();

        // The prologue LoadConstants reseed the counters on replay, so the second drain matches the first.
        Integers(DrainRows(statement)).Should().Equal(20, 30);
    }

    [Test]
    public void DisposeThenStepThrows()
    {
        var program = GatedScan(offset: null, limit: 2);
        var statement = new ResumableStatement(program, [Source(10, 20, 30)]);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    [Test]
    public void ExplainRendersGateOpcodesWithCounterAndTargets()
    {
        var program = GatedScan(offset: 1, limit: 2);

        var rendered = VdbeExplain.Describe(program);
        var opcodes = program.Instructions.Select(instruction => instruction.Opcode).ToList();

        var offsetAddress = opcodes.IndexOf(VdbeOpcode.OffsetGate);
        var limitAddress = opcodes.IndexOf(VdbeOpcode.LimitGate);
        offsetAddress.Should().BeGreaterThanOrEqualTo(0);
        limitAddress.Should().Be(offsetAddress + 1);

        // addr / opcode / p1(counter) / p2(target) / p3 / p4 / comment
        rendered[offsetAddress][1].AsText().Should().Be("OffsetGate");
        rendered[offsetAddress][6].AsText().Should().Contain("decrement");
        rendered[limitAddress][1].AsText().Should().Be("LimitGate");
        rendered[limitAddress][6].AsText().Should().Contain("goto");

        // Rendering never drops or adds rows.
        rendered.Should().HaveCount(program.Instructions.Count);
    }

    [Test]
    public void ValidationRejectsGateJumpTargetsOutsideTheProgram()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new OffsetGateInstruction(new Register(0), new ProgramCounter(99)),
                new HaltInstruction(),
            ]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new LimitGateInstruction(new Register(0), new ProgramCounter(99)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void ValidationRejectsGateCountersOutsideTheRegisterFile()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new OffsetGateInstruction(new Register(5), new ProgramCounter(1)),
                new HaltInstruction(),
            ]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LimitGateInstruction(new Register(5), new ProgramCounter(1)),
                new HaltInstruction(),
            ]));
    }

    // Builds a single-column scan gated by OFFSET and/or LIMIT counters seeded in a prologue. Gate
    // presence follows the evaluator's rules: a positive offset inserts an OffsetGate, a non-negative
    // limit inserts a LimitGate (LIMIT 0 included). Addresses are computed so OFFSET skips to the
    // loop-advance (Next) and LIMIT stops at the terminating Halt. The rows themselves come from a cursor
    // source supplied at run time, so the program is value-agnostic.
    private static VdbeProgram GatedScan(long? offset, long? limit)
    {
        var needOffset = offset is > 0;
        var needLimit = limit is >= 0;
        var prologue = (needOffset ? 1 : 0) + (needLimit ? 1 : 0);
        var gates = prologue;

        var offsetRegister = new Register(1);
        var limitRegister = new Register(needOffset ? 2 : 1);
        var registerCount = 1 + prologue;

        var openAddr = prologue;
        var rewindAddr = openAddr + 1;
        var loopStart = rewindAddr + 1;
        var resultAddr = loopStart + 1 + gates;
        var nextAddr = resultAddr + 1;
        var closeAddr = nextAddr + 1;
        var haltAddr = closeAddr + 1;

        var instructions = new List<VdbeInstruction>(haltAddr + 1);
        if (needOffset)
            instructions.Add(new LoadConstantInstruction(offsetRegister, SqlValue.Integer(offset!.Value)));
        if (needLimit)
            instructions.Add(new LoadConstantInstruction(limitRegister, SqlValue.Integer(limit!.Value)));

        instructions.Add(new OpenReadCursorInstruction(new Cursor(0), "t", 1));
        instructions.Add(new RewindCursorInstruction(new Cursor(0), new ProgramCounter(closeAddr)));
        instructions.Add(new ColumnInstruction(new Cursor(0), 0, new Register(0)));
        if (needOffset)
            instructions.Add(new OffsetGateInstruction(offsetRegister, new ProgramCounter(nextAddr)));
        if (needLimit)
            instructions.Add(new LimitGateInstruction(limitRegister, new ProgramCounter(haltAddr)));

        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), 1)));
        instructions.Add(new NextInstruction(new Cursor(0), new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(new Cursor(0)));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(registerCount, cursorCount: 1, instructions);
    }

    private static List<long> RunGatedScan(long? offset, long? limit, params long[] values)
        => Integers(Run(GatedScan(offset, limit), Source(values)));

    private static VdbeCursorSource Source(params long[] values)
        => new(values.Select(value => new[] { SqlValue.Integer(value) }).ToList());

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> Run(VdbeProgram program, VdbeCursorSource source)
    {
        using var statement = new ResumableStatement(program, [source]);
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
