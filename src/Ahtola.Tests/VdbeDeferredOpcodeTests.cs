using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

public sealed class VdbeDeferredOpcodeTests
{
    [Test]
    public void ColumnRangeCopiesConsecutiveColumnsAndFillsShortRecords()
    {
        var program = new VdbeProgram(
            registerCount: 4,
            cursorCount: 1,
            [
                new OpenReadCursorInstruction(new Cursor(0), "t", 2),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new ColumnRangeInstruction(new Cursor(0), 0, new Register(0), 3, [null, null, SqlValue.Text("d")]),
                new ResultRowInstruction(new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(
            program,
            [new VdbeCursorSource([
                [SqlValue.Integer(1), SqlValue.Integer(2)],
            ])]);

        Drain(statement).Should().ContainSingle().Subject
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Text("d"));
    }

    [Test]
    public void OpenPseudoExposesASingleRegisterRow()
    {
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 1,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(9)),
                new LoadConstantInstruction(new Register(1), SqlValue.Text("x")),
                new OpenPseudoInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
                new ColumnInstruction(new Cursor(0), 0, new Register(2)),
                new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
                new NextInstruction(new Cursor(0), new ProgramCounter(4)),
                new HaltInstruction(),
            ]);

        Drain(new ResumableStatement(program)).Should().ContainSingle().Subject
            .Should().Equal(SqlValue.Integer(9));
    }

    [Test]
    public void BlobOpcodesReadAndWriteInPlaceWithoutResizing()
    {
        var program = new VdbeProgram(
            registerCount: 6,
            cursorCount: 1,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Blob([1, 2, 3, 4])),
                new OpenPseudoInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(11)),
                new BlobLenInstruction(new Cursor(0), 0, new Register(1)),
                new LoadConstantInstruction(new Register(2), SqlValue.Integer(1)),
                new LoadConstantInstruction(new Register(3), SqlValue.Integer(2)),
                new BlobReadInstruction(new Cursor(0), 0, new Register(2), new Register(3), new Register(4)),
                new LoadConstantInstruction(new Register(5), SqlValue.Blob([9, 8])),
                new BlobWriteInstruction(new Cursor(0), 0, new Register(2), new Register(5), new Register(4)),
                new BlobReadInstruction(new Cursor(0), 0, new Register(2), new Register(3), new Register(4)),
                new ResultRowInstruction(new RegisterRange(new Register(1), 4)),
                new HaltInstruction(),
            ]);

        var row = Drain(new ResumableStatement(program)).Should().ContainSingle().Subject;
        row[0].Should().Be(SqlValue.Integer(4));
        row[3].AsBlob().ToArray().Should().Equal(9, 8);
    }

    [Test]
    public void TypeCheckRejectsStrictIntegerMismatch()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Text("nope")),
                new TypeCheckInstruction(
                    new RegisterRange(new Register(0), 1),
                    "t",
                    ["INTEGER"],
                    ColumnNames: ["n"]),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        Assert.Throws<EmbeddedSqlException>(() => Drain(statement))!
            .Message.Should().Contain("cannot store TEXT value in INTEGER column t.n");
    }

    [Test]
    public void TypeCheckAllowsIntegerInRealAndNamesTheColumn()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
                new TypeCheckInstruction(
                    new RegisterRange(new Register(0), 1),
                    "t",
                    ["REAL"],
                    ColumnNames: ["amount"]),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        Drain(new ResumableStatement(program)).Should().ContainSingle().Subject
            .Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void WindowBufferHonorsTheRetainedMemoryBudget()
    {
        var buffer = new WindowBuffer(0);
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new OpenWindowBufferInstruction(
                    buffer,
                    1,
                    1,
                    rows => rows.Select(_ => new[] { SqlValue.Integer(0) }).ToList()),
                new LoadConstantInstruction(new Register(0), SqlValue.Text(new string('x', 64))),
                new WindowBufferInsertInstruction(buffer, new RegisterRange(new Register(0), 1)),
                new WindowBufferInsertInstruction(buffer, new RegisterRange(new Register(0), 1)),
                new WindowBufferComputeInstruction(buffer, new ProgramCounter(5)),
                new HaltInstruction(),
            ],
            windowBufferCount: 1);

        var options = new VdbeExecutionOptions(
            new Ahtola.Core.Storage.InMemoryFileSystem(),
            sorterMemoryLimitBytes: 32,
            allowTemporaryFileSpill: false);
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);
        Assert.Throws<VdbeMemoryLimitExceededException>(() => Drain(statement));
    }

    [Test]
    public void OnceFallsThroughThenJumpsAndResetOnceReplays()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(0)),
                new ResetOnceInstruction(new ProgramCounter(6)),
                new OnceInstruction(new ProgramCounter(5)),
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new GotoInstruction(new ProgramCounter(2)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        Drain(new ResumableStatement(program)).Should().ContainSingle().Subject
            .Should().Equal(SqlValue.Integer(1));
    }

    private static List<SqlValue[]> Drain(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            rows.Add(statement.CurrentRow!.ToArray());

        return rows;
    }
}
