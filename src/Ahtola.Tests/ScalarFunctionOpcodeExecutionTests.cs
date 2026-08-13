using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the scalar-function family: the Function opcode that applies a
// VdbeScalarFunction delegate to an argument register block and writes the result into a destination
// register. Programs are built by hand from the public Execution contract and run through the resumable
// state machine, so the tests exercise the interpreter and validator directly rather than any database
// wiring. Arity, NULL, BLOB, error propagation, safe argument copying, reset, and dispose are all covered.
public class ScalarFunctionOpcodeExecutionTests
{
    [Test]
    public void FunctionAppliesUnaryDelegateAndWritesTheResult()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(-7)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(7));
    }

    [Test]
    public void FunctionAppliesBinaryDelegateOverAnArgumentBlock()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(20)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(22)),
            new FunctionInstruction(new Register(2), ScalarFunctionTestSupport.Add(), new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(42));
    }

    [Test]
    public void NullaryFunctionInvokesWithAZeroWidthArgumentRange()
    {
        VdbeInstruction[] instructions =
        [
            new FunctionInstruction(new Register(0), ScalarFunctionTestSupport.Always42(), new RegisterRange(new Register(0), 0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(42));
    }

    [Test]
    public void VariadicFunctionAcceptsAnyArgumentCount()
    {
        // coalesce over three arguments returns the first non-NULL; a null Arity skips the arity check.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Null),
            new LoadConstantInstruction(new Register(1), SqlValue.Null),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("third")),
            new FunctionInstruction(new Register(3), ScalarFunctionTestSupport.Coalesce(), new RegisterRange(new Register(0), 3)),
            new ResultRowInstruction(new RegisterRange(new Register(3), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 4, cursorCount: 0, instructions);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Text("third"));
    }

    [Test]
    public void FunctionPropagatesNullArgumentsThroughTheDelegate()
    {
        VdbeInstruction[] instructions =
        [
            // r[0] defaults to NULL; abs(NULL) is NULL.
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);

        RunToCompletion(program)[0][0].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void FunctionRoundTripsBlobArgumentsAndResults()
    {
        var blob = new byte[] { 1, 2, 3, 4 };
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Blob(blob)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.ReverseBlob(), new RegisterRange(new Register(0), 1)),
            new FunctionInstruction(new Register(2), ScalarFunctionTestSupport.BlobLength(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);

        var row = RunToCompletion(program)[0];
        row[0].Kind.Should().Be(SqlValueKind.Blob);
        row[0].AsBlob().ToArray().Should().Equal(4, 3, 2, 1);
        row[1].Should().Be(SqlValue.Integer(4));
    }

    [Test]
    public void FunctionResultDoesNotShareBlobStorageWithTheArgumentRegister()
    {
        var blob = new byte[] { 9, 8, 7 };
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Blob(blob)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.ReverseBlob(), new RegisterRange(new Register(0), 1)),
            // Emit both the untouched source blob and the reversed result: the two must be independent.
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);

        var row = RunToCompletion(program)[0];
        row[0].AsBlob().ToArray().Should().Equal(9, 8, 7);
        row[1].AsBlob().ToArray().Should().Equal(7, 8, 9);
    }

    [Test]
    public void FunctionMayWriteItsResultBackIntoItsSoleArgumentRegister()
    {
        // r[0]=f(r[0]): the interpreter snapshots the argument before invoking, so overwriting the
        // argument register with the result is well defined.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(-5)),
            new FunctionInstruction(new Register(0), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(5));
    }

    [Test]
    public void FunctionReceivesAPrivateCopyOfItsArgumentRegisters()
    {
        // scribble mutates its argument tuple; r[0] must survive unchanged because the delegate is handed
        // a copy, never the live register file.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Scribble(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);

        var row = RunToCompletion(program)[0];
        row[0].Should().Be(SqlValue.Integer(5));
        row[1].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void FunctionErrorPropagatesOutOfTheStepAndLeavesTheDestinationUntouched()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Boom(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        Assert.Throws<VdbeFunctionException>(() => statement.StepResumable());

        // The failed function never published a result: the destination register is still its default NULL.
        statement.GetRegister(new Register(1)).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ResetReplaysAScalarFunctionProgramFromTheStart()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(-3)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        DrainRows(statement)[0].Should().Equal(SqlValue.Integer(3));

        statement.Reset();

        DrainRows(statement)[0].Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void DisposeStopsAScalarFunctionStatementFromStepping()
    {
        VdbeInstruction[] instructions =
        [
            new FunctionInstruction(new Register(0), ScalarFunctionTestSupport.Always42(), new RegisterRange(new Register(0), 0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);
        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    private static List<SqlValue[]> RunToCompletion(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
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
