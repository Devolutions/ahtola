using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers SeekGE/Idx* family, IdxRowId, RowData, IdxInsert/Delete
/// (inventory: vdbe-index-cursor-opcode-family, vdbe-seek-op-family-partial).
/// </summary>
public sealed class VdbeIndexCursorOpcodeTests
{
    [Test]
    public void SeekGEPositionsOnFirstGreaterOrEqualKey()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Text("a"), SqlValue.Integer(1) },
            new[] { SqlValue.Text("c"), SqlValue.Integer(3) },
            new[] { SqlValue.Text("e"), SqlValue.Integer(5) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 1, 2, 3 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("c")),
            new SeekKeyInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                VdbeKeySeekOperator.GreaterThanOrEqual,
                EqOnly: false,
                IsIndex: false,
                new ProgramCounter(6),
                "seekge c"),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 1, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new GotoInstruction(new ProgramCounter(8)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("miss")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(3);
    }

    [Test]
    public void SeekGTSkipsEqualKey()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(10) },
            new[] { SqlValue.Integer(20) },
            new[] { SqlValue.Integer(30) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 1, 2, 3 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(20)),
            new SeekKeyInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                VdbeKeySeekOperator.GreaterThan,
                EqOnly: false,
                IsIndex: true,
                new ProgramCounter(6),
                "idxgt 20"),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new GotoInstruction(new ProgramCounter(8)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(-1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        program.Instructions[2].Opcode.Should().Be(VdbeOpcode.IdxGT);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(30);
    }

    [Test]
    public void SeekLEEqOnlyRequiresExactMatch()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(10) },
            new[] { SqlValue.Integer(20) },
            new[] { SqlValue.Integer(30) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 1, 2, 3 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(25)),
            new SeekKeyInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                VdbeKeySeekOperator.LessThanOrEqual,
                EqOnly: true,
                IsIndex: false,
                new ProgramCounter(6),
                "seekle eq 25"),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("hit")),
            new GotoInstruction(new ProgramCounter(7)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("miss")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("miss");
    }

    [Test]
    public void IdxRowIdAndRowDataReadCurrentEntry()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Text("x"), SqlValue.Integer(9) },
            new[] { SqlValue.Text("y"), SqlValue.Integer(8) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 100, 200 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("y")),
            new SeekKeyInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                VdbeKeySeekOperator.GreaterThanOrEqual,
                EqOnly: true,
                IsIndex: true,
                new ProgramCounter(7),
                "idxge y"),
            new IdxRowIdInstruction(new Cursor(0), new Register(1)),
            new RowDataInstruction(new Cursor(0), new RegisterRange(new Register(2), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 3)),
            new GotoInstruction(new ProgramCounter(9)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(-1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 4, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(200);
        statement.CurrentRow[1].AsText().Should().Be("y");
        statement.CurrentRow[2].AsInteger().Should().Be(8);
    }

    [Test]
    public void IdxInsertNoOpDuplicateAndIdxDelete()
    {
        VdbeInstruction[] instructions =
        [
            new OpenEphemeralInstruction(new Cursor(0), ColumnCount: 1),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("a")),
            new IdxInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), VdbeIdxInsertFlags.NChange),
            new IdxInsertInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                VdbeIdxInsertFlags.NoOpDuplicate | VdbeIdxInsertFlags.NChange),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("b")),
            new IdxInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 1), VdbeIdxInsertFlags.NChange),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("a")),
            new IdxDeleteInstruction(new Cursor(0), new RegisterRange(new Register(0), 1)),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(12)),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(9)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        var values = new List<string>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            values.Add(statement.CurrentRow![0].AsText());

        values.Should().Equal("b");
        statement.RowsAffected.Should().Be(2);
    }

    [Test]
    public void ExplainRendersSeekKeyAndIdxOpcodes()
    {
        var seek = new SeekKeyInstruction(
            new Cursor(1),
            new RegisterRange(new Register(3), 2),
            VdbeKeySeekOperator.GreaterThanOrEqual,
            EqOnly: true,
            IsIndex: false,
            new ProgramCounter(10),
            "seekge");
        seek.Opcode.Should().Be(VdbeOpcode.SeekGE);
        var (_, _, _, p4, comment) = VdbeExplain.Describe(seek);
        p4.Should().Contain("eq_only");
        comment.Should().Be("seekge");

        var idx = new IdxInsertInstruction(
            new Cursor(0),
            new RegisterRange(new Register(0), 1),
            VdbeIdxInsertFlags.Append);
        idx.Opcode.Should().Be(VdbeOpcode.IdxInsert);
    }
}
