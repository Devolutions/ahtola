using Ahtola.Core;
using Ahtola.Core.Execution;
using Ahtola.Core.Indexing;
using Ahtola.Core.Search;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// The appended index-method opcodes (107-115), their program validation and their EXPLAIN
/// rendering. Existing opcode numbers must never move, so the first test pins them.
/// </summary>
public sealed class VdbeIndexMethodOpcodeTests
{
    [Test]
    public void AppendedOpcodeValuesDoNotRenumberExistingOnes()
    {
        ((int)VdbeOpcode.VRollback).Should().Be(106);
        ((int)VdbeOpcode.IndexMethodCreate).Should().Be(107);
        ((int)VdbeOpcode.IndexMethodDestroy).Should().Be(108);
        ((int)VdbeOpcode.IndexMethodOptimize).Should().Be(109);
        ((int)VdbeOpcode.IndexMethodQuery).Should().Be(110);
        ((int)VdbeOpcode.IndexMethodNext).Should().Be(111);
        ((int)VdbeOpcode.IndexMethodColumn).Should().Be(112);
        ((int)VdbeOpcode.IndexMethodRowId).Should().Be(113);
        ((int)VdbeOpcode.IndexMethodInsert).Should().Be(114);
        ((int)VdbeOpcode.IndexMethodDelete).Should().Be(115);
    }

