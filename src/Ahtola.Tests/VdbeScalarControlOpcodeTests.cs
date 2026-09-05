using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers the scalar-control opcodes ported from Turso for gap
/// vdbe-scalar-control-inline (IfPos, IfNeg, DecrJumpZero, MustBeInt, SoftNull,
/// MemMax, AddImm, ZeroOrNull) plus the numeric-token classifier that backs
/// MustBeInt's text coercion.
/// </summary>
public sealed class VdbeScalarControlOpcodeTests
{
    [Test]
    public void IfPosJumpsAndDecrementsOnlyOnPositiveValues()
    {
        // 5 - 2 = 3 > 0: jump to target (index 3, Halt), skipping the ResultRow.
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
                new IfPosInstruction(new Register(0), new ProgramCounter(3), DecrementBy: 2),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void IfPosWriteBackIsObservableOnJumpPath()
    {
        // If the first IfPos correctly writes back 1 - 2 = -1 on the jump path, the
        // second IfPos sees -1 and falls through to the ResultRow; without the
        // write-back it would see 1, jump to the Halt, and yield no row at all.
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new IfPosInstruction(new Register(0), new ProgramCounter(2), DecrementBy: 2),
                new IfPosInstruction(new Register(0), new ProgramCounter(4)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Should().Be(SqlValue.Integer(-1));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void IfPosFallsThroughOnZeroAndNegative()
    {
        foreach (var (initial, expected) in new[] { (0L, 0L), (-1L, -1L) })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), SqlValue.Integer(initial)),
                    new IfPosInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
            statement.CurrentRow![0].AsInteger().Should().Be(expected);
        }
    }

