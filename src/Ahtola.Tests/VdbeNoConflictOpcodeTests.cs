using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers NoConflict (inventory: vdbe-seek-op-family-partial).
/// </summary>
public sealed class VdbeNoConflictOpcodeTests
{
    [Test]
    public void NoConflictJumpsWhenKeyIsAbsent()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Text("a"), SqlValue.Integer(1) },
            new[] { SqlValue.Text("b"), SqlValue.Integer(2) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 1, 2 });

        // OpenRead, Load "z", NoConflict ->5 if absent, Load present, Goto 6, Load absent, Result, Halt
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("z")),
            new NoConflictInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(5),
                "no conflict z"),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("conflict")),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("ok")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("ok");
    }

    [Test]
    public void NoConflictFallsThroughAndPositionsWhenKeyMatches()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Text("a"), SqlValue.Integer(10) },
            new[] { SqlValue.Text("b"), SqlValue.Integer(20) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 1, 2 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("b")),
            new NoConflictInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(6),
                "no conflict b"),
            new ColumnInstruction(new Cursor(0), ColumnIndex: 1, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new GotoInstruction(new ProgramCounter(8)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("missed")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(20);
    }

    [Test]
    public void NoConflictJumpsWhenAnyKeyRegisterIsNull()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Text("a"), SqlValue.Integer(1) },
        };
        var source = new VdbeCursorSource(rows, new List<long> { 1 });

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), ColumnCount: 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Null),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
            new NoConflictInstruction(
                new Cursor(0),
                new RegisterRange(new Register(0), 2),
                new ProgramCounter(6),
                "null key"),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("conflict")),
            new GotoInstruction(new ProgramCounter(7)),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("null-ok")),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("null-ok");
    }

    [Test]
    public void ExplainRendersNoConflict()
    {
        var insn = new NoConflictInstruction(
            new Cursor(2),
            new RegisterRange(new Register(4), 3),
            new ProgramCounter(12),
            "no conflict key");
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(insn);
        p1.Should().Be(2);
        p2.Should().Be(12);
        p3.Should().Be(4);
        p4.Should().NotBeNull();
        comment.Should().Be("no conflict key");
        insn.Opcode.Should().Be(VdbeOpcode.NoConflict);
    }
}
