using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

public sealed class VdbeSubprogramExecutionTests
{
    [Test]
    public void RaiseIgnoreJumpsToTheParentProgramTarget()
    {
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new HaltInstruction(
                    ErrorCode: SqliteResultCode.ConstraintTrigger,
                    OnError: VdbeHaltOnError.Ignore),
            ]));
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new ProgramInstruction([], child, new ProgramCounter(2)),
                new LoadConstantInstruction(new Register(0), SqlValue.Text("not reached")),
                new LoadConstantInstruction(new Register(0), SqlValue.Text("continued")),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Text("continued"));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void RaiseIgnoreWithoutAnExplicitTargetFallsThrough()
    {
        var child = IgnoringSubprogram();
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new ProgramInstruction([], child),
                new LoadConstantInstruction(new Register(0), SqlValue.Text("next")),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Text("next"));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void NestedRaiseIgnoreReturnsToItsImmediateParentFrame()
    {
        var calls = new List<string>();
        var capture = Capture(arguments =>
        {
            calls.Add(arguments[0].AsText());
            return arguments[0];
        });
        var middle = new VdbeSubprogram(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new ProgramInstruction([], IgnoringSubprogram(), new ProgramCounter(3)),
                new LoadConstantInstruction(new Register(0), SqlValue.Text("middle")),
                new FunctionInstruction(
                    new Register(0),
                    capture,
                    new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new ProgramInstruction([], middle),
                new LoadConstantInstruction(new Register(0), SqlValue.Text("outer")),
                new FunctionInstruction(
                    new Register(0),
                    capture,
                    new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        calls.Should().Equal("outer");
    }

    [Test]
    public void RecursiveProgramInvocationsUseFreshReentrantRegisterFrames()
    {
        var observed = new List<long>();
        var capture = Capture(arguments =>
        {
            observed.Add(arguments[0].AsInteger());
            return arguments[0];
        });
        var recursive = VdbeSubprogram.CreateDeferred(parameterSlotCount: 1);
        recursive.Resolve(new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new FunctionInstruction(new Register(2), capture, new RegisterRange(new Register(0), 1)),
                new JumpIfInstruction(new Register(0), new ProgramCounter(4)),
                new GotoInstruction(new ProgramCounter(8)),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
                new ArithmeticInstruction(
                    new Register(0),
                    ArithmeticOperator.Subtract,
                    new RegisterRange(new Register(0), 2)),
                new ProgramInstruction([new Register(0)], recursive),
                new FunctionInstruction(new Register(2), capture, new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 1));
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
                new ProgramInstruction([new Register(0)], recursive),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        observed.Should().Equal(3, 2, 1, 0, 0, 1, 2);
    }

    [Test]
    public void ChildDeferredForeignKeyCountersShareTheParentTransaction()
    {
        var transaction = new VdbeTransactionContext();
        transaction.Begin([]);
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new FkCounterInstruction(Increment: 1, Deferred: true),
                new HaltInstruction(),
            ]));
        using var statement = new ResumableStatement(
            new VdbeProgram(
                registerCount: 0,
                cursorCount: 0,
                [
                    new ProgramInstruction([], child),
                    new HaltInstruction(),
                ]),
            sharedTransaction: transaction);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        transaction.DeferredForeignKeyViolations.Should().Be(1);
        var error = Assert.Throws<EmbeddedSqlException>(() => transaction.Commit());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.ConstraintForeignKey);
    }

    [Test]
    public void CancellationInsideAChildPropagatesAndLeavesTheParentAtProgram()
    {
        using var cancellation = new CancellationTokenSource();
        var invocations = 0;
        var cancel = new VdbeScalarFunction
        {
            Name = "cancel",
            Arity = 0,
            Invoke = _ =>
            {
                invocations++;
                cancellation.Cancel();
                return SqlValue.Null;
            },
        };
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(0), cancel, new RegisterRange(new Register(0), 0)),
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new HaltInstruction(),
            ]));
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ProgramInstruction([], child),
                new HaltInstruction(),
            ]));

        var error = Assert.Throws<OperationCanceledException>(
            () => statement.StepResumable(cancellation.Token));

        error!.CancellationToken.Should().Be(cancellation.Token);
        statement.InstructionPointer.Should().Be(new ProgramCounter(0));
        statement.State.Should().Be(ResumableStatementState.Ready);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        invocations.Should().Be(2);
    }

    [Test]
    public void ChildErrorHaltPropagatesItsExactSqliteError()
    {
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new HaltInstruction(
                    ErrorCode: SqliteResultCode.ConstraintTrigger,
                    Description: "trigger failed",
                    OnError: VdbeHaltOnError.Abort),
            ]));
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ProgramInstruction([], child),
                new HaltInstruction(),
            ]));

        var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());

        error!.SqliteErrorCode.Should().Be(SqliteResultCode.ConstraintTrigger);
        error.Message.Should().Be("constraint failed: trigger failed");
        statement.InstructionPointer.Should().Be(new ProgramCounter(0));
    }

    [Test]
    public void ProgramRejectsAnOutOfRangeIgnoreJumpTarget()
    {
        var act = () => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ProgramInstruction([], IgnoringSubprogram(), new ProgramCounter(2)),
                new HaltInstruction(),
            ]);

        act.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*jumps to 2*");
    }

    [Test]
    public void ExplainIncludesTheRaiseIgnoreParentTarget()
    {
        var (_, p2, _, _, comment) = VdbeExplain.Describe(
            new ProgramInstruction([], IgnoringSubprogram(), new ProgramCounter(7)));

        p2.Should().Be(7);
        comment.Should().Contain("RAISE(IGNORE) goto 7");
    }

    private static VdbeSubprogram IgnoringSubprogram() =>
        new(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new HaltInstruction(
                    ErrorCode: SqliteResultCode.ConstraintTrigger,
                    OnError: VdbeHaltOnError.Ignore),
            ]));

    private static VdbeScalarFunction Capture(Func<SqlValue[], SqlValue> capture) =>
        new()
        {
            Name = "capture",
            Arity = 1,
            Invoke = capture,
        };
}
