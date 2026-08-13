using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// EXPLAIN description coverage for the aggregate opcode family (AggReset/AggStep/
// AggFinalize) and its grouped control flow (Goto/SameGroup). Confirms the
// addr/opcode/p1/p2/p3/p4/comment shape matches the sorter-family conventions and
// describes whole built aggregate programs end to end.
public class AggregateExplainTests
{
    private static readonly VdbeGroupComparer GroupComparer = (_, _) => true;

    [Test]
    public void DescribesAggReset()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(new AggResetInstruction(new Accumulator(1)));

        p1.Should().Be(1);
        p2.Should().Be(0);
        p3.Should().Be(0);
        p4.Should().BeNull();
        comment.Should().Be("reset accumulator 1");
    }

    [Test]
    public void DescribesAggStepForSingleAndNullaryArgumentRanges()
    {
        var single = VdbeExplain.Describe(
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(4), 1)));
        single.P1.Should().Be(0);
        single.P2.Should().Be(4);
        single.P3.Should().Be(1);
        single.P4.Should().Be("sum");
        single.Comment.Should().Be("accumulator 0=sum step r[4]");

        var nullary = VdbeExplain.Describe(
            new AggStepInstruction(new Accumulator(2), AggregateTestSupport.CountStar(), new RegisterRange(new Register(0), 0)));
        nullary.P1.Should().Be(2);
        nullary.P3.Should().Be(0);
        nullary.P4.Should().Be("count");
        nullary.Comment.Should().Be("accumulator 2=count step r[]");
    }

    [Test]
    public void DescribesAggFinalize()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(5)));

        p1.Should().Be(0);
        p2.Should().Be(5);
        p3.Should().Be(0);
        p4.Should().Be("sum");
        comment.Should().Be("r[5]=sum finalize accumulator 0");
    }

    [Test]
    public void DescribesGoto()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(new GotoInstruction(new ProgramCounter(9)));

        p1.Should().Be(0);
        p2.Should().Be(9);
        p3.Should().Be(0);
        p4.Should().BeNull();
        comment.Should().Be("goto 9");
    }

    [Test]
    public void DescribesSameGroupForSingleAndMultiColumnKeys()
    {
        var single = VdbeExplain.Describe(new SameGroupInstruction(
            new RegisterRange(new Register(3), 1),
            new RegisterRange(new Register(2), 1),
            GroupComparer,
            new ProgramCounter(7)));
        single.P1.Should().Be(3);
        single.P2.Should().Be(7);
        single.P3.Should().Be(2);
        single.P4.Should().BeNull();
        single.Comment.Should().Be("goto 7 if group r[3]==r[2]");

        var multi = VdbeExplain.Describe(new SameGroupInstruction(
            new RegisterRange(new Register(3), 2),
            new RegisterRange(new Register(1), 2),
            GroupComparer,
            new ProgramCounter(7)));
        multi.Comment.Should().Be("goto 7 if group r[3..4]==r[1..2]");
    }

    [Test]
    public void DescribesComputedGroupKeyAssignment()
    {
        var description = VdbeExplain.Describe(new GroupKeyInstruction(
            new RegisterRange(new Register(2), 3),
            new Register(5),
            KeyCount: 2,
            Projector: row => [row[0], row[1]],
            Equality: AggregateTestSupport.GroupKeysEqual(),
            GroupSetIndex: 0));

        description.P1.Should().Be(2);
        description.P2.Should().Be(5);
        description.P3.Should().Be(2);
        description.P4.Should().Be("group-set[0]");
        description.Comment.Should().Be("r[5]=group key r[2..4] in set 0");
    }

    [Test]
    public void DescribesDistinctAggregateGate()
    {
        var description = VdbeExplain.Describe(new DistinctGateInstruction(
            new RegisterRange(new Register(4), 2),
            static (left, right) => left.SequenceEqual(right),
            DistinctSetIndex: 1,
            DuplicateTarget: new ProgramCounter(9)));

        description.P1.Should().Be(4);
        description.P2.Should().Be(9);
        description.P3.Should().Be(1);
        description.Comment.Should().Be("goto 9 if r[4..5] is in distinct set 1");
    }

    [Test]
    public void DescribesAWholeBuiltScalarProgram()
    {
        var program = AggregateProgramBuilder.BuildScalar(
            "t",
            tableColumnCount: 1,
            aggregates: [new AggregateFunctionSpec(AggregateTestSupport.Sum(), [0])],
            outputs: [AggregateOutput.ForAggregate(0)]);

        var explain = VdbeExplain.Describe(program);

        explain.Should().HaveCount(11);
        explain.Select(row => row[0].AsInteger())
            .Should().Equal(Enumerable.Range(0, 11).Select(index => (long)index));

        explain.Select(row => row[1].AsText()).Should().Equal(
            "OpenReadCursor",
            "AggReset",
            "Rewind",
            "Column",
            "AggStep",
            "Next",
            "CloseCursor",
            "AggFinalize",
            "Copy",
            "ResultRow",
            "Halt");

        explain.Select(row => row[6].AsText()).Should().Equal(
            "open read cursor 0 on t (1 cols)",
            "reset accumulator 0",
            "rewind cursor 0, goto 6 if empty",
            "r[0]=c0.col[0]",
            "accumulator 0=sum step r[0]",
            "next cursor 0, goto 3 if more rows",
            "close cursor 0",
            "r[1]=sum finalize accumulator 0",
            "r[2]=r[1]",
            "output=r[2]",
            "halt");
    }

    [Test]
    public void DescribesTheGotoAndSameGroupRowsOfAWholeBuiltGroupedProgram()
    {
        var program = AggregateProgramBuilder.BuildGrouped(
            "t",
            tableColumnCount: 2,
            groupKeyColumns: [0],
            aggregates: [new AggregateFunctionSpec(AggregateTestSupport.Sum(), [1])],
            outputs: [AggregateOutput.ForGroupKey(0), AggregateOutput.ForAggregate(0)],
            groupOrderComparer: (_, _) => 0,
            groupComparer: GroupComparer);

        var explain = VdbeExplain.Describe(program);

        explain.Should().HaveCount(34);
        explain[15][1].AsText().Should().Be("Goto");
        explain[15][6].AsText().Should().Be("goto 28");
        explain[18][1].AsText().Should().Be("SameGroup");
        explain[18][6].AsText().Should().Be("goto 25 if group r[3]==r[2]");
    }
}
