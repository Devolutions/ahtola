using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// EXPLAIN description coverage for the Arithmetic opcode. Confirms the addr/opcode/p1/p2/p3/p4/comment shape
// carries the destination register, operand range, operand count, and the operator symbol, and renders a
// distinct infix comment for binary operators and a prefix comment for the unary sign operators.
public class ArithmeticExplainTests
{
    [Test]
    public void DescribesABinaryOperator()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)));

        p1.Should().Be(2); // destination register
        p2.Should().Be(0); // operand range start
        p3.Should().Be(2); // operand count
        p4.Should().Be("+"); // operator symbol
        comment.Should().Be("r[2]=r[0] + r[1]");
    }

    [Test]
    public void DescribesEachBinaryOperatorSymbol()
    {
        Symbol(ArithmeticOperator.Add).Should().Be("+");
        Symbol(ArithmeticOperator.Subtract).Should().Be("-");
        Symbol(ArithmeticOperator.Multiply).Should().Be("*");
        Symbol(ArithmeticOperator.Divide).Should().Be("/");
        Symbol(ArithmeticOperator.Modulo).Should().Be("%");
        Symbol(ArithmeticOperator.BitwiseAnd).Should().Be("&");
        Symbol(ArithmeticOperator.BitwiseOr).Should().Be("|");
        Symbol(ArithmeticOperator.ShiftLeft).Should().Be("<<");
        Symbol(ArithmeticOperator.ShiftRight).Should().Be(">>");
    }

    [Test]
    public void DescribesABinaryOperatorOverAShiftedOperandRange()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new ArithmeticInstruction(new Register(9), ArithmeticOperator.Multiply, new RegisterRange(new Register(4), 2)));

        p1.Should().Be(9);
        p2.Should().Be(4);
        p3.Should().Be(2);
        p4.Should().Be("*");
        comment.Should().Be("r[9]=r[4] * r[5]");
    }

    [Test]
    public void DescribesAUnaryNegationAsAPrefix()
    {
        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new ArithmeticInstruction(new Register(1), ArithmeticOperator.Negate, new RegisterRange(new Register(0), 1)));

        p1.Should().Be(1);
        p2.Should().Be(0);
        p3.Should().Be(1);
        p4.Should().Be("-");
        comment.Should().Be("r[1]=-r[0]");
    }

    [Test]
    public void DescribesAUnaryIdentityAsAPrefix()
    {
        var (_, _, _, p4, comment) = VdbeExplain.Describe(
            new ArithmeticInstruction(new Register(3), ArithmeticOperator.Identity, new RegisterRange(new Register(2), 1)));

        p4.Should().Be("+");
        comment.Should().Be("r[3]=+r[2]");
    }

    [Test]
    public void DescribesAWholeArithmeticProgram()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(3)),
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);

        var rendered = VdbeExplain.Describe(program);

        rendered.Should().HaveCount(program.Instructions.Count);
        // Every EXPLAIN row exposes the seven addr/opcode/p1/p2/p3/p4/comment columns.
        rendered.Should().OnlyContain(row => row.Length == VdbeExplain.Columns().Length);
        // The Arithmetic row names its opcode.
        rendered[2][1].Should().Be(SqlValue.Text(nameof(VdbeOpcode.Arithmetic)));
    }

    [Test]
    public void DescribesNumericAffinity()
    {
        var affinity = new VdbeNumericAffinity
        {
            Name = "numeric",
            Apply = value => value,
        };

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new NumericAffinityInstruction(new Register(4), affinity));

        p1.Should().Be(4);
        p2.Should().Be(0);
        p3.Should().Be(0);
        p4.Should().Be("numeric");
        comment.Should().Be("r[4]=numeric(r[4])");
    }

    private static string? Symbol(ArithmeticOperator op)
        => VdbeExplain.Describe(
            new ArithmeticInstruction(new Register(2), op, new RegisterRange(new Register(0), 2))).P4;
}
