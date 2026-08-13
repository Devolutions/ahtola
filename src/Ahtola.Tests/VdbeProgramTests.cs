using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

public class VdbeProgramTests
{
    [Test]
    public void PublicOpcodeValuesAndConstructorRemainCompatible()
    {
        ((int)VdbeOpcode.OpenJoinCursor).Should().Be(7);
        ((int)VdbeOpcode.Next).Should().Be(17);
        ((int)VdbeOpcode.AggStep).Should().Be(32);
        ((int)VdbeOpcode.Halt).Should().Be(57);
        ((int)VdbeOpcode.Compare).Should().Be(66);
        ((int)VdbeOpcode.JumpIfNotTrue).Should().Be(67);
        ((int)VdbeOpcode.Cast).Should().Be(68);
        ((int)VdbeOpcode.RowSetTest).Should().Be(74);
        ((int)VdbeOpcode.Program).Should().Be(75);
        ((int)VdbeOpcode.NotExists).Should().Be(76);
        ((int)VdbeOpcode.Found).Should().Be(77);
        ((int)VdbeOpcode.HaltIfNull).Should().Be(78);
        ((int)VdbeOpcode.OpenEphemeral).Should().Be(79);
        ((int)VdbeOpcode.EphemeralInsert).Should().Be(80);
        ((int)VdbeOpcode.NoConflict).Should().Be(81);
        ((int)VdbeOpcode.FkCounter).Should().Be(82);
        ((int)VdbeOpcode.FkIfZero).Should().Be(83);
        ((int)VdbeOpcode.FkCheck).Should().Be(84);
        ((int)VdbeOpcode.SeekGE).Should().Be(85);
        ((int)VdbeOpcode.IdxDelete).Should().Be(96);

        typeof(VdbeProgram).GetConstructor(
            [
                typeof(int),
                typeof(int),
                typeof(IEnumerable<VdbeInstruction>),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int),
            ]).Should().NotBeNull();

