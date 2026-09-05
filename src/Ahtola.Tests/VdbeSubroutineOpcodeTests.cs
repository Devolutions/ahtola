using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers the subroutine opcodes ported from Turso for gap vdbe-subroutine-machinery
/// (Gosub, Return, BeginSubrtn): return-address bookkeeping, fallthrough versus fault
/// behavior on non-integer return registers, negative program counters, inclusive
/// null-filling, and constructor validation.
/// </summary>
public sealed class VdbeSubroutineOpcodeTests
{
    [Test]
    public void GosubSavesReturnAddressAndReturnResumesExecution()
    {
        // Gosub at offset 0 must save 1 (the offset after itself) in the return
        // register before jumping to the subroutine at 3; the subroutine marks r2,
        // and Return must resume at the saved offset 1 so the ResultRow observes
        // both the saved address and the subroutine's side effect. The Goto keeps
        // the post-ResultRow fall-through away from the subroutine body while
        // keeping the clean Halt terminal.
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new GosubInstruction(new ProgramCounter(3), new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(1), 2)),
                new GotoInstruction(new ProgramCounter(5)),
                new LoadConstantInstruction(new Register(2), SqlValue.Integer(42)),
                new ReturnInstruction(new Register(1), CanFallThrough: false),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Should().Be(SqlValue.Integer(1));
        statement.CurrentRow![1].Should().Be(SqlValue.Integer(42));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void ReturnPastProgramEndFailsClosed()
    {
        // Turso's step loop completes when the program counter runs past the end
        // (core/vdbe/mod.rs), but Ahtola treats falling off a validated program
        // without halting as an integrity error — reachable only through a
        // hand-crafted return register, since validated programs end with Halt
        // and Gosub therefore always saves an in-range return address.
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
                new ReturnInstruction(new Register(0), CanFallThrough: false),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
    }

    [Test]
    public void ReturnFallsThroughOnNonIntegerWhenAllowed()
    {
        foreach (var value in new[] { SqlValue.Null, SqlValue.Text("nope"), SqlValue.Real(1.5) })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new ReturnInstruction(new Register(0), CanFallThrough: true),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
            statement.CurrentRow![0].Should().Be(value);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        }
    }

    [Test]
    public void ReturnFaultsOnNonIntegerWhenFallthroughIsNotAllowed()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Null),
                new ReturnInstruction(new Register(0), CanFallThrough: false),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        statement.State.Should().Be(ResumableStatementState.Faulted);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
    }

    [Test]
    public void ReturnFaultsOnNegativeProgramCounter()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(-1)),
                new ReturnInstruction(new Register(0), CanFallThrough: false),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        statement.State.Should().Be(ResumableStatementState.Faulted);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
    }

    [Test]
    public void BeginSubrtnNullFillsSingleRegister()
    {
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(7)),
                new BeginSubrtnInstruction(new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Null);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void BeginSubrtnNullFillsInclusiveRangeOnly()
    {
        // r0 and r1 are inside the destination range, r2 is past the end and must
        // keep its value.
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
                new LoadConstantInstruction(new Register(2), SqlValue.Integer(3)),
                new BeginSubrtnInstruction(new Register(0), new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Null);
        statement.CurrentRow![1].Kind.Should().Be(SqlValueKind.Null);
        statement.CurrentRow![2].Should().Be(SqlValue.Integer(3));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void SubroutineOpcodesRejectInvalidRegistersAndTargets()
    {
        // Gosub jump target at the program length is out of bounds.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new GosubInstruction(new ProgramCounter(3), new Register(0)),
                new HaltInstruction(),
            ]));

        // Gosub return register out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new GosubInstruction(new ProgramCounter(2), new Register(1)),
                new HaltInstruction(),
            ]));

        // Return register out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new ReturnInstruction(new Register(1), CanFallThrough: false),
                new HaltInstruction(),
            ]));

        // BeginSubrtn destination out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new BeginSubrtnInstruction(new Register(2)),
                new HaltInstruction(),
            ]));

        // BeginSubrtn range end out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new BeginSubrtnInstruction(new Register(0), new Register(2)),
                new HaltInstruction(),
            ]));
    }
}
