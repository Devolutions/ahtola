using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Compiler-output and execution coverage for the aggregate lowering. BuildScalar and
// BuildGrouped are the reusable whole-table and GROUP BY lowerings; these tests assert
// the emitted bytecode shape and run the programs through the resumable state machine to
// confirm the aggregated result rows, empty-input behavior, grouping equality/order, and
// finalization semantics supplied through the delegate contract.
public class AggregateProgramBuilderTests
{
    private static AggregateFunctionSpec Sum(int column) =>
        new(AggregateTestSupport.Sum(), [column]);

    private static AggregateFunctionSpec CountStar() =>
        new(AggregateTestSupport.CountStar(), []);

    [Test]
    public void BuildScalarEmitsTheScanFoldFinalizePipeline()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [Sum(0)],
            outputs: [AggregateOutput.ForAggregate(0)]);

        program.RegisterCount.Should().Be(3);
        program.CursorCount.Should().Be(1);
        program.SorterCount.Should().Be(0);
        program.AccumulatorCount.Should().Be(1);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.AggReset,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.AggStep,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);

        ((RewindCursorInstruction)program.Instructions[2]).EmptyTarget.Offset.Should().Be(6);
        ((NextInstruction)program.Instructions[5]).LoopTarget.Offset.Should().Be(3);
        ((ResultRowInstruction)program.Instructions[9]).Values.Count.Should().Be(1);
    }

    [Test]
    public void BuildCountStarEmitsRowCountWithoutScanning()
    {
        var program = AggregateProgramBuilder.BuildCountStar("t", tableColumnCount: 1);

        program.RegisterCount.Should().Be(1);
        program.CursorCount.Should().Be(1);
        program.SorterCount.Should().Be(0);
        program.AccumulatorCount.Should().Be(0);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.RowCount,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);

        ((RowCountInstruction)program.Instructions[1]).Cursor.Index.Should().Be(0);
        ((RowCountInstruction)program.Instructions[1]).Destination.Index.Should().Be(0);
        ((ResultRowInstruction)program.Instructions[2]).Values.Count.Should().Be(1);

        var rows = new TrackingRows(
        [
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)],
            [SqlValue.Integer(3)],
            [SqlValue.Integer(4)],
            [SqlValue.Integer(5)],
        ]);
        var result = Run(program, new VdbeCursorSource(rows));

        result.Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(5));
        rows.MaxIndexAccessed.Should().Be(-1);
    }

    [Test]
    public void BuildScalarInsertsAFilterStageWhenGivenAPredicate()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [CountStar(), Sum(0)],
            outputs: [AggregateOutput.ForAggregate(0), AggregateOutput.ForAggregate(1)],
            predicate: row => row[0].AsInteger() > 2);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.AggReset,
            VdbeOpcode.AggReset,
            VdbeOpcode.Rewind,
            VdbeOpcode.Filter,
            VdbeOpcode.AggStep,
            VdbeOpcode.Column,
            VdbeOpcode.AggStep,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);

        var filter = (FilterInstruction)program.Instructions[4];
        filter.FalseTarget.Offset.Should().Be(8);
    }

    [Test]
    public void ScalarSumsEveryScannedRow()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [Sum(0)],
            outputs: [AggregateOutput.ForAggregate(0)]);

        var rows = Run(program, Rows([10], [20], [30]));

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(60));
    }

    [Test]
    public void ScalarAggregationAlwaysEmitsOneRowEvenOverAnEmptyTable()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [Sum(0)],
            outputs: [AggregateOutput.ForAggregate(0)]);

        var rows = Run(program, new VdbeCursorSource([]));

        rows.Should().ContainSingle();
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ScalarCountStarOverAnEmptyTableIsZero()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [CountStar()],
            outputs: [AggregateOutput.ForAggregate(0)]);

        var rows = Run(program, new VdbeCursorSource([]));

        rows[0].Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void ScalarComputesMultipleAggregatesInASinglePass()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates:
            [
                CountStar(),
                Sum(0),
                new AggregateFunctionSpec(AggregateTestSupport.Min(), [0]),
                new AggregateFunctionSpec(AggregateTestSupport.Max(), [0]),
                new AggregateFunctionSpec(AggregateTestSupport.Avg(), [0]),
            ],
            outputs:
            [
                AggregateOutput.ForAggregate(0),
                AggregateOutput.ForAggregate(1),
                AggregateOutput.ForAggregate(2),
                AggregateOutput.ForAggregate(3),
                AggregateOutput.ForAggregate(4),
            ]);

        var rows = Run(program, Rows([2], [5], [3]));

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(3));
        rows[0][1].Should().Be(SqlValue.Integer(10));
        rows[0][2].Should().Be(SqlValue.Integer(2));
        rows[0][3].Should().Be(SqlValue.Integer(5));
        rows[0][4].AsReal().Should().BeApproximately(10.0 / 3.0, 1e-9);
    }

    [Test]
    public void ScalarAppliesTheWherePredicateBeforeAggregating()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [CountStar(), Sum(0)],
            outputs: [AggregateOutput.ForAggregate(0), AggregateOutput.ForAggregate(1)],
            predicate: row => row[0].AsInteger() > 2);

        var rows = Run(program, Rows([1], [2], [3], [4]));

        rows[0][0].Should().Be(SqlValue.Integer(2));
        rows[0][1].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void ScalarProjectsConstantsAlongsideAggregates()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [Sum(0)],
            outputs: [AggregateOutput.ForConstant(SqlValue.Integer(42)), AggregateOutput.ForAggregate(0)]);

        var rows = Run(program, Rows([1], [2]));

        rows[0][0].Should().Be(SqlValue.Integer(42));
        rows[0][1].Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void ScalarProgramReplaysAfterReset()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [Sum(0)],
            outputs: [AggregateOutput.ForAggregate(0)]);

        using var statement = new ResumableStatement(program, [Rows([1], [2], [3])]);
        Drain(statement)[0].Should().Equal(SqlValue.Integer(6));

        statement.Reset();

        Drain(statement)[0].Should().Equal(SqlValue.Integer(6));
    }

    [Test]
    public void BuildScalarValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AggregateProgramBuilder.BuildScalar(
            null!, 1, [Sum(0)], [AggregateOutput.ForAggregate(0)]));

        Assert.Throws<ArgumentOutOfRangeException>(() => AggregateProgramBuilder.BuildScalar(
            "t", 0, [Sum(0)], [AggregateOutput.ForAggregate(0)]));

        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildScalar(
            "t", 1, [], [AggregateOutput.ForAggregate(0)]));

        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildScalar(
            "t", 1, [Sum(0)], []));

        // A scalar aggregation cannot project a group key.
        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildScalar(
            "t", 1, [Sum(0)], [AggregateOutput.ForGroupKey(0)]));

        // Aggregate output index beyond the declared aggregates.
        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildScalar(
            "t", 1, [Sum(0)], [AggregateOutput.ForAggregate(1)]));

        // Aggregate argument column outside the table.
        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildScalar(
            "t", 1, [Sum(3)], [AggregateOutput.ForAggregate(0)]));
    }

    [Test]
    public void BuildGroupedEmitsTheIngestSortDrainPipeline()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyColumns: [0],
            aggregates: [Sum(1)],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0),
            groupComparer: AggregateTestSupport.GroupKeysEqual());

        program.RegisterCount.Should().Be(8);
        program.CursorCount.Should().Be(1);
        program.SorterCount.Should().Be(1);
        program.AccumulatorCount.Should().Be(1);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenSorter,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Column,
            VdbeOpcode.SorterInsert,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.SorterSort,
            VdbeOpcode.SorterData,
            VdbeOpcode.Copy,
            VdbeOpcode.AggReset,
            VdbeOpcode.Copy,
            VdbeOpcode.AggStep,
            VdbeOpcode.SorterNext,
            VdbeOpcode.Goto,
            VdbeOpcode.SorterData,
            VdbeOpcode.Copy,
            VdbeOpcode.SameGroup,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.AggReset,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.AggStep,
            VdbeOpcode.SorterNext,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.CloseSorter,
            VdbeOpcode.Halt);

        ((RewindCursorInstruction)program.Instructions[2]).EmptyTarget.Offset.Should().Be(8);
        ((NextInstruction)program.Instructions[6]).LoopTarget.Offset.Should().Be(3);
        ((SorterSortInstruction)program.Instructions[8]).EmptyTarget.Offset.Should().Be(32);
        ((SorterNextInstruction)program.Instructions[14]).LoopTarget.Offset.Should().Be(16);
        ((GotoInstruction)program.Instructions[15]).Target.Offset.Should().Be(28);
        ((SameGroupInstruction)program.Instructions[18]).SameGroupTarget.Offset.Should().Be(25);
        ((SorterNextInstruction)program.Instructions[27]).LoopTarget.Offset.Should().Be(16);
    }

    [Test]
    public void GroupedSumsEachGroupInKeyOrder()
    {
        var program = BuildGroupedSum();

        var rows = Run(program, Rows([1, 10], [2, 5], [1, 20], [2, 7], [3, 1]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
        rows.Select(row => row[1].AsInteger()).Should().Equal(30, 12, 1);
    }

    [Test]
    public void GroupedAggregationOverAnEmptyTableProducesNoRows()
    {
        var program = BuildGroupedSum();

        Run(program, new VdbeCursorSource([])).Should().BeEmpty();
    }

    [Test]
    public void GroupedSingleRowGroupTakesTheGotoPath()
    {
        var program = BuildGroupedSum();

        var rows = Run(program, Rows([7, 42]));

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(7), SqlValue.Integer(42));
    }

    [Test]
    public void GroupedNullKeysFallInOneGroup()
    {
        var program = BuildGroupedSum();

        var rows = Run(program, Rows([null, 5], [1, 10], [null, 7]));

        rows.Should().HaveCount(2);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
        rows[0][1].Should().Be(SqlValue.Integer(12));
        rows[1][0].Should().Be(SqlValue.Integer(1));
        rows[1][1].Should().Be(SqlValue.Integer(10));
    }

    [Test]
    public void GroupedCountStarCountsRowsPerGroup()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 1,
            groupKeyColumns: [0],
            aggregates: [CountStar()],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0),
            groupComparer: AggregateTestSupport.GroupKeysEqual());

        var rows = Run(program, Rows([1], [1], [2]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 2);
        rows.Select(row => row[1].AsInteger()).Should().Equal(2, 1);
    }

    [Test]
    public void GroupedSupportsMultiColumnKeys()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 3,
            groupKeyColumns: [0, 1],
            aggregates: [Sum(2)],
            outputs:
            [
                AggregateOutput.ForGroupKey(0),
                AggregateOutput.ForGroupKey(1),
                AggregateOutput.ForAggregate(0),
            ],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            groupComparer: AggregateTestSupport.GroupKeysEqual());

        var rows = Run(program, Rows([1, 1, 10], [1, 2, 5], [1, 1, 20], [1, 2, 7]));

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(30));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(12));
    }

    [Test]
    public void GroupedRespectsCaseInsensitiveGroupingSuppliedByTheDelegates()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 1,
            groupKeyColumns: [0],
            aggregates: [CountStar()],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: AggregateTestSupport.OrderByTextNoCase(0),
            groupComparer: AggregateTestSupport.GroupTextNoCase());

        var rows = Run(program, Rows(["a"], ["A"], ["b"]));

        rows.Should().HaveCount(2);
        rows[0][0].AsText().Should().Be("a");
        rows[0][1].Should().Be(SqlValue.Integer(2));
        rows[1][0].AsText().Should().Be("b");
        rows[1][1].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void GroupedComputesMultipleAggregatesPerGroup()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyColumns: [0],
            aggregates:
            [
                CountStar(),
                Sum(1),
                new AggregateFunctionSpec(AggregateTestSupport.Max(), [1]),
            ],
            outputs:
            [
                AggregateOutput.ForGroupKey(0),
                AggregateOutput.ForAggregate(0),
                AggregateOutput.ForAggregate(1),
                AggregateOutput.ForAggregate(2),
            ],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0),
            groupComparer: AggregateTestSupport.GroupKeysEqual());

        var rows = Run(program, Rows([1, 10], [1, 20], [2, 5]));

        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(30), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1), SqlValue.Integer(5), SqlValue.Integer(5));
    }

    [Test]
    public void GroupedAppliesTheWherePredicateBeforeGrouping()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyColumns: [0],
            aggregates: [Sum(1)],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0),
            groupComparer: AggregateTestSupport.GroupKeysEqual(),
            predicate: row => row[1].AsInteger() > 10);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Contain(VdbeOpcode.Filter);

        var rows = Run(program, Rows([1, 10], [1, 20], [2, 5], [2, 50]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 2);
        rows.Select(row => row[1].AsInteger()).Should().Equal(20, 50);
    }

    [Test]
    public void GroupedHavingFiltersFinalizedGroupsAndContinuesToTheNextGroup()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyColumns: [0],
            aggregates: [CountStar()],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0),
            groupComparer: AggregateTestSupport.GroupKeysEqual(),
            having: new AggregateHavingFilter(
                [AggregateOutput.ForAggregate(0)],
                values => values[0].AsInteger() >= 2,
                "skip group with fewer than two rows"));

        var filters = program.Instructions
            .OfType<FilterRegistersInstruction>()
            .ToArray();
        filters.Should().HaveCount(2);
        filters.Should().OnlyContain(filter => filter.Row.Count == 1);

        var rows = Run(program, Rows([1, 10], [2, 20], [2, 30], [3, 40]));

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));
    }

    [Test]
    public void GroupedProgramReplaysAfterReset()
    {
        var program = BuildGroupedSum();

        using var statement = new ResumableStatement(program, [Rows([2, 1], [1, 2], [2, 3])]);
        var first = Drain(statement);
        first.Select(row => row[0].AsInteger()).Should().Equal(1, 2);
        first.Select(row => row[1].AsInteger()).Should().Equal(2, 4);

        statement.Reset();

        var second = Drain(statement);
        second.Select(row => row[0].AsInteger()).Should().Equal(1, 2);
        second.Select(row => row[1].AsInteger()).Should().Equal(2, 4);
    }

    [Test]
    public void RowAwareGroupedProgramResumesAndClearsBothSortersAndGroupIdsOnReset()
    {
        var program = AggregateProgramBuilder.BuildRowGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyCount: 1,
            groupKeyProjector: row => [row[0]],
            groupEquality: (left, right) => left[0].Equals(right[0]),
            groupKeyOrder: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()),
            collector: CollectedRowsAggregate("rows", _ => SqlValue.Null),
            outputs:
            [
                CollectedRowsAggregate("group-key", rows => rows[0][0]),
                CollectedRowsAggregate(
                    "sum",
                    rows => SqlValue.Integer(rows.Sum(row => row[1].AsInteger()))),
            ],
            orderKeys: [],
            outputOrderComparer: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()));
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(2), SqlValue.Integer(20) },
            new[] { SqlValue.Integer(1), SqlValue.Integer(10) },
            new[] { SqlValue.Integer(2), SqlValue.Integer(5) },
            new[] { SqlValue.Integer(1), SqlValue.Integer(7) },
        };
        using var statement = new ResumableStatement(program, [new VdbeCursorSource(rows)]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(17));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(25));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        statement.Reset();
        rows.Add([SqlValue.Integer(3), SqlValue.Integer(4)]);
        rows.Add([SqlValue.Integer(2), SqlValue.Integer(1)]);

        var replay = Drain(statement);
        replay.Should().HaveCount(3);
        replay[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(17));
        replay[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(26));
        replay[2].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
    }

    [Test]
    public void CancellationRaisedBySorterComparisonIsNotWrapped()
    {
        using var cancellation = new CancellationTokenSource();
        var program = AggregateProgramBuilder.BuildRowGrouped(
            "t",
            tableColumnCount: 1,
            groupKeyCount: 1,
            groupKeyProjector: row => [row[0]],
            groupEquality: (left, right) => left[0].Equals(right[0]),
            groupKeyOrder: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()),
            collector: CollectedRowsAggregate("rows", _ => SqlValue.Null),
            outputs: [CollectedRowsAggregate("group-key", rows => rows[0][0])],
            orderKeys: [],
            outputOrderComparer: (_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return 0;
            });
        using var statement = new ResumableStatement(
            program,
            [Rows([1], [2], [3])]);

        Assert.Throws<OperationCanceledException>(
            () => statement.StepResumable(cancellation.Token));
    }

    [Test]
    public void HashableGroupKeysAvoidQuadraticEqualityProbes()
    {
        var equalityCalls = 0;
        var program = AggregateProgramBuilder.BuildRowGrouped(
            "t",
            tableColumnCount: 1,
            groupKeyCount: 1,
            groupKeyProjector: row => [row[0]],
            groupEquality: (left, right) =>
            {
                equalityCalls++;
                return left[0].Equals(right[0]);
            },
            groupKeyOrder: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()),
            collector: CollectedRowsAggregate("rows", _ => SqlValue.Null),
            outputs: [CollectedRowsAggregate("group-key", rows => rows[0][0])],
            orderKeys: [],
            outputOrderComparer: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()),
            groupHasher: key => key[0].AsInteger().GetHashCode());
        var rows = Enumerable.Range(0, 1_000)
            .Select(value => new[] { SqlValue.Integer(value) })
            .ToArray();

        Run(program, new VdbeCursorSource(rows)).Should().HaveCount(1_000);
        equalityCalls.Should().BeLessThan(100);
    }

    [Test]
    public void CompoundTermsResetCompletedAggregateContexts()
    {
        VdbeProgram Build(string table) => AggregateProgramBuilder.BuildRowScalar(
            table,
            tableColumnCount: 1,
            collector: CollectedRowsAggregate("rows", _ => SqlValue.Null),
            outputs:
            [
                CollectedRowsAggregate(
                    "count",
                    rows => SqlValue.Integer(rows.Count)),
            ]);
        var combined = CompoundProgramBuilder.BuildUnionAll(
        [
            new CompoundTerm(Build("first"), [Rows([1])]),
            new CompoundTerm(Build("second"), [Rows([2])]),
        ]);

        var secondOpen = combined.Program.Instructions
            .Select((instruction, index) => (instruction, index))
            .Single(entry => entry.instruction is OpenReadCursorInstruction { Cursor.Index: 1 })
            .index;
        combined.Program.Instructions[secondOpen - 1]
            .Should().BeOfType<AggResetInstruction>()
            .Which.Accumulator.Should().Be(new Accumulator(0));

        RunToCompletion(combined.Program, combined.CursorSources)
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 1);
    }

    [Test]
    public void SetOperationsRelocateGroupedAggregateState()
    {
        static VdbeProgram Build() => AggregateProgramBuilder.BuildRowGrouped(
            "t",
            tableColumnCount: 1,
            groupKeyCount: 1,
            groupKeyProjector: row => [row[0]],
            groupEquality: (left, right) => left[0].Equals(right[0]),
            groupKeyOrder: (left, right) => left[0].AsInteger().CompareTo(right[0].AsInteger()),
            collector: CollectedRowsAggregate("rows", _ => SqlValue.Null),
            outputs:
            [
                CollectedRowsAggregate("key", rows => rows[0][0]),
                CollectedRowsAggregate("count", rows => SqlValue.Integer(rows.Count)),
            ],
            orderKeys: [],
            outputOrderComparer: (left, right) =>
                left[0].AsInteger().CompareTo(right[0].AsInteger()),
            groupHasher: key => key[0].GetHashCode());
        static CompoundTerm Term(VdbeCursorSource source) =>
            new(Build(), [source]);
        static bool Equality(SqlValue[] left, SqlValue[] right) =>
            left.SequenceEqual(right);

        var intersect = CompoundProgramBuilder.BuildIntersect(
            [Term(Rows([1], [2])), Term(Rows([2], [3]))],
            Equality);
        RunToCompletion(intersect.Program, intersect.CursorSources)
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));

        var except = CompoundProgramBuilder.BuildExcept(
            [Term(Rows([1], [2])), Term(Rows([2], [3]))],
            Equality);
        RunToCompletion(except.Program, except.CursorSources)
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
    }

    [Test]
    public void BuildGroupedValidatesItsArguments()
    {
        var order = AggregateTestSupport.OrderByColumns(0);
        var group = AggregateTestSupport.GroupKeysEqual();

        Assert.Throws<ArgumentNullException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, [0], [Sum(1)], [AggregateOutput.ForAggregate(0)], null!, group));

        Assert.Throws<ArgumentNullException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, [0], [Sum(1)], [AggregateOutput.ForAggregate(0)], order, null!));

        Assert.Throws<ArgumentNullException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, null!, [Sum(1)], [AggregateOutput.ForAggregate(0)], order, group));

        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, [], [Sum(1)], [AggregateOutput.ForAggregate(0)], order, group));

        // Group-key column outside the table.
        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, [5], [Sum(1)], [AggregateOutput.ForAggregate(0)], order, group));

        // Group-key output index beyond the declared group columns.
        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, [0], [Sum(1)], [AggregateOutput.ForGroupKey(1)], order, group));

        // Aggregate output index beyond the declared aggregates.
        Assert.Throws<ArgumentException>(() => AggregateProgramBuilder.BuildGrouped(
            "t", 2, [0], [Sum(1)], [AggregateOutput.ForAggregate(2)], order, group));
    }

    [Test]
    public void AggregateOutputFactoriesRejectNegativeIndexes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AggregateOutput.ForGroupKey(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AggregateOutput.ForAggregate(-1));
    }

    private static VdbeProgram BuildGroupedSum() =>
        AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyColumns: [0],
            aggregates: [Sum(1)],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: AggregateTestSupport.OrderByColumns(0),
            groupComparer: AggregateTestSupport.GroupKeysEqual());

    private static VdbeAggregate CollectedRowsAggregate(
        string name,
        Func<IReadOnlyList<SqlValue[]>, SqlValue> finalize) => new()
        {
            Name = name,
            CreateContext = static () => new List<SqlValue[]>(),
            Accumulate = static (context, row) =>
            {
                ((List<SqlValue[]>)context!).Add(row);
                return context;
            },
            Finalize = context => finalize((List<SqlValue[]>)context!),
        };

    private static VdbeCursorSource Rows(params object?[][] rows)
    {
        var materialized = new List<SqlValue[]>(rows.Length);
        foreach (var row in rows)
        {
            var values = new SqlValue[row.Length];
            for (var column = 0; column < row.Length; column++)
            {
                values[column] = row[column] switch
                {
                    null => SqlValue.Null,
                    int integer => SqlValue.Integer(integer),
                    long integer => SqlValue.Integer(integer),
                    string text => SqlValue.Text(text),
                    _ => throw new InvalidOperationException($"Unsupported cell type {row[column]!.GetType()}."),
                };
            }

            materialized.Add(values);
        }

        return new VdbeCursorSource(materialized);
    }

    private static List<SqlValue[]> Run(VdbeProgram program, VdbeCursorSource source)
    {
        using var statement = new ResumableStatement(program, [source]);
        return Drain(statement);
    }

    private static List<SqlValue[]> RunToCompletion(
        VdbeProgram program,
        IReadOnlyList<VdbeCursorSource> sources)
    {
        using var statement = new ResumableStatement(program, sources);
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

    // A read-only row list that records the highest index accessed so a test can prove the
    // COUNT(*) shortcut reads the row count without iterating any row.
    private sealed class TrackingRows : IReadOnlyList<SqlValue[]>
    {
        private readonly SqlValue[][] _rows;

        public TrackingRows(SqlValue[][] rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            _rows = rows;
        }

        public int MaxIndexAccessed { get; private set; } = -1;

        public SqlValue[] this[int index]
        {
            get
            {
                MaxIndexAccessed = Math.Max(MaxIndexAccessed, index);
                return _rows[index];
            }
        }

        public int Count => _rows.Length;

        public IEnumerator<SqlValue[]> GetEnumerator() => ((IEnumerable<SqlValue[]>)_rows).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _rows.GetEnumerator();
    }
}
