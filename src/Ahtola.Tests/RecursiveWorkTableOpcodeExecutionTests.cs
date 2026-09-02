using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the recursive worktable family (OpenWorkTable / SeedWorkTable /
// WorkTableStep / WorkTableExpand / CloseWorkTable) added to the execution contract for bounded
// recursive-CTE evaluation. Programs are hand-assembled from the public Execution contract and run
// through the resumable state machine, so these tests exercise the interpreter's fixpoint loop, the
// validator, and the EXPLAIN renderer directly rather than any Compilation-layer lowering. The
// recursion itself — FIFO/breadth-first frontier draining, re-feeding descendants, de-duplication,
// depth bounding, and the row guard — lives in the interpreter; a test's transform delegate only
// computes one generation from one frontier row.
public class RecursiveWorkTableOpcodeExecutionTests
{
    // Byte-exact row equality for distinct worktables: NULLs equal each other, everything else compares
    // by exact kind and content. The executor defers every de-duplication decision to this delegate.
    private static readonly VdbeRowEquality ByteExactRows = (left, right) =>
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].Equals(right[index]))
                return false;
        }

        return true;
    };

    // A transform that never runs dry: n -> {n + 1}. Termination is left entirely to the depth/row
    // guards, so it isolates the interpreter's bounding behaviour from any transform-driven fixpoint.
    private static readonly VdbeRecursiveTransform Increment = row =>
        [[SqlValue.Integer(row[0].AsInteger() + 1)]];

    [Test]
    public void DrainsAnchorRowsInSeedOrderBeforeDescendants()
    {
        // Two anchors, one linear step each: the whole anchor generation (10, 20) must surface before any
        // descendant (11, 21). This is the observable breadth-first order the FIFO frontier guarantees.
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 1,
            Increment,
            Row(10),
            Row(20));

        Integers(RunToCompletion(program)).Should().Equal(10, 20, 11, 21);
    }

    [Test]
    public void ExpandsBreadthFirstAcrossAWholeGenerationAtATime()
    {
        // Binary tree: n -> {2n, 2n+1} while it stays within [1, 7]. Breadth-first draining yields the
        // level order 1, 2, 3, 4, 5, 6, 7; a depth-first stack would instead produce 1, 2, 4, 5, 3, 6, 7.
        VdbeRecursiveTransform branch = row =>
        {
            var n = row[0].AsInteger();
            var left = 2 * n;
            var right = 2 * n + 1;
            if (right > 7)
                return [];

            return [[SqlValue.Integer(left)], [SqlValue.Integer(right)]];
        };

        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 100,
            branch,
            Row(1));

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Test]
    public void KeepAllEmitsEveryAdmittedRowIncludingRepeats()
    {
        // A diamond: 1 -> {2, 3}, 2 -> {4}, 3 -> {4}. Under KeepAll the shared descendant 4 is admitted
        // twice (once via each parent), so it appears twice in level order.
        VdbeRecursiveTransform diamond = row => row[0].AsInteger() switch
        {
            1 => [[SqlValue.Integer(2)], [SqlValue.Integer(3)]],
            2 => [[SqlValue.Integer(4)]],
            3 => [[SqlValue.Integer(4)]],
            _ => [],
        };

        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 100,
            diamond,
            Row(1));

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3, 4, 4);
    }

    [Test]
    public void DistinctEmitsEachRowOnceAndBreaksCyclesToTerminate()
    {
        // A cyclic graph: 1 -> {2, 3}, 2 -> {3}, 3 -> {1}. The 3->1 back-edge would loop forever under
        // KeepAll, but distinct de-duplication drops already-seen rows, so the reachable set {1, 2, 3} is
        // emitted once each and the recursion terminates on its own within the guards.
        VdbeRecursiveTransform graph = row => row[0].AsInteger() switch
        {
            1 => [[SqlValue.Integer(2)], [SqlValue.Integer(3)]],
            2 => [[SqlValue.Integer(3)]],
            3 => [[SqlValue.Integer(1)]],
            _ => [],
        };

        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.Distinct,
            equality: ByteExactRows,
            maxRows: 100,
            maxDepth: 100,
            graph,
            Row(1));

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void DistinctDeduplicatesSeedsAgainstEachOther()
    {
        // Two identical anchors under distinct: only the first is admitted, so a single row is emitted and
        // the transform (which would run dry immediately) adds nothing.
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.Distinct,
            equality: ByteExactRows,
            maxRows: 100,
            maxDepth: 0,
            _ => [],
            Row(7),
            Row(7));

        Integers(RunToCompletion(program)).Should().Equal(7);
    }

    [Test]
    public void MaxDepthZeroEmitsOnlyTheAnchorGeneration()
    {
        // With depth guard 0 no anchor is ever expanded, so the never-dry Increment transform contributes
        // nothing and only the seeds surface.
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 0,
            Increment,
            Row(5),
            Row(6));

        Integers(RunToCompletion(program)).Should().Equal(5, 6);
    }

    [Test]
    public void MaxDepthBoundsTheRecursionToAFiniteSlice()
    {
        // The never-dry Increment transform relies solely on the depth guard: seed at depth 0 and expand
        // up to depth 3 yields exactly four generations 1, 2, 3, 4.
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 3,
            Increment,
            Row(1));

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void RowGuardOverflowThrowsForARunawayUnionAll()
    {
        // A non-terminating KeepAll recursion with room for only three admitted rows must fail loudly on
        // the fourth admission rather than exhaust memory.
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 3,
            maxDepth: 100,
            Increment,
            Row(1));

        using var statement = new ResumableStatement(program);
        var overflow = Assert.Throws<RecursiveWorkTableOverflowException>(() => Drain(statement));
        overflow!.MaxRows.Should().Be(3);
    }

    [Test]
    public void ResetReplaysTheRecursionFromTheAnchor()
    {
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.Distinct,
            equality: ByteExactRows,
            maxRows: 100,
            maxDepth: 2,
            Increment,
            Row(1));

        using var statement = new ResumableStatement(program);
        Integers(Drain(statement)).Should().Equal(1, 2, 3);

        statement.Reset();

        // If Reset did not clear the frontier and the distinct buffer, the replay would either resume mid
        // recursion or suppress every row as an already-seen duplicate.
        Integers(Drain(statement)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void StepsResumeAcrossExplicitStepResumableCallsPreservingFrontierState()
    {
        // The frontier is instance state, so pausing between rows and resuming must not lose or reorder the
        // pending generations. Drive the state machine one row at a time and confirm the full BFS stream.
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 3,
            Increment,
            Row(1));

        using var statement = new ResumableStatement(program);
        var seen = new List<long>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                seen.Add(statement.CurrentRow![0].AsInteger());
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        seen.Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void SteppingAfterDisposeThrowsObjectDisposedException()
    {
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 10,
            maxDepth: 0,
            _ => [],
            Row(1));

        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    [Test]
    public void ExpandWithoutAPrecedingStepThrowsAtRuntime()
    {
        // Control-flow ordering the validator cannot catch: expanding before any Step means there is no
        // current frontier row to expand from, which must be a hard error rather than a silent no-op.
        var workTable = new WorkTable(0);
        var range = new RegisterRange(new Register(0), 1);
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(workTable, 1, WorkTableDedupMode.KeepAll, 10, 10),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new SeedWorkTableInstruction(workTable, range),
            new WorkTableExpandInstruction(workTable, Increment, range),
            new CloseWorkTableInstruction(workTable),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(1, cursorCount: 0, instructions, workTableCount: 1);
        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => Drain(statement));
    }

    [Test]
    public void ExpandRejectsATransformReturningARowOfTheWrongWidth()
    {
        // The worktable stores 1-column records; a transform that yields a 2-column child violates the
        // record width invariant and must throw rather than corrupt the frontier.
        VdbeRecursiveTransform widen = _ => [[SqlValue.Integer(1), SqlValue.Integer(2)]];

        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 10,
            maxDepth: 5,
            widen,
            Row(1));

        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => Drain(statement));
    }

    [Test]
    public void ExpandRejectsATransformReturningNull()
    {
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 10,
            maxDepth: 5,
            _ => null!,
            Row(1));

        using var statement = new ResumableStatement(program);
        Assert.Throws<InvalidOperationException>(() => Drain(statement));
    }

    [Test]
    public void TwoColumnRecursionCarriesEveryColumnThroughTheFrontier()
    {
        // (value, generation): each row emits its successor with an incremented generation counter, proving
        // the frontier snapshots and re-feeds the whole tuple, not just the first column.
        VdbeRecursiveTransform step = row =>
            [[SqlValue.Integer(row[0].AsInteger() * 10), SqlValue.Integer(row[1].AsInteger() + 1)]];

        var workTable = new WorkTable(0);
        var range = new RegisterRange(new Register(0), 2);
        var doneTarget = new ProgramCounter(8);
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(workTable, 2, WorkTableDedupMode.KeepAll, 100, 2),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(0)),
            new SeedWorkTableInstruction(workTable, range),
            new WorkTableStepInstruction(workTable, range, doneTarget),
            new ResultRowInstruction(range),
            new WorkTableExpandInstruction(workTable, step, range),
            new GotoInstruction(new ProgramCounter(4)),
            new CloseWorkTableInstruction(workTable),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(2, cursorCount: 0, instructions, workTableCount: 1);
        var rows = RunToCompletion(program);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(0));
        rows[1].Should().Equal(SqlValue.Integer(10), SqlValue.Integer(1));
        rows[2].Should().Equal(SqlValue.Integer(100), SqlValue.Integer(2));
    }

    [Test]
    public void ValidationRejectsADistinctWorkTableWithANullEquality()
    {
        Assert.Throws<VdbeProgramValidationException>(() => Recursive(
            width: 1,
            mode: WorkTableDedupMode.Distinct,
            equality: null,
            maxRows: 10,
            maxDepth: 1,
            Increment,
            Row(1)));
    }

    [Test]
    public void ValidationRejectsAKeepAllWorkTableWithANonNullEquality()
    {
        Assert.Throws<VdbeProgramValidationException>(() => Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: ByteExactRows,
            maxRows: 10,
            maxDepth: 1,
            Increment,
            Row(1)));
    }

    [Test]
    public void ValidationRejectsANonPositiveRowGuard()
    {
        Assert.Throws<VdbeProgramValidationException>(() => Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 0,
            maxDepth: 1,
            Increment,
            Row(1)));
    }

    [Test]
    public void ValidationRejectsANegativeDepthGuard()
    {
        Assert.Throws<VdbeProgramValidationException>(() => Recursive(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 10,
            maxDepth: -1,
            Increment,
            Row(1)));
    }

    [Test]
    public void ValidationRejectsANonPositiveColumnCount()
    {
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(new WorkTable(0), 0, WorkTableDedupMode.KeepAll, 10, 1),
            new CloseWorkTableInstruction(new WorkTable(0)),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, workTableCount: 1));
    }

    [Test]
    public void ValidationRejectsANullTransform()
    {
        var workTable = new WorkTable(0);
        var range = new RegisterRange(new Register(0), 1);
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(workTable, 1, WorkTableDedupMode.KeepAll, 10, 1),
            new WorkTableStepInstruction(workTable, range, new ProgramCounter(3)),
            new WorkTableExpandInstruction(workTable, null!, range),
            new CloseWorkTableInstruction(workTable),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, workTableCount: 1));
    }

    [Test]
    public void ValidationRejectsASeedRowOfTheWrongWidth()
    {
        var workTable = new WorkTable(0);
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(workTable, 1, WorkTableDedupMode.KeepAll, 10, 1),
            new SeedWorkTableInstruction(workTable, new RegisterRange(new Register(0), 2)),
            new CloseWorkTableInstruction(workTable),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(2, cursorCount: 0, instructions, workTableCount: 1));
    }

    [Test]
    public void ValidationRejectsUsingAWorkTableBeforeOpeningIt()
    {
        var workTable = new WorkTable(0);
        VdbeInstruction[] instructions =
        [
            new SeedWorkTableInstruction(workTable, new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, workTableCount: 1));
    }

    [Test]
    public void ValidationRejectsOpeningAWorkTableTwice()
    {
        var workTable = new WorkTable(0);
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(workTable, 1, WorkTableDedupMode.KeepAll, 10, 1),
            new OpenWorkTableInstruction(workTable, 1, WorkTableDedupMode.KeepAll, 10, 1),
            new CloseWorkTableInstruction(workTable),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, workTableCount: 1));
    }

    [Test]
    public void ValidationRejectsAWorkTableIndexBeyondTheDeclaredCount()
    {
        VdbeInstruction[] instructions =
        [
            new OpenWorkTableInstruction(new WorkTable(1), 1, WorkTableDedupMode.KeepAll, 10, 1),
            new CloseWorkTableInstruction(new WorkTable(1)),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, workTableCount: 1));
    }

    [Test]
    public void ConstructionRejectsANegativeWorkTableCount()
    {
        VdbeInstruction[] instructions = [new HaltInstruction()];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VdbeProgram(0, cursorCount: 0, instructions, workTableCount: -1));
    }

    [Test]
    public void DistinctWorkTableHonorsTheRetainedMemoryBudget()
    {
        var seeds = Enumerable.Range(0, 64).Select(static value => Row(value)).ToArray();
        var program = Recursive(
            width: 1,
            mode: WorkTableDedupMode.Distinct,
            equality: ByteExactRows,
            maxRows: 1000,
            maxDepth: 0,
            Increment,
            seeds);
        var options = new VdbeExecutionOptions(
            new Ahtola.Core.Storage.InMemoryFileSystem(),
            sorterMemoryLimitBytes: 64,
            allowTemporaryFileSpill: false);
        using var statement = ResumableStatement.CreateWithExecutionOptions(program, options);
        Assert.Throws<VdbeMemoryLimitExceededException>(() => Drain(statement));
    }

    [Test]
    public void ExplainDescribesOpenWorkTableWithItsShapeGuardsAndMode()
    {
        var instruction = new OpenWorkTableInstruction(
            new WorkTable(0), 2, WorkTableDedupMode.Distinct, 500, 8, ByteExactRows);

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(0);
        p2.Should().Be(500);
        p3.Should().Be(8);
        p4.Should().Be("distinct");
        comment.Should().Be("open work table 0 (2 cols, distinct, <=500 rows, depth<=8)");
    }

    [Test]
    public void ExplainDescribesTheDrainLoopOpcodes()
    {
        var range = new RegisterRange(new Register(0), 1);

        var seed = VdbeExplain.Describe(new SeedWorkTableInstruction(new WorkTable(0), range));
        seed.Comment.Should().Be("seed work table 0 with r[0]");

        var step = VdbeExplain.Describe(
            new WorkTableStepInstruction(new WorkTable(0), range, new ProgramCounter(9)));
        step.P2.Should().Be(9);
        step.Comment.Should().Be("r[0]=work table 0 next, goto 9 if drained");

        var expand = VdbeExplain.Describe(
            new WorkTableExpandInstruction(new WorkTable(0), Increment, range));
        expand.Comment.Should().Be("expand work table 0 from r[0]");

        var close = VdbeExplain.Describe(new CloseWorkTableInstruction(new WorkTable(0)));
        close.Comment.Should().Be("close work table 0");
    }

    // Assembles the canonical recursive program shape directly from opcodes:
    //   OpenWorkTable; (LoadConstant* SeedWorkTable)*; loop: WorkTableStep->done, ResultRow, WorkTableExpand,
    //   Goto loop; done: CloseWorkTable; Halt
    // so each test exercises the interpreter's real fixpoint loop rather than a builder abstraction.
    private static VdbeProgram Recursive(
        int width,
        WorkTableDedupMode mode,
        VdbeRowEquality? equality,
        int maxRows,
        int maxDepth,
        VdbeRecursiveTransform transform,
        params long[][] seedRows)
    {
        var workTable = new WorkTable(0);
        var range = new RegisterRange(new Register(0), width);
        var instructions = new List<VdbeInstruction>
        {
            new OpenWorkTableInstruction(workTable, width, mode, maxRows, maxDepth, equality),
        };

        foreach (var seed in seedRows)
        {
            for (var column = 0; column < width; column++)
                instructions.Add(new LoadConstantInstruction(new Register(column), SqlValue.Integer(seed[column])));

            instructions.Add(new SeedWorkTableInstruction(workTable, range));
        }

        var loopTop = instructions.Count;
        var doneTarget = new ProgramCounter(loopTop + 4);
        instructions.Add(new WorkTableStepInstruction(workTable, range, doneTarget));
        instructions.Add(new ResultRowInstruction(range));
        instructions.Add(new WorkTableExpandInstruction(workTable, transform, range));
        instructions.Add(new GotoInstruction(new ProgramCounter(loopTop)));
        instructions.Add(new CloseWorkTableInstruction(workTable));
        instructions.Add(new HaltInstruction());

        return new VdbeProgram(width, cursorCount: 0, instructions, workTableCount: 1);
    }

    private static long[] Row(params long[] values) => values;

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

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
                rows.Add([.. statement.CurrentRow!]);
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