    [Test]
    public void QueryLoopEmitsRankedRowidsAndScores()
    {
        var binding = CreateBinding(
            (1, "the quick brown fox"),
            (2, "a lazy dog"),
            (3, "fox fox fox"));

        VdbeInstruction[] instructions =
        [
            new IndexMethodCreateInstruction(new Cursor(0), binding),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("fox")),
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 3,
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(8)),
            new IndexMethodRowIdInstruction(new Cursor(0), new Register(1)),
            new IndexMethodColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(2)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 2)),
            new IndexMethodNextInstruction(new Cursor(0), new ProgramCounter(3)),
            new GotoInstruction(new ProgramCounter(8)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);

        var rowIds = new List<long>();
        var scores = new List<double>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
        {
            rowIds.Add(statement.CurrentRow![0].AsInteger());
            scores.Add(statement.CurrentRow[1].AsReal());
        }

        rowIds.Should().Equal(3, 1);
        scores[0].Should().BeGreaterThan(scores[1]);
    }

    [Test]
    public void QueryBranchesToTheEmptyTargetWhenNothingMatches()
    {
        var binding = CreateBinding((1, "only tulips here"));

        VdbeInstruction[] instructions =
        [
            new IndexMethodCreateInstruction(new Cursor(0), binding),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("fox")),
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 3,
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(5)),
            new IndexMethodRowIdInstruction(new Cursor(0), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(-1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(-1);
    }

    [Test]
    public void InsertAndDeleteMaintainTheMethodState()
    {
        var binding = CreateBinding((1, "alpha"));

        VdbeInstruction[] instructions =
        [
            new IndexMethodCreateInstruction(new Cursor(0), binding),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("beta gamma")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new IndexMethodInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("beta")),
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 3,
                new RegisterRange(new Register(2), 1),
                new ProgramCounter(9)),
            new IndexMethodRowIdInstruction(new Cursor(0), new Register(3)),
            new ResultRowInstruction(new RegisterRange(new Register(3), 1)),
            new IndexMethodNextInstruction(new Cursor(0), new ProgramCounter(6)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 4, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Row);
        statement.CurrentRow![0].AsInteger().Should().Be(2);
    }

    [Test]
    public void OptimizeAndDestroyAreAcceptedByTheProgramValidator()
    {
        var binding = CreateBinding((1, "alpha"));

        VdbeInstruction[] instructions =
        [
            new IndexMethodOptimizeInstruction(new Cursor(0), binding),
            new IndexMethodDestroyInstruction(new Cursor(1), binding),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 1, cursorCount: 2, instructions);
        using var statement = new ResumableStatement(program);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
    }

    [Test]
    public void ProgramValidationRejectsAnUndeclaredPattern()
    {
        var binding = CreateBinding((1, "alpha"));

        VdbeInstruction[] instructions =
        [
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 99,
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(1)),
            new HaltInstruction(),
        ];

        var act = () => new VdbeProgram(registerCount: 1, cursorCount: 1, instructions);
        act.Should().Throw<VdbeProgramValidationException>().WithMessage("*undeclared index-method pattern 99*");
    }

    [Test]
    public void ExplainRendersEveryIndexMethodOpcode()
    {
        var binding = CreateBinding((1, "alpha"));

        VdbeInstruction[] instructions =
        [
            new IndexMethodCreateInstruction(new Cursor(0), binding),
            new LoadConstantInstruction(new Register(0), SqlValue.Text("alpha")),
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 3,
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(7)),
            new IndexMethodColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(1)),
            new IndexMethodRowIdInstruction(new Cursor(0), new Register(2)),
            new IndexMethodInsertInstruction(new Cursor(0), new RegisterRange(new Register(0), 2)),
            new IndexMethodNextInstruction(new Cursor(0), new ProgramCounter(3)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 3, cursorCount: 1, instructions);
        var rows = VdbeExplain.Describe(program);
        var text = string.Join("\n", rows.Select(static row => $"{row[1].AsText()} {row[6].AsText()}"));

        text.Should().Contain("IndexMethodCreate create fts index t_fts");
        text.Should().Contain("IndexMethodQuery query fts idx=t_fts pattern=3");
        text.Should().Contain("IndexMethodColumn read index-method column 0");
        text.Should().Contain("IndexMethodRowId read index-method rowid");
        text.Should().Contain("IndexMethodInsert index-method insert");
        text.Should().Contain("IndexMethodNext advance index-method cursor 0");
    }

    [Test]
    public void TheVectorMethodDrivesTheSameOpcodesAsFts()
    {
        // The opcodes are method agnostic: a vector index runs create, query, rowid, column and next
        // through exactly the same instructions, with no new opcode number and no vector-specific
        // instruction record.
        var configuration = new ManagedIndexMethodConfiguration(
            "points",
            "points_knn",
            [new ManagedIndexMethodColumn("embedding", 0)],
            [
                new ManagedIndexMethodParameter("dims", SqlValue.Integer(2)),
                new ManagedIndexMethodParameter("lists", SqlValue.Integer(2)),
            ]);
        var attachment = ManagedIndexMethodRegistry.Resolve("vector").Attach(configuration);
        var source = new ArrayManagedIndexSource(
            (1, [Vector("[0,0]")]),
            (2, [Vector("[10,10]")]),
            (3, [Vector("[1,1]")]));
        var binding = new VdbeIndexMethodBinding("vector", "points_knn", attachment, source);

        VdbeInstruction[] instructions =
        [
            new IndexMethodCreateInstruction(new Cursor(0), binding),
            new LoadConstantInstruction(new Register(0), Vector("[0,0]")),
            new LoadConstantInstruction(new Register(1), SqlValue.Integer(2)),
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 0,
                new RegisterRange(new Register(0), 2),
                new ProgramCounter(8)),
            new IndexMethodRowIdInstruction(new Cursor(0), new Register(2)),
            new IndexMethodColumnInstruction(new Cursor(0), ColumnIndex: 0, new Register(3)),
            new ResultRowInstruction(new RegisterRange(new Register(2), 2)),
            new IndexMethodNextInstruction(new Cursor(0), new ProgramCounter(4)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 4, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);

        var rowIds = new List<long>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            rowIds.Add(statement.CurrentRow![0].AsInteger());

        // Rows come back in scan order, and the two nearest points to the origin are 1 and 3.
        rowIds.Should().Equal(1, 3);

        var rows = VdbeExplain.Describe(program);
        var text = string.Join("\n", rows.Select(static row => $"{row[1].AsText()} {row[6].AsText()}"));
        text.Should().Contain("IndexMethodCreate create vector index points_knn");
        text.Should().Contain("IndexMethodQuery query vector idx=points_knn pattern=0");
    }

    [Test]
    public void TheVectorMethodServesItsUnlimitedPatternThroughTheSameOpcodes()
    {
        // The unlimited KNN pattern is only reachable through the opcodes — the planner always
        // prices it out — so this is the one place it runs. It must return every live row rather
        // than overflowing on the unbounded limit it is handed.
        var attachment = ManagedIndexMethodRegistry.Resolve("vector").Attach(new ManagedIndexMethodConfiguration(
            "points",
            "points_knn",
            [new ManagedIndexMethodColumn("embedding", 0)],
            [
                new ManagedIndexMethodParameter("dims", SqlValue.Integer(2)),
                new ManagedIndexMethodParameter("lists", SqlValue.Integer(2)),
            ]));
        var source = new ArrayManagedIndexSource(
            (1, [Vector("[0,0]")]),
            (2, [Vector("[10,10]")]),
            (3, [Vector("[1,1]")]));
        var binding = new VdbeIndexMethodBinding("vector", "points_knn", attachment, source);

        VdbeInstruction[] instructions =
        [
            new IndexMethodCreateInstruction(new Cursor(0), binding),
            new LoadConstantInstruction(new Register(0), Vector("[0,0]")),
            new IndexMethodQueryInstruction(
                new Cursor(0),
                binding,
                PatternIndex: 1,
                new RegisterRange(new Register(0), 1),
                new ProgramCounter(6)),
            new IndexMethodRowIdInstruction(new Cursor(0), new Register(1)),
            new ResultRowInstruction(new RegisterRange(new Register(1), 1)),
            new IndexMethodNextInstruction(new Cursor(0), new ProgramCounter(3)),
            new HaltInstruction(),
        ];

        var program = new VdbeProgram(registerCount: 2, cursorCount: 1, instructions);
        using var statement = new ResumableStatement(program);

        var rowIds = new List<long>();
        while (statement.StepResumable() == ResumableStatementStepResult.Row)
            rowIds.Add(statement.CurrentRow![0].AsInteger());

        rowIds.Should().Equal(1, 2, 3);
    }

    private static SqlValue Vector(string literal)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare($"SELECT vector32('{literal}');");
        statement.Step();
        return statement.GetValue(0);
    }

    private static VdbeIndexMethodBinding CreateBinding(params (long RowId, string Body)[] documents)
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 0)],
            []);
        var attachment = ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);
        var source = ArrayManagedIndexSource.FromText(documents);
        return new VdbeIndexMethodBinding("fts", "t_fts", attachment, source);
    }
}
