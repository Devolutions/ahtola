using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Program-validation coverage for the Function opcode. The VdbeProgram constructor validates on
// construction, so each malformed program is expected to throw VdbeProgramValidationException up front,
// before any execution. This pins the arity, null-function, null-delegate, and register-bounds contract.
public class ScalarFunctionValidationTests
{
    [Test]
    public void RejectsAFixedArityFunctionAppliedToTheWrongArgumentCount()
    {
        // add() declares arity 2 but is applied to a single-register argument range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Add(), new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsAFunctionDeclaringANegativeArity()
    {
        var negativeArity = new VdbeScalarFunction
        {
            Name = "bad",
            Arity = -1,
            Invoke = _ => SqlValue.Null,
        };

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(0), negativeArity, new RegisterRange(new Register(0), 0)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsANullFunction()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(0), null!, new RegisterRange(new Register(0), 0)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsAFunctionWithANullInvokeDelegate()
    {
        var missingDelegate = new VdbeScalarFunction
        {
            Name = "missing",
            Arity = 0,
            Invoke = null!,
        };

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(0), missingDelegate, new RegisterRange(new Register(0), 0)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsArgumentsThatReachOutsideTheRegisterFile()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(0), ScalarFunctionTestSupport.Coalesce(), new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsADestinationRegisterOutsideTheRegisterFile()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(5), ScalarFunctionTestSupport.Always42(), new RegisterRange(new Register(0), 0)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void AcceptsAVariadicFunctionOverAnyArgumentCount()
    {
        // A null Arity performs no arity check, so coalesce validates over a two-register range.
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new FunctionInstruction(new Register(2), ScalarFunctionTestSupport.Coalesce(), new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ]);

        program.Invoking(p => p.Validate()).Should().NotThrow();
    }
}
