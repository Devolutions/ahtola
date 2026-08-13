using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Direct coverage for the compiled DML RETURNING *expression* slice: DmlStatementCompiler lowers a
// DmlReturningExpression tree (bare column/rowid/constant leaves plus the arithmetic family) into real
// Column/RowId/LoadConstant/Arithmetic/ResultRow bytecode, hand-built VdbeWriteTargets supply the
// mutation/commit semantics, and ResumableStatement drives the program. These tests exercise the
// compiler/executor contract in isolation — no SQL parsing, no EmbeddedDatabase, no evaluator fallback —
// so the post-mutation / pre-delete snapshot timing of arithmetic RETURNING, its NULL/type/overflow/
// by-zero value semantics, error-before-commit atomicity, result buffering across rows, program/EXPLAIN
// shape, descriptor validation, invalid-bytecode rejection, composability/nesting, and reset lifecycle are
// pinned as first-class VDBE behaviour. Unlike the bare-projection DirectCompiledDmlExecutionTests, every
// scenario here uses at least one arithmetic RETURNING item.
public class DirectCompiledDmlReturningExpressionTests
{
    // ---- EXPLAIN / program-shape contracts --------------------------------------------------------

    [Test]
    public void CompilesInsertReturningColumnPlusConstantThroughRealBytecode()
    {
        // RETURNING id + 1: one arithmetic projection over a column and a folded constant.
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returning: [Add(Col(0), Const(1))], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "LoadConstant", "Arithmetic", "ResultRow",
            "Next", "Commit", "CloseCursor", "Halt");

