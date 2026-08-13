using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// EXPLAIN description coverage for the Function opcode. Confirms the addr/opcode/p1/p2/p3/p4/comment shape
// carries the destination register, argument range, and function name, and that a whole scalar-function
// program renders end to end.
public class ScalarFunctionExplainTests
{
    [Test]
    public void DescribesAUnaryFunction()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)));

        p1.Should().Be(1); // destination register
        p2.Should().Be(0); // argument range start
        p3.Should().Be(1); // argument count
        p4.Should().Be("abs"); // function name
        comment.Should().Be("r[1]=abs(r[0])");
    }

    [Test]
    public void DescribesAMultiArgumentFunctionWithARegisterSpan()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new FunctionInstruction(new Register(4), ScalarFunctionTestSupport.Coalesce(), new RegisterRange(new Register(1), 3)));

        p1.Should().Be(4);
        p2.Should().Be(1);
        p3.Should().Be(3);
        p4.Should().Be("coalesce");
        comment.Should().Be("r[4]=coalesce(r[1..3])");
    }

    [Test]
    public void DescribesANullaryFunctionWithAnEmptyArgumentRange()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new FunctionInstruction(new Register(0), ScalarFunctionTestSupport.Always42(), new RegisterRange(new Register(0), 0)));

        p1.Should().Be(0);
        p2.Should().Be(0);
        p3.Should().Be(0);
        p4.Should().Be("always_42");
        comment.Should().Be("r[0]=always_42(r[])");
    }

    [Test]
    public void DescribesAWholeScalarFunctionProgram()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(-1)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);

        var rendered = VdbeExplain.Describe(program);

        rendered.Should().HaveCount(program.Instructions.Count);
        // Every EXPLAIN row exposes the seven addr/opcode/p1/p2/p3/p4/comment columns.
        rendered.Should().OnlyContain(row => row.Length == VdbeExplain.Columns().Length);
        // The Function row names its opcode.
        rendered[1][1].Should().Be(SqlValue.Text(nameof(VdbeOpcode.Function)));
    }
}
