using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers VdbeInsertFlags on Insert/Update (inventory: vdbe-insert-update-flag-semantics).
/// </summary>
public sealed class VdbeInsertFlagSemanticsTests
{
    [Test]
    public void RequireSeekRejectsUnpositionedCursor()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 0,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(1)], 1),
            Commit = () => 1,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new InsertInstruction(new Cursor(0), VdbeInsertFlags.RequireSeek),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        var error = Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        error!.Message.Should().Contain("RequireSeek");
    }

    [Test]
    public void SkipStatementChangeCountDoesNotIncrementRowsAffected()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(2)], 1),
            Commit = () => null,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new UpdateInstruction(new Cursor(0), VdbeInsertFlags.SkipStatementChangeCount),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.RowsAffected.Should().Be(0);
    }

    [Test]
    public void DefaultInsertStillCountsRowsAffected()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(2)], 1),
            Commit = () => null,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new UpdateInstruction(new Cursor(0)),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void ExplainRendersInsertFlags()
    {
        var insert = new InsertInstruction(
            new Cursor(2),
            VdbeInsertFlags.RequireSeek | VdbeInsertFlags.SkipLastRowid);
        var (_, p2, _, p4, comment) = VdbeExplain.Describe(insert);
        p2.Should().Be((long)(VdbeInsertFlags.RequireSeek | VdbeInsertFlags.SkipLastRowid));
        p4.Should().Contain("RequireSeek");
        comment.Should().Contain("flags=");
    }

    [Test]
    public void SkipLastRowidLeavesLastInsertRowIdUnchangedOnCommit()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(9)], 99),
            Commit = () => 99,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                    new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                    new InsertInstruction(new Cursor(0), VdbeInsertFlags.SkipLastRowid),
                    new CommitInstruction(new Cursor(0)),
                    new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.LastInsertRowId.Should().BeNull();
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void IntermediateSkipLastRowidThenFinalInsertUpdatesLastInsertRowId()
    {
        var writes = 0;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 2,
            GetRow = i => [SqlValue.Integer(i + 1)],
            GetRowId = i => i + 1,
            MutateRow = i =>
            {
                writes++;
                return new VdbeRowMutation([SqlValue.Integer(100 + i)], 100 + i);
            },
            Commit = () => 101,
        };

        // 0 OpenWrite
        // 1 Rewind -> 7 if empty
        // 2 Insert SkipLastRowid (first row)
        // 3 Next -> 5 if more rows
        // 4 Goto 6 (single-row path)
        // 5 Insert (final row may update last_insert_rowid)
        // 6 Commit
        // 7 Halt
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                    new RewindCursorInstruction(new Cursor(0), new ProgramCounter(7)),
                    new InsertInstruction(new Cursor(0), VdbeInsertFlags.SkipLastRowid),
                    new NextInstruction(new Cursor(0), new ProgramCounter(5)),
                    new GotoInstruction(new ProgramCounter(6)),
                    new InsertInstruction(new Cursor(0)),
                    new CommitInstruction(new Cursor(0)),
                    new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        writes.Should().Be(2);
        statement.LastInsertRowId.Should().Be(101);
    }

    [Test]
    public void UpdateRowidChangeAllowsMutationAfterSeek()
    {
        long? seenOld = null;
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 7,
            MutateRow = _ =>
            {
                seenOld = 7;
                return new VdbeRowMutation([SqlValue.Integer(2)], 42);
            },
            Commit = () => null,
        };

        // 0 OpenWrite
        // 1 Rewind -> 3 if empty
        // 2 Update
        // 3 Halt
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                    new RewindCursorInstruction(new Cursor(0), new ProgramCounter(3)),
                    new UpdateInstruction(
                        new Cursor(0),
                        VdbeInsertFlags.RequireSeek | VdbeInsertFlags.UpdateRowidChange),
                    new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        seenOld.Should().Be(7);
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void SkipAllChangeCountsDoesNotIncrementRowsAffected()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(2)], 1),
            Commit = () => null,
        };

        // 0 OpenWrite
        // 1 Rewind -> 3 if empty
        // 2 Update SkipAllChangeCounts
        // 3 Halt
        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                    new RewindCursorInstruction(new Cursor(0), new ProgramCounter(3)),
                    new UpdateInstruction(new Cursor(0), VdbeInsertFlags.SkipAllChangeCounts),
                    new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.RowsAffected.Should().Be(0);
    }
}
