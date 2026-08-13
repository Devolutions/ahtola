using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// EXPLAIN description coverage for a directly built window program. Confirms that a
// partitioned running-aggregate window lowered by WindowProgramBuilder describes end to
// end through the shared addr/opcode/p1/p2/p3/p4/comment shape — the same renderer used
// by the sorter and aggregate opcode families — with no window-specific opcode required.
public class WindowProgramExplainDirectTests
{
    private static VdbeProgram BuildPartitionedRunningSum() =>
        WindowProgramBuilder.Build(
            "t",
            tableColumnCount: 2,
            partitionColumns: [0],
            windows: [new AggregateFunctionSpec(AggregateTestSupport.Sum(), [1])],
            outputs: [WindowOutput.ForColumn(0), WindowOutput.ForWindow(0)],
            orderComparer: AggregateTestSupport.OrderByColumns(0, 1),
            partitionComparer: AggregateTestSupport.GroupKeysEqual());

    [Test]
    public void DescribesEveryInstructionOfABuiltWindowProgram()
    {
        var program = BuildPartitionedRunningSum();

        var rows = VdbeExplain.Describe(program);

        VdbeExplain.Columns().Should().Equal("addr", "opcode", "p1", "p2", "p3", "p4", "comment");
        rows.Should().HaveCount(program.Instructions.Count);

        for (var address = 0; address < rows.Count; address++)
        {
            rows[address].Should().HaveCount(7);
            rows[address][0].Should().Be(SqlValue.Integer(address));
            rows[address][1].AsText().Should().Be(program.Instructions[address].Opcode.ToString());
        }
    }

    [Test]
    public void DescribesThePartitionBoundaryCheckOfABuiltWindowProgram()
    {
        var program = BuildPartitionedRunningSum();

        var sameGroup = (SameGroupInstruction)program.Instructions.First(i => i is SameGroupInstruction);
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(sameGroup);

        p1.Should().Be(sameGroup.CurrentKey.Start.Index);
        p2.Should().Be(sameGroup.SameGroupTarget.Offset);
        p3.Should().Be(sameGroup.SavedKey.Start.Index);
        p4.Should().BeNull();
        comment.Should().Be(
            $"goto {sameGroup.SameGroupTarget.Offset} if group r[{sameGroup.CurrentKey.Start.Index}]==r[{sameGroup.SavedKey.Start.Index}]");
    }

    [Test]
    public void DescribesThePerRowFinalizeOfABuiltWindowProgram()
    {
        var program = BuildPartitionedRunningSum();

        var finalize = (AggFinalizeInstruction)program.Instructions.First(i => i is AggFinalizeInstruction);
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(finalize);

        p1.Should().Be(finalize.Accumulator.Index);
        p2.Should().Be(finalize.Destination.Index);
        p3.Should().Be(0);
        p4.Should().Be("sum");
        comment.Should().Be($"r[{finalize.Destination.Index}]=sum finalize accumulator {finalize.Accumulator.Index}");
    }
}
