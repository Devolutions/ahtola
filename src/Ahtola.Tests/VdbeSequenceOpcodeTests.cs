using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers the per-cursor sequence opcodes ported from Turso for gap vdbe-sequence-opcodes
/// (Sequence, SequenceTest): counter publication and increment, the zero-jump behavior of
/// SequenceTest, per-cursor independence, reset semantics, and constructor validation.
/// </summary>
public sealed class VdbeSequenceOpcodeTests
{
    [Test]
    public void SequencePublishesCounterThenIncrements()
    {
        // Two Sequence reads on the same cursor must observe 0 then 1; a third on a
        // different cursor is independent and also starts at 0.
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 2,
            [
                new SequenceInstruction(new Cursor(0), new Register(0)),
                new SequenceInstruction(new Cursor(0), new Register(1)),
                new SequenceInstruction(new Cursor(1), new Register(2)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(0);
        statement.CurrentRow![1].AsInteger().Should().Be(1);
        statement.CurrentRow![2].AsInteger().Should().Be(0);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void SequenceTestJumpsOnZeroAndIncrementsDespiteTheJump()
    {
        // SequenceTest at offset 0 sees counter 0 and jumps to offset 2, skipping the
        // ResultRow at 1. The following Sequence then observes counter 1, proving the
        // counter incremented even though the jump was taken.
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceTestInstruction(new Cursor(0), new ProgramCounter(2), new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new SequenceInstruction(new Cursor(0), new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(1);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void SequenceTestFallsThroughOnNonZeroCounter()
    {
        // The leading Sequence lifts cursor 0's counter to 1, so SequenceTest must fall
        // through to the ResultRow instead of jumping to the terminal Halt (which would
        // complete the statement without producing a row).
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceInstruction(new Cursor(0), new Register(0)),
                new SequenceTestInstruction(new Cursor(0), new ProgramCounter(3), new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(0);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void SequenceCountersResetBetweenRuns()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceInstruction(new Cursor(0), new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(0);
        statement.Reset();
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        // SQLite keeps OP_Sequence state on the cursor, which is recreated per
        // execution — after a reset the counter starts at zero again. Turso leaks the
        // old value across reset (Vec::resize no-ops); that persistence is an upstream
        // artifact no observable program depends on.
        statement.CurrentRow![0].AsInteger().Should().Be(0);
    }

    [Test]
    public void SequenceOpcodesRejectInvalidCursorsRegistersAndTargets()
    {
        // Sequence cursor out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceInstruction(new Cursor(1), new Register(0)),
                new HaltInstruction(),
            ]));

        // Sequence destination register out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceInstruction(new Cursor(0), new Register(1)),
                new HaltInstruction(),
            ]));

        // SequenceTest jump target past the program end.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceTestInstruction(new Cursor(0), new ProgramCounter(2), new Register(0)),
                new HaltInstruction(),
            ]));

        // SequenceTest value register out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new SequenceTestInstruction(new Cursor(0), new ProgramCounter(1), new Register(1)),
                new HaltInstruction(),
            ]));
    }
}
