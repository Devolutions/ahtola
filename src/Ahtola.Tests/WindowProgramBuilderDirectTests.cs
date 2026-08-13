using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Compiler-output and execution coverage for the direct window-function lowering
// (WindowProgramBuilder). The builder lowers a partitioned running-aggregate window —
// the ROWS UNBOUNDED PRECEDING TO CURRENT ROW frame — into the sorter + aggregate opcode
// families. These tests assert the emitted bytecode shape/jump layout and run the programs
// through the resumable state machine to confirm real observable rows: per-partition
// running values, row_number ordering, tie/NULL handling, empty/single-row/replay
// behavior, and the frame/argument rejections. row_number() is modeled as a running
// count(*); running sum/count/avg/min/max follow from their accumulators.
public class WindowProgramBuilderDirectTests
{
    private static AggregateFunctionSpec Sum(int column) => new(AggregateTestSupport.Sum(), [column]);

    private static AggregateFunctionSpec RowNumber() => new(AggregateTestSupport.CountStar(), []);

    [Test]
    public void BuildEmitsTheIngestSortDrainPipelineForAPartitionedRunningSum()
    {
        var program = BuildPartitionedSum();

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
            VdbeOpcode.Copy,       // save partition key
            VdbeOpcode.AggReset,
            VdbeOpcode.Goto,
            VdbeOpcode.SorterData,
            VdbeOpcode.Copy,       // load current partition key
            VdbeOpcode.SameGroup,
            VdbeOpcode.AggReset,   // partition boundary
            VdbeOpcode.Copy,       // adopt new partition key
            VdbeOpcode.Copy,       // gather sum argument
            VdbeOpcode.AggStep,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.Copy,       // output column
            VdbeOpcode.Copy,       // output window value
            VdbeOpcode.ResultRow,
            VdbeOpcode.SorterNext,
            VdbeOpcode.CloseSorter,
            VdbeOpcode.Halt);

