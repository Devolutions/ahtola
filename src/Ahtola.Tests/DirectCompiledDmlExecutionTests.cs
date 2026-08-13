using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Direct coverage for the compiled INSERT/UPDATE/DELETE slice: DmlStatementCompiler emits the
// program, hand-built VdbeWriteTargets supply the mutation/commit semantics, and ResumableStatement
// drives the bytecode. Unlike CompiledDmlExecutionTests (which routes real SQL through the embedded
// connection), these tests exercise the compiler/executor contract in isolation — with no SQL parsing,
// no EmbeddedDatabase, and no evaluator fallback — so the RETURNING projection, row-snapshot timing,
// rows-affected / last-insert-rowid bookkeeping, empty-mutation short-circuit, atomicity delegation,
// reset/dispose lifecycle, and program validation are pinned as first-class VDBE behaviour.
public class DirectCompiledDmlExecutionTests
{
    // ---- EXPLAIN / program-shape contracts --------------------------------------------------------

    [Test]
    public void CompilesInsertWithoutReturningToTheFixedLayout()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returningOps: [], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "Commit", "CloseCursor", "Halt");

        // Rewind jumps past the loop straight to Commit (addr 4) when there is nothing to mutate.
        Describe(program, 1).P2.Should().Be(4);

        // Next loops back to the mutation opcode (addr 2).
        Describe(program, 3).P2.Should().Be(2);
    }

    [Test]
    public void CompilesInsertReturningColumnsWithProjectionRegisters()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 2, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForColumn(1)],
            writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "Column", "ResultRow", "Next",
            "Commit", "CloseCursor", "Halt");

        Describe(program, 3).Comment.Should().Be("r[0]=c0.col[0]");
        Describe(program, 4).Comment.Should().Be("r[1]=c0.col[1]");
        Describe(program, 5).Comment.Should().Be("output=r[0..1]");
        program.RegisterCount.Should().Be(2);
    }

    [Test]
    public void CompilesInsertReturningRowIdThroughADedicatedOpcode()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returningOps: [DmlProjectionOp.ForRowId()], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "RowId", "ResultRow", "Next",
            "Commit", "CloseCursor", "Halt");
        Describe(program, 3).Comment.Should().Be("r[0]=c0.rowid");
    }

    [Test]
    public void CompilesInsertReturningConstantThroughLoadConstant()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForConstant(SqlValue.Text("k"))],
            writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "LoadConstant", "ResultRow", "Next",
            "Commit", "CloseCursor", "Halt");
        Describe(program, 4).Comment.Should().Be("r[1]='k'");
    }

    [Test]
    public void CompilesUpdateWithFilterAheadOfTheMutation()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Update, "t", columnCount: 1, predicate: _ => true,
            returningOps: [], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Filter", "Update", "Next", "Commit", "CloseCursor", "Halt");

        // Filter jumps to Next (addr 4) when the predicate is false; Next loops back to the Filter (addr 2).
        Describe(program, 2).P2.Should().Be(4);
        Describe(program, 4).P2.Should().Be(2);
    }

    [Test]
    public void CompilesDeleteAllWithoutAFilter()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Delete, "t", columnCount: 1, predicate: null,
            returningOps: [], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Delete", "Next", "Commit", "CloseCursor", "Halt");
    }

    [Test]
    public void CompilesDeleteReturningWildcardWithFilter()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Delete, "t", columnCount: 2, predicate: _ => true,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForColumn(1)],
            writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Filter", "Delete", "Column", "Column", "ResultRow",
            "Next", "Commit", "CloseCursor", "Halt");
    }

    // ---- INSERT execution -------------------------------------------------------------------------

    [Test]
    public void InsertReturningColumnsEmitsWrittenRowsInProjectionOrder()
    {
        var table = new TestTable("t", columnCount: 2);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            // RETURNING name, id => columns projected out of source order.
            returningOps: [DmlProjectionOp.ForColumn(1), DmlProjectionOp.ForColumn(0)],
            writeTarget: InsertTarget(table, Row(1, "ada"), Row(2, "grace")));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Text("grace"), SqlValue.Integer(2));
        affected.Should().Be(2);
        lastId.Should().Be(2);
        AssertTable(table, (1, Row(1, "ada")), (2, Row(2, "grace")));
    }

    [Test]
    public void InsertReturningRowIdObservesTheAllocatedRowid()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForRowId(), DmlProjectionOp.ForColumn(0)],
            writeTarget: InsertTarget(table, Row("x"), Row("y")));

        var (rows, _, lastId) = RunDml(compiled);

        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("y"));
        lastId.Should().Be(2);
    }

    [Test]
    public void InsertReturningConstantEmitsTheFoldedValueEachRow()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForConstant(SqlValue.Integer(99))],
            writeTarget: InsertTarget(table, Row("a"), Row("b")));

        var (rows, _, _) = RunDml(compiled);

        rows[0].Should().Equal(SqlValue.Text("a"), SqlValue.Integer(99));
        rows[1].Should().Equal(SqlValue.Text("b"), SqlValue.Integer(99));
    }

    [Test]
    public void InsertReturningWildcardEmitsEveryColumnInOrder()
    {
        var table = new TestTable("t", columnCount: 3);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForColumn(1), DmlProjectionOp.ForColumn(2)],
            writeTarget: InsertTarget(table, Row(1, "a", "x")));

        var (rows, _, _) = RunDml(compiled);

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"), SqlValue.Text("x"));
    }

    [Test]
    public void InsertWithoutReturningEmitsNoRowsButStillCommits()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [], writeTarget: InsertTarget(table, Row(1), Row(2)));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().BeEmpty();
        affected.Should().Be(2);
        lastId.Should().Be(2);
        Opcodes(compiled.Program).Should().NotContain("ResultRow");
        AssertTable(table, (1, Row(1)), (2, Row(2)));
    }

    [Test]
    public void RegisterReuseAcrossRowsDoesNotCorruptEarlierReturningRows()
    {
        // Every row writes r[0], but ResultRow snapshots the register block, so drained rows must retain
        // their own value rather than the final register contents.
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0)],
            writeTarget: InsertTarget(table, Row(10), Row(20), Row(30)));

        var (rows, _, _) = RunDml(compiled);

        rows.Select(row => row[0].AsInteger()).Should().Equal(10, 20, 30);
    }

    // ---- UPDATE execution -------------------------------------------------------------------------

    [Test]
    public void UpdateReturningReadsThePostMutationRowNotTheSource()
    {
        var table = new TestTable("t", columnCount: 2);
        table.Seed(1, Row(1, 10));
        table.Seed(2, Row(2, 20));
        table.Seed(3, Row(3, 30));

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Update, table.Name, table.ColumnCount,
            predicate: row => row[0].AsInteger() >= 2,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForColumn(1)],
            // The written row differs from the source row, so RETURNING must observe the mutation.
            writeTarget: UpdateTarget(table, (row, rowid) => (Row(row[0].AsInteger(), row[1].AsInteger() + 100), rowid)));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(120));
        rows[1].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(130));
        affected.Should().Be(2);
        lastId.Should().BeNull();
        AssertTable(table, (1, Row(1, 10)), (2, Row(2, 120)), (3, Row(3, 130)));
    }

    [Test]
    public void UpdateWithoutReturningAppliesThePredicateAndReportsAffected()
    {
        var table = new TestTable("t", columnCount: 2);
        table.Seed(1, Row(1, 10));
        table.Seed(2, Row(2, 20));
        table.Seed(3, Row(3, 30));

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Update, table.Name, table.ColumnCount,
            predicate: row => row[1].AsInteger() > 15,
            returningOps: [],
            writeTarget: UpdateTarget(table, (row, rowid) => (Row(row[0].AsInteger(), 0), rowid)));

        var (rows, affected, _) = RunDml(compiled);

        rows.Should().BeEmpty();
        affected.Should().Be(2);
        AssertTable(table, (1, Row(1, 10)), (2, Row(2, 0)), (3, Row(3, 0)));
    }

    // ---- DELETE execution -------------------------------------------------------------------------

    [Test]
    public void DeleteReturningReadsThePreMutationRows()
    {
        var table = new TestTable("t", columnCount: 2);
        table.Seed(1, Row(1, 10));
        table.Seed(2, Row(2, 20));
        table.Seed(3, Row(3, 30));

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Delete, table.Name, table.ColumnCount,
            predicate: row => row[1].AsInteger() >= 20,
            returningOps: [DmlProjectionOp.ForColumn(0), DmlProjectionOp.ForColumn(1)],
            writeTarget: DeleteTarget(table));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(20));
        rows[1].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(30));
        affected.Should().Be(2);
        lastId.Should().BeNull();
        AssertTable(table, (1, Row(1, 10)));
    }

    [Test]
    public void DeleteWithoutPredicateRemovesEveryRow()
    {
        var table = new TestTable("t", columnCount: 1);
        table.Seed(1, Row(1));
        table.Seed(2, Row(2));
        table.Seed(3, Row(3));

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Delete, table.Name, table.ColumnCount, predicate: null,
            returningOps: [], writeTarget: DeleteTarget(table));

        var (_, affected, _) = RunDml(compiled);

        affected.Should().Be(3);
        AssertTable(table);
    }

    // ---- Empty mutations --------------------------------------------------------------------------

    [Test]
    public void EmptyInsertShortCircuitsToCommitWithoutEmittingRows()
    {
        var table = new TestTable("t", columnCount: 1);
        var committed = false;
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0)],
            writeTarget: InsertTarget(table, onCommit: () => committed = true));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().BeEmpty();
        affected.Should().Be(0);
        lastId.Should().BeNull();
        committed.Should().BeTrue("Rewind still falls through to Commit even with nothing to mutate");
        AssertTable(table);
    }

    [Test]
    public void DeleteOnEmptyTableEmitsNothing()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Delete, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0)], writeTarget: DeleteTarget(table));

        var (rows, affected, _) = RunDml(compiled);

        rows.Should().BeEmpty();
        affected.Should().Be(0);
    }

    [Test]
    public void UpdateThatFiltersOutEveryRowEmitsNothingButCommits()
    {
        var table = new TestTable("t", columnCount: 2);
        table.Seed(1, Row(1, 10));
        table.Seed(2, Row(2, 20));
        var committed = false;

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Update, table.Name, table.ColumnCount,
            predicate: _ => false,
            returningOps: [DmlProjectionOp.ForColumn(0)],
            writeTarget: UpdateTarget(table, (row, rowid) => (row, rowid), onCommit: () => committed = true));

        var (rows, affected, _) = RunDml(compiled);

        rows.Should().BeEmpty();
        affected.Should().Be(0);
        committed.Should().BeTrue();
        AssertTable(table, (1, Row(1, 10)), (2, Row(2, 20)));
    }

    // ---- Atomicity: failure delegated to the write target -----------------------------------------

    [Test]
    public void FailedMutationPropagatesAndNeverReachesCommit()
    {
        var table = new TestTable("t", columnCount: 1);
        var committed = false;
        var target = new VdbeWriteTarget
        {
            TableName = table.Name,
            RowCount = 2,
            MutateRow = index => index == 0
                ? new VdbeRowMutation(Row(1), 1)
                : throw new InvalidOperationException("boom"),
            Commit = () => { committed = true; return null; },
        };
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0)], writeTarget: target);

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());

        // The mutation opcode increments rows-affected only after MutateRow returns, so the failed row is
        // not counted, and the loop never reaches Commit — the store stays untouched.
        statement.RowsAffected.Should().Be(1);
        committed.Should().BeFalse();
        AssertTable(table);
    }

    [Test]
    public void FailedCommitDiscardsBufferedReturningRowsAndLeavesTheTableUntouched()
    {
        var table = new TestTable("t", columnCount: 1);
        // Commit enforces the (delegated) constraint and throws, applying nothing.
        var target = new VdbeWriteTarget
        {
            TableName = table.Name,
            RowCount = 2,
            MutateRow = index => new VdbeRowMutation(Row(index + 1), index + 1),
            Commit = () => throw new InvalidOperationException("constraint violation"),
        };
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(0)], writeTarget: target);

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        // Both RETURNING rows are produced inside the loop, before Commit runs.
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        // Commit raises the failure, so a caller must discard the buffered rows: nothing is persisted.
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        statement.RowsAffected.Should().Be(2);
        AssertTable(table);
    }

    [Test]
    public void FailedCommitFaultsTheStatementUntilReset()
    {
        var commitAttempts = 0;
        var target = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 0,
            MutateRow = _ => throw new AssertionException("No rows should be mutated."),
            Commit = () =>
            {
                commitAttempts++;
                throw new InvalidOperationException("commit failed after applying its side effect");
            },
        };
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null, returningOps: [], writeTarget: target);
        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        statement.State.Should().Be(ResumableStatementState.Faulted);
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable())!
            .Message.Should().Contain("Call Reset");
        commitAttempts.Should().Be(1);

        statement.Reset();
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        commitAttempts.Should().Be(2);
    }

    [Test]
    public void MutationOpcodeWithoutABoundActionThrows()
    {
        // A write target missing its MutateRow delegate is a hard executor error, not a silent no-op.
        var target = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            Commit = () => null,
        };
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returningOps: [], writeTarget: target);

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        Assert.Throws<InvalidOperationException>(() => Drain(statement));
    }

    // ---- Lifecycle: reset / dispose ---------------------------------------------------------------

    [Test]
    public void ResetReplaysTheProgramAndReRunsTheMutations()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [DmlProjectionOp.ForRowId()], writeTarget: InsertTarget(table, Row("a")));

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        var first = Drain(statement);
        first.Should().ContainSingle();
        first[0][0].Should().Be(SqlValue.Integer(1));
        statement.RowsAffected.Should().Be(1);
        statement.LastInsertRowId.Should().Be(1);

        statement.Reset();
        statement.RowsAffected.Should().Be(0);

        var second = Drain(statement);
        second[0][0].Should().Be(SqlValue.Integer(2));
        statement.RowsAffected.Should().Be(1);
        statement.LastInsertRowId.Should().Be(2);
        AssertTable(table, (1, Row("a")), (2, Row("a")));
    }

    [Test]
    public void DisposePreventsFurtherStepping()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returningOps: [], writeTarget: InsertTarget(table, Row(1)));
        var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        statement.Dispose();
        statement.State.Should().Be(ResumableStatementState.Disposed);
        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    // ---- Compiler / bytecode validation -----------------------------------------------------------

    [Test]
    public void CompileRejectsAnInsertThatCarriesAPredicate()
    {
        Assert.Throws<StatementCompilationException>(() => DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: _ => true,
            returningOps: [], writeTarget: NullTarget("t", 0)));
    }

    [Test]
    public void CompileRejectsAReturningColumnOutsideTheCursorColumns()
    {
        // The emitted Column opcode is validated against the write cursor's column count at program
        // construction, so an out-of-range RETURNING projection is caught as invalid bytecode.
        Assert.Throws<VdbeProgramValidationException>(() => DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(3)], writeTarget: NullTarget("t", 0)));
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static SqlValue[] Row(params object[] values)
    {
        var row = new SqlValue[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            row[index] = values[index] switch
            {
                int i => SqlValue.Integer(i),
                long l => SqlValue.Integer(l),
                string s => SqlValue.Text(s),
                SqlValue v => v,
                _ => throw new ArgumentException($"Unsupported cell {values[index]}"),
            };
        }

        return row;
    }

    private static IEnumerable<string> Opcodes(VdbeProgram program)
        => program.Instructions.Select(instruction => instruction.Opcode.ToString());

    private static (long P1, long P2, long P3, string? P4, string Comment) Describe(VdbeProgram program, int address)
        => VdbeExplain.Describe(program.Instructions[address]);

    private static (List<SqlValue[]> Rows, int Affected, long? LastInsertRowId) RunDml(CompiledDml compiled)
    {
        using var statement = new ResumableStatement(
            compiled.Program, cursorSources: null, writeTargets: compiled.WriteTargets);
        var rows = Drain(statement);
        return (rows, statement.RowsAffected, statement.LastInsertRowId);
    }

    private static List<SqlValue[]> Drain(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            switch (statement.StepResumable())
            {
                case ResumableStatementStepResult.Row:
                    rows.Add([.. statement.CurrentRow!]);
                    break;
                case ResumableStatementStepResult.Done:
                    return rows;
                default:
                    throw new InvalidOperationException("A DML program must never yield.");
            }
        }
    }

    private static void AssertTable(TestTable table, params (long RowId, SqlValue[] Row)[] expected)
    {
        table.RowCount.Should().Be(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            table.RowIds[index].Should().Be(expected[index].RowId);
            var actualRow = table.Rows[index];
            var expectedRow = expected[index].Row;
            actualRow.Length.Should().Be(expectedRow.Length);
            for (var column = 0; column < expectedRow.Length; column++)
                actualRow[column].Should().Be(expectedRow[column]);
        }
    }

    private static VdbeWriteTarget NullTarget(string name, int rowCount) => new()
    {
        TableName = name,
        RowCount = rowCount,
        Commit = () => null,
    };

    private static VdbeWriteTarget InsertTarget(TestTable table, params SqlValue[][] rowsToInsert)
        => InsertTarget(table, onCommit: null, rowsToInsert);

    private static VdbeWriteTarget InsertTarget(TestTable table, Action? onCommit, params SqlValue[][] rowsToInsert)
    {
        var pending = new List<(SqlValue[] Row, long RowId)>();
        return new VdbeWriteTarget
        {
            TableName = table.Name,
            RowCount = rowsToInsert.Length,
            MutateRow = index =>
            {
                var row = rowsToInsert[index];
                var rowid = table.RowCount + pending.Count + 1;
                pending.Add((row, rowid));
                return new VdbeRowMutation(row, rowid);
            },
            Commit = () =>
            {
                foreach (var (row, rowid) in pending)
                    table.Seed(rowid, row);
                var last = pending.Count > 0 ? pending[^1].RowId : (long?)null;
                pending.Clear();
                onCommit?.Invoke();
                return last;
            },
        };
    }

    private static VdbeWriteTarget UpdateTarget(
        TestTable table,
        Func<SqlValue[], long, (SqlValue[] Row, long RowId)> update,
        Action? onCommit = null)
    {
        var newRows = new List<SqlValue[]>(table.Rows);
        var newIds = new List<long>(table.RowIds);
        return new VdbeWriteTarget
        {
            TableName = table.Name,
            RowCount = table.RowCount,
            GetRow = index => table.Rows[index],
            GetRowId = index => table.RowIds[index],
            MutateRow = index =>
            {
                var (row, rowid) = update(table.Rows[index], table.RowIds[index]);
                newRows[index] = row;
                newIds[index] = rowid;
                return new VdbeRowMutation(row, rowid);
            },
            Commit = () =>
            {
                table.ReplaceAll(newRows, newIds);
                onCommit?.Invoke();
                return null;
            },
        };
    }

    private static VdbeWriteTarget DeleteTarget(TestTable table)
    {
        var deleted = new bool[table.RowCount];
        return new VdbeWriteTarget
        {
            TableName = table.Name,
            RowCount = table.RowCount,
            GetRow = index => table.Rows[index],
            GetRowId = index => table.RowIds[index],
            DeleteRow = index => deleted[index] = true,
            Commit = () =>
            {
                var keptRows = new List<SqlValue[]>();
                var keptIds = new List<long>();
                for (var index = 0; index < table.RowCount; index++)
                {
                    if (deleted[index])
                        continue;
                    keptRows.Add(table.Rows[index]);
                    keptIds.Add(table.RowIds[index]);
                }

                table.ReplaceAll(keptRows, keptIds);
                return null;
            },
        };
    }

    // A minimal rowid-addressed table backing the hand-built write targets. Mutations are buffered by
    // the targets and applied here only through Commit, so atomicity stays owned by the write target
    // rather than the compiler or the interpreter.
    private sealed class TestTable
    {
        private readonly List<SqlValue[]> _rows = [];
        private readonly List<long> _rowIds = [];

        public TestTable(string name, int columnCount)
        {
            Name = name;
            ColumnCount = columnCount;
        }

        public string Name { get; }

        public int ColumnCount { get; }

        public IReadOnlyList<SqlValue[]> Rows => _rows;

        public IReadOnlyList<long> RowIds => _rowIds;

        public int RowCount => _rows.Count;

        public void Seed(long rowId, SqlValue[] values)
        {
            _rows.Add(values);
            _rowIds.Add(rowId);
        }

        public void ReplaceAll(IReadOnlyList<SqlValue[]> rows, IReadOnlyList<long> rowIds)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            _rowIds.Clear();
            _rowIds.AddRange(rowIds);
        }
    }
}
