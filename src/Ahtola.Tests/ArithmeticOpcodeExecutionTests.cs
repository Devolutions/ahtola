using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the arithmetic family: the Arithmetic opcode that applies an
// ArithmeticOperator to an operand register block and writes the single result into a destination
// register. Programs are built by hand from the public Execution contract and run through the resumable
// state machine, so the tests exercise the interpreter (VdbeArithmetic value semantics) and validator
// directly rather than any database wiring or the tree-walking evaluator. NULL, integer/real typing,
// division/modulo by zero, overflow, type errors, operand snapshotting, reset, dispose, and composition
// with the Function opcode are all covered.
public class ArithmeticOpcodeExecutionTests
{
    // ---- Binary operators over integers --------------------------------------------------------------

    [Test]
    public void AddsTwoIntegers()
        => EvalBinary(ArithmeticOperator.Add, SqlValue.Integer(20), SqlValue.Integer(22))
            .Should().Be(SqlValue.Integer(42));

    [Test]
    public void SubtractsTwoIntegers()
        => EvalBinary(ArithmeticOperator.Subtract, SqlValue.Integer(50), SqlValue.Integer(8))
            .Should().Be(SqlValue.Integer(42));

    [Test]
    public void MultipliesTwoIntegers()
        => EvalBinary(ArithmeticOperator.Multiply, SqlValue.Integer(6), SqlValue.Integer(7))
            .Should().Be(SqlValue.Integer(42));

    [Test]
    public void DividesTwoIntegersWithTruncationTowardZero()
    {
        EvalBinary(ArithmeticOperator.Divide, SqlValue.Integer(7), SqlValue.Integer(2))
            .Should().Be(SqlValue.Integer(3));
        // C#/SQLite integer division truncates toward zero, so a negative dividend rounds up.
        EvalBinary(ArithmeticOperator.Divide, SqlValue.Integer(-7), SqlValue.Integer(2))
            .Should().Be(SqlValue.Integer(-3));
    }

    [Test]
    public void ComputesIntegerRemainderWithTheDividendSign()
    {
        EvalBinary(ArithmeticOperator.Modulo, SqlValue.Integer(7), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(1));
        // The remainder takes the sign of the dividend, matching C/SQLite '%'.
        EvalBinary(ArithmeticOperator.Modulo, SqlValue.Integer(-7), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(-1));
    }

    // ---- Real typing ---------------------------------------------------------------------------------

    [Test]
    public void AnyRealOperandPromotesTheResultToReal()
    {
        EvalBinary(ArithmeticOperator.Add, SqlValue.Real(1.5), SqlValue.Integer(2))
            .Should().Be(SqlValue.Real(3.5));
        EvalBinary(ArithmeticOperator.Multiply, SqlValue.Integer(3), SqlValue.Real(2.5))
            .Should().Be(SqlValue.Real(7.5));
    }

    [Test]
    public void RealDivisionKeepsTheFraction()
        => EvalBinary(ArithmeticOperator.Divide, SqlValue.Real(7.0), SqlValue.Integer(2))
            .Should().Be(SqlValue.Real(3.5));

    [Test]
    public void RealModuloTakesTheRemainderOfIntegerTruncations()
        => EvalBinary(ArithmeticOperator.Modulo, SqlValue.Real(7.5), SqlValue.Real(3.0))
            .Should().Be(SqlValue.Real(1.0));

    // ---- Unary sign operators ------------------------------------------------------------------------

    [Test]
    public void NegatesAnInteger()
        => EvalUnary(ArithmeticOperator.Negate, SqlValue.Integer(5))
            .Should().Be(SqlValue.Integer(-5));

    [Test]
    public void NegatesAReal()
        => EvalUnary(ArithmeticOperator.Negate, SqlValue.Real(2.5))
            .Should().Be(SqlValue.Real(-2.5));

