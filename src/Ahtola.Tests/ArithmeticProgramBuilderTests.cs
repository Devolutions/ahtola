using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// End-to-end coverage for ArithmeticProgramBuilder, the direct lowering of arithmetic evaluation onto the
// resumable state machine. These tests pin the emitted opcode shape, the input validation boundary, and —
// most importantly — the composition of the Arithmetic opcode with VALUES/parameter sources, with a
// base-table scan, and with the compound builder. Every built program is executed through the resumable
// state machine so the tests assert real emitted output computed by the Arithmetic opcode, never a façade
// over the tree-walking evaluator.
public class ArithmeticProgramBuilderTests
{
    private static readonly VdbeRowEquality ByteExactRows = (left, right) =>
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].Equals(right[index]))
                return false;
        }

        return true;
    };

    // ---- BuildOverValues: composition with VALUES and parameters -------------------------------------

    [Test]
    public void BuildOverValuesEmitsOperandLoadsThenArithmeticThenResultRow()
    {
        var program = ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Add,
            [ValuesCell.Constant(SqlValue.Integer(2)), ValuesCell.Constant(SqlValue.Integer(3))]);

        Opcodes(program).Should().Equal(
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Arithmetic,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);

        // Operands occupy r[0..1]; the result register is the one past them.
        program.RegisterCount.Should().Be(3);
        program.CursorCount.Should().Be(0);
        var arithmetic = program.Instructions.OfType<ArithmeticInstruction>().Single();
        arithmetic.Destination.Index.Should().Be(2);
        arithmetic.Operator.Should().Be(ArithmeticOperator.Add);
        arithmetic.Operands.Start.Index.Should().Be(0);
        arithmetic.Operands.Count.Should().Be(2);
    }

    [Test]
    public void BuildOverValuesEmitsASingleOperandForAUnaryOperator()
    {
        var program = ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Negate,
            [ValuesCell.Constant(SqlValue.Integer(5))]);

        Opcodes(program).Should().Equal(
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Arithmetic,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);

        program.RegisterCount.Should().Be(2);
        var arithmetic = program.Instructions.OfType<ArithmeticInstruction>().Single();
        arithmetic.Destination.Index.Should().Be(1);
        arithmetic.Operands.Count.Should().Be(1);

        Run(program)[0][0].Should().Be(SqlValue.Integer(-5));
    }

    [Test]
    public void BuildOverValuesExecutesTheOperatorOverConstantOperands()
        => Run(ArithmeticProgramBuilder.BuildOverValues(
                ArithmeticOperator.Multiply,
                [ValuesCell.Constant(SqlValue.Integer(6)), ValuesCell.Constant(SqlValue.Integer(7))]))
            [0][0].Should().Be(SqlValue.Integer(42));

    [Test]
    public void BuildOverValuesLowersParameterCellsToLateBoundSlots()
    {
        var program = ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Subtract,
            [ValuesCell.Parameter(0), ValuesCell.Parameter(1)]);

        program.Instructions.OfType<LoadParameterInstruction>().Should().HaveCount(2);
        program.ParameterSlotCount.Should().Be(2);

        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(50), SqlValue.Integer(8));
        using var statement = new ResumableStatement(program, parameterBinding: binding);
        Drain(statement)[0][0].Should().Be(SqlValue.Integer(42));

        // The compiled program re-runs with fresh parameters after a reset/rebind, without recompilation.
        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(10), SqlValue.Integer(3)));
        Drain(statement)[0][0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void BuildOverValuesMixesConstantAndParameterOperands()
    {
        var program = ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Add,
            [ValuesCell.Constant(SqlValue.Integer(100)), ValuesCell.Parameter(0)]);

        program.ParameterSlotCount.Should().Be(1);
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(23));
        using var statement = new ResumableStatement(program, parameterBinding: binding);
        Drain(statement)[0][0].Should().Be(SqlValue.Integer(123));
    }

    [Test]
    public void BuildOverValuesRejectsAnOperandCountThatDisagreesWithTheOperatorArity()
    {
        Action build = () => ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Add,
            [ValuesCell.Constant(SqlValue.Integer(1))]);

        build.Should().Throw<ArgumentException>().WithMessage("*arity 2*1 operand*");
    }

    [Test]
    public void BuildOverValuesRejectsMultipleOperandsForAUnaryOperator()
    {
        Action build = () => ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Negate,
            [ValuesCell.Constant(SqlValue.Integer(1)), ValuesCell.Constant(SqlValue.Integer(2))]);

        build.Should().Throw<ArgumentException>().WithMessage("*arity 1*2 operand*");
    }

    [Test]
    public void BuildOverValuesRejectsNullOperands()
    {
        Assert.Throws<ArgumentNullException>(() => ArithmeticProgramBuilder.BuildOverValues(
            ArithmeticOperator.Add,
            null!));
    }

    // ---- BuildOverScan: composition with a base-table scan ------------------------------------------

    [Test]
    public void BuildOverScanEmitsARealCursorLoop()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate,
            "t",
            columnCount: 1,
            operandColumns: [0],
            rows: Rows([SqlValue.Integer(1)], [SqlValue.Integer(2)]));

        Opcodes(term.Program).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Arithmetic,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        term.Program.CursorCount.Should().Be(1);
        term.CursorSources.Should().ContainSingle();
    }

    [Test]
    public void BuildOverScanAppliesAUnaryOperatorToEveryScannedRow()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate,
            "t",
            columnCount: 1,
            operandColumns: [0],
            rows: Rows([SqlValue.Integer(-7)], [SqlValue.Integer(3)], [SqlValue.Integer(-9)]));

        Integers(Run(term)).Should().Equal(7L, -3L, 9L);
    }

    [Test]
    public void BuildOverScanFeedsTwoOperandColumnsToABinaryOperator()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Add,
            "t",
            columnCount: 2,
            operandColumns: [0, 1],
            rows: Rows(
                [SqlValue.Integer(10), SqlValue.Integer(1)],
                [SqlValue.Integer(20), SqlValue.Integer(2)]));

        Integers(Run(term)).Should().Equal(11L, 22L);
    }

    [Test]
    public void BuildOverScanProjectsPassthroughColumnsBeforeTheArithmeticResult()
    {
        // id carried alongside a computed value: SELECT id, a * b FROM t.
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Multiply,
            "t",
            columnCount: 3,
            operandColumns: [1, 2],
            rows: Rows(
                [SqlValue.Integer(1), SqlValue.Integer(3), SqlValue.Integer(4)],
                [SqlValue.Integer(2), SqlValue.Integer(5), SqlValue.Integer(6)]),
            passthroughColumns: [0]);

        var rows = Run(term);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(12));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(30));
    }

    [Test]
    public void BuildOverScanYieldsNullForADivideByZeroRow()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Divide,
            "t",
            columnCount: 2,
            operandColumns: [0, 1],
            rows: Rows(
                [SqlValue.Integer(10), SqlValue.Integer(2)],
                [SqlValue.Integer(10), SqlValue.Integer(0)]));

        var rows = Run(term);
        rows[0][0].Should().Be(SqlValue.Integer(5));
        rows[1][0].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void BuildOverScanEmitsNothingForAnEmptyTable()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate,
            "t",
            columnCount: 1,
            operandColumns: [0],
            rows: []);

        Run(term).Should().BeEmpty();
    }

    [Test]
    public void BuildOverScanReplaysAfterReset()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate,
            "t",
            columnCount: 1,
            operandColumns: [0],
            rows: Rows([SqlValue.Integer(-4)], [SqlValue.Integer(5)]));

        using var statement = new ResumableStatement(term.Program, term.CursorSources);
        Integers(Drain(statement)).Should().Equal(4L, -5L);

        statement.Reset();

        Integers(Drain(statement)).Should().Equal(4L, -5L);
    }

    [Test]
    public void BuildOverScanRejectsAnEmptyTableName()
    {
        Assert.Throws<ArgumentException>(() => ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate, "", columnCount: 1, operandColumns: [0], rows: []));
    }

    [Test]
    public void BuildOverScanRejectsANonPositiveColumnCount()
    {
        Assert.Throws<ArgumentException>(() => ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate, "t", columnCount: 0, operandColumns: [0], rows: []));
    }

    [Test]
    public void BuildOverScanRejectsAnOperandColumnOutsideTheTable()
    {
        Assert.Throws<ArgumentException>(() => ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate, "t", columnCount: 1, operandColumns: [3], rows: []));
    }

    [Test]
    public void BuildOverScanRejectsAPassthroughColumnOutsideTheTable()
    {
        Assert.Throws<ArgumentException>(() => ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate,
            "t",
            columnCount: 1,
            operandColumns: [0],
            rows: [],
            passthroughColumns: [9]));
    }

    [Test]
    public void BuildOverScanRejectsAnOperandColumnCountThatDisagreesWithTheOperatorArity()
    {
        Action build = () => ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Add, "t", columnCount: 1, operandColumns: [0], rows: []);

        build.Should().Throw<ArgumentException>().WithMessage("*arity 2*1 operand*");
    }

    // ---- Composition with the compound builder and EXPLAIN ------------------------------------------

    [Test]
    public void ArithmeticTermsComposeUnderTheCompoundBuilder()
    {
        // A source-less arithmetic term (over VALUES) and a scan-backed arithmetic term sequence together
        // under UNION ALL, proving the emitted programs are ordinary ResultRow-producing terms.
        var valuesTerm = ArithmeticProgramBuilder.BuildOverValuesTerm(
            ArithmeticOperator.Negate,
            [ValuesCell.Constant(SqlValue.Integer(-1))]);

        var scanTerm = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Negate,
            "t",
            columnCount: 1,
            operandColumns: [0],
            rows: Rows([SqlValue.Integer(-2)], [SqlValue.Integer(-3)]));

        var compound = CompoundProgramBuilder.BuildUnionAll([valuesTerm, scanTerm]);

        compound.Program.CursorCount.Should().Be(1);
        compound.CursorSources.Should().ContainSingle();
        Integers(Run(compound)).Should().Equal(1L, 2L, 3L);
    }

    [Test]
    public void ArithmeticTermsDeduplicateUnderTheDistinctCompoundBuilder()
    {
        var left = ArithmeticProgramBuilder.BuildOverValuesTerm(
            ArithmeticOperator.Add,
            [ValuesCell.Constant(SqlValue.Integer(1)), ValuesCell.Constant(SqlValue.Integer(1))]);
        var right = ArithmeticProgramBuilder.BuildOverValuesTerm(
            ArithmeticOperator.Multiply,
            [ValuesCell.Constant(SqlValue.Integer(2)), ValuesCell.Constant(SqlValue.Integer(1))]);

        var compound = CompoundProgramBuilder.BuildUnionDistinct([left, right], ByteExactRows);

        // Both terms compute 2, so the distinct compound emits it once.
        Integers(Run(compound)).Should().Equal(2L);
    }

    [Test]
    public void BuildOverScanIsRenderableByExplain()
    {
        var term = ArithmeticProgramBuilder.BuildOverScan(
            ArithmeticOperator.Multiply,
            "t",
            columnCount: 3,
            operandColumns: [1, 2],
            rows: Rows([SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3)]),
            passthroughColumns: [0]);

        var rendered = VdbeExplain.Describe(term.Program);

        rendered.Should().HaveCount(term.Program.Instructions.Count);
        rendered.Should().OnlyContain(row => row.Length == VdbeExplain.Columns().Length);
    }

    private static IReadOnlyList<SqlValue[]> Rows(params SqlValue[][] rows) => rows;

    private static List<VdbeOpcode> Opcodes(VdbeProgram program)
        => program.Instructions.Select(instruction => instruction.Opcode).ToList();

    private static List<SqlValue[]> Run(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return Drain(statement);
    }

    private static List<SqlValue[]> Run(CompoundTerm term)
    {
        using var statement = new ResumableStatement(term.Program, term.CursorSources);
        return Drain(statement);
    }

    private static List<SqlValue[]> Drain(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
            {
                rows.Add([.. statement.CurrentRow!]);
            }
            else if (result == ResumableStatementStepResult.Done)
            {
                break;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected step result {result}.");
            }
        }

        return rows;
    }

    private static List<long> Integers(IReadOnlyList<SqlValue[]> rows)
        => rows.Select(row => row[^1].AsInteger()).ToList();
}
