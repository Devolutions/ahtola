using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Opcode-level coverage for the DistinctResultRow primitive added to the execution contract for
// compound-select de-duplication (UNION/DISTINCT). Programs are hand-built from the public Execution
// contract and run through the resumable state machine, so these tests exercise the interpreter,
// validator, and EXPLAIN renderer directly rather than the CompoundProgramBuilder lowering. Row
// equality is supplied through the VdbeRowEquality delegate contract, never re-derived here.
public class CompoundOpcodeExecutionTests
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
    // proving the executor defers all comparison rules to the supplied delegate.
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
    public void DistinctResultRowEmitsFirstOccurrenceAndSkipsDuplicates()
    {
        // Emit 1, 1, 2 through one distinct set; the second 1 is a duplicate and is dropped.
        var program = DistinctSequence(ByteExactRows, SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(2));

        var rows = RunToCompletion(program);

        rows.Select(row => row[0].AsInteger()).Should().Equal(1, 2);
    }

    [Test]
    public void DistinctResultRowTreatsNullsAsEqualUnderTheSuppliedEquality()
    {
        var program = DistinctSequence(ByteExactRows, SqlValue.Null, SqlValue.Null, SqlValue.Integer(0));

        var rows = RunToCompletion(program);

        rows.Should().HaveCount(2);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
        rows[1][0].Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void DistinctResultRowDefersComparisonSemanticsToTheEqualityDelegate()
    {
        // 1 and 1.0 are distinct under byte-exact equality but duplicates under numeric-aware equality.
        var seed = new[] { SqlValue.Integer(1), SqlValue.Real(1.0), SqlValue.Integer(2) };

        RunToCompletion(DistinctSequence(ByteExactRows, seed))
            .Should().HaveCount(3);

        var numeric = RunToCompletion(DistinctSequence(NumericAwareRows, seed));
        numeric.Should().HaveCount(2);
        numeric[0][0].Should().Be(SqlValue.Integer(1));
        numeric[1][0].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void DistinctResultRowDeduplicatesAcrossEveryColumnOfTheRow()
    {
        // Two-column rows: (1,'a'), (1,'a') duplicate, (1,'b') distinct on the second column.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 2), ByteExactRows, 0),
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("a")),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 2), ByteExactRows, 0),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("b")),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 2), ByteExactRows, 0),
            new HaltInstruction(),
        ];

        var rows = RunToCompletion(new VdbeProgram(2, cursorCount: 0, instructions, distinctSetCount: 1));

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b"));
    }

    [Test]
    public void SeparateDistinctSetsDeduplicateIndependently()
    {
        // Set 0 sees 1,1; set 1 sees 1. Each set drops only its own repeat, so three emissions
        // (1 via set 0, 1 via set 1) survive because a value repeated across different sets is novel
        // to each set.
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 1),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 0),
            new HaltInstruction(),
        ];

        var rows = RunToCompletion(new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 2));

        // set 0 emits the first 1, set 1 emits its first 1, and set 0 drops the trailing repeat.
        rows.Should().HaveCount(2);
    }

    [Test]
    public void ResetClearsDistinctSetsSoAReplayReemitsRows()
    {
        var program = DistinctSequence(ByteExactRows, SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(2));
        using var statement = new ResumableStatement(program);

        Drain(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2);

        statement.Reset();

        // If Reset did not clear the distinct set, the second run would suppress every row as a duplicate.
        Drain(statement).Select(row => row[0].AsInteger()).Should().Equal(1, 2);
    }

    [Test]
    public void ValidationRejectsDistinctResultRowWithANullEquality()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 1), null!, 0),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ValidationRejectsDistinctResultRowReferencingAnUnknownDistinctSet()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 1), ByteExactRows, 3),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ValidationRejectsDistinctResultRowReadingOutsideTheRegisterRange()
    {
        VdbeInstruction[] instructions =
        [
            new DistinctResultRowInstruction(new RegisterRange(new Register(0), 4), ByteExactRows, 0),
            new HaltInstruction(),
        ];

        Assert.Throws<VdbeProgramValidationException>(
            () => new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1));
    }

    [Test]
    public void ConstructionRejectsANegativeDistinctSetCount()
    {
        VdbeInstruction[] instructions = [new HaltInstruction()];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VdbeProgram(0, cursorCount: 0, instructions, distinctSetCount: -1));
    }

    [Test]
    public void ExplainDescribesDistinctResultRowWithItsRangeAndSet()
    {
        var instruction = new DistinctResultRowInstruction(new RegisterRange(new Register(3), 2), ByteExactRows, 1);

        var (p1, p2, p3, p4, comment) = VdbeExplain.Describe(instruction);

        p1.Should().Be(3);
        p2.Should().Be(2);
        p3.Should().Be(1);
        p4.Should().BeNull();
        comment.Should().Be("output=r[3..4] if new to distinct set 1");
    }

    [Test]
    public void SteppingAfterDisposeThrowsObjectDisposedException()
    {
        var program = DistinctSequence(ByteExactRows, SqlValue.Integer(1));
        var statement = new ResumableStatement(program);
        statement.Dispose();

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
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

    // Loads each seed value into r0 and emits it through one shared distinct set, then halts.
    private static VdbeProgram DistinctSequence(VdbeRowEquality equality, params SqlValue[] seeds)
    {
        var instructions = new List<VdbeInstruction>(seeds.Length * 2 + 1);
        foreach (var seed in seeds)
        {
            instructions.Add(new LoadConstantInstruction(new Register(0), seed));
            instructions.Add(new DistinctResultRowInstruction(new RegisterRange(new Register(0), 1), equality, 0));
        }

        instructions.Add(new HaltInstruction());
        return new VdbeProgram(1, cursorCount: 0, instructions, distinctSetCount: 1);
    }

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