        ((RewindCursorInstruction)program.Instructions[2]).EmptyTarget.Offset.Should().Be(8);
        ((NextInstruction)program.Instructions[6]).LoopTarget.Offset.Should().Be(3);
        ((SorterSortInstruction)program.Instructions[8]).EmptyTarget.Offset.Should().Be(25);
        ((GotoInstruction)program.Instructions[12]).Target.Offset.Should().Be(18);
        ((SameGroupInstruction)program.Instructions[15]).SameGroupTarget.Offset.Should().Be(18);
        ((SorterNextInstruction)program.Instructions[24]).LoopTarget.Offset.Should().Be(13);
    }

    [Test]
    public void BuildEmitsAFilterStageWhenGivenAPredicate()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows: [Sum(1)],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual(),
            predicate: row => row[1].AsInteger() > 5);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Contain(VdbeOpcode.Filter);

        var filter = (FilterInstruction)program.Instructions.First(i => i is FilterInstruction);
        // The false target is the ingest-loop Next, so a filtered row is never materialized.
        program.Instructions[filter.FalseTarget.Offset].Should().BeOfType<NextInstruction>();
    }

    [Test]
    public void BuildOmitsPartitionBoundaryOpcodesForAnUnpartitionedWindow()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            partitionColumns: [],
            windows: [RowNumber()],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0));

        // With no PARTITION BY there is exactly one partition, so no SameGroup boundary check is emitted.
        program.Instructions.Select(instruction => instruction.Opcode).Should().NotContain(VdbeOpcode.SameGroup);
        program.Instructions.Count(instruction => instruction.Opcode == VdbeOpcode.AggReset).Should().Be(1);
        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenSorter,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.SorterInsert,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.SorterSort,
            VdbeOpcode.SorterData,
            VdbeOpcode.AggReset,
            VdbeOpcode.Goto,
            VdbeOpcode.SorterData,
            VdbeOpcode.AggStep,
            VdbeOpcode.AggFinalize,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.SorterNext,
            VdbeOpcode.CloseSorter,
            VdbeOpcode.Halt);
    }

    [Test]
    public void RowNumberEnumeratesRowsWithinEachPartitionInOrder()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows: [RowNumber()],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForColumn(1), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual());

        var rows = Run(program, Rows([1, 30], [2, 5], [1, 10], [2, 40], [1, 20]));

        // Ordered by (partition, value): partition 1 -> 10,20,30 ; partition 2 -> 5,40.
        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 1, 1, 2, 2);
        rows.Select(row => row[1].AsInteger()).Should().Equal(10, 20, 30, 5, 40);
        rows.Select(row => row[2].AsInteger()).Should().Equal(1, 2, 3, 1, 2);
    }

    [Test]
    public void RunningSumAccumulatesUpToTheCurrentRowAndResetsPerPartition()
    {
        var program = BuildPartitionedSum();

        var rows = Run(program, Rows([1, 10], [1, 20], [1, 30], [2, 7], [2, 3]));

        // Rows sort by (partition, value): partition 1 -> 10,20,30 (running 10,30,60);
        // partition 2 -> 3,7 (running 3,10). The accumulator resets at the boundary.
        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 1, 1, 2, 2);
        rows.Select(row => row[1].AsInteger()).Should().Equal(10, 30, 60, 3, 10);
    }

    [Test]
    public void RunningSumOverAnUnpartitionedWindowFoldsTheWholeInput()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            partitionColumns: [],
            windows: [Sum(0)],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0));

        var rows = Run(program, Rows([5], [10], [15]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(5, 10, 15);
        rows.Select(row => row[1].AsInteger()).Should().Equal(5, 15, 30);
    }

    [Test]
    public void RunningFrameGivesEachTiedRowItsOwnRowsValueNotAPeerInclusiveOne()
    {
        // ORDER BY o with ties on o=1. The ROWS frame includes rows up to the current row only,
        // so tied rows get 10 then 30 (not the peer-inclusive 30, 30 a RANGE frame would give).
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [],
            windows: [Sum(1)],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForColumn(1), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0));

        var rows = Run(program, Rows([1, 10], [1, 20], [2, 30]));

        rows.Select(row => row[2].AsInteger()).Should().Equal(10, 30, 60);
    }

    [Test]
    public void NullPartitionKeysFallInOneRunningPartition()
    {
        var program = BuildPartitionedSum();

        var rows = Run(program, Rows([null, 5], [1, 10], [null, 7]));

        // NULL keys sort first and group together: partition NULL -> 5, 12 ; partition 1 -> 10.
        rows.Should().HaveCount(3);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
        rows[0][1].Should().Be(SqlValue.Integer(5));
        rows[1][0].Kind.Should().Be(SqlValueKind.Null);
        rows[1][1].Should().Be(SqlValue.Integer(12));
        rows[2][0].Should().Be(SqlValue.Integer(1));
        rows[2][1].Should().Be(SqlValue.Integer(10));
    }

    [Test]
    public void ComputesMultipleWindowFunctionsSharingOnePartitionInASinglePass()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows:
            [
                RowNumber(),
                Sum(1),
                new AggregateFunctionSpec(AggregateTestSupport.Min(), [1]),
                new AggregateFunctionSpec(AggregateTestSupport.Max(), [1]),
                new AggregateFunctionSpec(AggregateTestSupport.Avg(), [1]),
            ],
            outputs:
            [
                WindowOutput.ForColumn(0),
                WindowOutput.ForWindow(0),
                WindowOutput.ForWindow(1),
                WindowOutput.ForWindow(2),
                WindowOutput.ForWindow(3),
                WindowOutput.ForWindow(4),
            ],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual());

        var rows = Run(program, Rows([1, 10], [1, 30], [1, 20]));

        // Ordered within partition 1: 10, 20, 30. Running row_number / sum / min / max / avg.
        rows.Select(row => row[1].AsInteger()).Should().Equal(1, 2, 3);
        rows.Select(row => row[2].AsInteger()).Should().Equal(10, 30, 60);
        rows.Select(row => row[3].AsInteger()).Should().Equal(10, 10, 10);
        rows.Select(row => row[4].AsInteger()).Should().Equal(10, 20, 30);
        rows[0][5].AsReal().Should().BeApproximately(10.0, 1e-9);
        rows[1][5].AsReal().Should().BeApproximately(15.0, 1e-9);
        rows[2][5].AsReal().Should().BeApproximately(20.0, 1e-9);
    }

    [Test]
    public void ProjectsConstantsAlongsideColumnsAndWindowValues()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows: [Sum(1)],
            outputs:
            [
                WindowOutput.ForConstant(SqlValue.Text("w")),
                WindowOutput.ForColumn(0),
                WindowOutput.ForWindow(0),
            ],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual());

        var rows = Run(program, Rows([1, 10], [1, 20]));

        rows.Should().HaveCount(2);
        rows[0][0].AsText().Should().Be("w");
        rows[0][1].Should().Be(SqlValue.Integer(1));
        rows[0][2].Should().Be(SqlValue.Integer(10));
        rows[1][2].Should().Be(SqlValue.Integer(30));
    }

    [Test]
    public void AppliesTheWherePredicateBeforeWindowing()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows: [Sum(1)],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual(),
            predicate: row => row[1].AsInteger() >= 10);

        var rows = Run(program, Rows([1, 5], [1, 10], [1, 20], [2, 3]));

        // Only rows with value >= 10 survive: partition 1 -> 10, 30 ; partition 2 dropped entirely.
        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 1);
        rows.Select(row => row[1].AsInteger()).Should().Equal(10, 30);
    }

    [Test]
    public void WindowScanOverAnEmptyTableProducesNoRows()
    {
        var program = BuildPartitionedSum();

        Run(program, new VdbeCursorSource([])).Should().BeEmpty();
    }

    [Test]
    public void SingleRowPartitionTakesThePrimeGotoPath()
    {
        var program = BuildPartitionedSum();

        var rows = Run(program, Rows([7, 42]));

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(7), SqlValue.Integer(42));
    }

    [Test]
    public void WindowProgramReplaysAfterReset()
    {
        var program = BuildPartitionedSum();

        using var statement = new ResumableStatement(program, [Rows([2, 1], [1, 2], [2, 3])]);
        var first = Drain(statement);
        first.Select(row => row[0].AsInteger()).Should().Equal(1, 2, 2);
        first.Select(row => row[1].AsInteger()).Should().Equal(2, 1, 4);

        statement.Reset();

        var second = Drain(statement);
        second.Select(row => row[0].AsInteger()).Should().Equal(1, 2, 2);
        second.Select(row => row[1].AsInteger()).Should().Equal(2, 1, 4);
    }

    [Test]
    public void RunningMinAndMaxTrackExtremaUpToTheCurrentRow()
    {
        var program = WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            partitionColumns: [],
            windows:
            [
                new AggregateFunctionSpec(AggregateTestSupport.Min(), [0]),
                new AggregateFunctionSpec(AggregateTestSupport.Max(), [0]),
            ],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0), WindowOutput.ForWindow(1)],
            orderComparer: (_, _) => 0); // preserve insertion order so we can assert running extrema.

        var rows = Run(program, Rows([5], [3], [8], [1]));

        rows.Select(row => row[1].AsInteger()).Should().Equal(5, 3, 3, 1);
        rows.Select(row => row[2].AsInteger()).Should().Equal(5, 5, 8, 8);
    }

    [Test]
    public void BuildRejectsFramesItCannotRepresent()
    {
        // RANGE framing.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(0)],
            AggregateTestSupport.OrderByColumns(0),
            frame: new WindowFrameSpec(WindowFrameMode.Range, WindowBound.UnboundedPreceding, WindowBound.CurrentRow)));

        // GROUPS framing.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(0)],
            AggregateTestSupport.OrderByColumns(0),
            frame: new WindowFrameSpec(WindowFrameMode.Groups, WindowBound.UnboundedPreceding, WindowBound.CurrentRow)));

        // Bounded / forward-looking ROWS bounds.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(0)],
            AggregateTestSupport.OrderByColumns(0),
            frame: new WindowFrameSpec(WindowFrameMode.Rows, WindowBound.Preceding, WindowBound.CurrentRow)));

        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(0)],
            AggregateTestSupport.OrderByColumns(0),
            frame: new WindowFrameSpec(WindowFrameMode.Rows, WindowBound.UnboundedPreceding, WindowBound.Following)));
    }

    [Test]
    public void BuildAcceptsTheExplicitRunningFrame()
    {
        var program = WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(0)],
            AggregateTestSupport.OrderByColumns(0),
            frame: WindowFrameSpec.Running);

        program.AccumulatorCount.Should().Be(1);
        WindowFrameSpec.Running.IsRunning.Should().BeTrue();
    }

    [Test]
    public void BuildValidatesItsArguments()
    {
        var order = AggregateTestSupport.OrderByColumns(0);
        var group = AggregateTestSupport.GroupKeysEqual();

        Assert.Throws<ArgumentNullException>(() => WindowProgramBuilder.Build(
            null!, 1, [], [Sum(0)], [WindowOutput.ForWindow(0)], order));

        Assert.Throws<ArgumentNullException>(() => WindowProgramBuilder.Build(
            "t", 1, null!, [Sum(0)], [WindowOutput.ForWindow(0)], order));

        Assert.Throws<ArgumentNullException>(() => WindowProgramBuilder.Build(
            "t", 1, [], null!, [WindowOutput.ForWindow(0)], order));

        Assert.Throws<ArgumentNullException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], null!, order));

        Assert.Throws<ArgumentNullException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(0)], null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => WindowProgramBuilder.Build(
            "t", 0, [], [Sum(0)], [WindowOutput.ForWindow(0)], order));

        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [], [WindowOutput.ForWindow(0)], order));

        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [], order));

        // Partition column outside the table.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [5], [Sum(0)], [WindowOutput.ForWindow(0)], order, group));

        // Window argument column outside the table.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(3)], [WindowOutput.ForWindow(0)], order));

        // Window output index beyond the declared window functions.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForWindow(1)], order));

        // Column output index outside the table.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 1, [], [Sum(0)], [WindowOutput.ForColumn(5)], order));

        // Partitioned window without a partition comparer.
        Assert.Throws<ArgumentException>(() => WindowProgramBuilder.Build(
            "t", 2, [0], [Sum(1)], [WindowOutput.ForWindow(0)],
            AggregateTestSupport.OrderByColumns(0, 1)));
    }

    [Test]
    public void WindowOutputFactoriesRejectNegativeIndexes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowOutput.ForColumn(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowOutput.ForWindow(-1));
    }

    private static VdbeProgram BuildPartitionedSum() =>
        WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows: [Sum(1)],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual());

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
}
