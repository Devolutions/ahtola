using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers Halt/HaltIfNull error model and NotExists/Found rowid probes
/// (inventory: vdbe-halt-error-model, vdbe-seek-op-family-partial).
/// </summary>
public sealed class VdbeHaltAndSeekProbeOpcodeTests
{
    [Test]
    public void CleanHaltEndsProgram()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(1);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void HaltWithConstraintCodeRaisesEmbeddedSqlException()
    {
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new HaltInstruction(
                    ErrorCode: SqliteResultCode.ConstraintNotNull,
                    Description: "t.x",
                    OnError: VdbeHaltOnError.Abort),
            ]);

        using var statement = new ResumableStatement(program);
        var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.ConstraintNotNull);
        error.Message.Should().Contain("NOT NULL");
        error.Message.Should().Contain("t.x");
    }

    [Test]
    public void HaltIfNullFallsThroughWhenRegisterIsNotNull()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(42)),
                new HaltIfNullInstruction(new Register(0), SqliteResultCode.ConstraintNotNull, "t.x"),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(42);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void HaltIfNullRaisesWhenRegisterIsNull()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Null),
                new HaltIfNullInstruction(new Register(0), SqliteResultCode.ConstraintNotNull, "t.x"),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program);
        var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.ConstraintNotNull);
        error.Message.Should().Contain("NOT NULL constraint failed: t.x");
    }

    [Test]
    public void HaltDescriptionRegisterIsReadAtRuntime()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Text("runtime message")),
                new HaltInstruction(
                    ErrorCode: SqliteResultCode.Error,
                    DescriptionRegister: new Register(0),
                    OnError: VdbeHaltOnError.Abort),
            ]);

        using var statement = new ResumableStatement(program);
        var error = Assert.Throws<EmbeddedSqlException>(() => statement.StepResumable());
        error!.SqliteErrorCode.Should().Be(SqliteResultCode.Error);
        error.Message.Should().Be("runtime message");
    }

    [Test]
    public void MidProgramErrorHaltIsValid()
    {
        var act = () => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Null),
                new HaltInstruction(ErrorCode: SqliteResultCode.Constraint, Description: "mid"),
                new HaltInstruction(),
            ]);

        act.Should().NotThrow();
    }

    [Test]
    public void CleanMidProgramHaltIsRejected()
    {
        var act = () => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new HaltInstruction(),
                new HaltInstruction(),
            ]);

        act.Should().Throw<VdbeProgramValidationException>();
    }

    [Test]
    public void NotExistsJumpsWhenRowidAbsentAndFallsThroughWhenPresent()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(1), SqlValue.Text("a") },
            new[] { SqlValue.Integer(2), SqlValue.Text("b") },
        };
        var rowIds = new List<long> { 10, 20 };
        var source = new VdbeCursorSource(rows, rowIds);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(99)),
            new NotExistsInstruction(
                new Cursor(0),
                new Register(0),
                new ProgramCounter(5),
                "not exists 99"),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("present")),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("absent")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("absent");
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void FoundJumpsWhenRowidPresent()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(1), SqlValue.Text("a") },
            new[] { SqlValue.Integer(2), SqlValue.Text("b") },
        };
        var rowIds = new List<long> { 10, 20 };
        var source = new VdbeCursorSource(rows, rowIds);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(20)),
            new FoundInstruction(
                new Cursor(0),
                new Register(0),
                new ProgramCounter(5),
                "found 20"),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("missing")),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("found")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("found");
    }

    [Test]
    public void FoundFallsThroughWhenRowidAbsent()
    {
        var rows = new List<SqlValue[]>
        {
            new[] { SqlValue.Integer(1), SqlValue.Text("a") },
        };
        var rowIds = new List<long> { 10 };
        var source = new VdbeCursorSource(rows, rowIds);

        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(99)),
            new FoundInstruction(
                new Cursor(0),
                new Register(0),
                new ProgramCounter(5),
                "found 99"),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("missing")),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("found")),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program, cursorSources: [source]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsText().Should().Be("missing");
    }

    [Test]
    public void ExplainRendersHaltIfNullAndNotExists()
    {
        var haltIfNull = new HaltIfNullInstruction(
            new Register(3),
            SqliteResultCode.ConstraintNotNull,
            "t.x");
        var (p1, _, p3, p4, comment) = VdbeExplain.Describe(haltIfNull);
        p1.Should().Be(SqliteResultCode.ConstraintNotNull);
        p3.Should().Be(3);
        p4.Should().Be("t.x");
        comment.Should().Contain("null");

        var notExists = new NotExistsInstruction(
            new Cursor(1),
            new Register(4),
            new ProgramCounter(8),
            "not exists");
        var (n1, n2, n3, _, nComment) = VdbeExplain.Describe(notExists);
        n1.Should().Be(1);
        n2.Should().Be(8);
        n3.Should().Be(4);
        nComment.Should().Be("not exists");
    }
}
