using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

public class RowSetTestOpcodeExecutionTests
{
    [Test]
    public void PriorBatchDuplicateJumpsButTheInitialBatchDoesNotProbe()
    {
        var rowSet = new Register(0);
        var value = new Register(1);
        var result = new Register(2);
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadConstantInstruction(value, SqlValue.Integer(7)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(5), value, Batch: 0),
                new LoadConstantInstruction(result, SqlValue.Integer(10)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new GotoInstruction(new ProgramCounter(7)),
                new LoadConstantInstruction(result, SqlValue.Integer(-10)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new LoadConstantInstruction(value, SqlValue.Integer(7)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(12), value, Batch: 1),
                new LoadConstantInstruction(result, SqlValue.Integer(20)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new GotoInstruction(new ProgramCounter(14)),
                new LoadConstantInstruction(result, SqlValue.Integer(30)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new HaltInstruction(),
            ]);

        RunToCompletion(program).Should().Equal(10, 30);
    }

    [Test]
    public void SameBatchDuplicateDoesNotMatchPendingValues()
    {
        var rowSet = new Register(0);
        var value = new Register(1);
        var result = new Register(2);
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadConstantInstruction(value, SqlValue.Integer(7)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(6), value, Batch: 1),
                new RowSetTestInstruction(rowSet, new ProgramCounter(6), value, Batch: 1),
                new LoadConstantInstruction(result, SqlValue.Integer(1)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new GotoInstruction(new ProgramCounter(8)),
                new LoadConstantInstruction(result, SqlValue.Integer(0)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new HaltInstruction(),
            ]);

        RunToCompletion(program).Should().Equal(1);
    }

    [Test]
    public void FinalBatchDoesNotInsertUnmatchedValues()
    {
        var rowSet = new Register(0);
        var value = new Register(1);
        var result = new Register(2);
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadConstantInstruction(value, SqlValue.Integer(1)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(8), value, Batch: 0),
                new LoadConstantInstruction(value, SqlValue.Integer(2)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(8), value, Batch: -1),
                new RowSetTestInstruction(rowSet, new ProgramCounter(8), value, Batch: 2),
                new LoadConstantInstruction(result, SqlValue.Integer(1)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new GotoInstruction(new ProgramCounter(10)),
                new LoadConstantInstruction(result, SqlValue.Integer(0)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new HaltInstruction(),
            ]);

        RunToCompletion(program).Should().Equal(1);
    }

    [Test]
    public void ResetClearsIntegerRowSets()
    {
        var rowSet = new Register(0);
        var value = new Register(1);
        var result = new Register(2);
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadConstantInstruction(value, SqlValue.Integer(7)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(5), value, Batch: 1),
                new LoadConstantInstruction(result, SqlValue.Integer(1)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new GotoInstruction(new ProgramCounter(7)),
                new LoadConstantInstruction(result, SqlValue.Integer(0)),
                new ResultRowInstruction(new RegisterRange(result, 1)),
                new RowSetTestInstruction(rowSet, new ProgramCounter(8), value, Batch: 2),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        Drain(statement).Should().Equal(1);

        statement.Reset();

        Drain(statement).Should().Equal(1);
    }

    [Test]
    public void RejectsNonIntegerValueRegisters()
    {
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(1), SqlValue.Text("7")),
                new RowSetTestInstruction(new Register(0), new ProgramCounter(2), new Register(1), Batch: 0),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        var exception = Assert.Throws<InvalidOperationException>(() => statement.StepResumable());

        exception!.Message.Should().Be("RowSetTest: P3 must be an integer");
        statement.State.Should().Be(ResumableStatementState.Faulted);
    }

    [Test]
    public void ValidationAndExplainUseRowSetTestOperands()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new RowSetTestInstruction(new Register(2), new ProgramCounter(1), new Register(1), Batch: 0),
                new HaltInstruction(),
            ]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new RowSetTestInstruction(new Register(0), new ProgramCounter(2), new Register(1), Batch: 0),
                new HaltInstruction(),
            ]));

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new RowSetTestInstruction(new Register(2), new ProgramCounter(7), new Register(4), Batch: -1));

        p1.Should().Be(2);
        p2.Should().Be(7);
        p3.Should().Be(4);
        p4.Should().Be("-1");
        comment.Should().Be("goto 7 if r[4] is in integer row set r[2] from an earlier batch");
    }

    private static List<long> RunToCompletion(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return Drain(statement);
    }

    private static List<long> Drain(ResumableStatement statement)
    {
        var values = new List<long>();
        while (true)
        {
            switch (statement.StepResumable())
            {
                case ResumableStatementStepResult.Row:
                    values.Add(statement.CurrentRow![0].AsInteger());
                    break;
                case ResumableStatementStepResult.Done:
                    return values;
                default:
                    throw new InvalidOperationException("The test program must not yield.");
            }
        }
    }
}
