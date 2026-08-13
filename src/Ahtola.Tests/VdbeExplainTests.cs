using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// EXPLAIN description coverage for the sorter opcode family and for a whole built
// sorted-scan program. Confirms the addr/opcode/p1/p2/p3/p4/comment shape matches the
// wired database conventions for shared opcodes and extends them to the sorter family.
public class VdbeExplainTests
{
    private static readonly VdbeRowComparer Comparer = (left, right) => 0;

    [Test]
    public void ColumnsMatchTheExplainResultSetShape()
    {
        VdbeExplain.Columns().Should().Equal("addr", "opcode", "p1", "p2", "p3", "p4", "comment");
    }

    [Test]
    public void DescribesOpenSorter()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new OpenSorterInstruction(new Sorter(0), Comparer, 3));

        p1.Should().Be(0);
        p2.Should().Be(0);
        p3.Should().Be(3);
        p4.Should().BeNull();
        comment.Should().Be("open sorter 0 (3 cols)");
    }

    [Test]
    public void DescribesSorterInsertForSingleAndMultiRegisterRecords()
    {
        var multi = VdbeExplain.Describe(
            new SorterInsertInstruction(new Sorter(0), new RegisterRange(new Register(2), 3)));
        multi.P1.Should().Be(0);
        multi.P2.Should().Be(2);
        multi.P3.Should().Be(3);
        multi.Comment.Should().Be("sorter 0 insert r[2..4]");

        var single = VdbeExplain.Describe(
            new SorterInsertInstruction(new Sorter(1), new RegisterRange(new Register(1), 1)));
        single.P1.Should().Be(1);
        single.Comment.Should().Be("sorter 1 insert r[1]");
    }

    [Test]
    public void DescribesSorterSort()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new SorterSortInstruction(new Sorter(0), new ProgramCounter(7)));

        p1.Should().Be(0);
        p2.Should().Be(7);
        p3.Should().Be(0);
        p4.Should().BeNull();
        comment.Should().Be("sort sorter 0, goto 7 if empty");
    }

    [Test]
    public void DescribesSorterData()
    {
        var (p1, p2, p3, _, comment) = VdbeExplain.Describe(
            new SorterDataInstruction(new Sorter(0), new RegisterRange(new Register(0), 3)));

        p1.Should().Be(0);
        p2.Should().Be(0);
        p3.Should().Be(3);
        comment.Should().Be("r[0..2]=sorter 0 data");
    }

    [Test]
    public void DescribesSorterNext()
    {
        var (p1, p2, _, _, comment) = VdbeExplain.Describe(
            new SorterNextInstruction(new Sorter(0), new ProgramCounter(4)));

        p1.Should().Be(0);
        p2.Should().Be(4);
        comment.Should().Be("next sorter 0, goto 4 if more rows");
    }

    [Test]
    public void DescribesCloseSorter()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(new CloseSorterInstruction(new Sorter(2)));

        p1.Should().Be(2);
        p2.Should().Be(0);
        p3.Should().Be(0);
        p4.Should().BeNull();
        comment.Should().Be("close sorter 2");
    }

    [Test]
    public void DescribesAWholeBuiltSortedScanProgram()
    {
        var program = SortedScanProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            projections: [SortedScanColumn.ForColumn(0), SortedScanColumn.ForColumn(1)],
            comparer: Comparer);

        var explain = VdbeExplain.Describe(program);

        explain.Should().HaveCount(16);
        explain.Select(row => row[0].AsInteger())
            .Should().Equal(Enumerable.Range(0, 16).Select(index => (long)index));

        explain.Select(row => row[1].AsText()).Should().Equal(
            "OpenReadCursor",
            "OpenSorter",
            "Rewind",
            "Column",
            "Column",
            "SorterInsert",
            "Next",
            "CloseCursor",
            "SorterSort",
            "SorterData",
            "Copy",
            "Copy",
            "ResultRow",
            "SorterNext",
            "CloseSorter",
            "Halt");

        explain.Select(row => row[6].AsText()).Should().Equal(
            "open read cursor 0 on t (2 cols)",
            "open sorter 0 (2 cols)",
            "rewind cursor 0, goto 8 if empty",
            "r[0]=c0.col[0]",
            "r[1]=c0.col[1]",
            "sorter 0 insert r[0..1]",
            "next cursor 0, goto 3 if more rows",
            "close cursor 0",
            "sort sorter 0, goto 14 if empty",
            "r[0..1]=sorter 0 data",
            "r[2]=r[0]",
            "r[3]=r[1]",
            "output=r[2..3]",
            "next sorter 0, goto 9 if more rows",
            "close sorter 0",
            "halt");

        // p4 carries the table name only for the cursor open.
        explain[0][5].AsText().Should().Be("t");
        explain[1][5].Kind.Should().Be(SqlValueKind.Null);

        // Spot-check the p1/p2/p3 operands on the control-flow rows.
        explain[2].Skip(2).Take(3).Select(cell => cell.AsInteger()).Should().Equal(0, 8, 0);
        explain[8].Skip(2).Take(3).Select(cell => cell.AsInteger()).Should().Equal(0, 14, 0);
        explain[13].Skip(2).Take(3).Select(cell => cell.AsInteger()).Should().Equal(0, 9, 0);
    }

    [Test]
    public void DescribeRejectsAnUnsupportedInstruction()
    {
        Assert.Throws<VdbeProgramValidationException>(() => VdbeExplain.Describe(new UnknownInstruction()));
    }

    private sealed record UnknownInstruction : VdbeInstruction
    {
        public override VdbeOpcode Opcode => (VdbeOpcode)(-1);
    }
}
