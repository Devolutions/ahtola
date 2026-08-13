using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Compiler-output and execution coverage for the nested-loop join lowering. JoinProgramBuilder is
// the reusable INNER / LEFT OUTER lowering; these tests assert its emitted bytecode shape and run
// the programs through the resumable state machine to confirm Cartesian products, equi-predicate
// matching, multi-column result mapping, empty inputs, predicate behavior, reset replay, and the
// LEFT OUTER null-extension state machine. SQL comparison semantics are supplied through the
// VdbeRowPredicate delegate contract, never re-derived here.
public class JoinProgramBuilderTests
{
    // l.<a> == r.<b>, comparing the combined row's columns as integers (NULLs never match).
    private static VdbeRowPredicate CombinedIntegerEquals(int a, int b) => row =>
        row[a].Kind == SqlValueKind.Integer
        && row[b].Kind == SqlValueKind.Integer
        && row[a].AsInteger() == row[b].AsInteger();

    [Test]
    public void BuildInnerEmitsTheNestedLoopPipelineWithoutAPredicate()
    {
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.Inner,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)]);

        program.RegisterCount.Should().Be(4);
        program.CursorCount.Should().Be(2);
        program.SorterCount.Should().Be(0);
        program.AccumulatorCount.Should().Be(0);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.Rewind,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Column,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        // The outer rewind exits the whole program when the left table is empty; the inner rewind
        // skips straight to the outer Next when the right table is empty.
        var outerRewind = (RewindCursorInstruction)program.Instructions[2];
        outerRewind.Cursor.Index.Should().Be(0);
        outerRewind.EmptyTarget.Offset.Should().Be(11);

        var innerRewind = (RewindCursorInstruction)program.Instructions[3];
        innerRewind.Cursor.Index.Should().Be(1);
        innerRewind.EmptyTarget.Offset.Should().Be(10);

        var innerNext = (NextInstruction)program.Instructions[9];
        innerNext.Cursor.Index.Should().Be(1);
        innerNext.LoopTarget.Offset.Should().Be(4);

        var outerNext = (NextInstruction)program.Instructions[10];
        outerNext.Cursor.Index.Should().Be(0);
        outerNext.LoopTarget.Offset.Should().Be(3);
    }

    [Test]
    public void BuildInnerInsertsAFilterRegistersStageWhenGivenAPredicate()
    {
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.Inner,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)],
            predicate: CombinedIntegerEquals(0, 1));

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.Rewind,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Column,
            VdbeOpcode.FilterRegisters,
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        // The join predicate tests the whole combined row and, on false, skips to the inner Next.
        var filter = (FilterRegistersInstruction)program.Instructions[6];
        filter.Row.Start.Index.Should().Be(0);
        filter.Row.Count.Should().Be(2);
        filter.FalseTarget.Offset.Should().Be(10);
        filter.Description.Should().Be("skip pair when join predicate is false, goto 10");
    }

    [Test]
    public void BuildLeftOuterEmitsTheMatchFlagAndNullExtensionPipeline()
    {
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.LeftOuter,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)],
            predicate: CombinedIntegerEquals(0, 1));

        // One extra register beyond the inner pipeline holds the per-outer-row match flag.
        program.RegisterCount.Should().Be(5);

        program.Instructions.Select(instruction => instruction.Opcode).Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.Rewind,
            VdbeOpcode.LoadConstant, // flag = 0 at the top of each outer row
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Column,
            VdbeOpcode.FilterRegisters,
            VdbeOpcode.LoadConstant, // flag = 1 on a matching pair
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.JumpIf, // matched: skip the null-extension block
            VdbeOpcode.Column, // no-match block: re-read outer columns
            VdbeOpcode.LoadConstant, // null-extend the inner columns
            VdbeOpcode.Copy,
            VdbeOpcode.Copy,
            VdbeOpcode.ResultRow,
            VdbeOpcode.Next,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        // The inner rewind lands on the null-extension block so an empty right table still yields
        // one null-extended row per outer row.
        ((RewindCursorInstruction)program.Instructions[4]).EmptyTarget.Offset.Should().Be(14);

        // JumpIf on the match flag jumps to the outer Next, skipping null-extension when matched.
        var jump = (JumpIfInstruction)program.Instructions[13];
        jump.Register.Index.Should().Be(4);
        jump.Target.Offset.Should().Be(19);

        // The null-extension LoadConstant writes NULL into the inner staging column.
        var nullExtend = (LoadConstantInstruction)program.Instructions[15];
        nullExtend.Destination.Index.Should().Be(1);
        nullExtend.Value.Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void InnerJoinWithoutPredicateProducesTheCartesianProduct()
    {
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.Inner,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)]);

        var rows = Run(program, Rows([1], [2]), Rows([10], [20]));

        rows.Select(row => (row[0].AsInteger(), row[1].AsInteger())).Should().Equal(
            (1, 10), (1, 20), (2, 10), (2, 20));
    }

    [Test]
    public void InnerJoinMatchesOnTheEquiPredicate()
    {
        var program = InnerEquiJoin();

        var rows = Run(
            program,
            Rows([1], [2], [3]),
            Rows([1, "a"], [1, "b"], [2, "c"], [4, "d"]));

        // l.id=3 matches nothing and is dropped; multiple right matches fan out.
        rows.Select(row => (row[0].AsInteger(), row[1].AsText())).Should().Equal(
            (1, "a"), (1, "b"), (2, "c"));
    }

    [Test]
    public void InnerJoinProjectsMixedLeftRightColumnsAndConstants()
    {
        // Combined row is [l.id, r.id, r.tag]; project r.tag, l.id, and a constant.
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 2,
            JoinType.Inner,
            projections:
            [
                JoinProjection.ForColumn(2),
                JoinProjection.ForColumn(0),
                JoinProjection.ForConstant(SqlValue.Integer(99)),
            ],
            predicate: CombinedIntegerEquals(0, 1));

        var rows = Run(program, Rows([5], [6]), Rows([6, "x"], [5, "y"]));

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Text("y"), SqlValue.Integer(5), SqlValue.Integer(99));
        rows[1].Should().Equal(SqlValue.Text("x"), SqlValue.Integer(6), SqlValue.Integer(99));
    }

    [Test]
    public void InnerJoinProducesNoRowsWhenTheLeftTableIsEmpty()
    {
        var program = InnerEquiJoin();

        Run(program, Rows(), Rows([1, "a"])).Should().BeEmpty();
    }

    [Test]
    public void InnerJoinProducesNoRowsWhenTheRightTableIsEmpty()
    {
        var program = InnerEquiJoin();

        Run(program, Rows([1], [2]), Rows()).Should().BeEmpty();
    }

    [Test]
    public void InnerJoinProducesNoRowsWhenThePredicateRejectsEveryPair()
    {
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.Inner,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)],
            predicate: _ => false);

        Run(program, Rows([1], [2]), Rows([3], [4])).Should().BeEmpty();
    }

    [Test]
    public void LeftOuterJoinNullExtendsUnmatchedOuterRows()
    {
        var program = LeftOuterEquiJoin();

        var rows = Run(
            program,
            Rows([1], [2], [3]),
            Rows([1, "a"], [1, "b"], [2, "c"], [4, "d"]));

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b"));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Text("c"));
        // l.id=3 matched no right row and is emitted once with the inner column null-extended.
        rows[3][0].Should().Be(SqlValue.Integer(3));
        rows[3][1].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void LeftOuterJoinNullExtendsEveryRowWhenTheRightTableIsEmpty()
    {
        var program = LeftOuterEquiJoin();

        var rows = Run(program, Rows([1], [2]), Rows());

        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Kind.Should().Be(SqlValueKind.Null);
        rows[1][0].Should().Be(SqlValue.Integer(2));
        rows[1][1].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void LeftOuterJoinProducesNoRowsWhenTheLeftTableIsEmpty()
    {
        var program = LeftOuterEquiJoin();

        Run(program, Rows(), Rows([1, "a"])).Should().BeEmpty();
    }

    [Test]
    public void LeftOuterJoinWithoutPredicateIsACrossJoinWhenTheRightTableIsNonEmpty()
    {
        // With no ON predicate every outer row matches every inner row, so no null-extension occurs.
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.LeftOuter,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)]);

        var rows = Run(program, Rows([1], [2]), Rows([10], [20]));

        rows.Select(row => (row[0].AsInteger(), row[1].AsInteger())).Should().Equal(
            (1, 10), (1, 20), (2, 10), (2, 20));
    }

    [Test]
    public void InnerJoinReplaysAfterReset()
    {
        var program = InnerEquiJoin();
        using var statement = new ResumableStatement(
            program,
            [Rows([1], [2]), Rows([1, "a"], [2, "b"])]);

        Drain(statement).Select(row => (row[0].AsInteger(), row[1].AsText())).Should().Equal((1, "a"), (2, "b"));

        statement.Reset();

        Drain(statement).Select(row => (row[0].AsInteger(), row[1].AsText())).Should().Equal((1, "a"), (2, "b"));
    }

    [Test]
    public void LeftOuterJoinReplaysAfterReset()
    {
        var program = LeftOuterEquiJoin();
        using var statement = new ResumableStatement(
            program,
            [Rows([1], [9]), Rows([1, "a"])]);

        var first = Drain(statement);
        first.Should().HaveCount(2);
        first[1][1].Kind.Should().Be(SqlValueKind.Null);

        statement.Reset();

        var second = Drain(statement);
        second.Should().HaveCount(2);
        second[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("a"));
        second[1][0].Should().Be(SqlValue.Integer(9));
        second[1][1].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void LeftOuterJoinAppliesPostJoinPredicateAfterNullExtension()
    {
        var program = JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 1,
            JoinType.LeftOuter,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(1)],
            predicate: CombinedIntegerEquals(0, 1),
            postJoinPredicate: row => row[1].Kind == SqlValueKind.Null);

        var rows = Run(program, Rows([1], [2]), Rows([1], [9]));

        // The matched (1,1) pair fails the post-join filter, but it must still set the match flag
        // before filtering so no null-extended (1,NULL) row is fabricated. Only unmatched 2 survives.
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(2));
        rows[0][1].Kind.Should().Be(SqlValueKind.Null);

        var postJoinFilters = program.Instructions
            .OfType<FilterRegistersInstruction>()
            .Where(filter => filter.Description.StartsWith("skip result when post-join WHERE is false"))
            .ToArray();
        postJoinFilters.Should().HaveCount(2);
    }

    [Test]
    public void BuildValidatesItsArguments()
    {
        JoinProjection[] projection = [JoinProjection.ForColumn(0)];

        Assert.Throws<ArgumentNullException>(() => JoinProgramBuilder.Build(
            null!, 1, "r", 1, JoinType.Inner, projection));

        Assert.Throws<ArgumentNullException>(() => JoinProgramBuilder.Build(
            "l", 1, null!, 1, JoinType.Inner, projection));

        Assert.Throws<ArgumentNullException>(() => JoinProgramBuilder.Build(
            "l", 1, "r", 1, JoinType.Inner, null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => JoinProgramBuilder.Build(
            "l", 0, "r", 1, JoinType.Inner, projection));

        Assert.Throws<ArgumentOutOfRangeException>(() => JoinProgramBuilder.Build(
            "l", 1, "r", 0, JoinType.Inner, projection));

        Assert.Throws<ArgumentException>(() => JoinProgramBuilder.Build(
            "l", 1, "r", 1, JoinType.Inner, []));

        // Combined column ordinal beyond the joined row width (1 + 1 = 2 columns, index 2 invalid).
        Assert.Throws<ArgumentException>(() => JoinProgramBuilder.Build(
            "l", 1, "r", 1, JoinType.Inner, [JoinProjection.ForColumn(2)]));

        Assert.Throws<ArgumentOutOfRangeException>(() => JoinProgramBuilder.Build(
            "l", 1, "r", 1, (JoinType)999, projection));
    }

    [Test]
    public void JoinProjectionRejectsNegativeColumnOrdinals()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JoinProjection.ForColumn(-1));
    }

    private static VdbeProgram InnerEquiJoin() =>
        JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 2,
            JoinType.Inner,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(2)],
            predicate: CombinedIntegerEquals(0, 1));

    private static VdbeProgram LeftOuterEquiJoin() =>
        JoinProgramBuilder.Build(
            "l",
            leftColumnCount: 1,
            "r",
            rightColumnCount: 2,
            JoinType.LeftOuter,
            projections: [JoinProjection.ForColumn(0), JoinProjection.ForColumn(2)],
            predicate: CombinedIntegerEquals(0, 1));

    private static VdbeCursorSource Rows(params object?[][] rows)
    {
        var materialized = new List<SqlValue[]>(rows.Length);
        foreach (var row in rows)
        {
            var values = new SqlValue[row.Length];
            for (var column = 0; column < row.Length; column++)
            {
                values[column] = row[column] switch
                {
                    null => SqlValue.Null,
                    int integer => SqlValue.Integer(integer),
                    long integer => SqlValue.Integer(integer),
                    string text => SqlValue.Text(text),
                    _ => throw new InvalidOperationException($"Unsupported cell type {row[column]!.GetType()}."),
                };
            }

            materialized.Add(values);
        }

        return new VdbeCursorSource(materialized);
    }

    private static List<SqlValue[]> Run(VdbeProgram program, VdbeCursorSource left, VdbeCursorSource right)
    {
        using var statement = new ResumableStatement(program, [left, right]);
        return Drain(statement);
    }

    private static List<SqlValue[]> Drain(ResumableStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (true)
        {
            var result = statement.StepResumable();
            if (result == ResumableStatementStepResult.Row)
            {
                rows.Add([.. statement.CurrentRow!]);
            }
            else if (result == ResumableStatementStepResult.Done)
            {
                break;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected step result {result}.");
            }
        }

        return rows;
    }
}
