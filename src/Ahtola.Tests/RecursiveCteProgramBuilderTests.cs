using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Coverage for RecursiveCteProgramBuilder: it lowers seed rows + a recursive transform + a dedup mode +
// depth/row guards into a runnable VdbeProgram whose Open/Seed*/loop(Step,ResultRow,Expand,Goto)/Close/Halt
// shape drives the interpreter's real fixpoint loop. These tests assert both the emitted bytecode shape and
// the observable recursion it produces end to end, including parameterized anchors that re-bind without
// recompilation. Value and recursion semantics stay with the supplied transform/equality, never re-derived
// here; SQL routing into this builder is intentionally out of scope.
public class RecursiveCteProgramBuilderTests
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
    public void LowersToTheCanonicalRecursiveLoopShape()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            Increment,
            maxRows: 100,
            maxDepth: 3);

        program.WorkTableCount.Should().Be(1);
        program.RegisterCount.Should().Be(1);
        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenWorkTable,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.SeedWorkTable,
            VdbeOpcode.WorkTableStep,
            VdbeOpcode.ResultRow,
            VdbeOpcode.WorkTableExpand,
            VdbeOpcode.Goto,
            VdbeOpcode.CloseWorkTable,
            VdbeOpcode.Halt);
    }

    [Test]
    public void RunsAClassicCountingRecursionToItsBase()
    {
        // WITH RECURSIVE c(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM c WHERE n < 5): the transform is the
        // recursive term and self-terminates at 5, so the guards never fire.
        VdbeRecursiveTransform countToFive = row =>
        {
            var n = row[0].AsInteger();
            return n < 5 ? [[SqlValue.Integer(n + 1)]] : [];
        };

        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            countToFive,
            maxRows: 100,
            maxDepth: 100);

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public void GenerationTransformReceivesTheWholeFrontierAndReplaysAfterReset()
    {
        var generations = new List<long[]>();
        VdbeRecursiveGenerationTransform expand = rows =>
        {
            generations.Add(rows.Select(row => row[0].AsInteger()).ToArray());
            return rows.Select(row => new[] { SqlValue.Integer(row[0].AsInteger() + 10) }).ToArray();
        };
        var program = RecursiveCteProgramBuilder.BuildUnionAllGenerations(
            [[SqlValue.Integer(1)], [SqlValue.Integer(2)]],
            expand,
            maxRows: 10,
            maxDepth: 1);
        using var statement = new ResumableStatement(program);

        Integers(Drain(statement)).Should().Equal(1, 2, 11, 12);
        generations.Should().ContainSingle().Which.Should().Equal(1, 2);
        program.Instructions.Select(instruction => instruction.Opcode)
            .Should().Contain(VdbeOpcode.WorkTableExpandGeneration);

        statement.Reset();
        Integers(Drain(statement)).Should().Equal(1, 2, 11, 12);
        generations.Should().HaveCount(2);
        generations[1].Should().Equal(1, 2);
    }

    [Test]
    public void EmitsMultipleAnchorsBreadthFirstBeforeTheirDescendants()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(10)], [SqlValue.Integer(20)]],
            Increment,
            maxRows: 100,
            maxDepth: 1);

        Integers(RunToCompletion(program)).Should().Equal(10, 20, 11, 21);
    }

    [Test]
    public void UnionAllKeepsDuplicateDescendants()
    {
        // Diamond 1 -> {2, 3}, 2 -> {4}, 3 -> {4}: KeepAll admits the shared 4 twice.
        VdbeRecursiveTransform diamond = row => row[0].AsInteger() switch
        {
            1 => [[SqlValue.Integer(2)], [SqlValue.Integer(3)]],
            2 => [[SqlValue.Integer(4)]],
            3 => [[SqlValue.Integer(4)]],
            _ => [],
        };

        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            diamond,
            maxRows: 100,
            maxDepth: 100);

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3, 4, 4);
    }

    [Test]
    public void UnionDistinctDeduplicatesAndBreaksCycles()
    {
        // Graph reachability with a back-edge: WITH RECURSIVE reach(n) AS (VALUES(1) UNION SELECT edge ...).
        // Distinct de-duplication both collapses the diamond's shared node and terminates the 3->1 cycle.
        VdbeRecursiveTransform graph = row => row[0].AsInteger() switch
        {
            1 => [[SqlValue.Integer(2)], [SqlValue.Integer(3)]],
            2 => [[SqlValue.Integer(4)]],
            3 => [[SqlValue.Integer(4)], [SqlValue.Integer(1)]],
            _ => [],
        };

        var program = RecursiveCteProgramBuilder.BuildUnionDistinct(
            [[SqlValue.Integer(1)]],
            graph,
            ByteExactRows,
            maxRows: 100,
            maxDepth: 100);

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void DepthGuardBoundsANeverDryTransform()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(0)]],
            Increment,
            maxRows: 100,
            maxDepth: 4);

        Integers(RunToCompletion(program)).Should().Equal(0, 1, 2, 3, 4);
    }

    [Test]
    public void RowGuardOverflowSurfacesFromALoweredProgram()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            Increment,
            maxRows: 3,
            maxDepth: 100);

        using var statement = new ResumableStatement(program);
        var overflow = Assert.Throws<RecursiveWorkTableOverflowException>(() => Drain(statement));
        overflow!.MaxRows.Should().Be(3);
    }

    [Test]
    public void CarriesEveryColumnOfAWideAnchorThroughTheRecursion()
    {
        VdbeRecursiveTransform step = row =>
        {
            var value = row[0].AsInteger();
            var generation = row[1].AsInteger();
            return generation < 2 ? [[SqlValue.Integer(value * 10), SqlValue.Integer(generation + 1)]] : [];
        };

        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1), SqlValue.Integer(0)]],
            step,
            maxRows: 100,
            maxDepth: 100);

        program.RegisterCount.Should().Be(2);
        var rows = RunToCompletion(program);
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(0));
        rows[1].Should().Equal(SqlValue.Integer(10), SqlValue.Integer(1));
        rows[2].Should().Equal(SqlValue.Integer(100), SqlValue.Integer(2));
    }

    [Test]
    public void ParameterizedAnchorReExecutesWithFreshBindingsWithoutRecompilation()
    {
        IReadOnlyList<IReadOnlyList<ValuesCell>> cellRows = new[]
        {
            new[] { ValuesCell.Parameter(0) },
        };

        var program = RecursiveCteProgramBuilder.BuildUnionAllCells(
            cellRows,
            Increment,
            maxRows: 100,
            maxDepth: 2);

        program.ParameterSlotCount.Should().Be(1);

        using var statement = new ResumableStatement(
            program,
            parameterBinding: VdbeParameterBinding.FromValues(SqlValue.Integer(10)));
        Integers(Drain(statement)).Should().Equal(10, 11, 12);

        statement.Reset();
        statement.Rebind(VdbeParameterBinding.FromValues(SqlValue.Integer(20)));
        Integers(Drain(statement)).Should().Equal(20, 21, 22);
    }

    [Test]
    public void ConstantAnchorsInferNoParameterSlots()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            Increment,
            maxRows: 10,
            maxDepth: 1);

        program.ParameterSlotCount.Should().Be(0);
    }

    [Test]
    public void ExplainRendersTheLoweredRecursionForInspection()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionDistinct(
            [[SqlValue.Integer(1)]],
            Increment,
            ByteExactRows,
            maxRows: 50,
            maxDepth: 4);

        var rows = VdbeExplain.Describe(program);

        rows[0][1].Should().Be(SqlValue.Text(nameof(VdbeOpcode.OpenWorkTable)));
        rows[0][6].Should().Be(SqlValue.Text("open work table 0 (1 cols, distinct, <=50 rows, depth<=4)"));
        rows.Select(row => row[1].AsText()).Should().Contain(nameof(VdbeOpcode.WorkTableStep));
        rows.Select(row => row[1].AsText()).Should().Contain(nameof(VdbeOpcode.WorkTableExpand));
    }

    [Test]
    public void RejectsAnEmptyAnchorSet()
    {
        Assert.Throws<ArgumentException>(() => RecursiveCteProgramBuilder.BuildUnionAll(
            [],
            Increment,
            maxRows: 10,
            maxDepth: 1));
    }

    [Test]
    public void RejectsAnchorRowsOfDifferingWidth()
    {
        Assert.Throws<ArgumentException>(() => RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)], [SqlValue.Integer(1), SqlValue.Integer(2)]],
            Increment,
            maxRows: 10,
            maxDepth: 1));
    }

    [Test]
    public void RejectsAZeroWidthAnchorRow()
    {
        Assert.Throws<ArgumentException>(() => RecursiveCteProgramBuilder.BuildUnionAll(
            [[]],
            Increment,
            maxRows: 10,
            maxDepth: 1));
    }

    [Test]
    public void RejectsANullTransform()
    {
        Assert.Throws<ArgumentNullException>(() => RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            null!,
            maxRows: 10,
            maxDepth: 1));
    }

    [Test]
    public void RejectsANullEqualityForDistinct()
    {
        Assert.Throws<ArgumentNullException>(() => RecursiveCteProgramBuilder.BuildUnionDistinct(
            [[SqlValue.Integer(1)]],
            Increment,
            null!,
            maxRows: 10,
            maxDepth: 1));
    }

    [Test]
    public void RejectsANonPositiveRowGuard()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            Increment,
            maxRows: 0,
            maxDepth: 1));
    }

    [Test]
    public void RejectsANegativeDepthGuard()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(1)]],
            Increment,
            maxRows: 10,
            maxDepth: -1));
    }

    [Test]
    public void MaxDepthZeroEmitsOnlyTheAnchorGeneration()
    {
        var program = RecursiveCteProgramBuilder.BuildUnionAll(
            [[SqlValue.Integer(5)], [SqlValue.Integer(6)]],
            Increment,
            maxRows: 10,
            maxDepth: 0);

        Integers(RunToCompletion(program)).Should().Equal(5, 6);
    }

    // The recursive term used by most cases: n -> {n + 1}. It never runs dry, so termination is governed by
    // the guards, which is exactly what these tests probe.
    private static readonly VdbeRecursiveTransform Increment = row =>
        [[SqlValue.Integer(row[0].AsInteger() + 1)]];

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> RunToCompletion(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return Drain(statement);
    }

    private static List<SqlValue[]> Drain(ResumableStatement statement)
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