    [Test]
    public void IdentityReturnsANumericOperandUnchanged()
    {
        EvalUnary(ArithmeticOperator.Identity, SqlValue.Integer(9)).Should().Be(SqlValue.Integer(9));
        EvalUnary(ArithmeticOperator.Identity, SqlValue.Real(1.25)).Should().Be(SqlValue.Real(1.25));
    }

    [Test]
    public void IdentityPreservesTextAndBlobStorageClasses()
    {
        EvalUnary(ArithmeticOperator.Identity, SqlValue.Text("10")).Should().Be(SqlValue.Text("10"));
        EvalUnary(ArithmeticOperator.Identity, SqlValue.Blob([0x31, 0x30]))
            .Should().Be(SqlValue.Blob([0x31, 0x30]));
    }

    [Test]
    public void BitwiseOperatorsUseSignedIntegerSemantics()
    {
        EvalBinary(ArithmeticOperator.BitwiseAnd, SqlValue.Integer(10), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(2));
        EvalBinary(ArithmeticOperator.BitwiseOr, SqlValue.Integer(8), SqlValue.Integer(3))
            .Should().Be(SqlValue.Integer(11));
        EvalUnary(ArithmeticOperator.BitwiseNot, SqlValue.Integer(10))
            .Should().Be(SqlValue.Integer(-11));
    }

    [Test]
    public void ShiftOperatorsSaturateAndReverseNegativeCounts()
    {
        EvalBinary(ArithmeticOperator.ShiftLeft, SqlValue.Integer(8), SqlValue.Integer(-1))
            .Should().Be(SqlValue.Integer(4));
        EvalBinary(ArithmeticOperator.ShiftRight, SqlValue.Integer(8), SqlValue.Integer(-1))
            .Should().Be(SqlValue.Integer(16));
        EvalBinary(ArithmeticOperator.ShiftLeft, SqlValue.Integer(1), SqlValue.Integer(64))
            .Should().Be(SqlValue.Integer(0));
        EvalBinary(ArithmeticOperator.ShiftRight, SqlValue.Integer(-1), SqlValue.Integer(64))
            .Should().Be(SqlValue.Integer(-1));
    }

    // ---- NULL propagation ----------------------------------------------------------------------------