    [Test]
    public void IfPosRejectsNonIntegerWithFaultedState()
    {
        foreach (var value in new[] { SqlValue.Real(1.5), SqlValue.Text("1"), SqlValue.Null })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new IfPosInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
            statement.State.Should().Be(ResumableStatementState.Faulted);
            Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        }
    }

    [Test]
    public void IfNegJumpsOnNegativeIntegerAndReal()
    {
        foreach (var value in new[] { SqlValue.Integer(-1), SqlValue.Real(-1.5) })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new IfNegInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        }
    }

    [Test]
    public void IfNegFallsThroughOnNonNegativeAndNullAndText()
    {
        foreach (var value in new[]
                 {
                     SqlValue.Integer(0),
                     SqlValue.Integer(5),
                     SqlValue.Real(0.0),
                     SqlValue.Real(1.5),
                     SqlValue.Null,
                     SqlValue.Text("-1"),
                 })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new IfNegInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        }
    }

    [Test]
    public void DecrJumpZeroDecrementsToZeroAndJumps()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new DecrJumpZeroInstruction(new Register(0), new ProgramCounter(3)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        // Jump path: register becomes 0 and the ResultRow at index 2 is skipped.
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.CurrentRow.Should().BeNull();
    }

    [Test]
    public void DecrJumpZeroFallsThroughAboveZeroAndWritesBack()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
                new DecrJumpZeroInstruction(new Register(0), new ProgramCounter(3)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(4);
    }

    [Test]
    public void DecrJumpZeroSaturatesAtLongMinValue()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(long.MinValue)),
                new DecrJumpZeroInstruction(new Register(0), new ProgramCounter(3)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(long.MinValue);
    }

    [Test]
    public void DecrJumpZeroRejectsNonIntegerRegisters()
    {
        foreach (var value in new[]
                 {
                     SqlValue.Real(2.5),
                     SqlValue.Real(2.0),
                     SqlValue.Null,
                     SqlValue.Text("1"),
                 })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new DecrJumpZeroInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());
            error!.SqliteErrorCode.Should().Be(SqliteResultCode.Constraint);
            error.Message.Should().Contain("datatype mismatch");
        }
    }

    [Test]
    public void MustBeIntCoercesWholeRealToIntegerKind()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Real(2.0)),
                new MustBeIntInstruction(new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Integer);
        statement.CurrentRow![0].AsInteger().Should().Be(2);
    }

    [Test]
    public void MustBeIntJumpsToTargetWhenCoercionFails()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), SqlValue.Real(2.5)),
                    new MustBeIntInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

        using var statement = new ResumableStatement(program);
        // Coercion fails; jump skips the ResultRow and halts cleanly.
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void MustBeIntCoercesIntegerTextTokens()
    {
        foreach (var (text, expected) in new[]
                 {
                     ("1.0", 1L),
                     ("1e3", 1000L),
                     (" 12 ", 12L),
                     ("12", 12L),
                     ("+5", 5L),
                     ("0007", 7L),
                 })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), SqlValue.Text(text)),
                    new MustBeIntInstruction(new Register(0)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
            statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Integer);
            statement.CurrentRow![0].AsInteger().Should().Be(expected);
        }
    }

    [Test]
    public void MustBeIntAcceptsLeadingDecimalPointRealTokens()
    {
        // ".5" classifies Float and parses to 0.5, which fails the strict integer cast
        // and jumps — matching Turso's MustBeInt semantics (SQLite accepts the column
        // value but the jump records the lossy conversion).
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Text(".5")),
                new MustBeIntInstruction(new Register(0), new ProgramCounter(3)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void MustBeIntRejectsMalformedAndTrailingMarkerTokens()
    {
        // Every entry must fail lossless coercion and take the jump target.
        foreach (var text in new[]
                 {
                     "42abc",
                     " 42abc",
                     "12e",
                     "12E",
                     "12e+",
                     "12e-",
                     "12.5e",
                     "12.5e+",
                     "+e",
                     "-e",
                     "e5",
                     "E5",
                     ".e5",
                     ".E5",
                     "5+",
                     "1e2e3",
                     "-",
                     "+",
                 })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), SqlValue.Text(text)),
                    new MustBeIntInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(
                ResumableStatementStepResult.Done,
                $"text {text!} should fail lossless integer coercion");
        }
    }

    [Test]
    public void MustBeIntAcceptsDecimalPointTokenAsZero()
    {
        // "." classifies Float, f64 parse fails → 0.0, and the strict cast accepts it.
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Text(".")),
                new MustBeIntInstruction(new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Integer);
        statement.CurrentRow![0].AsInteger().Should().Be(0);
    }

    [Test]
    public void MustBeIntOverflowFallsBackToStrictRealCast()
    {
        // "99999999999999999999" overflows i64: lossless classification is Integer-kind
        // but the i64 parse fails; the f64 fallback yields 1e20, whose strict integer
        // cast fails, so the coercion fails and the jump is taken.
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount:  0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Text("99999999999999999999")),
                new MustBeIntInstruction(new Register(0), new ProgramCounter(3)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void MustBeIntNullAndBlobFailClosed()
    {
        foreach (var value in new[] { SqlValue.Null, SqlValue.Blob([1, 2, 3]) })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new MustBeIntInstruction(new Register(0), new ProgramCounter(3)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        }
    }

    [Test]
    public void MustBeIntNoTargetConstraintMismatch()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Real(2.5)),
                new MustBeIntInstruction(new Register(0)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.Constraint);
        error.Message.Should().Contain("datatype mismatch");
    }

    [Test]
    public void SoftNullNullsAnyRegisterKind()
    {
        foreach (var value in new[]
                 {
                     SqlValue.Integer(42),
                     SqlValue.Real(1.5),
                     SqlValue.Text("x"),
                     SqlValue.Blob([1]),
                     SqlValue.Null,
                 })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new SoftNullInstruction(new Register(0)),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
            statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Null);
        }
    }

    [Test]
    public void MemMaxWritesSourceWhenLarger()
    {
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Null),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(5)),
                new MemMaxInstruction(new Register(0), new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(5);
    }

    [Test]
    public void MemMaxKeepsExistingMaximum()
    {
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(10)),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(5)),
                new MemMaxInstruction(new Register(0), new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(10);
    }

    [Test]
    public void MemMaxUsesLossyIntegerViewsOfAllKinds()
    {
        // Real 2.9 → 2 (not > 1, kept); Text "5" → 5 (> 2, overwritten to 7 via src).
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Real(2.9)),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
                new MemMaxInstruction(new Register(0), new Register(1)),
                new LoadConstantInstruction(new Register(1), SqlValue.Text("5")),
                new MemMaxInstruction(new Register(0), new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        // dest 2 vs src 5 → overwrite with extract(src)=5.
        statement.CurrentRow![0].AsInteger().Should().Be(5);
    }

    [Test]
    public void MemMaxEqualValuesDoNotOverwrite()
    {
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(5)),
                new MemMaxInstruction(new Register(0), new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        // Kind stays Integer (no overwrite happened; both were already Integer).
        statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Integer);
        statement.CurrentRow![0].AsInteger().Should().Be(5);
    }

    [Test]
    public void AddImmAddsImmediateToInteger()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
                new AddImmInstruction(new Register(0), Value: 3),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(8);
    }

    [Test]
    public void AddImmWrapsOnOverflow()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(long.MaxValue)),
                new AddImmInstruction(new Register(0), Value: 1),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(long.MinValue);
    }

    [Test]
    public void AddImmUsesLossyIntegerViewOfNonIntegerKinds()
    {
        foreach (var (value, expected) in new[]
                 {
                     (SqlValue.Real(2.9), 5L),
                     (SqlValue.Null, 3L),
                 })
        {
            var program = new VdbeProgram(
                registerCount: 1,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), value),
                    new AddImmInstruction(new Register(0), Value: 3),
                    new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
            statement.CurrentRow![0].AsInteger().Should().Be(expected);
        }
    }

    [Test]
    public void AddImmParsesLeadingDigitsOfText()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Text("12abc")),
                new AddImmInstruction(new Register(0), Value: 3),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(15);
    }

    [Test]
    public void ZeroOrNullYieldsNullWhenEitherSideIsNull()
    {
        foreach (var (left, right) in new[]
                 {
                     (SqlValue.Null, SqlValue.Integer(1)),
                     (SqlValue.Integer(1), SqlValue.Null),
                     (SqlValue.Null, SqlValue.Null),
                 })
        {
            var program = new VdbeProgram(
                registerCount: 3,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), left),
                    new LoadConstantInstruction(new Register(1), right),
                    new ZeroOrNullInstruction(new Register(0), new Register(1), new Register(2)),
                    new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
                    new HaltInstruction(),
                ]);

            using var statement = new ResumableStatement(program);
            statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
            statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Null);
        }
    }

    [Test]
    public void ZeroOrNullYieldsZeroWhenBothSidesAreNonNull()
    {
        var program = new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(3)),
                new ZeroOrNullInstruction(new Register(0), new Register(1), new Register(2)),
                new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].Kind.Should().Be(SqlValueKind.Integer);
        statement.CurrentRow![0].AsInteger().Should().Be(0);
    }

    [Test]
    public void ScalarControlOpcodesRejectInvalidRegistersAndTargets()
    {
        // Register index out of range.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new IfPosInstruction(new Register(1), new ProgramCounter(0)),
                new HaltInstruction(),
            ]));
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new AddImmInstruction(new Register(1), Value: 1),
                new HaltInstruction(),
            ]));

        // Jump target past the end of the program.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new IfPosInstruction(new Register(0), new ProgramCounter(3)),
                new HaltInstruction(),
            ]));

        // MustBeInt without a target is a valid construction (fails closed at run time).
        var noTarget = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new MustBeIntInstruction(new Register(0)),
                new HaltInstruction(),
            ]);
        using var statement = new ResumableStatement(noTarget);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }
}
