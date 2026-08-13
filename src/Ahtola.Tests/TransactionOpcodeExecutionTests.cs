using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the transaction/savepoint family (BeginTransaction, CommitTransaction,
// RollbackTransaction, Savepoint, ReleaseSavepoint, RollbackToSavepoint). Programs are built by hand from
// the public Execution contract and run through the resumable state machine, so these tests exercise the
// interpreter, its transaction context resource, and the validator directly rather than any SQL wiring.
//
// The family transacts the interpreter's own mutable register file, not a durable store: ROLLBACK and
// ROLLBACK TO restore the register snapshot taken at the enclosing BEGIN/SAVEPOINT, RELEASE folds a nested
// savepoint into its enclosing scope without restoring, and COMMIT keeps the current register values. That
// register restoration is the observable behavior these tests assert, alongside the transaction context's
// InTransaction/TransactionDepth/TransactionSavepoints state.
public class TransactionOpcodeExecutionTests
{
    [Test]
    public void RollbackTransactionRestoresRegisterStateToBegin()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new BeginTransactionInstruction(),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new RollbackTransactionInstruction(),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Integers(DrainRows(statement)).Should().Equal(1);
        statement.InTransaction.Should().BeFalse();
        statement.TransactionDepth.Should().Be(0);
    }

    [Test]
    public void CommitTransactionKeepsMutatedRegisterState()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new BeginTransactionInstruction(),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new CommitTransactionInstruction(),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Integers(DrainRows(statement)).Should().Equal(2);
        statement.InTransaction.Should().BeFalse();
    }

    [Test]
    public void RollbackToSavepointRestoresToSavepointSnapshotAndKeepsTransactionOpen()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new BeginTransactionInstruction(),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new SavepointInstruction("sp"),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
            new RollbackToSavepointInstruction("sp"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        // ROLLBACK TO restores the snapshot taken at the savepoint (r0=2), not the BEGIN snapshot (r0=1).
        Integers(DrainRows(statement)).Should().Equal(2);
        // ROLLBACK TO keeps the savepoint and the enclosing transaction open.
        statement.InTransaction.Should().BeTrue();
        statement.TransactionDepth.Should().Be(2);
        statement.TransactionSavepoints.Should().Equal(null, "sp");
    }

    [Test]
    public void RollbackToSameSavepointTwiceIsAllowedBecauseItIsRetained()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(10)),
            new BeginTransactionInstruction(),
            new SavepointInstruction("s"),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(20)),
            new RollbackToSavepointInstruction("s"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(30)),
            new RollbackToSavepointInstruction("s"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        // Both rollbacks restore the same retained savepoint snapshot (r0=10).
        Integers(DrainRows(statement)).Should().Equal(10, 10);
    }

    [Test]
    public void ReleaseSavepointFoldsNestedChangesIntoEnclosingScopeWithoutRestoring()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new BeginTransactionInstruction(),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new SavepointInstruction("sp"),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
            new ReleaseSavepointInstruction("sp"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new RollbackTransactionInstruction(),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        // RELEASE does not restore registers (r0 stays 3), and the outer ROLLBACK then reverts to the
        // BEGIN snapshot (r0=1) — proving the nested change folded into the transaction rather than
        // committing independently.
        Integers(DrainRows(statement)).Should().Equal(3, 1);
        statement.InTransaction.Should().BeFalse();
    }

    [Test]
    public void ReleaseSavepointThatOpenedTheTransactionEndsIt()
    {
        VdbeInstruction[] instructions =
        [
            new SavepointInstruction("outer"),
            new ReleaseSavepointInstruction("outer"),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        DrainRows(statement);
        statement.InTransaction.Should().BeFalse();
        statement.TransactionDepth.Should().Be(0);
    }

    [Test]
    public void NestedSavepointsRollBackToEachLevel()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new BeginTransactionInstruction(),
            new SavepointInstruction("a"),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new SavepointInstruction("b"),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
            new RollbackToSavepointInstruction("b"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new RollbackToSavepointInstruction("a"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        // ROLLBACK TO b restores r0=2; ROLLBACK TO a cancels b and restores r0=1.
        Integers(DrainRows(statement)).Should().Equal(2, 1);
        // Rolling back to a cancels the inner savepoint b but keeps a and the transaction root.
        statement.TransactionSavepoints.Should().Equal(null, "a");
    }

    [Test]
    public void SavepointOutsideTransactionImplicitlyOpensOne()
    {
        VdbeInstruction[] instructions =
        [
            new SavepointInstruction("s"),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        DrainRows(statement);
        statement.InTransaction.Should().BeTrue();
        statement.TransactionDepth.Should().Be(1);
        statement.TransactionSavepoints.Should().Equal("s");
    }

    [Test]
    public void BeginTransactionWithinTransactionThrows()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new BeginTransactionInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Assert.Throws<VdbeTransactionException>(() => statement.StepResumable());
    }

    [Test]
    public void CommitWithNoActiveTransactionThrows()
    {
        VdbeInstruction[] instructions =
        [
            new CommitTransactionInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Assert.Throws<VdbeTransactionException>(() => statement.StepResumable());
    }

    [Test]
    public void RollbackWithNoActiveTransactionThrows()
    {
        VdbeInstruction[] instructions =
        [
            new RollbackTransactionInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Assert.Throws<VdbeTransactionException>(() => statement.StepResumable());
    }

    [Test]
    public void ReleaseUnknownSavepointThrows()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new ReleaseSavepointInstruction("missing"),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Assert.Throws<VdbeTransactionException>(() => statement.StepResumable());
    }

    [Test]
    public void RollbackToUnknownSavepointThrows()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new RollbackToSavepointInstruction("missing"),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Assert.Throws<VdbeTransactionException>(() => statement.StepResumable());
    }

    [Test]
    public void TransactionStateIsObservableAtYieldPoints()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new YieldInstruction(),
            new SavepointInstruction("a"),
            new YieldInstruction(),
            new ReleaseSavepointInstruction("a"),
            new YieldInstruction(),
            new CommitTransactionInstruction(),
            new YieldInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);

        StepToPause(statement).Should().Be(ResumableStatementStepResult.Yielded);
        statement.InTransaction.Should().BeTrue();
        statement.TransactionDepth.Should().Be(1);
        statement.TransactionSavepoints.Should().Equal(new string?[] { null });

        StepToPause(statement).Should().Be(ResumableStatementStepResult.Yielded);
        statement.TransactionDepth.Should().Be(2);
        statement.TransactionSavepoints.Should().Equal(null, "a");

        StepToPause(statement).Should().Be(ResumableStatementStepResult.Yielded);
        statement.TransactionDepth.Should().Be(1);

        StepToPause(statement).Should().Be(ResumableStatementStepResult.Yielded);
        statement.InTransaction.Should().BeFalse();
        statement.TransactionDepth.Should().Be(0);

        StepToPause(statement).Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void HaltLeavesAnOpenTransactionObservable()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        DrainRows(statement);
        // Nothing auto-commits or auto-rolls-back at Halt: the transaction stays open until Reset/Dispose.
        statement.State.Should().Be(ResumableStatementState.Done);
        statement.InTransaction.Should().BeTrue();
        statement.TransactionDepth.Should().Be(1);
    }

    [Test]
    public void ResetClearsAnOpenTransaction()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new SavepointInstruction("s"),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        DrainRows(statement);
        statement.TransactionDepth.Should().Be(2);

        statement.Reset();

        statement.InTransaction.Should().BeFalse();
        statement.TransactionDepth.Should().Be(0);
    }

    [Test]
    public void ResetReplaysATransactionProgramFromTheStart()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new BeginTransactionInstruction(),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new SavepointInstruction("sp"),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
            new RollbackToSavepointInstruction("sp"),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 1, cursorCount: 0, instructions);

        using var statement = new ResumableStatement(program);
        Integers(DrainRows(statement)).Should().Equal(2);

        statement.Reset();

        // Reset discards the prior savepoint stack, so the second drain rebuilds it and matches the first.
        Integers(DrainRows(statement)).Should().Equal(2);
    }

    [Test]
    public void DisposeClearsTransactionStateAndBlocksStepping()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        var statement = new ResumableStatement(program);
        DrainRows(statement);
        statement.InTransaction.Should().BeTrue();

        statement.Dispose();

        statement.InTransaction.Should().BeFalse();
        statement.TransactionDepth.Should().Be(0);
        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    [Test]
    public void ValidationRejectsEmptySavepointName()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new SavepointInstruction(string.Empty),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void ValidationRejectsNullSavepointName()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new ReleaseSavepointInstruction(null!),
                new HaltInstruction(),
            ]));

        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [
                new RollbackToSavepointInstruction(null!),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void ExplainRendersTransactionOpcodesWithNamesAndComments()
    {
        VdbeInstruction[] instructions =
        [
            new BeginTransactionInstruction(),
            new SavepointInstruction("sp"),
            new RollbackToSavepointInstruction("sp"),
            new ReleaseSavepointInstruction("sp"),
            new CommitTransactionInstruction(),
            new RollbackTransactionInstruction(),
            new HaltInstruction(),
        ];
        var program = new VdbeProgram(registerCount: 0, cursorCount: 0, instructions);

        var rendered = VdbeExplain.Describe(program);
        rendered.Should().HaveCount(program.Instructions.Count);

        // addr / opcode / p1 / p2 / p3 / p4 / comment
        rendered[0][1].AsText().Should().Be("BeginTransaction");
        rendered[0][6].AsText().Should().Be("begin transaction");

        rendered[1][1].AsText().Should().Be("Savepoint");
        rendered[1][5].AsText().Should().Be("sp");
        rendered[1][6].AsText().Should().Be("open savepoint sp");

        rendered[2][1].AsText().Should().Be("RollbackToSavepoint");
        rendered[2][5].AsText().Should().Be("sp");
        rendered[2][6].AsText().Should().Be("rollback to savepoint sp");

        rendered[3][1].AsText().Should().Be("ReleaseSavepoint");
        rendered[3][5].AsText().Should().Be("sp");
        rendered[3][6].AsText().Should().Be("release savepoint sp");

        rendered[4][1].AsText().Should().Be("CommitTransaction");
        rendered[4][6].AsText().Should().Be("commit transaction");
        rendered[4][5].Kind.Should().Be(SqlValueKind.Null);

        rendered[5][1].AsText().Should().Be("RollbackTransaction");
        rendered[5][6].AsText().Should().Be("rollback transaction");
    }

    private static ResumableStatementStepResult StepToPause(ResumableStatement statement)
    {
        if (statement.State == ResumableStatementState.Yielded)
            statement.Resume();

        return statement.StepResumable();
    }

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> DrainRows(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                rows.Add([.. statement.CurrentRow!]);
            else if (result == ResumableStatementStepResult.Done)
                break;
            else if (result == ResumableStatementStepResult.Yielded)
                statement.Resume();
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
