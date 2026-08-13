using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Regression coverage for the recursive worktable's row-ownership boundary. A recursive transform is free to
// reuse one SqlValue[] output buffer across the rows it emits and across successive expansions, so the
// runtime must snapshot each admitted row before storing it in the frontier queue or the distinct set.
// Without that snapshot a later overwrite of the shared buffer would rewrite an already-admitted row,
// corrupting the frontier (a queued row surfaces with the wrong values) or the distinct set (a genuinely new
// row is misread as a duplicate). Both tests drive a transform that deliberately reuses a single buffer.
public class RecursiveWorkTableRowSnapshotTests
{
    // Byte-exact row equality for the distinct worktable: NULLs equal each other, everything else compares by
    // exact kind and content.
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

    [Test]
    public void DistinctSnapshotsAdmittedRowsSoABufferReusingTransformDoesNotCorruptDedup()
    {
        // n -> {n + 1}, but every expansion returns the SAME array instance, mutated in place. Under DISTINCT
        // the dedup set records each admitted row; storing the shared buffer by reference would let the next
        // generation's expansion overwrite that representative to equal the new candidate, so the candidate
        // would look like a duplicate and the chain would terminate early (emitting only 1, 2). Snapshotting
        // on admission keeps each recorded row independent, so the full distinct chain 1..5 surfaces.
        var shared = new SqlValue[1];
        VdbeRecursiveTransform reuseBuffer = row =>
        {
            shared[0] = SqlValue.Integer(row[0].AsInteger() + 1);
            return [shared];
        };

        var program = BuildRecursiveProgram(
            width: 1,
            mode: WorkTableDedupMode.Distinct,
            equality: ByteExactRows,
            maxRows: 100,
            maxDepth: 4,
            transform: reuseBuffer,
            seedRows: [[1]]);

        RunIntegers(program).Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public void KeepAllSnapshotsQueuedRowsSoABufferReusingTransformDoesNotCorruptTheFrontier()
    {
        // Two seeds (10, 20), each expanded once (depth guard 1) with n -> {n + 1} through a single reused
        // buffer. Breadth-first draining expands 10 (queuing its child) and then 20, whose expansion
        // overwrites the shared buffer. If the child of 10 were queued by reference it would then read 21 too,
        // so the stream would be 10, 20, 21, 21 with 11 lost. Snapshotting on admission keeps each queued row
        // stable, yielding the correct level order 10, 20, 11, 21.
        var shared = new SqlValue[1];
        VdbeRecursiveTransform reuseBuffer = row =>
        {
            shared[0] = SqlValue.Integer(row[0].AsInteger() + 1);
            return [shared];
        };

        var program = BuildRecursiveProgram(
            width: 1,
            mode: WorkTableDedupMode.KeepAll,
            equality: null,
            maxRows: 100,
            maxDepth: 1,
            transform: reuseBuffer,
            seedRows: [[10], [20]]);

        RunIntegers(program).Should().Equal(10, 20, 11, 21);
    }

    // Assembles the canonical recursive loop directly from opcodes:
    //   OpenWorkTable; (LoadConstant* SeedWorkTable)*; loop: WorkTableStep->done, ResultRow, WorkTableExpand,
    //   Goto loop; done: CloseWorkTable; Halt.
    private static VdbeProgram BuildRecursiveProgram(
        int width,
        WorkTableDedupMode mode,
        VdbeRowEquality? equality,
        int maxRows,
        int maxDepth,
        VdbeRecursiveTransform transform,
        long[][] seedRows)
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

    private static List<long> RunIntegers(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        var integers = new List<long>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
                integers.Add(statement.CurrentRow![0].AsInteger());
            else if (result == ResumableStatementStepResult.Done)
                break;
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return integers;
    }
}
