using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// End-to-end coverage for CompoundProgramBuilder, the lowering that sequences independently compiled
// child programs (constant projections and table scans here) into one runnable program for compound
// SELECT execution. Each built program is executed through the resumable state machine so the tests
// assert real emitted output, not just program shape. Row-equality for UNION/DISTINCT is always
// supplied by the caller through VdbeRowEquality, mirroring how a SQL router would forward the
// evaluator's affinity/collation-aware contract.
public class CompoundProgramBuilderTests
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
    public void BuildUnionAllConcatenatesTwoConstantTerms()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ConstantTerm(1), ConstantTerm(2)]);

        compound.Program.CursorCount.Should().Be(0);
        compound.Program.DistinctSetCount.Should().Be(0);
        Integers(Run(compound)).Should().Equal(1, 2);
    }

    [Test]
    public void BuildUnionAllConcatenatesTwoScans()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ScanTerm("a", 1, 2),
            ScanTerm("b", 3, 4),
        ]);

        compound.Program.CursorCount.Should().Be(2);
        compound.CursorSources.Should().HaveCount(2);
        Integers(Run(compound)).Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void BuildUnionAllChainsThreeMixedTerms()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ScanTerm("a", 1),
            ConstantTerm(2),
            ScanTerm("b", 3),
        ]);

        // The two scans contribute one cursor each; the constant term contributes none. Their sources
        // must concatenate in term order into the two combined cursor slots.
        compound.Program.CursorCount.Should().Be(2);
        compound.CursorSources.Should().HaveCount(2);
        Integers(Run(compound)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void BuildUnionAllEmitsNothingWhenEveryTermIsEmpty()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ScanTerm("a"), ScanTerm("b")]);

        Run(compound).Should().BeEmpty();
    }

    [Test]
    public void BuildUnionAllSkipsEmptyTermsAndFallsThroughToNonEmptyOnes()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ScanTerm("empty-left"),
            ConstantTerm(5),
            ScanTerm("empty-right"),
        ]);

        Integers(Run(compound)).Should().Equal(5);
    }

    [Test]
    public void BuildUnionAllPreservesDuplicateRows()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ConstantTerm(1), ConstantTerm(1)]);

        Integers(Run(compound)).Should().Equal(1, 1);
    }

    [Test]
    public void BuildUnionAllReplaysAfterReset()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ScanTerm("a", 1, 2), ConstantTerm(3)]);
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);

        Integers(Drain(statement)).Should().Equal(1, 2, 3);

        statement.Reset();

        Integers(Drain(statement)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void BuildUnionAllRelocatesTheSecondTermCursorToADisjointIndex()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ScanTerm("a", 1), ScanTerm("b", 2)]);

        var opens = compound.Program.Instructions
            .OfType<OpenReadCursorInstruction>()
            .ToList();

        opens.Should().HaveCount(2);
        opens[0].Cursor.Index.Should().Be(0);
        opens[1].Cursor.Index.Should().Be(1);
    }

    [Test]
    public void BuildUnionAllProducesTheExpectedOpcodeSequenceForConstantTerms()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ConstantTerm(1), ConstantTerm(2)]);

        compound.Program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.LoadConstant,
            VdbeOpcode.ResultRow,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Halt);
    }

    [Test]
    public void BuildUnionDistinctDropsDuplicatesAcrossTerms()
    {
        var compound = CompoundProgramBuilder.BuildUnionDistinct(
            [ScanTerm("a", 1, 2, 2), ScanTerm("b", 2, 3)],
            ByteExactRows);

        compound.Program.DistinctSetCount.Should().Be(1);
        Integers(Run(compound)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void BuildUnionDistinctSubstitutesDistinctResultRowForEveryTerm()
    {
        var compound = CompoundProgramBuilder.BuildUnionDistinct(
            [ConstantTerm(1), ConstantTerm(2)],
            ByteExactRows);

        compound.Program.Instructions.OfType<ResultRowInstruction>().Should().BeEmpty();
        compound.Program.Instructions.OfType<DistinctResultRowInstruction>().Should().HaveCount(2);
        compound.Program.Instructions
            .OfType<DistinctResultRowInstruction>()
            .Should().OnlyContain(instruction => instruction.DistinctSetIndex == 0);
    }

    [Test]
    public void BuildUnionDistinctDefersEqualityToTheSuppliedDelegate()
    {
        var terms = new[] { ConstantTerm(SqlValue.Integer(1)), ConstantTerm(SqlValue.Real(1.0)) };

        // Byte-exact equality keeps an integer and a real as distinct rows.
        Run(CompoundProgramBuilder.BuildUnionDistinct(terms, ByteExactRows)).Should().HaveCount(2);

        // Numeric-aware equality collapses them into one.
        var numeric = Run(CompoundProgramBuilder.BuildUnionDistinct(terms, NumericAwareRows));
        numeric.Should().HaveCount(1);
        numeric[0][0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void BuildUnionDistinctReplaysAfterReset()
    {
        var compound = CompoundProgramBuilder.BuildUnionDistinct(
            [ScanTerm("a", 1, 1, 2), ConstantTerm(2)],
            ByteExactRows);
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);

        Integers(Drain(statement)).Should().Equal(1, 2);

        statement.Reset();

        // Reset must clear the distinct set; otherwise the replay would suppress every row as a duplicate.
        Integers(Drain(statement)).Should().Equal(1, 2);
    }

    [Test]
    public void BuildUnionAllPreservesATermsInternalDeduplication()
    {
        // A distinct sub-term (1 UNION 1 -> one row) used as a UNION ALL term keeps its own dedup while
        // the outer UNION ALL performs none: the trailing constant 1 is emitted again.
        var distinct = CompoundProgramBuilder.BuildUnionDistinct(
            [ConstantTerm(1), ConstantTerm(1)],
            ByteExactRows);

        var compound = CompoundProgramBuilder.BuildUnionAll([distinct, ConstantTerm(1)]);

        compound.Program.DistinctSetCount.Should().Be(1);
        Integers(Run(compound)).Should().Equal(1, 1);
    }

    [Test]
    public void CompoundProgramIsRenderableByExplain()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll([ScanTerm("a", 1), ConstantTerm(2)]);

        var rendered = VdbeExplain.Describe(compound.Program);

        rendered.Should().HaveCount(compound.Program.Instructions.Count);
    }

    [Test]
    public void BuildUnionAllRejectsFewerThanTwoTerms()
    {
        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildUnionAll([ConstantTerm(1)]));
    }

    [Test]
    public void BuildUnionAllRejectsANullTermsList()
    {
        Assert.Throws<ArgumentNullException>(
            () => CompoundProgramBuilder.BuildUnionAll(null!));
    }

    [Test]
    public void BuildUnionAllRejectsANullTerm()
    {
        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildUnionAll([ConstantTerm(1), null!]));
    }

    [Test]
    public void BuildUnionAllRejectsMismatchedColumnCounts()
    {
        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildUnionAll(
                [ConstantTerm(SqlValue.Integer(1)), ConstantTerm(SqlValue.Integer(2), SqlValue.Integer(3))]));
    }

    [Test]
    public void BuildRejectsTermsWhoseCursorSourceCountDisagreesWithTheProgram()
    {
        var scan = ScanTerm("a", 1);
        var missingSource = scan with { CursorSources = [] };

        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildUnionAll([missingSource, ConstantTerm(1)]));
    }

    [Test]
    public void BuildRejectsTermsThatEmitNoResultRows()
    {
        VdbeInstruction[] instructions =
        [
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new HaltInstruction(),
        ];
        var noResultRows = new CompoundTerm(new VdbeProgram(1, cursorCount: 0, instructions), []);

        Assert.Throws<ArgumentException>(
            () => CompoundProgramBuilder.BuildUnionAll([noResultRows, ConstantTerm(1)]));
    }

    [Test]
    public void BuildUnionDistinctRejectsANullEquality()
    {
        Assert.Throws<ArgumentNullException>(
            () => CompoundProgramBuilder.BuildUnionDistinct([ConstantTerm(1), ConstantTerm(2)], null!));
    }

    [Test]
    public void BuildUnionDistinctComposesTermsThatAlreadyDeduplicate()
    {
        var alreadyDistinct = CompoundProgramBuilder.BuildUnionDistinct(
            [ConstantTerm(1), ConstantTerm(1)],
            ByteExactRows);

        var nested = CompoundProgramBuilder.BuildUnionDistinct(
            [alreadyDistinct, ConstantTerm(1), ConstantTerm(2)],
            ByteExactRows);

        Integers(Run(nested)).Should().Equal(1, 2);
        nested.Program.Instructions.OfType<GuardedRowInstruction>().Should().NotBeEmpty();
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
