using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// End-to-end coverage for ScalarFunctionProgramBuilder, the direct lowering of scalar-function evaluation
// onto the resumable state machine. These tests pin the emitted opcode shape, the input validation
// boundary, and — most importantly — the composition of the Function opcode with VALUES/parameter sources
// and with a base-table scan. Every built program is executed through the resumable state machine so the
// tests assert real emitted output produced by invoking the caller-supplied delegate, never a façade over
// an evaluator.
public class ScalarFunctionProgramBuilderTests
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
    public void BuildOverValuesEmitsArgumentLoadsThenFunctionThenResultRow()
    {
        var program = ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Add(),
            [ValuesCell.Constant(SqlValue.Integer(2)), ValuesCell.Constant(SqlValue.Integer(3))]);

        Opcodes(program).Should().Equal(
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Function,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);

        // Arguments occupy r[0..1]; the result register is the one past them.
        program.RegisterCount.Should().Be(3);
        program.CursorCount.Should().Be(0);
        var function = program.Instructions.OfType<FunctionInstruction>().Single();
        function.Destination.Index.Should().Be(2);
        function.Arguments.Start.Index.Should().Be(0);
        function.Arguments.Count.Should().Be(2);
    }

    [Test]
    public void BuildOverValuesExecutesTheDelegateOverConstantArguments()
    {
        var program = ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Concat(),
            [
                ValuesCell.Constant(SqlValue.Text("a")),
                ValuesCell.Constant(SqlValue.Integer(1)),
                ValuesCell.Constant(SqlValue.Text("b")),
            ]);

        Run(program)[0][0].Should().Be(SqlValue.Text("a1b"));
    }

    [Test]
    public void BuildOverValuesLowersParameterCellsToLateBoundSlots()
    {
        var program = ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Add(),
            [ValuesCell.Parameter(0), ValuesCell.Parameter(1)]);

        program.Instructions.OfType<LoadParameterInstruction>().Should().HaveCount(2);
        program.ParameterSlotCount.Should().Be(2);

        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(40), SqlValue.Integer(2));
        using var statement = new ResumableStatement(program, parameterBinding: binding);
        Drain(statement)[0][0].Should().Be(SqlValue.Integer(42));

        // The compiled program re-runs with fresh parameters after a reset/rebind, without recompilation.
        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(1), SqlValue.Integer(1)));
        Drain(statement)[0][0].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void BuildOverValuesMixesConstantAndParameterArguments()
    {
        var program = ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Add(),
            [ValuesCell.Constant(SqlValue.Integer(100)), ValuesCell.Parameter(0)]);

        program.ParameterSlotCount.Should().Be(1);
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(23));
        using var statement = new ResumableStatement(program, parameterBinding: binding);
        Drain(statement)[0][0].Should().Be(SqlValue.Integer(123));
    }

    [Test]
    public void BuildOverValuesAcceptsAVariadicFunction()
    {
        var program = ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Coalesce(),
            [ValuesCell.Constant(SqlValue.Null), ValuesCell.Constant(SqlValue.Text("fallback"))]);

        Run(program)[0][0].Should().Be(SqlValue.Text("fallback"));
    }

    [Test]
    public void BuildOverValuesRejectsAnArgumentCountThatDisagreesWithAFixedArity()
    {
        Action build = () => ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Add(),
            [ValuesCell.Constant(SqlValue.Integer(1))]);

        build.Should().Throw<ArgumentException>().WithMessage("*arity 2*1 argument*");
    }

    [Test]
    public void BuildOverValuesRejectsANullFunction()
    {
        Assert.Throws<ArgumentNullException>(() => ScalarFunctionProgramBuilder.BuildOverValues(
            null!,
            [ValuesCell.Constant(SqlValue.Integer(1))]));
    }

    [Test]
    public void BuildOverValuesRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ScalarFunctionProgramBuilder.BuildOverValues(
            ScalarFunctionTestSupport.Coalesce(),
            null!));
    }

    // ---- BuildOverScan: composition with a base-table scan ------------------------------------------

    [Test]
    public void BuildOverScanEmitsARealCursorLoop()
    {
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(),
            "t",
            columnCount: 1,
            argumentColumns: [0],
            rows: Rows([SqlValue.Integer(-1)], [SqlValue.Integer(-2)]));

        Opcodes(term.Program).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Function,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        term.Program.CursorCount.Should().Be(1);
        term.CursorSources.Should().ContainSingle();
    }

    [Test]
    public void BuildOverScanAppliesTheFunctionToEveryScannedRow()
    {
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(),
            "t",
            columnCount: 1,
            argumentColumns: [0],
            rows: Rows([SqlValue.Integer(-7)], [SqlValue.Integer(3)], [SqlValue.Integer(-9)]));

        Integers(Run(term)).Should().Equal(7L, 3L, 9L);
    }

    [Test]
    public void BuildOverScanProjectsPassthroughColumnsBeforeTheFunctionResult()
    {
        // upper(name) carried alongside the key column: SELECT id, upper(name) FROM t.
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Upper(),
            "t",
            columnCount: 2,
            argumentColumns: [1],
            rows: Rows(
                [SqlValue.Integer(1), SqlValue.Text("alice")],
                [SqlValue.Integer(2), SqlValue.Text("bob")]),
            passthroughColumns: [0]);

        var rows = Run(term);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("ALICE"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("BOB"));
    }

    [Test]
    public void BuildOverScanFeedsMultipleArgumentColumnsToTheFunction()
    {
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Add(),
            "t",
            columnCount: 2,
            argumentColumns: [0, 1],
            rows: Rows(
                [SqlValue.Integer(10), SqlValue.Integer(1)],
                [SqlValue.Integer(20), SqlValue.Integer(2)]));

        Integers(Run(term)).Should().Equal(11L, 22L);
    }

    [Test]
    public void BuildOverScanEmitsNothingForAnEmptyTable()
    {
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(),
            "t",
            columnCount: 1,
            argumentColumns: [0],
            rows: []);

        Run(term).Should().BeEmpty();
    }

    [Test]
    public void BuildOverScanReplaysAfterReset()
    {
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(),
            "t",
            columnCount: 1,
            argumentColumns: [0],
            rows: Rows([SqlValue.Integer(-4)], [SqlValue.Integer(5)]));

        using var statement = new ResumableStatement(term.Program, term.CursorSources);
        Integers(Drain(statement)).Should().Equal(4L, 5L);

        statement.Reset();

        Integers(Drain(statement)).Should().Equal(4L, 5L);
    }

    [Test]
    public void BuildOverScanRejectsAnEmptyTableName()
    {
        Assert.Throws<ArgumentException>(() => ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(), "", columnCount: 1, argumentColumns: [0], rows: []));
    }

    [Test]
    public void BuildOverScanRejectsANonPositiveColumnCount()
    {
        Assert.Throws<ArgumentException>(() => ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(), "t", columnCount: 0, argumentColumns: [0], rows: []));
    }

    [Test]
    public void BuildOverScanRejectsAnArgumentColumnOutsideTheTable()
    {
        Assert.Throws<ArgumentException>(() => ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(), "t", columnCount: 1, argumentColumns: [3], rows: []));
    }

    [Test]
    public void BuildOverScanRejectsAPassthroughColumnOutsideTheTable()
    {
        Assert.Throws<ArgumentException>(() => ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(),
            "t",
            columnCount: 1,
            argumentColumns: [0],
            rows: [],
            passthroughColumns: [9]));
    }

    [Test]
    public void BuildOverScanRejectsAnArgumentColumnCountThatDisagreesWithAFixedArity()
    {
        Action build = () => ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Add(), "t", columnCount: 1, argumentColumns: [0], rows: []);

        build.Should().Throw<ArgumentException>().WithMessage("*arity 2*1 argument*");
    }

    // ---- Composition with the compound builder and EXPLAIN ------------------------------------------

    [Test]
    public void ScalarFunctionTermsComposeUnderTheCompoundBuilder()
    {
        // A source-less scalar-function term (over VALUES) and a scan-backed scalar-function term sequence
        // together under UNION ALL, proving the emitted programs are ordinary ResultRow-producing terms.
        var valuesTerm = ScalarFunctionProgramBuilder.BuildOverValuesTerm(
            ScalarFunctionTestSupport.Abs(),
            [ValuesCell.Constant(SqlValue.Integer(-1))]);

        var scanTerm = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Abs(),
            "t",
            columnCount: 1,
            argumentColumns: [0],
            rows: Rows([SqlValue.Integer(-2)], [SqlValue.Integer(-3)]));

        var compound = CompoundProgramBuilder.BuildUnionAll([valuesTerm, scanTerm]);

        compound.Program.CursorCount.Should().Be(1);
        compound.CursorSources.Should().ContainSingle();
        Integers(Run(compound)).Should().Equal(1L, 2L, 3L);
    }

    [Test]
    public void ScalarFunctionTermsDeduplicateUnderTheDistinctCompoundBuilder()
    {
        var left = ScalarFunctionProgramBuilder.BuildOverValuesTerm(
            ScalarFunctionTestSupport.Abs(),
            [ValuesCell.Constant(SqlValue.Integer(-1))]);
        var right = ScalarFunctionProgramBuilder.BuildOverValuesTerm(
            ScalarFunctionTestSupport.Abs(),
            [ValuesCell.Constant(SqlValue.Integer(1))]);

        var compound = CompoundProgramBuilder.BuildUnionDistinct([left, right], ByteExactRows);

        // Both terms compute abs -> 1, so the distinct compound emits it once.
        Integers(Run(compound)).Should().Equal(1L);
    }

    [Test]
    public void BuildOverScanIsRenderableByExplain()
    {
        var term = ScalarFunctionProgramBuilder.BuildOverScan(
            ScalarFunctionTestSupport.Upper(),
            "t",
            columnCount: 2,
            argumentColumns: [1],
            rows: Rows([SqlValue.Integer(1), SqlValue.Text("x")]),
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
