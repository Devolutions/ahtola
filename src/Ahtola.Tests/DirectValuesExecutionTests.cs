using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// End-to-end coverage for programs built by ValuesProgramBuilder, executed through the resumable state
// machine. These tests assert the real emitted rows (multi-row ordering, mixed value kinds, end-of-stream
// and reset/dispose behaviour) and that a VALUES program composes with the shared result-row machinery:
// it sequences under CompoundProgramBuilder (UNION ALL / UNION DISTINCT) and gates under
// LimitOffsetProgramBuilder, mirroring how a SQL router would combine these slices.
public class DirectValuesExecutionTests
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

    [Test]
    public void EmitsEveryRowInOrder()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1), SqlValue.Text("a")],
            [SqlValue.Integer(2), SqlValue.Text("b")],
            [SqlValue.Integer(3), SqlValue.Text("c")]));

        var rows = Run(program);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b"));
        rows[2].Should().Equal(SqlValue.Integer(3), SqlValue.Text("c"));
    }

    [Test]
    public void PreservesEveryValueKindAcrossRows()
    {
        // A VALUES cell is a resolved value regardless of whether it came from a literal or a bound
        // parameter, so all SqlValue kinds must survive the LoadConstant/ResultRow round trip unchanged.
        var blob = new byte[] { 0x01, 0x02, 0xFF };
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(long.MinValue), SqlValue.Real(3.5), SqlValue.Text("π"), SqlValue.Blob(blob), SqlValue.Null]));

        var row = Run(program).Single();

        row[0].Should().Be(SqlValue.Integer(long.MinValue));
        row[1].Should().Be(SqlValue.Real(3.5));
        row[2].Should().Be(SqlValue.Text("π"));
        row[3].Kind.Should().Be(SqlValueKind.Blob);
        row[3].AsBlob().ToArray().Should().Equal(blob);
        row[4].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ReusingRegistersDoesNotCorruptEarlierRows()
    {
        // Every row overwrites r[0], but the interpreter snapshots the row at ResultRow, so drained rows
        // must retain their own values rather than the final register contents.
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(10)],
            [SqlValue.Integer(20)],
            [SqlValue.Integer(30)]));

        Integers(Run(program)).Should().Equal(10, 20, 30);
    }

    [Test]
    public void StreamEndsWithDoneAndNoCurrentRowAfterTheLastRow()
    {
        var program = ValuesProgramBuilder.Build(Rows([SqlValue.Integer(1)], [SqlValue.Integer(2)]));
        using var statement = new ResumableStatement(program);

        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);

        // No further rows: the terminating Halt reports Done and clears the current row, and stepping a
        // finished statement stays Done.
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.State.Should().Be(ResumableStatementState.Done);
        statement.CurrentRow.Should().BeNull();
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void ReplaysTheSameRowsAfterReset()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)],
            [SqlValue.Integer(3)]));
        using var statement = new ResumableStatement(program);

        Integers(DrainRows(statement)).Should().Equal(1, 2, 3);

        statement.Reset();

        Integers(DrainRows(statement)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void DisposePreventsFurtherStepping()
    {
        var program = ValuesProgramBuilder.Build(Rows([SqlValue.Integer(1)]));
        var statement = new ResumableStatement(program);

        statement.Dispose();
        statement.State.Should().Be(ResumableStatementState.Disposed);

        Assert.Throws<ObjectDisposedException>(() => statement.StepResumable());
    }

    [Test]
    public void SequencesTwoValuesTermsWithUnionAll()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ValuesProgramBuilder.BuildTerm(Rows([SqlValue.Integer(1)], [SqlValue.Integer(2)])),
            ValuesProgramBuilder.BuildTerm(Rows([SqlValue.Integer(3)], [SqlValue.Integer(2)])),
        ]);

        // UNION ALL concatenates without de-duplication, preserving the repeated 2.
        Integers(RunCompound(compound)).Should().Equal(1, 2, 3, 2);
    }

    [Test]
    public void SequencesTwoValuesTermsWithUnionDistinct()
    {
        var compound = CompoundProgramBuilder.BuildUnionDistinct(
            [
                ValuesProgramBuilder.BuildTerm(Rows([SqlValue.Integer(1)], [SqlValue.Integer(2)])),
                ValuesProgramBuilder.BuildTerm(Rows([SqlValue.Integer(2)], [SqlValue.Integer(3)])),
            ],
            ByteExactRows);

        // UNION de-duplicates across terms, emitting each distinct row once in arrival order.
        Integers(RunCompound(compound)).Should().Equal(1, 2, 3);
    }

    [Test]
    public void GatesAValuesProgramWithLimitAndOffset()
    {
        var program = ValuesProgramBuilder.Build(Rows(
            [SqlValue.Integer(10)],
            [SqlValue.Integer(20)],
            [SqlValue.Integer(30)],
            [SqlValue.Integer(40)]));

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 1, limit: 2);

        // OFFSET skips 10, LIMIT then emits 20 and 30.
        Integers(Run(gated)).Should().Equal(20, 30);
        Opcodes(gated).Should().Contain(VdbeOpcode.OffsetGate).And.Contain(VdbeOpcode.LimitGate);
    }

    [Test]
    public void GatesAUnionAllOfValuesTermsWithLimitOffset()
    {
        var compound = CompoundProgramBuilder.BuildUnionAll(
        [
            ValuesProgramBuilder.BuildTerm(Rows([SqlValue.Integer(1)], [SqlValue.Integer(2)], [SqlValue.Integer(3)])),
            ValuesProgramBuilder.BuildTerm(Rows([SqlValue.Integer(4)], [SqlValue.Integer(5)])),
        ]);

        var gated = LimitOffsetProgramBuilder.Apply(compound, offset: 1, limit: 3);

        // The shared counters span the concatenated 1,2,3,4,5 stream: skip 1, then take 2,3,4.
        Integers(RunCompound(gated)).Should().Equal(2, 3, 4);
        gated.CursorSources.Should().BeEmpty();
    }

    [Test]
    public void LimitZeroOverAValuesProgramEmitsNoRows()
    {
        var program = ValuesProgramBuilder.Build(Rows([SqlValue.Integer(1)], [SqlValue.Integer(2)]));

        var gated = LimitOffsetProgramBuilder.Apply(program, offset: 0, limit: 0);

        Run(gated).Should().BeEmpty();
    }

    private static IReadOnlyList<IReadOnlyList<SqlValue>> Rows(params SqlValue[][] rows) => rows;

    private static List<VdbeOpcode> Opcodes(VdbeProgram program)
        => program.Instructions.Select(instruction => instruction.Opcode).ToList();

    private static List<long> Integers(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[0].AsInteger()).ToList();

    private static List<SqlValue[]> Run(VdbeProgram program)
    {
        using var statement = new ResumableStatement(program);
        return DrainRows(statement);
    }

    private static List<SqlValue[]> RunCompound(CompoundTerm compound)
    {
        using var statement = new ResumableStatement(compound.Program, compound.CursorSources);
        return DrainRows(statement);
    }

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
            else
                throw new InvalidOperationException($"Unexpected step result {result}.");
        }

        return rows;
    }
}
