using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Regression coverage for INTERSECT/EXCEPT parameter slots. Set operations now evaluate and bind every
// term in SQL source order before iterating the captured first-term set.
public class SetOperationParameterSlotMappingTests
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
    public void ExceptTwoTermsMapsInputBindingToPrimaryMinusProbe()
    {
        // A = {?0, 100} (primary), B = {?0} (probe). Slots lay out A -> 0, B -> 1.
        var compound = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(100))])),
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(2);

        // Binding [10, 100]: A -> {10, 100}, B -> {100}; A EXCEPT B = {10}. The reversed (buggy) mapping
        // would compute {10, 100} EXCEPT {10} incorrectly and surface 100 instead.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(10), SqlValue.Integer(100));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(10);
    }

    [Test]
    public void IntersectTwoTermsMapsInputBindingByTermOrder()
    {
        // A = {?0, 1} (primary), B = {?0, 2, 1} (probe). Slots lay out A -> 0, B -> 1.
        var compound = CompoundProgramBuilder.BuildIntersect(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Constant(SqlValue.Integer(1))])),
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)],
                    [ValuesCell.Constant(SqlValue.Integer(2))],
                    [ValuesCell.Constant(SqlValue.Integer(1))])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(2);

        // Binding [2, 9]: A -> {2, 1}, B -> {9, 2, 1}; INTERSECT in first-term order = {2, 1}. The reversed
        // mapping would make A -> {9, 1}, B -> {2, 2, 1} and collapse the result to {1}.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(2), SqlValue.Integer(9));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(2, 1);
    }

    [Test]
    public void ExceptThreeTermChainMapsEachTermToItsInputSlots()
    {
        // A = {?0, 1, 2, 3} (primary), B = {?0} (probe 1), C = {?0} (probe 2).
        // Slots and execution both lay out A -> 0, B -> 1, C -> 2.
        var compound = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)],
                    [ValuesCell.Constant(SqlValue.Integer(1))],
                    [ValuesCell.Constant(SqlValue.Integer(2))],
                    [ValuesCell.Constant(SqlValue.Integer(3))])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(3);

        // Binding [10, 2, 3]: A -> {10, 1, 2, 3}, B -> {2}, C -> {3}; A EXCEPT (B ∪ C) = {10, 1}. The
        // a reversed mapping would send 10 -> B, 2 -> C, 3 -> A and yield {3, 1} instead.
        var binding = VdbeParameterBinding.FromValues(
            SqlValue.Integer(10), SqlValue.Integer(2), SqlValue.Integer(3));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(10, 1);
    }

    [Test]
    public void NestedExceptWithinUnionAllRelocatesParametersByTermIdentity()
    {
        // inner = A EXCEPT B with A = {?0}, B = {?0}; slots inner:0 -> A, inner:1 -> B.
        var inner = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);
        inner.Program.ParameterSlotCount.Should().Be(2);

        // outer = inner UNION ALL C with C = {?0}; the union relocates inner's two slots to 0..1 and C's
        // to 2, so the combined slots stay in input-term order end to end.
        var outer = CompoundProgramBuilder.BuildUnionAll(
            [inner, ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)]))]);
        outer.Program.ParameterSlotCount.Should().Be(3);

        // Binding [10, 20, 30]: inner -> (10 EXCEPT 20) = {10}, C -> {30}; UNION ALL = {10, 30}. A reversed
        // inner mapping would emit {20, 30}.
        var binding = VdbeParameterBinding.FromValues(
            SqlValue.Integer(10), SqlValue.Integer(20), SqlValue.Integer(30));
        Integers(RunCompoundWithBinding(outer, binding)).Should().Equal(10, 30);
    }

    [Test]
    public void SetOperationParameterSlotCountIsSumOfTermSlotsAndValidatesBindingWidth()
    {
        // A references two slots (?0, ?1) across its rows; B references one (?0). Combined width = 2 + 1.
        var compound = CompoundProgramBuilder.BuildIntersect(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows(
                    [ValuesCell.Parameter(0)], [ValuesCell.Parameter(1)])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);

        compound.Program.ParameterSlotCount.Should().Be(3);

        // A binding narrower than the declared slot space is rejected before execution.
        Assert.Throws<VdbeParameterBindingException>(() => new ResumableStatement(
            compound.Program,
            compound.CursorSources,
            parameterBinding: VdbeParameterBinding.FromValues(SqlValue.Integer(7), SqlValue.Integer(6))));

        // A correct-width binding executes: A -> {7, 6}, B -> {7}; INTERSECT in A's order = {7}.
        var binding = VdbeParameterBinding.FromValues(
            SqlValue.Integer(7), SqlValue.Integer(6), SqlValue.Integer(7));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(7);
    }

    [Test]
    public void ExceptReplaysMappingAcrossResetAndRebind()
    {
        // A = {?0} (primary), B = {?0} (probe); slots A -> 0, B -> 1.
        var compound = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);

        using var statement = new ResumableStatement(
            compound.Program,
            compound.CursorSources,
            parameterBinding: VdbeParameterBinding.FromValues(SqlValue.Integer(10), SqlValue.Integer(20)));

        // 10 EXCEPT 20 = {10}.
        Integers(DrainRows(statement)).Should().Equal(10);

        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(99), SqlValue.Integer(3)));

        // 99 EXCEPT 3 = {99}; the mapping must survive reset/rebind unchanged.
        Integers(DrainRows(statement)).Should().Equal(99);

        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(7), SqlValue.Integer(8)));

        // 7 EXCEPT 8 = {7}.
        Integers(DrainRows(statement)).Should().Equal(7);
    }

    [Test]
    public void SetOperationEmitsLoadParameterSlotsInSourceOrder()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);

        var loadParameters = compound.Program.Instructions
            .OfType<LoadParameterInstruction>()
            .ToList();
        loadParameters.Should().HaveCount(2);

        loadParameters[0].Slot.Index.Should().Be(0);
        loadParameters[1].Slot.Index.Should().Be(1);
    }

    [Test]
    public void ExplainRendersSetOperationParameterSlotsByTermIdentity()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
                ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ],
            ByteExactRows);

        var rendered = VdbeExplain.Describe(compound.Program);
        rendered.Should().HaveCount(compound.Program.Instructions.Count);

        // EXPLAIN column order is addr, opcode, p1, p2, p3, p4, comment; LoadParameter puts the slot in p2.
        var paramRows = rendered
            .Where(row => row[1].AsText() == VdbeOpcode.LoadParameter.ToString())
            .ToList();
        paramRows.Should().HaveCount(2);

        paramRows[0][3].AsInteger().Should().Be(0);
        paramRows[0][5].AsText().Should().Be("param[0]");
        paramRows[1][3].AsInteger().Should().Be(1);
        paramRows[1][5].AsText().Should().Be("param[1]");
    }

    [Test]
    public void UnionAllParameterMappingRemainsInInputTermOrder()
    {
        // Guards the unchanged UNION path: two single-parameter terms lay out in input order 0, 1.
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
            ValuesProgramBuilder.BuildTermCells(CellRows([ValuesCell.Parameter(0)])),
        ]);

        compound.Program.ParameterSlotCount.Should().Be(2);

        // Binding [10, 20] streams term 0 then term 1 -> {10, 20}, matching input-term order.
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(10), SqlValue.Integer(20));
        Integers(RunCompoundWithBinding(compound, binding)).Should().Equal(10, 20);
    }

    private static IReadOnlyList<IReadOnlyList<ValuesCell>> CellRows(params ValuesCell[][] rows) => rows;

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

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
