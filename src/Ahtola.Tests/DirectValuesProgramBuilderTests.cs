using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Unit coverage for ValuesProgramBuilder, the direct lowering of a VALUES row list onto the resumable
// state machine. These tests pin the emitted opcode shape, the input validation boundary (equal width,
// non-empty), the register/cursor sizing, and EXPLAIN renderability. Execution and composition behaviour
// live in DirectValuesExecutionTests.
public class DirectValuesProgramBuilderTests
{
    [Test]
    public void BuildEmitsLoadConstantsThenResultRowPerRowThenHalt()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1), SqlValue.Text("a")],
            [SqlValue.Integer(2), SqlValue.Text("b")]));

        Opcodes(program).Should().Equal(
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.ResultRow,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);
    }

    [Test]
    public void BuildReusesTheSameRegisterBlockForEveryRow()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1), SqlValue.Integer(2)],
            [SqlValue.Integer(3), SqlValue.Integer(4)]));

        // Register count is the row width; both ResultRows read the same r[0..1] block, and every
        // LoadConstant targets registers 0 or 1.
        program.RegisterCount.Should().Be(2);
        program.CursorCount.Should().Be(0);
        program.SorterCount.Should().Be(0);
        program.AccumulatorCount.Should().Be(0);
        program.DistinctSetCount.Should().Be(0);

        program.Instructions.OfType<LoadConstantInstruction>()
            .Select(load => load.Destination.Index)
            .Should().OnlyContain(index => index == 0 || index == 1);

        program.Instructions.OfType<ResultRowInstruction>()
            .Should().OnlyContain(row => row.Values.Start.Index == 0 && row.Values.Count == 2);
    }

    [Test]
    public void BuildEphemeralCellsMaterializesMultiRowValues()
    {
        var cells = new IReadOnlyList<ValuesCell>[]
        {
            [ValuesCell.Constant(SqlValue.Integer(1)), ValuesCell.Constant(SqlValue.Text("a"))],
            [ValuesCell.Constant(SqlValue.Integer(2)), ValuesCell.Constant(SqlValue.Text("b"))],
        };
        var program = ValuesProgramBuilder.BuildEphemeralCells(cells);

        Opcodes(program).Should().ContainInOrder(
            VdbeOpcode.OpenEphemeral,
            VdbeOpcode.EphemeralInsert,
            VdbeOpcode.EphemeralInsert,
            VdbeOpcode.Rewind,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
        program.CursorCount.Should().Be(1);

        using var statement = new ResumableStatement(program, cursorSources: null);
        var rows = new List<(long, string)>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            rows.Add((statement.CurrentRow![0].AsInteger(), statement.CurrentRow[1].AsText()));
        rows.Should().Equal((1L, "a"), (2L, "b"));
    }

    [Test]
    public void BuildLoadsEachCellValueInColumnOrder()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(7), SqlValue.Text("z")],
            [SqlValue.Text("z"), SqlValue.Integer(7)]));

        var loads = program.Instructions.OfType<LoadConstantInstruction>().ToList();
        loads.Should().HaveCount(4);
        loads[0].Value.Should().Be(SqlValue.Integer(7));
        loads[1].Value.Should().Be(SqlValue.Text("z"));
        loads[2].Value.Should().Be(SqlValue.Text("z"));
        loads[3].Value.Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void BuildSingleRowSingleColumnEmitsOneResultRow()
    {
        var program = ValuesProgramBuilder.Build(Rows([SqlValue.Integer(42)]));

        program.RegisterCount.Should().Be(1);
        Opcodes(program).Should().Equal(VdbeOpcode.LoadConstant, VdbeOpcode.ResultRow, VdbeOpcode.Halt);
        program.Instructions[^1].Should().BeOfType<HaltInstruction>();
    }

    [Test]
    public void BuiltProgramPassesValidationAndEndsWithHalt()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)],
            [SqlValue.Integer(3)]));

        // The VdbeProgram constructor validates on construction; an explicit re-validate must also pass.
        program.Invoking(p => p.Validate()).Should().NotThrow();
        program.Instructions[^1].Should().BeOfType<HaltInstruction>();
    }

    [Test]
    public void BuildIsRenderableByExplain()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1), SqlValue.Real(2.5)],
            [SqlValue.Null, SqlValue.Text("x")]));

        var rendered = VdbeExplain.Describe(program);

        rendered.Should().HaveCount(program.Instructions.Count);
        // Every EXPLAIN row exposes the seven addr/opcode/p1/p2/p3/p4/comment columns.
        rendered.Should().OnlyContain(row => row.Length == VdbeExplain.Columns().Length);
    }

    [Test]
    public void BuildTermWrapsProgramWithEmptyCursorSources()
    {
        var term = ValuesProgramBuilder.BuildTerm(Rows(
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)]));

        term.CursorSources.Should().BeEmpty();
        term.Program.CursorCount.Should().Be(0);
        term.Program.Instructions.OfType<ResultRowInstruction>().Should().HaveCount(2);
    }

    [Test]
    public void RejectsMismatchedRowWidth()
    {
        Action build = () => ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1), SqlValue.Integer(2)],
            [SqlValue.Integer(3)]));

        build.Should().Throw<ArgumentException>()
            .WithMessage("all VALUES must have the same number of terms*");
    }

    [Test]
    public void RejectsAnEmptyRowList()
    {
        Action build = () => ValuesProgramBuilder.Build(Array.Empty<IReadOnlyList<SqlValue>>());

        build.Should().Throw<ArgumentException>().WithMessage("*at least one row*");
    }

    [Test]
    public void RejectsAZeroWidthRow()
    {
        // One row that has no terms. Built explicitly to avoid the collection-expression ambiguity where
        // Rows([]) would bind to an empty row list rather than a single empty row.
        IReadOnlyList<IReadOnlyList<SqlValue>> rows = new IReadOnlyList<SqlValue>[] { Array.Empty<SqlValue>() };

        Action build = () => ValuesProgramBuilder.Build(rows);

        build.Should().Throw<ArgumentException>().WithMessage("*at least one term*");
    }

    [Test]
    public void RejectsANullRowList()
    {
        Action build = () => ValuesProgramBuilder.Build(null!);

        build.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void RejectsANullRow()
    {
        IReadOnlyList<IReadOnlyList<SqlValue>> rows = [[SqlValue.Integer(1)], null!];

        Action build = () => ValuesProgramBuilder.Build(rows);

        build.Should().Throw<ArgumentException>().WithMessage("*row 1 must not be null*");
    }

    private static IReadOnlyList<IReadOnlyList<SqlValue>> Rows(params SqlValue[][] rows) => rows;

    private static List<VdbeOpcode> Opcodes(VdbeProgram program)
        => program.Instructions.Select(instruction => instruction.Opcode).ToList();
}
