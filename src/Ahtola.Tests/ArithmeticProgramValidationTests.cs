using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Program-validation coverage for the Arithmetic opcode. The VdbeProgram constructor validates on
// construction, so each malformed program is expected to throw VdbeProgramValidationException up front,
// before any execution. This pins the arity, undefined-operator, and register-bounds contract that keeps
// an arity mismatch or an out-of-range register from ever reaching the interpreter.
public class ArithmeticProgramValidationTests
{
    [Test]
    public void RejectsABinaryOperatorAppliedToASingleOperand()
    {
        // Add has arity 2 but is applied to a single-register operand range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(1), ArithmeticOperator.Add, new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsAUnaryOperatorAppliedToTwoOperands()
    {
        // Negate has arity 1 but is applied to a two-register operand range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(2), ArithmeticOperator.Negate, new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsAnUndefinedOperator()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(2), (ArithmeticOperator)999, new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsOperandsThatReachOutsideTheRegisterFile()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(0), ArithmeticOperator.Add, new RegisterRange(new Register(1), 2)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsADestinationRegisterOutsideTheRegisterFile()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(5), ArithmeticOperator.Negate, new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void AcceptsAWellFormedBinaryProgram()
    {
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ]);

        program.Invoking(p => p.Validate()).Should().NotThrow();
    }

    [Test]
    public void AcceptsAWellFormedUnaryProgram()
    {
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new ArithmeticInstruction(new Register(1), ArithmeticOperator.Identity, new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        program.Invoking(p => p.Validate()).Should().NotThrow();
    }

    [Test]
    public void RejectsNumericAffinityOutsideTheRegisterFile()
    {
        var affinity = NumericAffinity("numeric");

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new NumericAffinityInstruction(new Register(1), affinity),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsUnnamedNumericAffinity()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new NumericAffinityInstruction(new Register(0), NumericAffinity("")),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsNumericAffinityWithANullDelegate()
    {
        var affinity = new VdbeNumericAffinity
        {
            Name = "numeric",
            Apply = null!,
        };

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new NumericAffinityInstruction(new Register(0), affinity),
                new HaltInstruction(),
            ]));
    }

    private static VdbeNumericAffinity NumericAffinity(string name) => new()
    {
        Name = name,
        Apply = value => value,
    };
}
