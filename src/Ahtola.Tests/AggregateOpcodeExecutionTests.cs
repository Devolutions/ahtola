using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the aggregate family (AggReset/AggStep/AggInverse/AggFinalize)
// and its grouped control flow (Goto/SameGroup). Programs are built by hand from the public
// Execution contract and run through the resumable state machine, so the tests exercise
// the interpreter and validator directly rather than any database wiring.
public class AggregateOpcodeExecutionTests
{
    [Test]
    public void AggStepFoldsSteppedRowsAndAggFinalizeProducesTheResult()
    {
        VdbeInstruction[] instructions =
        [
            new AggResetInstruction(new Accumulator(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(10)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(20)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        var rows = RunToCompletion(program);

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(30));
    }

    [Test]
    public void FinalizingAnUnsteppedAccumulatorYieldsTheEmptyInputValue()
    {
        // No AggStep runs, so SUM finalizes to NULL and COUNT(*) to 0 from fresh contexts,
        // even without an explicit AggReset (accumulators start uninitialized).
        VdbeInstruction[] instructions =
        [
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(0)),
            new AggFinalizeInstruction(new Accumulator(1), AggregateTestSupport.CountStar(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 2);

        var rows = RunToCompletion(program);

        rows.Should().ContainSingle();
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
        rows[0][1].Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void AggFinalizeDoesNotResetTheAccumulator()
    {
        // The accumulator is finalized once, then stepped again without a reset: the second
        // finalize must include both steps, proving AggFinalize leaves state intact.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger()).Should().Equal(5, 12);
    }

    [Test]
    public void AggValueDoesNotResetTheAccumulator()
    {
        // AggValue reads the current value without touching the accumulator: a second step
        // afterwards must fold into the same context, unlike AggFinalize which pairs with a
        // reset in SQLite's classic flow.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggValueInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggValueInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger()).Should().Equal(5, 12);
    }

    [Test]
    public void AggValueOnNeverSteppedAccumulatorYieldsTheEmptyInputValue()
    {
        // Reading a never-stepped accumulator must behave like finalizing a fresh context:
        // SUM yields NULL and COUNT(*) yields 0.
        VdbeInstruction[] instructions =
        [
            new AggValueInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(0)),
            new AggValueInstruction(new Accumulator(1), AggregateTestSupport.CountStar(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 2);

        var rows = RunToCompletion(program);

        rows.Should().ContainSingle();
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
        rows[0][1].Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void AggResetDiscardsAccumulatedState()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggResetInstruction(new Accumulator(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        var rows = RunToCompletion(program);

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(7));
    }

    [Test]
    public void NullaryAggStepCountsRowsWithoutReadingArguments()
    {
        // COUNT(*) steps a zero-width argument range, so the accumulator only counts rows.
        VdbeInstruction[] instructions =
        [
            new AggResetInstruction(new Accumulator(0)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.CountStar(), new RegisterRange(new Register(0), 0)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.CountStar(), new RegisterRange(new Register(0), 0)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.CountStar(), new RegisterRange(new Register(0), 0)),
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.CountStar(), new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, accumulatorCount: 1);

        var rows = RunToCompletion(program);

        rows[0].Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void AggInverseRemovesSteppedRowsBeforeFinalize()
    {
        var sum = AggregateTestSupport.Sum();
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(10)),
            new AggStepInstruction(new Accumulator(0), sum, new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(20)),
            new AggStepInstruction(new Accumulator(0), sum, new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(10)),
            new AggInverseInstruction(new Accumulator(0), sum, new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), sum, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(20));
    }

    [Test]
    public void NullaryAggInverseRemovesOneCountStarStep()
    {
        var count = AggregateTestSupport.CountStar();
        var noArguments = new RegisterRange(new Register(0), 0);
        VdbeInstruction[] instructions =
        [
            new AggStepInstruction(new Accumulator(0), count, noArguments),
            new AggStepInstruction(new Accumulator(0), count, noArguments),
            new AggStepInstruction(new Accumulator(0), count, noArguments),
            new AggInverseInstruction(new Accumulator(0), count, noArguments),
            new AggFinalizeInstruction(new Accumulator(0), count, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, accumulatorCount: 1);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(2));
    }

    [Test]
    public void AggInverseStoresTheReplacementContextAndCannotMutateRegisters()
    {
        var aggregate = new VdbeAggregate
        {
            Name = "replace",
            CreateContext = static () => 0L,
            Accumulate = static (context, arguments) => (long)context! + arguments[0].AsInteger(),
            Inverse = static (context, arguments) =>
            {
                var next = (long)context! - arguments[0].AsInteger();
                arguments[0] = SqlValue.Integer(999);
                return next;
            },
            Finalize = static context => SqlValue.Integer((long)context!),
        };
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
            new AggStepInstruction(new Accumulator(0), aggregate, new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new AggInverseInstruction(new Accumulator(0), aggregate, new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), aggregate, new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(5));
    }

    [Test]
    public void AggInverseRequiresAPriorStep()
    {
        var sum = AggregateTestSupport.Sum();
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggInverseInstruction(
                    new Accumulator(0),
                    sum,
                    new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1);
        using var statement = new ResumableStatement(program);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Be("AggInverse accumulator 0 is not initialized; AggStep must run first.");
        statement.State.Should().Be(ResumableStatementState.Faulted);
    }

    [Test]
    public void AggInverseComposesWithCompoundAndLimitRelocation()
    {
        static CompoundTerm Term(long stepped, long removed)
        {
            var sum = AggregateTestSupport.Sum();
            var program = new VdbeProgram(
                registerCount: 2,
                cursorCount: 0,
                [
                    new LoadConstantInstruction(new Register(0), SqlValue.Integer(stepped)),
                    new AggStepInstruction(
                        new Accumulator(0),
                        sum,
                        new RegisterRange(new Register(0), 1)),
                    new LoadConstantInstruction(new Register(0), SqlValue.Integer(removed)),
                    new AggStepInstruction(
                        new Accumulator(0),
                        sum,
                        new RegisterRange(new Register(0), 1)),
                    new AggInverseInstruction(
                        new Accumulator(0),
                        sum,
                        new RegisterRange(new Register(0), 1)),
                    new AggFinalizeInstruction(new Accumulator(0), sum, new Register(1)),
                    new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
                    new HaltInstruction(),
                ],
                accumulatorCount: 1);
            return new CompoundTerm(program, Array.Empty<VdbeCursorSource>());
        }
        var compound = CompoundProgramBuilder.BuildUnionAll([Term(10, 3), Term(20, 5)]);
        var gated = LimitOffsetProgramBuilder.Apply(compound.Program, offset: 0, limit: 2);

        RunToCompletion(gated).Select(row => row[0]).Should().Equal(
            SqlValue.Integer(10),
            SqlValue.Integer(20));
    }

    [Test]
    public void GotoJumpsUnconditionally()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new GotoInstruction(new ProgramCounter(3)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(999)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        var rows = RunToCompletion(program);

        rows[0].Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void SameGroupJumpsWhenKeysMatch()
    {
        RunSameGroupProbe(savedKey: 1, currentKey: 1).Should().Be(200);
    }

    [Test]
    public void SameGroupFallsThroughOnANewGroupBoundary()
    {
        RunSameGroupProbe(savedKey: 1, currentKey: 2).Should().Be(100);
    }

    [Test]
    public void SameGroupTreatsTwoNullKeysAsTheSameGroup()
    {
        // NULL == NULL for grouping: two NULL keys fall in the same group and jump.
        VdbeInstruction[] instructions =
        [
            new AggResetInstruction(new Accumulator(0)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.CountStar(), new RegisterRange(new Register(0), 0)),
            new SameGroupInstruction(
                new RegisterRange(new Register(0), 1),
                new RegisterRange(new Register(1), 1),
                AggregateTestSupport.GroupKeysEqual(),
                new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(100)),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(200)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        // r0 and r1 both default to NULL.
        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions, accumulatorCount: 1);

        RunToCompletion(program)[0].Should().Equal(SqlValue.Integer(200));
    }

    [Test]
    public void ResetReplaysAnAggregateProgramFromTheStart()
    {
        VdbeInstruction[] instructions =
        [
            new AggResetInstruction(new Accumulator(0)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(4)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(6)),
            new AggStepInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new RegisterRange(new Register(0), 1)),
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.Sum(), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 0, instructions, accumulatorCount: 1);

        using var statement = new ResumableStatement(program);
        DrainRows(statement)[0].Should().Equal(SqlValue.Integer(10));

        statement.Reset();

        DrainRows(statement)[0].Should().Equal(SqlValue.Integer(10));
    }

    [Test]
    public void DisposeStopsAnAggregateStatementFromStepping()
    {
        VdbeInstruction[] instructions =
        [
            new AggFinalizeInstruction(new Accumulator(0), AggregateTestSupport.CountStar(), new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions, accumulatorCount: 1);
        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    [Test]
    public void ValidationRejectsMalformedAggregateBytecode()
    {
        var sum = AggregateTestSupport.Sum();
        var comparer = AggregateTestSupport.GroupKeysEqual();

        // Accumulator index beyond the declared count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new AggResetInstruction(new Accumulator(0)), new HaltInstruction()],
            accumulatorCount: 0));

        // Stepping with a null aggregate.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggStepInstruction(new Accumulator(0), null!, new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Finalizing with a null aggregate.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggFinalizeInstruction(new Accumulator(0), null!, new Register(0)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Reading with a null aggregate.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggValueInstruction(new Accumulator(0), null!, new Register(0)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Stepping arguments that reach outside the register file.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggStepInstruction(new Accumulator(0), sum, new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Inversing with a null aggregate.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggInverseInstruction(
                    new Accumulator(0),
                    null!,
                    new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Inversing with an aggregate that does not implement inverse.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggInverseInstruction(
                    new Accumulator(0),
                    AggregateTestSupport.Min(),
                    new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Inversing arguments that reach outside the register file.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggInverseInstruction(
                    new Accumulator(0),
                    sum,
                    new RegisterRange(new Register(0), 3)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Inversing with an accumulator beyond the declared count.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggInverseInstruction(
                    new Accumulator(1),
                    sum,
                    new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // Finalizing into a register outside the register file.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new AggFinalizeInstruction(new Accumulator(0), sum, new Register(5)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1));

        // SameGroup with a null comparer.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new SameGroupInstruction(
                    new RegisterRange(new Register(0), 1),
                    new RegisterRange(new Register(1), 1),
                    null!,
                    new ProgramCounter(1)),
                new HaltInstruction(),
            ]));

        // SameGroup comparing key tuples of different widths.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 3,
            cursorCount: 0,
            [
                new SameGroupInstruction(
                    new RegisterRange(new Register(0), 2),
                    new RegisterRange(new Register(2), 1),
                    comparer,
                    new ProgramCounter(1)),
                new HaltInstruction(),
            ]));

        // SameGroup jump target outside the program.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new SameGroupInstruction(
                    new RegisterRange(new Register(0), 1),
                    new RegisterRange(new Register(1), 1),
                    comparer,
                    new ProgramCounter(99)),
                new HaltInstruction(),
            ]));

        // Computed group keys require a positive key width and an allocated group set.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new GroupKeyInstruction(
                    new RegisterRange(new Register(0), 1),
                    new Register(1),
                    KeyCount: 0,
                    Projector: row => row,
                    Equality: comparer,
                    GroupSetIndex: 0),
                new HaltInstruction(),
            ],
            distinctSetCount: 1));
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new GroupKeyInstruction(
                    new RegisterRange(new Register(0), 1),
                    new Register(1),
                    KeyCount: 1,
                    Projector: row => row,
                    Equality: comparer,
                    GroupSetIndex: 0),
                new HaltInstruction(),
            ]));

        // Goto jump target outside the program.
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new GotoInstruction(new ProgramCounter(99)), new HaltInstruction()]));
    }

    [Test]
    public void ConstructorRejectsNegativeAccumulatorCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new HaltInstruction()],
            accumulatorCount: -1));
    }

    [Test]
    public void AccumulatorHandleRejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Accumulator(-1));
    }

    [Test]
    public void ResumableStatementRetainsTheParameterlessStepAbi()
    {
        typeof(ResumableStatement)
            .GetMethod(nameof(ResumableStatement.StepResumable), Type.EmptyTypes)
            .Should().NotBeNull();
        typeof(ResumableStatement)
            .GetMethod(
                nameof(ResumableStatement.StepResumable),
                [typeof(CancellationToken)])
            .Should().NotBeNull();
    }

    [Test]
    public void FailedAggregateStepRequiresResetBeforeRetry()
    {
        var failOnce = true;
        var aggregate = new VdbeAggregate
        {
            Name = "fail-once",
            CreateContext = static () => new List<SqlValue>(),
            Accumulate = (context, arguments) =>
            {
                var values = (List<SqlValue>)context!;
                values.Add(arguments[0]);
                if (failOnce)
                {
                    failOnce = false;
                    throw new InvalidOperationException("aggregate step failed");
                }

                return values;
            },
            Finalize = context => SqlValue.Integer(((List<SqlValue>)context!).Count),
        };
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new AggStepInstruction(
                    new Accumulator(0),
                    aggregate,
                    new RegisterRange(new Register(0), 1)),
                new AggFinalizeInstruction(new Accumulator(0), aggregate, new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1);
        using var statement = new ResumableStatement(program);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Be("aggregate step failed");
        statement.State.Should().Be(ResumableStatementState.Faulted);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("Call Reset");

        statement.Reset();
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void FailedAggregateInverseRequiresResetBeforeRetry()
    {
        var failOnce = true;
        var aggregate = new VdbeAggregate
        {
            Name = "fail-inverse-once",
            CreateContext = static () => 0L,
            Accumulate = static (context, arguments) => (long)context! + arguments[0].AsInteger(),
            Inverse = (context, arguments) =>
            {
                if (failOnce)
                {
                    failOnce = false;
                    throw new InvalidOperationException("aggregate inverse failed");
                }

                return (long)context! - arguments[0].AsInteger();
            },
            Finalize = static context => SqlValue.Integer((long)context!),
        };
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
                new AggStepInstruction(
                    new Accumulator(0),
                    aggregate,
                    new RegisterRange(new Register(0), 1)),
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
                new AggInverseInstruction(
                    new Accumulator(0),
                    aggregate,
                    new RegisterRange(new Register(0), 1)),
                new AggFinalizeInstruction(new Accumulator(0), aggregate, new Register(1)),
                new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
                new HaltInstruction(),
            ],
            accumulatorCount: 1);
        using var statement = new ResumableStatement(program);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Be("aggregate inverse failed");
        statement.State.Should().Be(ResumableStatementState.Faulted);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("Call Reset");

        statement.Reset();
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow.Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void NewAggregateOpcodesPreserveExistingNumericValues()
    {
        ((int)VdbeOpcode.Next).Should().Be(17);
        ((int)VdbeOpcode.RowSetInsert).Should().Be(38);
        ((int)VdbeOpcode.Halt).Should().Be(57);
        ((int)VdbeOpcode.GroupKey).Should().Be(58);
        ((int)VdbeOpcode.DistinctGate).Should().Be(59);
        ((int)VdbeOpcode.AggInverse).Should().Be(136);
        ((int)VdbeOpcode.ResetSorter).Should().Be(146);
        ((int)VdbeOpcode.AggValue).Should().Be(147);
        ((int)VdbeOpcode.OpenDup).Should().Be(148);
        ((int)VdbeOpcode.OpenAutoindex).Should().Be(149);
        ((int)VdbeOpcode.ColumnHasField).Should().Be(150);
        ((int)VdbeOpcode.DeferredSeek).Should().Be(151);
        ((int)VdbeOpcode.SeekEnd).Should().Be(152);
        ((int)VdbeOpcode.BloomFilter).Should().Be(153);
        ((int)VdbeOpcode.BloomFilterAdd).Should().Be(154);
        ((int)VdbeOpcode.HashBuild).Should().Be(155);
        ((int)VdbeOpcode.HashDistinct).Should().Be(156);
        ((int)VdbeOpcode.HashBuildFinalize).Should().Be(157);
        ((int)VdbeOpcode.HashProbe).Should().Be(158);
        ((int)VdbeOpcode.HashNext).Should().Be(159);
        ((int)VdbeOpcode.HashClose).Should().Be(160);
        ((int)VdbeOpcode.HashClear).Should().Be(161);
        ((int)VdbeOpcode.HashMarkMatched).Should().Be(162);
        ((int)VdbeOpcode.HashResetMatched).Should().Be(163);
        ((int)VdbeOpcode.HashScanUnmatched).Should().Be(164);
        ((int)VdbeOpcode.HashNextUnmatched).Should().Be(165);
    }

    // Loads saved/current keys, compares them with SameGroup, and returns the value the
    // taken branch writes: 200 on the same-group jump, 100 on the new-group fall-through.
    private static long RunSameGroupProbe(long savedKey, long currentKey)
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(savedKey)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(currentKey)),
            new SameGroupInstruction(
                new RegisterRange(new Register(1), 1),
                new RegisterRange(new Register(0), 1),
                AggregateTestSupport.GroupKeysEqual(),
                new ProgramCounter(5)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(100)),
            new GotoInstruction(new ProgramCounter(6)),
            new LoadConstantInstruction(new Register(2), SqlValue.Integer(200)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 0, instructions);

        return RunToCompletion(program)[0][0].AsInteger();
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
