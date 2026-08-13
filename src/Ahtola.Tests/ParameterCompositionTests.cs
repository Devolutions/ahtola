using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Composition coverage for parameterized VALUES: the ValuesProgramBuilder cell path (mixing baked constants
// and late-bound parameters, inferring the slot width), and how a parameterized VALUES term composes with
// the direct compound builders (UNION ALL / UNION DISTINCT / INTERSECT / EXCEPT) — which relocate each
// term's slots into a disjoint range so the combined program takes one wide binding — and with the
// LIMIT/OFFSET gate, which preserves the underlying slots while gating the bound rows.
public class ParameterCompositionTests
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

    [Test]
    public void ValuesBuilderInfersSlotWidthAsHighestSlotPlusOne()
    {
        var program = ValuesProgramBuilder.BuildCells(CellRows(
            [ValuesCell.Constant(SqlValue.Integer(1)), ValuesCell.Parameter(2)]));

        program.ParameterSlotCount.Should().Be(3);
        Opcodes(program).Should().Contain(VdbeOpcode.LoadConstant).And.Contain(VdbeOpcode.LoadParameter);

        // Slots 0 and 1 are declared but never read; the binding must still supply them positionally.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Null, SqlValue.Null, SqlValue.Integer(9));
        var row = RunWithBinding(program, binding).Single();

        row[0].Should().Be(SqlValue.Integer(1));
        row[1].Should().Be(SqlValue.Integer(9));
    }

    [Test]
    public void ConstantOnlyCellRowsInferNoSlots()
    {
        var program = ValuesProgramBuilder.BuildCells(CellRows(
            [ValuesCell.Constant(SqlValue.Integer(1)), ValuesCell.Constant(SqlValue.Integer(2))]));

        program.ParameterSlotCount.Should().Be(0);
        Opcodes(program).Should().NotContain(VdbeOpcode.LoadParameter);
        Run(program).Single().Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void ParameterizedValuesRebindsAcrossExecutionsWithoutRebuilding()
    {
        var program = ValuesProgramBuilder.BuildCells(CellRows(
            [ValuesCell.Parameter(0), ValuesCell.Constant(SqlValue.Text("x"))],
            [ValuesCell.Parameter(0), ValuesCell.Constant(SqlValue.Text("y"))]));

        program.ParameterSlotCount.Should().Be(1);
        using var statement = new ResumableStatement(
            program, parameterBinding: VdbeParameterBinding.FromValues(SqlValue.Integer(7)));

        var first = DrainRows(statement);
        first[0].Should().Equal(SqlValue.Integer(7), SqlValue.Text("x"));
        first[1].Should().Equal(SqlValue.Integer(7), SqlValue.Text("y"));

        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(8)));

        var second = DrainRows(statement);
        second[0].Should().Equal(SqlValue.Integer(8), SqlValue.Text("x"));
        second[1].Should().Equal(SqlValue.Integer(8), SqlValue.Text("y"));
    }

    [Test]
    public void UnionAllRelocatesEachTermsSlotsIntoADisjointRange()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
        ]);

        // Both terms use slot 0 locally; the combined program declares two disjoint slots.
        compound.Program.ParameterSlotCount.Should().Be(2);

        using var statement = new ResumableStatement(
            compound.Program,
            compound.CursorSources,
            parameterBinding: VdbeParameterBinding.FromValues(SqlValue.Integer(7), SqlValue.Integer(9)));

        Integers(DrainRows(statement)).Should().Equal(7, 9);

        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(1), SqlValue.Integer(2)));
        Integers(DrainRows(statement)).Should().Equal(1, 2);
    }

    [Test]
    public void UnionDistinctDeDuplicatesAcrossParameterizedTerms()
    {
        var compound = CompoundProgramBuilder.BuildUnionDistinct(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(2))])),
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(2))])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(2);

        // term0 -> {1, 2}, term1 -> {2, 2}; UNION de-duplicates to {1, 2}.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(1), SqlValue.Integer(2));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(1, 2);
    }

    [Test]
    public void IntersectComposesWithParameterizedTerms()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(1))])),
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(3))])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(2);

        // Binding both disjoint slots to 3 makes term0 -> {3, 1} and term1 -> {3}; the intersection is {3}
        // regardless of which combined slot the relocation assigned to which term.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(3), SqlValue.Integer(3));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(3);
    }

    [Test]
    public void ExceptComposesWithParameterizedTerms()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)],
                    [ValuesCell.Constant(SqlValue.Integer(1))],
                    [ValuesCell.Constant(SqlValue.Integer(2))])),
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(2))])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(2);

        // Binding both slots to 5: term0 -> {5, 1, 2}, term1 -> {5, 2}; EXCEPT leaves {1}.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(5), SqlValue.Integer(5));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(1);
    }

    [Test]
    public void LimitOffsetPreservesParameterSlotsAndGatesBoundRows()
    {
        var program = ValuesProgramBuilder.BuildCells(CellRows(
            [ValuesCell.Parameter(0)],
            [ValuesCell.Parameter(1)],
            [ValuesCell.Parameter(2)],
            [ValuesCell.Parameter(3)]));

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);

        gated.ParameterSlotCount.Should().Be(4);
        Opcodes(gated).Should().Contain(VdbeOpcode.LoadParameter)
            .And.Contain(VdbeOpcode.OffsetGate).And.Contain(VdbeOpcode.LimitGate);

        using var statement = new ResumableStatement(
            gated,
            parameterBinding: VdbeParameterBinding.FromValues(
                SqlValue.Integer(10), SqlValue.Integer(20), SqlValue.Integer(30), SqlValue.Integer(40)));

        // OFFSET skips the first bound row, LIMIT then emits the next two.
        Integers(DrainRows(statement)).Should().Equal(20, 30);

        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(
            SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3), SqlValue.Integer(4)));
        Integers(DrainRows(statement)).Should().Equal(2, 3);
    }

    private static IReadOnlyList<IReadOnlyList<ValuesCell>> CellRows(params ValuesCell[][] rows) => rows;

    private static List<VdbeOpcode> Opcodes(VdbeProgram program)
        => program.Instructions.Select(instruction => instruction.Opcode).ToList();

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> Run(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> RunWithBinding(VdbeProgram program, VdbeParameterBinding binding)
    {
        using var statement = new ResumableStatement(program, parameterBinding: binding);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> RunCompoundWithBinding(CompoundTerm compound, VdbeParameterBinding binding)
    {
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources, parameterBinding: binding);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                rows.Add([.. statement.CurrentRow!]);
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