    [Test]
    public void AnyNullOperandYieldsNull()
    {
        EvalBinary(ArithmeticOperator.Add, SqlValue.Null, SqlValue.Integer(1)).Kind
            .Should().Be(SqlValueKind.Null);
        EvalBinary(ArithmeticOperator.Multiply, SqlValue.Integer(1), SqlValue.Null).Kind
            .Should().Be(SqlValueKind.Null);
        EvalUnary(ArithmeticOperator.Negate, SqlValue.Null).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void NullShortCircuitsBeforeANonNumericOperandIsTypeChecked()
    {
        // The text operand would be a type error, but the NULL operand short-circuits to NULL first.
        EvalBinary(ArithmeticOperator.Add, SqlValue.Null, SqlValue.Text("x")).Kind
            .Should().Be(SqlValueKind.Null);
        EvalBinary(ArithmeticOperator.Modulo, SqlValue.Text("x"), SqlValue.Null).Kind
            .Should().Be(SqlValueKind.Null);
    }

    // ---- Division / modulo by zero -------------------------------------------------------------------

    [Test]
    public void IntegerDivisionByZeroYieldsNull()
        => EvalBinary(ArithmeticOperator.Divide, SqlValue.Integer(1), SqlValue.Integer(0)).Kind
            .Should().Be(SqlValueKind.Null);

    [Test]
    public void RealDivisionByZeroYieldsNull()
        => EvalBinary(ArithmeticOperator.Divide, SqlValue.Real(1.0), SqlValue.Real(0.0)).Kind
            .Should().Be(SqlValueKind.Null);

    [Test]
    public void IntegerModuloByZeroYieldsNull()
        => EvalBinary(ArithmeticOperator.Modulo, SqlValue.Integer(5), SqlValue.Integer(0)).Kind
            .Should().Be(SqlValueKind.Null);

    [Test]
    public void RealModuloByZeroYieldsNull()
        => EvalBinary(ArithmeticOperator.Modulo, SqlValue.Real(5.0), SqlValue.Real(0.0)).Kind
            .Should().Be(SqlValueKind.Null);

    // ---- Overflow and the two's-complement corner cases ----------------------------------------------

    [Test]
    public void IntegerAdditionOverflowFallsBackToReal()
    {
        var result = EvalBinary(ArithmeticOperator.Add, SqlValue.Integer(long.MaxValue), SqlValue.Integer(1));
        result.Kind.Should().Be(SqlValueKind.Real);
        result.AsReal().Should().Be((double)long.MaxValue + 1.0);
    }

    [Test]
    public void IntegerMultiplicationOverflowFallsBackToReal()
    {
        var result = EvalBinary(ArithmeticOperator.Multiply, SqlValue.Integer(long.MaxValue), SqlValue.Integer(2));
        result.Kind.Should().Be(SqlValueKind.Real);
        result.AsReal().Should().Be((double)long.MaxValue * 2.0);
    }

    [Test]
    public void NegatingLongMinValueFallsBackToReal()
    {
        var result = EvalUnary(ArithmeticOperator.Negate, SqlValue.Integer(long.MinValue));
        result.Kind.Should().Be(SqlValueKind.Real);
        result.AsReal().Should().Be(-(double)long.MinValue);
    }

    [Test]
    public void LongMinValueDividedByMinusOneFallsBackToReal()
    {
        var result = EvalBinary(ArithmeticOperator.Divide, SqlValue.Integer(long.MinValue), SqlValue.Integer(-1));
        result.Kind.Should().Be(SqlValueKind.Real);
        result.AsReal().Should().Be(-(double)long.MinValue);
    }

    [Test]
    public void IntegerModuloByMinusOneIsZero()
    {
        // x % -1 is mathematically zero; special-casing it avoids the long.MinValue % -1 overflow.
        EvalBinary(ArithmeticOperator.Modulo, SqlValue.Integer(long.MinValue), SqlValue.Integer(-1))
            .Should().Be(SqlValue.Integer(0));
        EvalBinary(ArithmeticOperator.Modulo, SqlValue.Integer(9), SqlValue.Integer(-1))
            .Should().Be(SqlValue.Integer(0));
    }

    // ---- Type errors ---------------------------------------------------------------------------------

    [Test]
    public void ATextOperandRaisesAnArithmeticTypeError()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Text("nope")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        Assert.Throws<VdbeArithmeticException>(() => Drain(statement));
    }