        // Output register r[0]; the two operands materialize into scratch registers r[1], r[2].
        Describe(program, 3).Comment.Should().Be("r[1]=c0.col[0]");
        Describe(program, 4).Comment.Should().Be("r[2]=1");
        Describe(program, 5).Comment.Should().Be("r[0]=r[1] + r[2]");
        Describe(program, 6).Comment.Should().Be("output=r[0]");
        program.RegisterCount.Should().Be(3);
    }

    [Test]
    public void CompilesNestedArithmeticProjectionWithScratchRegisterReuse()
    {
        // RETURNING (a + b) * c: nested arithmetic whose inner fold uses scratch registers above the outer
        // operand block, then frees them so the outer operator can consume the two results.
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 3, predicate: null,
            returning: [Mul(Add(Col(0), Col(1)), Col(2))], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "Column", "Arithmetic", "Column",
            "Arithmetic", "ResultRow", "Next", "Commit", "CloseCursor", "Halt");

        Describe(program, 3).Comment.Should().Be("r[3]=c0.col[0]");
        Describe(program, 4).Comment.Should().Be("r[4]=c0.col[1]");
        Describe(program, 5).Comment.Should().Be("r[1]=r[3] + r[4]");
        Describe(program, 6).Comment.Should().Be("r[2]=c0.col[2]");
        Describe(program, 7).Comment.Should().Be("r[0]=r[1] * r[2]");
        Describe(program, 8).Comment.Should().Be("output=r[0]");
        program.RegisterCount.Should().Be(5);
    }

    [Test]
    public void CompilesUnaryNegationReturningProjection()
    {
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returning: [Negate(Col(0))], writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "Arithmetic", "ResultRow",
            "Next", "Commit", "CloseCursor", "Halt");
        Describe(program, 4).Comment.Should().Be("r[0]=-r[1]");
        program.RegisterCount.Should().Be(2);
    }

    // ---- INSERT execution -------------------------------------------------------------------------

    [Test]
    public void InsertReturningArithmeticObservesTheWrittenRow()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Col(0), Add(Col(0), Const(100))],
            writeTarget: InsertTarget(table, Row(1), Row(2)));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(101));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(102));
        affected.Should().Be(2);
        lastId.Should().Be(2);
        AssertTable(table, (1, Row(1)), (2, Row(2)));
    }

    [Test]
    public void InsertReturningRowIdArithmeticObservesTheAllocatedRowid()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Add(RowIdExpr(), Const(1000))],
            writeTarget: InsertTarget(table, Row("x"), Row("y")));

        var (rows, _, _) = RunDml(compiled);

        rows[0].Should().Equal(SqlValue.Integer(1001));
        rows[1].Should().Equal(SqlValue.Integer(1002));
    }

    [Test]
    public void InsertReturningNestedArithmeticComputesTheWholeTree()
    {
        var table = new TestTable("t", columnCount: 3);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Mul(Add(Col(0), Col(1)), Col(2))],
            writeTarget: InsertTarget(table, Row(2, 3, 4)));

        var (rows, _, _) = RunDml(compiled);

        // (2 + 3) * 4 = 20
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(20));
    }

    [Test]
    public void ResultRowSnapshotsArithmeticResultPerRow()
    {
        // Every row reuses the same registers, but ResultRow snapshots the output block, so drained rows
        // keep their own computed value rather than the final register contents.
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Mul(Col(0), Const(2))],
            writeTarget: InsertTarget(table, Row(10), Row(20), Row(30)));

        var (rows, _, _) = RunDml(compiled);

        rows.Select(row => row[0].AsInteger()).Should().Equal(20, 40, 60);
    }

    // ---- UPDATE execution -------------------------------------------------------------------------

    [Test]
    public void UpdateReturningArithmeticReadsThePostMutationRow()
    {
        var table = new TestTable("t", columnCount: 2);
        table.Seed(1, Row(1, 10));
        table.Seed(2, Row(2, 20));
        table.Seed(3, Row(3, 30));

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Update, table.Name, table.ColumnCount,
            predicate: row => row[0].AsInteger() >= 2,
            // RETURNING col1, col1 * 2 — over the *written* value (source col1 + 100).
            returning: [Col(1), Mul(Col(1), Const(2))],
            writeTarget: UpdateTarget(table, (row, rowid) => (Row(row[0].AsInteger(), row[1].AsInteger() + 100), rowid)));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(120), SqlValue.Integer(240));
        rows[1].Should().Equal(SqlValue.Integer(130), SqlValue.Integer(260));
        affected.Should().Be(2);
        lastId.Should().BeNull();
        AssertTable(table, (1, Row(1, 10)), (2, Row(2, 120)), (3, Row(3, 130)));
    }

    // ---- DELETE execution -------------------------------------------------------------------------

    [Test]
    public void DeleteReturningArithmeticReadsThePreDeleteRow()
    {
        var table = new TestTable("t", columnCount: 2);
        table.Seed(1, Row(1, 10));
        table.Seed(2, Row(2, 20));
        table.Seed(3, Row(3, 30));

        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Delete, table.Name, table.ColumnCount,
            predicate: row => row[1].AsInteger() >= 20,
            // RETURNING col0 + col1, rowid + 0 — reads the row about to be deleted.
            returning: [Add(Col(0), Col(1)), Add(RowIdExpr(), Const(0))],
            writeTarget: DeleteTarget(table));

        var (rows, affected, lastId) = RunDml(compiled);

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(22), SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Integer(33), SqlValue.Integer(3));
        affected.Should().Be(2);
        lastId.Should().BeNull();
        AssertTable(table, (1, Row(1, 10)));
    }

    // ---- Value semantics: NULL / by-zero / overflow -----------------------------------------------

    [Test]
    public void ReturningArithmeticPropagatesNull()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Add(Col(0), Const(SqlValue.Null))],
            writeTarget: InsertTarget(table, Row(5)));

        var (rows, _, _) = RunDml(compiled);

        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ReturningArithmeticDivideByZeroYieldsNull()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Divide(Col(0), Const(0))],
            writeTarget: InsertTarget(table, Row(42)));

        var (rows, _, _) = RunDml(compiled);

        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ReturningArithmeticIntegerOverflowPromotesToReal()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Mul(Col(0), Const(2))],
            writeTarget: InsertTarget(table, Row(SqlValue.Integer(long.MaxValue))));

        var (rows, _, _) = RunDml(compiled);

        rows[0][0].Kind.Should().Be(SqlValueKind.Real);
        rows[0][0].AsReal().Should().Be((double)long.MaxValue * 2);
    }

    // ---- Errors: arithmetic type error propagates before Commit -----------------------------------

    [Test]
    public void ReturningArithmeticTypeErrorPropagatesAndNeverReachesCommit()
    {
        var table = new TestTable("t", columnCount: 1);
        var committed = false;
        // A text column fed to '+' is a hard arithmetic type error; the family applies no affinity.
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Add(Col(0), Const(1))],
            writeTarget: InsertTarget(table, onCommit: () => committed = true, Row("not-a-number")));

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        // The mutation runs, then the arithmetic fold throws before ResultRow / Commit are reached.
        Assert.Throws<VdbeArithmeticException>(() => statement.StepResumable());
        statement.RowsAffected.Should().Be(1);
        committed.Should().BeFalse("the arithmetic error aborts the statement before Commit");
        AssertTable(table);
    }

    [Test]
    public void FailedCommitDiscardsBufferedArithmeticRowsAndLeavesTheTableUntouched()
    {
        var table = new TestTable("t", columnCount: 1);
        var target = new VdbeWriteTarget
        {
            TableName = table.Name,
            RowCount = 2,
            MutateRow = index => new VdbeRowMutation(Row(index + 1), index + 1),
            Commit = () => throw new InvalidOperationException("constraint violation"),
        };
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Add(Col(0), Const(10))], writeTarget: target);

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        // Both arithmetic RETURNING rows are produced inside the loop, before Commit runs.
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow!.Single().Should().Be(SqlValue.Integer(11));
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow!.Single().Should().Be(SqlValue.Integer(12));
        // Commit raises: a caller must discard the buffered rows and nothing is persisted.
        Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        statement.RowsAffected.Should().Be(2);
        AssertTable(table);
    }

    // ---- Descriptor validation --------------------------------------------------------------------

    [Test]
    public void ArithmeticDescriptorRejectsAnOperandCountThatDisagreesWithTheOperatorArity()
    {
        // Binary '+' with one operand is malformed and cannot even be constructed.
        Assert.Throws<StatementCompilationException>(() =>
            DmlReturningExpression.Arithmetic(ArithmeticOperator.Add, Col(0)));

        // Unary negation with two operands is likewise malformed.
        Assert.Throws<StatementCompilationException>(() =>
            DmlReturningExpression.Arithmetic(ArithmeticOperator.Negate, Col(0), Col(0)));
    }

    [Test]
    public void ArithmeticDescriptorRejectsANullOperand()
    {
        Assert.Throws<StatementCompilationException>(() =>
            DmlReturningExpression.Arithmetic(ArithmeticOperator.Add, Col(0), null!));
    }

    [Test]
    public void ArithmeticDescriptorRejectsAnUndefinedOperator()
    {
        Assert.Throws<StatementCompilationException>(() =>
            DmlReturningExpression.Arithmetic((ArithmeticOperator)999, Col(0), Col(0)));
    }

    [Test]
    public void ColumnDescriptorRejectsANegativeColumnIndex()
    {
        Assert.Throws<StatementCompilationException>(() => DmlReturningExpression.Column(-1));
    }

    // ---- Invalid bytecode -------------------------------------------------------------------------

    [Test]
    public void CompileRejectsAnArithmeticColumnOutsideTheCursorColumns()
    {
        // The lowered Column opcode is validated against the write cursor's column count at program
        // construction, so an out-of-range operand is caught as invalid bytecode.
        Assert.Throws<VdbeProgramValidationException>(() => DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 1, predicate: null,
            returning: [Add(Col(3), Const(1))], writeTarget: NullTarget("t", 0)));
    }

    // ---- Composability ----------------------------------------------------------------------------

    [Test]
    public void ReturningMixesBareAndArithmeticProjectionsInDeclaredOrder()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            // RETURNING col0, col0 + 1, rowid, 'k' — a bare column, arithmetic, rowid, and constant mixed.
            returning: [Col(0), Add(Col(0), Const(1)), RowIdExpr(), Const(SqlValue.Text("k"))],
            writeTarget: InsertTarget(table, Row(5)));

        var (rows, _, _) = RunDml(compiled);

        rows.Should().ContainSingle();
        rows[0].Should().Equal(
            SqlValue.Integer(5), SqlValue.Integer(6), SqlValue.Integer(1), SqlValue.Text("k"));
    }

    [Test]
    public void BareProjectionOverloadStillLowersThroughTheSharedExpressionPath()
    {
        // The DmlProjectionOp overload projects each op onto a leaf expression; the emitted program must be
        // identical to the pre-existing bare path (no arithmetic, output registers only).
        var program = DmlStatementCompiler.Compile(
            DmlKind.Insert, "t", columnCount: 2, predicate: null,
            returningOps: [DmlProjectionOp.ForColumn(1), DmlProjectionOp.ForRowId()],
            writeTarget: NullTarget("t", 0)).Program;

        Opcodes(program).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Column", "RowId", "ResultRow", "Next",
            "Commit", "CloseCursor", "Halt");
        Describe(program, 3).Comment.Should().Be("r[0]=c0.col[1]");
        Describe(program, 4).Comment.Should().Be("r[1]=c0.rowid");
        program.RegisterCount.Should().Be(2);
    }

    // ---- Lifecycle: reset -------------------------------------------------------------------------

    [Test]
    public void ResetReplaysTheArithmeticProjection()
    {
        var table = new TestTable("t", columnCount: 1);
        var compiled = DmlStatementCompiler.Compile(
            DmlKind.Insert, table.Name, table.ColumnCount, predicate: null,
            returning: [Add(RowIdExpr(), Const(100))], writeTarget: InsertTarget(table, Row("a")));

        using var statement = new ResumableStatement(compiled.Program, writeTargets: compiled.WriteTargets);

        var first = Drain(statement);
        first.Should().ContainSingle();
        first[0][0].Should().Be(SqlValue.Integer(101));

        statement.Reset();
        statement.RowsAffected.Should().Be(0);

        var second = Drain(statement);
        second[0][0].Should().Be(SqlValue.Integer(102));
        AssertTable(table, (1, Row("a")), (2, Row("a")));
    }

    // ---- Descriptor factories ---------------------------------------------------------------------

    private static DmlReturningExpression Col(int index) => DmlReturningExpression.Column(index);

    private static DmlReturningExpression RowIdExpr() => DmlReturningExpression.RowId();

    private static DmlReturningExpression Const(long value) => DmlReturningExpression.Constant(SqlValue.Integer(value));

    private static DmlReturningExpression Const(SqlValue value) => DmlReturningExpression.Constant(value);

    private static DmlReturningExpression Add(DmlReturningExpression left, DmlReturningExpression right)
        => DmlReturningExpression.Arithmetic(ArithmeticOperator.Add, left, right);

    private static DmlReturningExpression Mul(DmlReturningExpression left, DmlReturningExpression right)
        => DmlReturningExpression.Arithmetic(ArithmeticOperator.Multiply, left, right);

    private static DmlReturningExpression Divide(DmlReturningExpression left, DmlReturningExpression right)
        => DmlReturningExpression.Arithmetic(ArithmeticOperator.Divide, left, right);

    private static DmlReturningExpression Negate(DmlReturningExpression operand)
        => DmlReturningExpression.Arithmetic(ArithmeticOperator.Negate, operand);

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

    // A minimal rowid-addressed table backing the hand-built write targets. Mutations are buffered by the
    // targets and applied here only through Commit, so atomicity stays owned by the write target rather
    // than the compiler or interpreter.
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