        var program = new VdbeProgram(
            0,
            0,
            [new HaltInstruction()],
            0,
            0,
            0,
            0,
            0);
        program.WindowBufferCount.Should().Be(0);
    }

    [Test]
    public void ProgramValidatesTypedOperandsAndOwnsItsInstructionSequence()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
            new OpenReadCursorInstruction(new Cursor(0)),
            new CloseCursorInstruction(new Cursor(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        instructions[0] = new HaltInstruction();

        program.Instructions.Should().HaveCount(5);
        program.Instructions[0].Should().BeOfType<LoadConstantInstruction>();
        program.Validate();
    }

    [Test]
    public void ProgramRejectsMalformedBytecode()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(1), SqlValue.Integer(1)),
                new HaltInstruction(),
            ]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new YieldInstruction()]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                null!,
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void StatementPreservesTheRowAndDoneLifecycle()
    {
        var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));

        statement.State.Should().Be(ResumableStatementState.Ready);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.State.Should().Be(ResumableStatementState.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(7));

        statement.Step().Should().Be(StatementStepResult.Done);
        statement.State.Should().Be(ResumableStatementState.Done);
        statement.CurrentRow.Should().BeNull();
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.Reset();
        statement.State.Should().Be(ResumableStatementState.Ready);
        statement.InstructionPointer.Should().Be(new ProgramCounter(0));
        statement.Step().Should().Be(StatementStepResult.Row);
    }

    [Test]
    public void YieldAdvancesTheProgramCounterAndRequiresAnExplicitResume()
    {
        var register = new Register(0);
        var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(register, SqlValue.Integer(1)),
                new YieldInstruction(),
                new LoadConstantInstruction(register, SqlValue.Integer(2)),
                new ResultRowInstruction(new RegisterRange(register, 1)),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Yielded);
        statement.State.Should().Be(ResumableStatementState.Yielded);
        statement.InstructionPointer.Should().Be(new ProgramCounter(2));
        statement.GetRegister(register).Should().Be(SqlValue.Integer(1));
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());

        statement.Resume();
        statement.State.Should().Be(ResumableStatementState.Ready);
        statement.InstructionPointer.Should().Be(new ProgramCounter(2));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(2));
        Assert.Throws<InvalidOperationException>(() => statement.Resume());
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void CompatibilityStepSignalsYieldWithoutLosingResumeState()
    {
        var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new YieldInstruction(),
                new HaltInstruction(),
            ]));

        Assert.Throws<StatementYieldedException>(() => statement.Step());
        statement.State.Should().Be(ResumableStatementState.Yielded);
        statement.InstructionPointer.Should().Be(new ProgramCounter(1));

        statement.Resume();
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void ProgramInstructionBindsParentRegistersAndSuppressesChildRows()
    {
        var childRegister = new Register(0);
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(childRegister, new ParameterSlot(0)),
                new ResultRowInstruction(new RegisterRange(childRegister, 1)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 1));
        var parentRegister = new Register(0);
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(parentRegister, SqlValue.Integer(7)),
                new ProgramInstruction([parentRegister], child),
                new LoadConstantInstruction(parentRegister, SqlValue.Integer(9)),
                new ResultRowInstruction(new RegisterRange(parentRegister, 1)),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(9));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void ProgramInstructionRendersItsRegisterBindingsForExplain()
    {
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new HaltInstruction()]));

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(
            new ProgramInstruction([new Register(2), new Register(4)], child));

        p1.Should().Be(2);
        p2.Should().Be(0);
        p3.Should().Be(0);
        p4.Should().Be("subprogram");
        comment.Should().Be("invoke subprogram with r[2, 4]");
    }

    [Test]
    public void ProgramInstructionPropagatesChildYieldUntilResumed()
    {
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new YieldInstruction(),
                new HaltInstruction(),
            ]));
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ProgramInstruction([], child),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Yielded);
        statement.State.Should().Be(ResumableStatementState.Yielded);

        statement.Resume();
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void ProgramInstructionResetsItsCachedChildBeforeEachInvocation()
    {
        var deleted = new List<int>();
        var commits = 0;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "child",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            DeleteRow = deleted.Add,
            Commit = () =>
            {
                commits++;
                return null;
            },
        };
        var child = new VdbeSubprogram(
            new VdbeProgram(
                registerCount: 0,
                cursorCount: 1,
                [
                    new OpenWriteCursorInstruction(new Cursor(0), "child", 1),
                    new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                    new DeleteInstruction(new Cursor(0)),
                    new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                    new CommitInstruction(new Cursor(0)),
                    new CloseCursorInstruction(new Cursor(0)),
                    new HaltInstruction(),
                ]),
            writeTargets: [writeTarget]);
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenReadCursorInstruction(new Cursor(0), "parent", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new ProgramInstruction([], child),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]);
        using var statement = new ResumableStatement(
            program,
            [new VdbeCursorSource([[SqlValue.Integer(1)], [SqlValue.Integer(2)]])]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        deleted.Should().Equal(0, 0);
        commits.Should().Be(2);
    }

    [Test]
    public void DeleteInstructionCountsOnlyRowsDeletedByItsLiveWriteTarget()
    {
        var attemptedPositions = new List<int>();
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 2,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = index => index + 1,
            TryDeleteRow = position =>
            {
                attemptedPositions.Add(position);
                return position == 0;
            },
            Commit = () => null,
        };
        using var statement = new ResumableStatement(
            new VdbeProgram(
                registerCount: 0,
                cursorCount: 1,
                [
                    new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                    new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                    new DeleteInstruction(new Cursor(0)),
                    new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                    new CommitInstruction(new Cursor(0)),
                    new CloseCursorInstruction(new Cursor(0)),
                    new HaltInstruction(),
                ]),
            writeTargets: [writeTarget]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        attemptedPositions.Should().Equal(0, 1);
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void DeferredProgramInstructionResolvesARecursiveSubprogram()
    {
        var parameter = new Register(0);
        var decrement = new Register(1);
        var recursive = VdbeSubprogram.CreateDeferred(parameterSlotCount: 1);
        var childProgram = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadParameterInstruction(parameter, new ParameterSlot(0)),
                new JumpIfInstruction(parameter, new ProgramCounter(3)),
                new GotoInstruction(new ProgramCounter(6)),
                new LoadConstantInstruction(decrement, SqlValue.Integer(1)),
                new ArithmeticInstruction(
                    parameter,
                    ArithmeticOperator.Subtract,
                    new RegisterRange(parameter, 2)),
                new ProgramInstruction([parameter], recursive),
                new HaltInstruction(),
            ],
            parameterSlotCount: 1);
        recursive.Resolve(childProgram);
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(parameter, SqlValue.Integer(4)),
                new ProgramInstruction([parameter], recursive),
                new HaltInstruction(),
            ]));

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void DeferredProgramInstructionFailsClearlyBeforeResolution()
    {
        var recursive = VdbeSubprogram.CreateDeferred(parameterSlotCount: 0);
        using var statement = new ResumableStatement(new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ProgramInstruction([], recursive),
                new HaltInstruction(),
            ]));

        var exception = Assert.Throws<InvalidOperationException>(() => statement.StepResumable());

        exception!.Message.Should().Be("The recursive VDBE subprogram was not resolved before execution.");
    }

    [Test]
    public void ProgramInstructionRejectsAnArgumentCountDifferentFromItsSubprogramSlots()
    {
        var child = new VdbeSubprogram(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 1));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ProgramInstruction([], child),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void ConstructorRejectsWriteTargetCountThatDoesNotMatchCursors()
    {
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(3)),
                new CommitInstruction(new Cursor(0)),
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]);

        Assert.Throws<ArgumentException>(
            () => new ResumableStatement(program, cursorSources: null, writeTargets: []));
    }

    [Test]
    public void InsertProgramMaterializesWrittenRowsAndTracksMetadata()
    {
        var mutated = new List<int>();
        var committed = false;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 2,
            MutateRow = index =>
            {
                mutated.Add(index);
                return new VdbeRowMutation([SqlValue.Integer(index + 10)], index + 1);
            },
            Commit = () =>
            {
                committed = true;
                return 2;
            },
        };

        // 0 OpenWriteCursor / 1 Rewind->6 / 2 Insert / 3 RowId r0 / 4 ResultRow / 5 Next->2
        // 6 Commit / 7 CloseCursor / 8 Halt
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(6)),
                new InsertInstruction(new Cursor(0)),
                new RowIdInstruction(new Cursor(0), new Register(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new CommitInstruction(new Cursor(0)),
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, cursorSources: null, writeTargets: [writeTarget]);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(1));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(2));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        mutated.Should().Equal(0, 1);
        committed.Should().BeTrue();
        statement.RowsAffected.Should().Be(2);
        statement.LastInsertRowId.Should().Be(2);
    }

    [Test]
    public void UpdateProgramMutatesOnlyRowsPassingTheFilter()
    {
        var source = new SqlValue[][] { [SqlValue.Integer(1)], [SqlValue.Integer(2)] };
        var mutated = new List<int>();
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = source.Length,
            GetRow = index => source[index],
            GetRowId = index => index + 1,
            MutateRow = index =>
            {
                mutated.Add(index);
                return new VdbeRowMutation([SqlValue.Integer(99)], index + 1);
            },
            Commit = () => null,
        };

        // Filter keeps only even values, so only the second row is updated.
        // 0 OpenWriteCursor / 1 Rewind->5 / 2 Filter->4 / 3 Update / 4 Next->2
        // 5 Commit / 6 CloseCursor / 7 Halt
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(5)),
                new FilterInstruction(
                    new Cursor(0),
                    row => row[0].AsInteger() % 2 == 0,
                    new ProgramCounter(4),
                    "keep even rows"),
                new UpdateInstruction(new Cursor(0)),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new CommitInstruction(new Cursor(0)),
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, cursorSources: null, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        mutated.Should().Equal(1);
        statement.RowsAffected.Should().Be(1);
        statement.LastInsertRowId.Should().BeNull();
    }

    [Test]
    public void DeleteProgramMarksEveryScannedRow()
    {
        var deleted = new List<int>();
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 3,
            GetRow = index => [SqlValue.Integer(index)],
            GetRowId = index => index + 1,
            DeleteRow = deleted.Add,
            Commit = () => null,
        };

        // 0 OpenWriteCursor / 1 Rewind->4 / 2 Delete / 3 Next->2 / 4 Commit / 5 CloseCursor / 6 Halt
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new DeleteInstruction(new Cursor(0)),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new CommitInstruction(new Cursor(0)),
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, cursorSources: null, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        deleted.Should().Equal(0, 1, 2);
        statement.RowsAffected.Should().Be(3);
    }

    [Test]
    public void EmptyWriteCursorSkipsTheMutationLoopButStillCommits()
    {
        var mutated = false;
        var committed = false;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 0,
            MutateRow = _ =>
            {
                mutated = true;
                return new VdbeRowMutation([], 0);
            },
            Commit = () =>
            {
                committed = true;
                return null;
            },
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new UpdateInstruction(new Cursor(0)),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new CommitInstruction(new Cursor(0)),
                new CloseCursorInstruction(new Cursor(0)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, cursorSources: null, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);

        mutated.Should().BeFalse();
        committed.Should().BeTrue();
        statement.RowsAffected.Should().Be(0);
    }
}