    [Test]
    public void ABlobOperandRaisesAnArithmeticTypeError()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Blob([1, 2, 3])),
            new ArithmeticInstruction(new Register(1), ArithmeticOperator.Negate, new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        Assert.Throws<VdbeArithmeticException>(() => statement.StepResumable());
    }

    [Test]
    public void ATypeErrorLeavesTheDestinationRegisterUntouched()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("boom")),
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        // A single step runs the constant loads then the failing Arithmetic op, which throws.
        Assert.Throws<VdbeArithmeticException>(() => statement.StepResumable());

        // The failed operation never published a result: r[2] is still its default NULL.
        statement.GetRegister(new Register(2)).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void NumericAffinityTransformsARegisterBeforeArithmetic()
    {
        var affinity = new VdbeNumericAffinity
        {
            Name = "test-numeric",
            Apply = value => value.Kind == SqlValueKind.Text
                ? SqlValue.Integer(long.Parse(value.AsText()))
                : value,
        };
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Text("40")),
            new NumericAffinityInstruction(new Register(0), affinity),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);

        RunToCompletion(program)[0][0].Should().Be(SqlValue.Integer(42));
    }

    [Test]
    public void ThrowingNumericAffinityLeavesTheRegisterUntouched()
    {
        var affinity = new VdbeNumericAffinity
        {
            Name = "throwing",
            Apply = _ => throw new InvalidOperationException("coercion failed"),
        };
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Text("unchanged")),
            new NumericAffinityInstruction(new Register(0), affinity),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Be("coercion failed");
        statement.GetRegister(new Register(0)).Should().Be(SqlValue.Text("unchanged"));
    }

    // ---- Operand snapshotting ------------------------------------------------------------------------

    [Test]
    public void UnaryOperatorMayWriteItsResultBackIntoItsOperandRegister()
    {
        // r[0]=-(r[0]): the interpreter snapshots the operand before computing, so overwriting the operand
        // register with the result is well defined.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(-5)),
            new ArithmeticInstruction(new Register(0), ArithmeticOperator.Negate, new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        RunToCompletion(program)[0][0].Should().Be(SqlValue.Integer(5));
    }

    [Test]
    public void BinaryOperatorMayWriteItsResultOverAnOperandRegister()
    {
        // r[0]=r[0] - r[1]: the destination overlaps the first operand, which the pre-computation snapshot
        // keeps well defined.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(30)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(4)),
            new ArithmeticInstruction(new Register(0), ArithmeticOperator.Subtract, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);

        RunToCompletion(program)[0][0].Should().Be(SqlValue.Integer(26));
    }

    // ---- Composition with the Function opcode --------------------------------------------------------

    [Test]
    public void ComposesArithmeticOverAFunctionResult()
    {
        // r[3] = abs(r[0]) + r[1], i.e. abs(-7) + 5 = 12, folding a FunctionInstruction result into an
        // ArithmeticInstruction operand block. abs lands in r[1] so it sits adjacent to the r[2] operand,
        // giving the add a contiguous r[1..2] operand block.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(-7)),
            new FunctionInstruction(new Register(1), ScalarFunctionTestSupport.Abs(), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(5)),
            new ArithmeticInstruction(new Register(3), ArithmeticOperator.Add, new RegisterRange(new Register(1), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(3), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 4, cursorCount: 0, instructions);

        RunToCompletion(program)[0][0].Should().Be(SqlValue.Integer(12));
    }

    [Test]
    public void NestsArithmeticResultsThroughSuccessiveOperations()
    {
        // r[4] = (r[0] * r[1]) - r[2] = (6 * 7) - 2 = 40. The multiply result lands in r[2] so it sits
        // adjacent to the r[3] operand, letting the subtract read a contiguous r[2..3] operand block.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(6)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(7)),
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Multiply, new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(3), SqlValue.Integer(2)),
            new ArithmeticInstruction(new Register(4), ArithmeticOperator.Subtract, new RegisterRange(new Register(2), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(4), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 5, cursorCount: 0, instructions);

        RunToCompletion(program)[0][0].Should().Be(SqlValue.Integer(40));
    }

    // ---- Reset and dispose ---------------------------------------------------------------------------

    [Test]
    public void ResetReplaysAnArithmeticProgramFromTheStart()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(8)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(9)),
            new ArithmeticInstruction(new Register(2), ArithmeticOperator.Add, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);
        using var statement = new ResumableStatement(program);

        Drain(statement)[0][0].Should().Be(SqlValue.Integer(17));

        statement.Reset();

        Drain(statement)[0][0].Should().Be(SqlValue.Integer(17));
    }

    [Test]
    public void DisposeStopsAnArithmeticStatementFromStepping()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new ArithmeticInstruction(new Register(1), ArithmeticOperator.Identity, new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);
        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    private static SqlValue EvalBinary(ArithmeticOperator op, SqlValue left, SqlValue right)
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), left),
            new LoadConstantInstruction(new Register(1), right),
            new ArithmeticInstruction(new Register(2), op, new RegisterRange(new Register(0), 2)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);
        return RunToCompletion(program)[0][0];
    }

    private static SqlValue EvalUnary(ArithmeticOperator op, SqlValue operand)
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), operand),
            new ArithmeticInstruction(new Register(1), op, new RegisterRange(new Register(0), 1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions);
        return RunToCompletion(program)[0][0];
    }

    private static List<SqlValue[]> RunToCompletion(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
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
