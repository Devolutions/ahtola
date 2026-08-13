using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Compiler-output and execution coverage for the sorted-scan lowering. The builder is
// the reusable ORDER BY lowering; these tests assert its emitted bytecode shape and run
// the programs through the resumable state machine to confirm the ordered results.
public class SortedScanProgramBuilderTests
{
    private static VdbeRowComparer AscendingBy(int column) =>
        (left, right) => left[column].AsInteger().CompareTo(right[column].AsInteger());

    private static VdbeRowComparer DescendingBy(int column) =>
        (left, right) => right[column].AsInteger().CompareTo(left[column].AsInteger());

    [Test]
    public void BuildEmitsTheScanSortDrainPipelineWithoutAPredicate()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            projections: [SortedScanColumn.ForColumn(0), SortedScanColumn.ForColumn(1)],
            comparer: AscendingBy(0));

        program.RegisterCount.Should().Be(4);
        program.CursorCount.Should().Be(1);
        program.SorterCount.Should().Be(1);

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
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.SorterNext,
            VdbeOpcode.CloseSorter,
            VdbeOpcode.Halt);

        ((RewindCursorInstruction)program.Instructions[2]).EmptyTarget.Offset.Should().Be(8);
        ((NextInstruction)program.Instructions[6]).LoopTarget.Offset.Should().Be(3);
        ((SorterSortInstruction)program.Instructions[8]).EmptyTarget.Offset.Should().Be(14);
        ((SorterNextInstruction)program.Instructions[13]).LoopTarget.Offset.Should().Be(9);

        var insert = (SorterInsertInstruction)program.Instructions[5];
        insert.Record.Start.Index.Should().Be(0);
        insert.Record.Count.Should().Be(2);

        var data = (SorterDataInstruction)program.Instructions[9];
        data.Destination.Start.Index.Should().Be(0);
        data.Destination.Count.Should().Be(2);

        var firstCopy = (CopyInstruction)program.Instructions[10];
        firstCopy.Source.Index.Should().Be(0);
        firstCopy.Destination.Index.Should().Be(2);

        var result = (ResultRowInstruction)program.Instructions[12];
        result.Values.Start.Index.Should().Be(2);
        result.Values.Count.Should().Be(2);
    }

    [Test]
    public void BuildInsertsAFilterStageAndShiftsTheLayoutWhenGivenAPredicate()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            projections: [SortedScanColumn.ForColumn(0)],
            comparer: AscendingBy(0),
            predicate: row => row[0].AsInteger() > 1);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenSorter,
            VdbeOpcode.Rewind,
            VdbeOpcode.Filter,
            VdbeOpcode.Column,
            VdbeOpcode.SorterInsert,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.SorterSort,
            VdbeOpcode.SorterData,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.SorterNext,
            VdbeOpcode.CloseSorter,
            VdbeOpcode.Halt);

        var filter = (FilterInstruction)program.Instructions[3];
        filter.FalseTarget.Offset.Should().Be(6);
        filter.Description.Should().Be("skip row when WHERE is false, goto 6");

        ((RewindCursorInstruction)program.Instructions[2]).EmptyTarget.Offset.Should().Be(8);
        ((SorterSortInstruction)program.Instructions[8]).EmptyTarget.Offset.Should().Be(13);
    }

    [Test]
    public void ProgramSortsScannedRowsAscending()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            projections: [SortedScanColumn.ForColumn(0), SortedScanColumn.ForColumn(1)],
            comparer: AscendingBy(0));

        var rows = Run(program, Rows([3, "c"], [1, "a"], [2, "b"]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
        rows.Select(row => row[1].AsText()).Should().Equal("a", "b", "c");
    }

    [Test]
    public void ProgramSortsScannedRowsDescending()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            projections: [SortedScanColumn.ForColumn(0), SortedScanColumn.ForColumn(1)],
            comparer: DescendingBy(0));

        var rows = Run(program, Rows([3, "c"], [1, "a"], [2, "b"]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(3, 2, 1);
    }

    [Test]
    public void ProgramCanOrderByAColumnItDoesNotProject()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            projections: [SortedScanColumn.ForColumn(1)],
            comparer: AscendingBy(0));

        var rows = Run(program, Rows([3, "x"], [1, "y"], [2, "z"]));

        rows.Should().AllSatisfy(row => row.Should().HaveCount(1));
        rows.Select(row => row[0].AsText()).Should().Equal("y", "z", "x");
    }

    [Test]
    public void ProgramProjectsAMixOfConstantsAndColumns()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            projections: [SortedScanColumn.ForConstant(SqlValue.Integer(9)), SortedScanColumn.ForColumn(0)],
            comparer: AscendingBy(0));

        var rows = Run(program, Rows([2, "p"], [1, "q"]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(9, 9);
        rows.Select(row => row[1].AsInteger()).Should().Equal(1, 2);
    }

    [Test]
    public void ProgramAppliesTheWherePredicateBeforeSorting()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            projections: [SortedScanColumn.ForColumn(0)],
            comparer: AscendingBy(0),
            predicate: row => row[0].AsInteger() > 1);

        var rows = Run(program, Rows([3], [1], [2]));

        rows.Select(row => row[0].AsInteger()).Should().Equal(2, 3);
    }

    [Test]
    public void ProgramProducesNoRowsForAnEmptyTable()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            projections: [SortedScanColumn.ForColumn(0)],
            comparer: AscendingBy(0));

        Run(program, new VdbeCursorSource([])).Should().BeEmpty();
    }

    [Test]
    public void ProgramProducesNoRowsWhenEveryRowIsFilteredOut()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            projections: [SortedScanColumn.ForColumn(0)],
            comparer: AscendingBy(0),
            predicate: _ => false);

        Run(program, Rows([1], [2], [3])).Should().BeEmpty();
    }

    [Test]
    public void ProgramReplaysAfterReset()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 1,
            projections: [SortedScanColumn.ForColumn(0)],
            comparer: AscendingBy(0));

        using var statement = new ResumableStatement(program, [Rows([2], [1], [3])]);
        Drain(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);

        statement.Reset();

        Drain(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2, 3);
    }

    [Test]
    public void BuildValidatesItsArguments()
    {
        var comparer = AscendingBy(0);

        Assert.Throws<ArgumentNullException>(() => SortedScanProgramBuilder.Build(
            null!, 1, [SortedScanColumn.ForColumn(0)], comparer));

        Assert.Throws<ArgumentNullException>(() => SortedScanProgramBuilder.Build(
            "t", 1, null!, comparer));

        Assert.Throws<ArgumentNullException>(() => SortedScanProgramBuilder.Build(
            "t", 1, [SortedScanColumn.ForColumn(0)], null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => SortedScanProgramBuilder.Build(
            "t", 0, [SortedScanColumn.ForColumn(0)], comparer));

        Assert.Throws<ArgumentException>(() => SortedScanProgramBuilder.Build(
            "t", 1, [], comparer));

        Assert.Throws<ArgumentException>(() => SortedScanProgramBuilder.Build(
            "t", 1, [SortedScanColumn.ForColumn(1)], comparer));
    }

    [Test]
    public void SortedScanColumnRejectsNegativeColumnOrdinals()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SortedScanColumn.ForColumn(-1));
    }

    private static VdbeCursorSource Rows(params object[][] rows)
    {
        var materialized = new List<SqlValue[]>(rows.Length);
        foreach (var row in rows)
        {
            var values = new SqlValue[row.Length];
            for (var column = 0; column < row.Length; column++)
            {
                values[column] = row[column] switch
                {
                    int integer => SqlValue.Integer(integer),
                    long integer => SqlValue.Integer(integer),
                    string text => SqlValue.Text(text),
                    _ => throw new InvalidOperationException($"Unsupported cell type {row[column].GetType()}."),
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
