using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers OpenEphemeral / EphemeralInsert (inventory: vdbe-open-ephemeral).
/// </summary>
public sealed class VdbeEphemeralTableOpcodeTests
{
    [Test]
    public void EphemeralTableInsertsAndScansInOrder()
    {
        // Open ephemeral, insert three rows, rewind and yield each first column.
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("a")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("b")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("c")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(3)),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(14)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(11)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().Equal("a", "b", "c");
    }

    [Test]
    public void EphemeralSeekRowidFindsAssignedRowIds()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("first")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("second")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            // Seek rowid 2
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new SeekRowidInstruction(
                new Cursor(0),
                new Register(1),
                new ProgramCounter(10),
                "seek 2"),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new GotoInstruction(new ProgramCounter(12)),
            // not-found: emit sentinel then halt
            new LoadConstantInstruction(new Register(2), SqlValue.Text("missing")),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("second");
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void EmptyEphemeralRewindJumpsToEmptyTarget()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("row")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("empty")),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("empty");
    }

    [Test]
    public void NotExistsOnEphemeralDetectsMissingRowid()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("only")),
            new EphemeralInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(99)),
            new NotExistsInstruction(
                new Cursor(0),
                new Register(1),
                new ProgramCounter(7),
                "missing"),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("present")),
            new GotoInstruction(new ProgramCounter(8)),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("absent")),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("absent");
    }

    [Test]
    public void ExplainRendersOpenEphemeral()
    {
        var open = new OpenEphemeralInstruction(new Cursor(3), ColumnCount: 4);
        var (p1, p2, _, _, comment) = VdbeExplain.Describe(open);
        p1.Should().Be(3);
        p2.Should().Be(4);
        comment.Should().Contain("ephemeral");
        open.Opcode.Should().Be(VdbeOpcode.OpenEphemeral);
    }
}
