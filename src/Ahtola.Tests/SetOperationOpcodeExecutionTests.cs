using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the RowSetInsert and CompoundResultRow primitives added to the execution
// contract for compound set operations (INTERSECT/EXCEPT). Programs are hand-built from the public
// Execution contract and run through the resumable state machine, so these tests exercise the
// interpreter, validator, and EXPLAIN renderer directly rather than the CompoundProgramBuilder lowering.
// Row equality is always supplied through the VdbeRowEquality delegate contract, never re-derived here.
public class SetOperationOpcodeExecutionTests
{
    // Byte-exact row equality mirroring SqlValue equality: NULLs are equal to each other and other
    // values compare by exact kind and content.
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

    // Numeric-aware row equality that treats an integer and a real of the same magnitude as equal,
    // proving the executor defers every membership comparison to the supplied delegate.
    private static readonly VdbeRowEquality NumericAwareRows = (left, right) =>
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!NumericEqual(left[index], right[index]))
                return false;
        }

        return true;
    };

    [Test]
    public void IntersectEmitsPrimaryRowsPresentInTheSingleProbeSet()
    {
        // Primary 1,2,3 against probe set {2,3,4}: only 2 and 3 are present in the probe.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [1, 2, 3],
            [2, 3, 4]);

        Integers(RunToCompletion(program)).Should().Equal(2, 3);
    }

    [Test]
    public void IntersectRequiresPresenceInEveryProbeSet()
    {
        // Row must be in both {2,3,4} and {3,5}; only 3 qualifies.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [1, 2, 3, 5],
            [2, 3, 4],
            [3, 5]);

        Integers(RunToCompletion(program)).Should().Equal(3);
    }

    [Test]
    public void IntersectPreservesFirstTermOrderAndDeduplicatesOutput()
    {
        // The primary streams 3,1,3,1,2 in that order; 3 and 1 are in the probe and repeat, 2 is not.
        // Output keeps first-term order (3 before 1) and emits each distinct qualifying row once.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [3, 1, 3, 1, 2],
            [1, 3]);

        Integers(RunToCompletion(program)).Should().Equal(3, 1);
    }

    [Test]
    public void IntersectAgainstAnEmptyProbeSetEmitsNothing()
    {
        // A ∩ ∅ = ∅: an unpopulated probe set makes every membership test fail.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [1, 2, 3],
            System.Array.Empty<long>());

        RunToCompletion(program).Should().BeEmpty();
    }

    [Test]
    public void ExceptEmitsPrimaryRowsAbsentFromEveryProbeSet()
    {
        // Primary 1,2,3,4 minus rows in {2} and {4}: 1 and 3 survive, in first-term order.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.AbsentFromAll,
            ByteExactRows,
            primary: [1, 2, 3, 4],
            [2],
            [4]);

        Integers(RunToCompletion(program)).Should().Equal(1, 3);
    }

    [Test]
    public void ExceptDeduplicatesTheSurvivingPrimaryRows()
    {
        // 1 repeats and is absent from the probe; it is emitted once. 2 is excluded.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.AbsentFromAll,
            ByteExactRows,
            primary: [1, 1, 2, 1],
            [2]);

        Integers(RunToCompletion(program)).Should().Equal(1);
    }

    [Test]
    public void ExceptAgainstAnEmptyProbeSetKeepsEveryDistinctPrimaryRow()
    {
        // A - ∅ = distinct(A): nothing is excluded, duplicates still collapse.
        var program = SingleColumnSetOp(
            CompoundMembershipMode.AbsentFromAll,
            ByteExactRows,
            primary: [1, 2, 2, 3],
            System.Array.Empty<long>());

        Integers(RunToCompletion(program)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void MembershipDefersComparisonSemanticsToTheEqualityDelegate()
    {
        // Primary integer 1 against a probe holding the real 1.0.
        var program = SetOpValues(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [[SqlValue.Integer(1)]],
            [[SqlValue.Real(1.0)]]);

        // Byte-exact equality keeps 1 and 1.0 distinct, so the intersection is empty.
        RunToCompletion(program).Should().BeEmpty();

        // Numeric-aware equality treats them as the same row, so 1 qualifies.
        var numeric = SetOpValues(
            CompoundMembershipMode.PresentInAll,
            NumericAwareRows,
            primary: [[SqlValue.Integer(1)]],
            [[SqlValue.Real(1.0)]]);
        RunToCompletion(numeric).Should().HaveCount(1);
    }

    [Test]
    public void MembershipTreatsNullsAsEqualUnderTheSuppliedEquality()
    {
        // INTERSECT: a NULL primary row matches a NULL probe row because the delegate equates NULLs.
        var program = SetOpValues(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [[SqlValue.Null], [SqlValue.Integer(1)]],
            [[SqlValue.Null]]);

        var rows = RunToCompletion(program);
        rows.Should().HaveCount(1);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void MembershipComparesEveryColumnOfMultiColumnRows()
    {
        // Two-column INTERSECT: (1,'a') and (1,'b') stream; the probe holds only (1,'a').
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 2), ByteExactRows, 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 2), ByteExactRows, 1, [0], CompoundMembershipMode.PresentInAll),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 2), ByteExactRows, 1, [0], CompoundMembershipMode.PresentInAll),
            new HaltInstruction(),
        ];

        var rows = RunToCompletion(new VdbeProgram(2, cursorCount: 0, instructions, distinctSetCount: 2));

        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));
    }

    [Test]
    public void EmptyMembershipListMakesCompoundResultRowDegenerateToDistinctOutput()
    {
        // With no probe sets, PresentInAll is vacuously true, so the opcode behaves as plain distinct.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0, [], CompoundMembershipMode.PresentInAll),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0, [], CompoundMembershipMode.PresentInAll),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0, [], CompoundMembershipMode.PresentInAll),
            new HaltInstruction(),
        ];

        Integers(RunToCompletion(new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1)))
            .Should().Equal(1, 2);
    }

    [Test]
    public void RowSetInsertNeverProducesAResultRow()
    {
        // A program of pure inserts followed by a halt yields no rows.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(2)),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0),
            new HaltInstruction(),
        ];

        RunToCompletion(new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1)).Should().BeEmpty();
    }

    [Test]
    public void RowSetInsertReplacesTheRepresentativeForAnEqualLaterRow()
    {
        static bool EqualsIgnoreCase(SqlValue[] left, SqlValue[] right) =>
            string.Equals(left[0].AsText(), right[0].AsText(), StringComparison.OrdinalIgnoreCase);

        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Text("first")),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 1), EqualsIgnoreCase, 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("FIRST")),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 1), EqualsIgnoreCase, 0),
            new RowSetRewindInstruction(0, new RegisterRange(new Register(0), 1), new ProgramCounter(6)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new HaltInstruction(),
        ];

        RunToCompletion(new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Equal(SqlValue.Text("FIRST"));
    }

    [Test]
    public void ResetClearsRowSetsSoAReplayReproducesTheSameOutput()
    {
        var program = SingleColumnSetOp(
            CompoundMembershipMode.PresentInAll,
            ByteExactRows,
            primary: [1, 2, 3],
            [2, 3]);
        using var statement = new ResumableStatement(program);

        Integers(Drain(statement)).Should().Equal(2, 3);

        statement.Reset();

        // If Reset left the probe/output sets populated, the replay's inserts would still work but the
        // output dedup set would suppress every row. Clearing restores the full result.
        Integers(Drain(statement)).Should().Equal(2, 3);
    }

    [Test]
    public void SteppingAfterDisposeThrowsObjectDisposedException()
    {
        var program = SingleColumnSetOp(CompoundMembershipMode.PresentInAll, ByteExactRows, [1], [1]);
        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    [Test]
    public void ValidationRejectsRowSetInsertWithANullEquality()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 1), null!, 0),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ValidationRejectsRowSetInsertReferencingAnUnknownRowSet()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 3),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ValidationRejectsRowSetInsertReadingOutsideTheRegisterRange()
    {
        VdbeInstruction[] instructions =
        [
            new RowSetInsertInstruction(new RegisterRange(new Register(0), 4), ByteExactRows, 0),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ValidationRejectsCompoundResultRowWithANullEquality()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), null!, 1, [0], CompoundMembershipMode.PresentInAll),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 2));
    }

    [Test]
    public void ValidationRejectsCompoundResultRowWithANullMembershipList()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 1, null!, CompoundMembershipMode.PresentInAll),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 2));
    }

    [Test]
    public void ValidationRejectsCompoundResultRowWhoseOutputSetIsAlsoAMembershipSet()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0, [0], CompoundMembershipMode.PresentInAll),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ValidationRejectsCompoundResultRowReferencingAnUnknownMembershipSet()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0, [5], CompoundMembershipMode.PresentInAll),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 2));
    }

    [Test]
    public void ValidationRejectsCompoundResultRowWithAnUndefinedMembershipMode()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new CompoundResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 1, [0], (CompoundMembershipMode)42),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 2));
    }

    [Test]
    public void ExplainDescribesRowSetInsertWithItsRangeAndSet()
    {
        var instruction = new RowSetInsertInstruction(new RegisterRange(new Register(2), 3), ByteExactRows, 1);

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(2);
        p2.Should().Be(3);
        p3.Should().Be(1);
        p4.Should().BeNull();
        comment.Should().Be("insert r[2..4] into row set 1");
    }

    [Test]
    public void ExplainDescribesCompoundResultRowForIntersect()
    {
        var instruction = new CompoundResultRowInstruction(
            new RegisterRange(new Register(0), 2),
            ByteExactRows,
            2,
            [0, 1],
            CompoundMembershipMode.PresentInAll);

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(0);
        p2.Should().Be(2);
        p3.Should().Be(2);
        p4.Should().Be("sets {0,1}");
        comment.Should().Be("output=r[0..1] if new to distinct set 2 and present in all of sets {0,1}");
    }

    [Test]
    public void ExplainDescribesCompoundResultRowForExcept()
    {
        var instruction = new CompoundResultRowInstruction(
            new RegisterRange(new Register(4), 1),
            ByteExactRows,
            1,
            [0],
            CompoundMembershipMode.AbsentFromAll);

        var (_, _, _, p4, comment) = VdbeExplain.Describe(instruction);

        p4.Should().Be("sets {0}");
        comment.Should().Be("output=r[4] if new to distinct set 1 and absent from all of sets {0}");
    }

    private static bool NumericEqual(SqlValue left, SqlValue right)
    {
        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return left.Kind == right.Kind;

        var leftNumber = AsNumber(left);
        var rightNumber = AsNumber(right);
        if (leftNumber is double x && rightNumber is double y)
            return x.Equals(y);

        return left.Equals(right);
    }

    private static double? AsNumber(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        _ => null,
    };

    // Builds a single-column set operation: each probe set is materialized first via RowSetInsert, then
    // the primary stream is emitted via CompoundResultRow. Probe sets occupy set indices 0..n-1 and the
    // output de-duplication set is the last index.
    private static VdbeProgram SingleColumnSetOp(
        CompoundMembershipMode mode,
        VdbeRowEquality equality,
        long[] primary,
        params long[][] probeSets)
    {
        var primaryValues = primary.Select(value => new[] { SqlValue.Integer(value) }).ToArray();
        var probeValues = probeSets
            .Select(set => set.Select(value => new[] { SqlValue.Integer(value) }).ToArray())
            .ToArray();
        return SetOpValues(mode, equality, primaryValues, probeValues);
    }

    private static VdbeProgram SetOpValues(
        CompoundMembershipMode mode,
        VdbeRowEquality equality,
        SqlValue[][] primary,
        params SqlValue[][][] probeSets)
    {
        var width = primary.Length > 0
            ? primary[0].Length
            : probeSets.Length > 0 && probeSets[0].Length > 0 ? probeSets[0][0].Length : 1;
        var outputSet = probeSets.Length;
        var membership = Enumerable.Range(0, probeSets.Length).ToArray();
        var instructions = new List<VdbeInstruction>();
        for (var setIndex = 0; setIndex < probeSets.Length; setIndex++)
        {
            foreach (var row in probeSets[setIndex])
            {
                for (var column = 0; column < row.Length; column++)
                    instructions.Add(new LoadConstantInstruction(new Register(column), row[column]));

                instructions.Add(new RowSetInsertInstruction(new RegisterRange(new Register(0), row.Length), equality, setIndex));
            }
        }

        foreach (var row in primary)
        {
            for (var column = 0; column < row.Length; column++)
                instructions.Add(new LoadConstantInstruction(new Register(column), row[column]));

            instructions.Add(new CompoundResultRowInstruction(
                new RegisterRange(new Register(0), row.Length),
                equality,
                outputSet,
                membership,
                mode));
        }

        instructions.Add(new HaltInstruction());
        return new VdbeProgram(width, cursorCount: 0, instructions, distinctSetCount: probeSets.Length + 1);
    }

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
