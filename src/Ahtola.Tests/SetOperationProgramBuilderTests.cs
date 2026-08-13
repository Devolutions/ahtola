using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// End-to-end coverage for CompoundProgramBuilder.BuildIntersect and BuildExcept, the lowering that
// sequences independently compiled child programs (constant projections and table scans here) into one
// runnable program for compound set-operation execution. Each built program is executed through the
// resumable state machine so the tests assert real emitted output, not just program shape. Row-equality
// is always supplied by the caller through VdbeRowEquality, mirroring how a SQL router would forward the
// evaluator's affinity/collation-aware contract.
public class SetOperationProgramBuilderTests
{
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

    // Coerces integers and reals of equal magnitude to equal, proving the builder forwards comparison
    // semantics to the supplied delegate instead of hardcoding SqlValue equality.
    private static readonly VdbeRowEquality NumericAwareRows = (left, right) =>
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.Kind is SqlValueKind.Integer or SqlValueKind.Real
                && b.Kind is SqlValueKind.Integer or SqlValueKind.Real)
            {
                var x = a.Kind == SqlValueKind.Integer ? a.AsInteger() : a.AsReal();
                var y = b.Kind == SqlValueKind.Integer ? b.AsInteger() : b.AsReal();
                if (x != y)
                    return false;
            }
            else if (!a.Equals(b))
            {
                return false;
            }
        }

        return true;
    };

    [Test]
    public void BuildIntersectKeepsRowsPresentInBothScans()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1, 2, 2, 3), ScanTerm("b", 2, 3, 4)],
            ByteExactRows);

        compound.Program.DistinctSetCount.Should().Be(3);
        Integers(Run(compound)).Should().Equal(2, 3);
    }

    [Test]
    public void BuildIntersectRequiresPresenceInEveryTerm()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1, 2, 3, 5), ScanTerm("b", 2, 3, 4), ScanTerm("c", 3, 5)],
            ByteExactRows);

        compound.Program.DistinctSetCount.Should().Be(4);
        Integers(Run(compound)).Should().Equal(3);
    }

    [Test]
    public void BuildIntersectPreservesFirstTermOrder()
    {
        // The primary term streams 3,2,1; the intersection keeps that order, not the probe term's.
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 3, 2, 1), ScanTerm("b", 1, 2, 3)],
            ByteExactRows);

        Integers(Run(compound)).Should().Equal(3, 2, 1);
    }

    [Test]
    public void BuildIntersectWithAnEmptyTermEmitsNothing()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1, 2, 3), ScanTerm("b")],
            ByteExactRows);

        Run(compound).Should().BeEmpty();
    }

    [Test]
    public void BuildIntersectDefersEqualityToTheSuppliedDelegate()
    {
        var terms = new[] { ConstantTerm(SqlValue.Integer(1)), ConstantTerm(SqlValue.Real(1.0)) };

        // Byte-exact equality keeps an integer and a real distinct, so the intersection is empty.
        Run(CompoundProgramBuilder.BuildIntersect(terms, ByteExactRows)).Should().BeEmpty();

        // Numeric-aware equality treats them as equal, so the shared value survives.
        var numeric = Run(CompoundProgramBuilder.BuildIntersect(terms, NumericAwareRows));
        numeric.Should().HaveCount(1);
        numeric[0][0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void BuildExceptDropsRowsPresentInAnyLaterTerm()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [ScanTerm("a", 1, 2, 3, 4), ScanTerm("b", 2), ScanTerm("c", 4)],
            ByteExactRows);

        compound.Program.DistinctSetCount.Should().Be(4);
        Integers(Run(compound)).Should().Equal(1, 3);
    }

    [Test]
    public void BuildExceptDeduplicatesTheSurvivingRows()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [ScanTerm("a", 1, 1, 2, 3, 3), ScanTerm("b", 2)],
            ByteExactRows);

        Integers(Run(compound)).Should().Equal(1, 3);
    }

    [Test]
    public void BuildExceptPreservesFirstTermOrder()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [ScanTerm("a", 5, 3, 1, 3), ConstantTerm(1)],
            ByteExactRows);

        Integers(Run(compound)).Should().Equal(5, 3);
    }

    [Test]
    public void BuildIntersectEvaluatesTermsInSourceOrder()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("primary", 1), ScanTerm("probe", 1)],
            ByteExactRows);

        var opens = compound.Program.Instructions.OfType<OpenReadCursorInstruction>().ToList();
        opens.Should().HaveCount(2);
        opens[0].TableName.Should().Be("primary");
        opens[0].Cursor.Index.Should().Be(0);
        opens[1].TableName.Should().Be("probe");
        opens[1].Cursor.Index.Should().Be(1);
        compound.CursorSources.Should().HaveCount(2);
    }

    [Test]
    public void BuildIntersectCapturesEveryTermThenIteratesThePrimarySet()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ConstantTerm(1), ConstantTerm(1), ConstantTerm(1)],
            ByteExactRows);

        compound.Program.Instructions.OfType<ResultRowInstruction>().Should().BeEmpty();

        compound.Program.Instructions.OfType<RowSetInsertInstruction>().Should().HaveCount(3);
        compound.Program.Instructions.OfType<RowSetRewindInstruction>().Should().ContainSingle();
        compound.Program.Instructions.OfType<RowSetNextInstruction>().Should().ContainSingle();
        var emits = compound.Program.Instructions.OfType<CompoundResultRowInstruction>().ToList();
        emits.Should().HaveCount(1);
        emits[0].Mode.Should().Be(CompoundMembershipMode.PresentInAll);
        emits[0].OutputSetIndex.Should().Be(3);
        emits[0].MembershipSetIndices.Should().Equal(1, 2);
    }

    [Test]
    public void BuildExceptSubstitutesTheAbsentFromAllMode()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [ConstantTerm(1), ConstantTerm(2)],
            ByteExactRows);

        var emits = compound.Program.Instructions.OfType<CompoundResultRowInstruction>().ToList();
        emits.Should().HaveCount(1);
        emits[0].Mode.Should().Be(CompoundMembershipMode.AbsentFromAll);
        emits[0].MembershipSetIndices.Should().Equal(1);
    }

    [Test]
    public void BuildIntersectReplaysAfterReset()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1, 2, 3), ScanTerm("b", 2, 3)],
            ByteExactRows);
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);

        Integers(Drain(statement)).Should().Equal(2, 3);

        statement.Reset();

        // Reset must clear the probe and output sets; otherwise the replay would suppress every row.
        Integers(Drain(statement)).Should().Equal(2, 3);
    }

    [Test]
    public void BuildExceptReplaysAfterReset()
    {
        var compound = CompoundProgramBuilder.BuildExcept(
            [ScanTerm("a", 1, 2, 3), ScanTerm("b", 2)],
            ByteExactRows);
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);

        Integers(Drain(statement)).Should().Equal(1, 3);

        statement.Reset();

        Integers(Drain(statement)).Should().Equal(1, 3);
    }

    [Test]
    public void SetOperationResultIsRenderableByExplain()
    {
        var compound = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1), ConstantTerm(1)],
            ByteExactRows);

        var rendered = VdbeExplain.Describe(compound.Program);

        rendered.Should().HaveCount(compound.Program.Instructions.Count);
    }

    [Test]
    public void SetOperationResultCanFeedAUnionAllTerm()
    {
        // A set-operation sub-term uses RowSetInsert/CompoundResultRow internally; the surrounding
        // UNION ALL must relocate those opcodes and count the compound emit as a result row.
        var intersect = CompoundProgramBuilder.BuildIntersect(
            [ScanTerm("a", 1, 2, 3), ScanTerm("b", 2, 3)],
            ByteExactRows);

        var compound = CompoundProgramBuilder.BuildUnionAll([intersect, ConstantTerm(9)]);

        Integers(Run(compound)).Should().Equal(2, 3, 9);
    }

    [Test]
    public void BuildIntersectRejectsFewerThanTwoTerms()
    {
        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildIntersect([ConstantTerm(1)], ByteExactRows));
    }

    [Test]
    public void BuildExceptRejectsANullTermsList()
    {
        Assert.Throws<ArgumentNullException>(
            () => CompoundProgramBuilder.BuildExcept(null!, ByteExactRows));
    }

    [Test]
    public void BuildIntersectRejectsANullEquality()
    {
        Assert.Throws<ArgumentNullException>(
            () => CompoundProgramBuilder.BuildIntersect([ConstantTerm(1), ConstantTerm(2)], null!));
    }

    [Test]
    public void BuildIntersectRejectsANullTerm()
    {
        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildIntersect([ConstantTerm(1), null!], ByteExactRows));
    }

    [Test]
    public void BuildIntersectRejectsMismatchedColumnCounts()
    {
        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildIntersect(
                [ConstantTerm(SqlValue.Integer(1)), ConstantTerm(SqlValue.Integer(2), SqlValue.Integer(3))],
                ByteExactRows));
    }

    [Test]
    public void BuildExceptRejectsTermsWhoseCursorSourceCountDisagreesWithTheProgram()
    {
        var scan = ScanTerm("a", 1);
        var missingSource = scan with { CursorSources = [] };

        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildExcept([missingSource, ConstantTerm(1)], ByteExactRows));
    }

    [Test]
    public void BuildIntersectRejectsTermsThatEmitNoResultRows()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HaltInstruction(),
        ];
        var noResultRows = new CompoundTerm(new VdbeProgram(1, cursorCount: 0, instructions), []);

        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildIntersect([noResultRows, ConstantTerm(1)], ByteExactRows));
    }

    [Test]
    public void BuildIntersectComposesTermsThatAlreadyUseRowSets()
    {
        var nested = CompoundProgramBuilder.BuildIntersect(
            [ConstantTerm(1), ConstantTerm(1)],
            ByteExactRows);

        var composed = CompoundProgramBuilder.BuildIntersect(
            [nested, ConstantTerm(1)],
            ByteExactRows);

        Integers(Run(composed)).Should().Equal(1);
        composed.Program.Instructions.OfType<GuardedRowInstruction>().Should().NotBeEmpty();
    }

    [Test]
    public void BuildExceptComposesUnionDistinctTermsThatAlreadyDeduplicate()
    {
        var distinct = CompoundProgramBuilder.BuildUnionDistinct(
            [ConstantTerm(1), ConstantTerm(2)],
            ByteExactRows);

        var composed = CompoundProgramBuilder.BuildExcept(
            [distinct, ConstantTerm(2)],
            ByteExactRows);

        Integers(Run(composed)).Should().Equal(1);
    }

    // A constant projection term: loads each value into successive registers, emits them as one result
    // row, and halts. It owns no cursors.
    private static CompoundTerm ConstantTerm(params SqlValue[] values)
    {
        var instructions = new List<VdbeInstruction>(values.Length + 2);
        for (var index = 0; index < values.Length; index++)
            instructions.Add(new LoadConstantInstruction(new Register(index), values[index]));

        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), values.Length)));
        instructions.Add(new HaltInstruction());
        return new CompoundTerm(new VdbeProgram(values.Length, cursorCount: 0, instructions), []);
    }

    private static CompoundTerm ConstantTerm(params long[] values)
        => ConstantTerm([.. values.Select(SqlValue.Integer)]);

    // A single-column table scan term over the supplied integer rows.
    private static CompoundTerm ScanTerm(string table, params long[] values)
    {
        var rows = values.Select(value => new[] { SqlValue.Integer(value) }).ToList();
        var source = new VdbeCursorSource(rows);
        VdbeInstruction[] instructions =
        [
            new OpenReadCursorInstruction(new Cursor(0), table, 1),
            new RewindCursorInstruction(new Cursor(0), new ProgramCounter(5)),
            new ColumnInstruction(new Cursor(0), 0, new Register(0)),
            new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
            new NextInstruction(new Cursor(0), new ProgramCounter(2)),
            new CloseCursorInstruction(new Cursor(0)),
            new HaltInstruction(),
        ];
        return new CompoundTerm(new VdbeProgram(1, cursorCount: 1, instructions), [source]);
    }

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> Run(CompoundTerm compound)
    {
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);
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
