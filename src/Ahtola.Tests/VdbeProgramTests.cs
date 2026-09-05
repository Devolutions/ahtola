using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

public class VdbeProgramTests
{
    [Test]
    public void PublicOpcodeValuesAndConstructorRemainCompatible()
    {
        Enum.GetValues<VdbeOpcode>()
            .Select(static opcode => $"{opcode}={(int)opcode}")
            .Should()
            .Equal(
                "LoadConstant=0", "LoadParameter=1", "Copy=2", "Function=3", "Arithmetic=4",
                "NumericAffinity=5", "OpenReadCursor=6", "OpenJoinCursor=7", "OpenWriteCursor=8",
                "Rewind=9", "Column=10", "RowId=11", "Filter=12", "FilterRowId=13",
                "FilterRegisters=14", "ProjectRegisters=15", "DistinctFilter=16", "Next=17",
                "Delete=18", "Insert=19", "Update=20", "Commit=21", "CloseCursor=22",
                "OpenSorter=23", "SorterInsert=24", "SorterSort=25", "SorterData=26",
                "SorterNext=27", "CloseSorter=28", "Goto=29", "JumpIf=30", "AggReset=31",
                "AggStep=32", "AggFinalize=33", "SameGroup=34", "Yield=35", "ResultRow=36",
                "DistinctResultRow=37", "RowSetInsert=38", "RowSetRewind=39", "RowSetNext=40",
                "CompoundResultRow=41", "GuardedRow=42", "OffsetGate=43", "LimitGate=44",
                "BeginTransaction=45", "CommitTransaction=46", "RollbackTransaction=47",
                "Savepoint=48", "ReleaseSavepoint=49", "RollbackToSavepoint=50",
                "OpenWorkTable=51", "SeedWorkTable=52", "WorkTableStep=53",
                "WorkTableExpand=54", "WorkTableExpandGeneration=55", "CloseWorkTable=56",
                "Halt=57", "GroupKey=58", "DistinctGate=59", "OpenWindowBuffer=60",
                "WindowBufferInsert=61", "WindowBufferCompute=62", "WindowBufferData=63",
                "WindowBufferNext=64", "CloseWindowBuffer=65", "Compare=66",
                "JumpIfNotTrue=67", "Cast=68", "SeekRowid=69", "SeekRowidRange=70",
                "RowCount=71", "Last=72", "Prev=73", "RowSetTest=74", "Program=75",
                "NotExists=76", "Found=77", "HaltIfNull=78", "OpenEphemeral=79",
                "EphemeralInsert=80", "NoConflict=81", "FkCounter=82", "FkIfZero=83",
                "FkCheck=84", "SeekGE=85", "SeekGT=86", "SeekLE=87", "SeekLT=88",
                "IdxGE=89", "IdxGT=90", "IdxLE=91", "IdxLT=92", "IdxRowId=93",
                "RowData=94", "IdxInsert=95", "IdxDelete=96", "RowGate=97", "VOpen=98",
                "VFilter=99", "VColumn=100", "VUpdate=101", "VNext=102", "VBegin=103",
                "VSync=104", "VCommit=105", "VRollback=106", "IndexMethodCreate=107",
                "IndexMethodDestroy=108", "IndexMethodOptimize=109", "IndexMethodQuery=110",
                "IndexMethodNext=111", "IndexMethodColumn=112", "IndexMethodRowId=113",
                "IndexMethodInsert=114", "IndexMethodDelete=115", "VCreate=116",
                "VDestroy=117", "VRename=118", "MakeRecord=119", "NewRowid=120",
                "CreateBtree=121", "ClearBtree=122", "Destroy=123", "ReadCookie=124",
                "SetCookie=125", "ParseSchema=126", "DropTable=127", "DropView=128",
                "DropIndex=129", "DropTrigger=130", "RenameTable=131", "AddColumn=132",
                "DropColumn=133", "AlterColumn=134", "IndexBuild=135", "AggInverse=136",
                "BlobRead=137", "BlobWrite=138", "BlobLen=139", "ColumnRange=140",
                "OpenPseudo=141", "TypeCheck=142", "Once=143", "ResetOnce=144",
                "ChangeCount=145", "ResetSorter=146", "AggValue=147", "OpenDup=148",
                "OpenAutoindex=149", "ColumnHasField=150", "DeferredSeek=151",
                "SeekEnd=152", "BloomFilter=153", "BloomFilterAdd=154", "HashBuild=155",
                "HashDistinct=156", "HashBuildFinalize=157", "HashProbe=158",
                "HashNext=159", "HashClose=160", "HashClear=161", "HashMarkMatched=162",
                "HashResetMatched=163", "HashScanUnmatched=164", "HashNextUnmatched=165",
                "IfPos=166", "IfNeg=167", "DecrJumpZero=168", "MustBeInt=169",
                "SoftNull=170", "MemMax=171", "AddImm=172", "ZeroOrNull=173",
                "Gosub=174", "Return=175", "BeginSubrtn=176",
                "Sequence=177", "SequenceTest=178");

        var constructors = typeof(VdbeProgram).GetConstructors();
        Type[] legacyParameterTypes =
        [
            typeof(int),
            typeof(int),
            typeof(IEnumerable<VdbeInstruction>),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
        ];
        Type[] currentParameterTypes = [.. legacyParameterTypes, typeof(int), typeof(int)];
        constructors.Any(constructor => constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .SequenceEqual(legacyParameterTypes)).Should().BeTrue();
        constructors.Any(constructor => constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .SequenceEqual(currentParameterTypes)).Should().BeTrue();

        var legacyProgram = new VdbeProgram(
            0,
            0,
            [new HaltInstruction()],
            0,
            0,
            0,
            0,
            0);
        legacyProgram.WindowBufferCount.Should().Be(0);

        var defaultProgram = new VdbeProgram(0, 0, [new HaltInstruction()]);
        defaultProgram.SorterCount.Should().Be(0);
        defaultProgram.AccumulatorCount.Should().Be(0);
        defaultProgram.DistinctSetCount.Should().Be(0);
        defaultProgram.ParameterSlotCount.Should().Be(0);
        defaultProgram.WorkTableCount.Should().Be(0);
        defaultProgram.WindowBufferCount.Should().Be(0);

        var currentProgram = new VdbeProgram(
            0,
            0,
            [new HaltInstruction()],
            sorterCount: 0,
            accumulatorCount: 0,
            distinctSetCount: 0,
            parameterSlotCount: 0,
            workTableCount: 0,
            windowBufferCount: 0,
            hashTableCount: 0);
        currentProgram.WindowBufferCount.Should().Be(0);
        currentProgram.HashTableCount.Should().Be(0);

        var legacyInsert = new InsertInstruction(new Cursor(3), VdbeInsertFlags.SkipLastRowid);
        var (legacyCursor, legacyFlags) = legacyInsert;
        legacyCursor.Should().Be(new Cursor(3));
        legacyFlags.Should().Be(VdbeInsertFlags.SkipLastRowid);
        typeof(InsertInstruction).GetConstructor([typeof(Cursor), typeof(VdbeInsertFlags)])
            .Should()
            .NotBeNull();
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
        p3.Should().Be(2);
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
