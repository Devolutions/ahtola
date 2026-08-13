using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers FkCounter / FkIfZero / FkCheck (inventory: vdbe-fk-enforcement-opcodes).
/// </summary>
public sealed class VdbeFkCounterOpcodeTests
{
    [Test]
    public void FkIfZeroJumpsWhenCounterIsZero()
    {
        VdbeInstruction[] instructions =
        [
            new FkIfZeroInstruction(Deferred: false, new ProgramCounter(3)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("nonzero")),
            new GotoInstruction(new ProgramCounter(4)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("zero")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("zero");
    }

    [Test]
    public void FkCounterThenIfZeroFallsThroughWhenNonZero()
    {
        VdbeInstruction[] instructions =
        [
            new FkCounterInstruction(Increment: 1, Deferred: false),
            new FkIfZeroInstruction(Deferred: false, new ProgramCounter(4)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("violations")),
            new GotoInstruction(new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("clean")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("violations");
    }

    [Test]
    public void FkCheckRaisesWhenCounterNonZero()
    {
        VdbeInstruction[] instructions =
        [
            new FkCounterInstruction(Increment: 2, Deferred: false),
            new FkCheckInstruction(Deferred: false),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);
        var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.ConstraintForeignKey);
        error.Message.Should().Contain("FOREIGN KEY");
    }

    [Test]
    public void FkCheckPassesWhenCounterIsZero()
    {
        VdbeInstruction[] instructions =
        [
            new FkCheckInstruction(Deferred: false),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void DeferredAndImmediateCountersAreIndependent()
    {
        VdbeInstruction[] instructions =
        [
            new FkCounterInstruction(Increment: 1, Deferred: true),
            new FkIfZeroInstruction(Deferred: false, new ProgramCounter(4)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("imm-nonzero")),
            new GotoInstruction(new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("imm-zero")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            // After result, check deferred is non-zero
            new FkIfZeroInstruction(Deferred: true, new ProgramCounter(9)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("def-nonzero")),
            new GotoInstruction(new ProgramCounter(10)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("def-zero")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("imm-zero");
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("def-nonzero");
    }

    [Test]
    public void DeferredCounterSurvivesUntilTransactionCommit()
    {
        // Begin txn, bump deferred counter, FkCheck deferred is a no-op in-txn,
        // Commit fails with FOREIGN KEY.
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new FkCounterInstruction(Increment: 1, Deferred: true),
            new FkCheckInstruction(Deferred: true),
            new CommitTransactionInstruction(),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);
        var error = Assert.Throws<EmbeddedSqlException>(() =>
        {
            while (statement.StepResumable() != ResumableStatementStepResult.Done)
            {
            }
        });
        error!.Message.Should().Contain("FOREIGN KEY");
    }

    [Test]
    public void ExplainRendersFkOpcodes()
    {
        var counter = new FkCounterInstruction(3, Deferred: true);
        var (_, p2, _, _, comment) = VdbeExplain.Describe(counter);
        p2.Should().Be(3);
        comment.Should().Contain("deferred");

        var check = new FkCheckInstruction(Deferred: false);
        check.Opcode.Should().Be(VdbeOpcode.FkCheck);
    }
}
