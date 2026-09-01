using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Direct coverage for the schema-aware VDBE runtime substrate: the upstream-backed DDL prerequisite
/// opcodes, their program validation, their <c>EXPLAIN</c> rendering, and their execution against an
/// internal <see cref="VdbeSchemaExecutionContext"/>. No SQL DDL is routed through these opcodes yet, so
/// every case here drives the interpreter directly.
/// </summary>
public sealed class VdbeSchemaRuntimeTests
{
    /// <summary>
    /// A recording <see cref="IVdbeSchemaOperations"/>. It performs no catalog work — that lands with the
    /// transaction-local schema rows — but it answers every operation deterministically so opcode
    /// semantics can be asserted exactly.
    /// </summary>
    private sealed class RecordingSchemaOperations : IVdbeSchemaOperations
    {
        private long _nextRootPage = 2;

        public List<string> Calls { get; } = [];

        public Dictionary<(int Database, VdbeSchemaCookie Cookie), long> Cookies { get; } = [];

        public long MovedRootPage { get; set; }

        public Func<string, Exception?>? FailOn { get; set; }

        public long CreateBtree(int database, VdbeCreateBtreeFlags flags)
        {
            Record($"CreateBtree({database},{flags})");
            return _nextRootPage++;
        }

        public void ClearBtree(int database, long rootPage) => Record($"ClearBtree({database},{rootPage})");

        public long Destroy(int database, long rootPage, bool isTemporary)
        {
            Record($"Destroy({database},{rootPage},{isTemporary})");
            return MovedRootPage;
        }

        public long ReadCookie(int database, VdbeSchemaCookie cookie)
        {
            Record($"ReadCookie({database},{cookie})");
            return Cookies.TryGetValue((database, cookie), out var value) ? value : 0;
        }

        public void SetCookie(int database, VdbeSchemaCookie cookie, long value)
        {
            Record($"SetCookie({database},{cookie},{value})");
            Cookies[(database, cookie)] = value;
        }

        public void ParseSchema(int database, string? whereClause, int? triggerTargetDatabase)
            => Record($"ParseSchema({database},{whereClause ?? "NULL"},{triggerTargetDatabase?.ToString() ?? "NULL"})");

        public void BuildIndex(int database, string tableName, string indexName, bool unique)
            => Record($"IndexBuild({database},{tableName},{indexName},{(unique ? "unique" : "non-unique")})");

        public void DropObject(int database, VdbeSchemaObjectKind kind, string name)
            => Record($"DropObject({database},{kind},{name})");

        public void RenameTable(int database, string from, string to)
            => Record($"RenameTable({database},{from},{to})");

        public void AddColumn(
            int database,
            string table,
            string columnName,
            string columnDefinition,
            string? columnSql)
            => Record($"AddColumn({database},{table},{columnName},{columnDefinition})");

        public void DropColumn(int database, string table, int columnIndex)
            => Record($"DropColumn({database},{table},{columnIndex})");

        public void AlterColumn(
            int database,
            string table,
            int columnIndex,
            string columnDefinition,
            bool rename,
            bool quoteNewName)
            => Record($"AlterColumn({database},{table},{columnIndex},{columnDefinition},{rename})");

        private void Record(string call)
        {
            Calls.Add(call);
            if (FailOn?.Invoke(call) is { } failure)
                throw failure;
        }
    }

    private static VdbeProgram Program(params VdbeInstruction[] instructions)
        => new(registerCount: 8, cursorCount: 1, [.. instructions, new HaltInstruction()]);

    private static ResumableStatement Statement(
        VdbeProgram program,
        VdbeSchemaExecutionContext context,
        IReadOnlyList<VdbeCursorSource?>? cursorSources = null)
        => ResumableStatement.CreateWithSchemaContext(program, context, cursorSources);

    private static VdbeSchemaExecutionContext Context(
        RecordingSchemaOperations operations,
        int databaseCount = 1,
        bool isReadOnly = false)
        => new(operations, databaseCount, isReadOnly);

    // ---------------------------------------------------------------- opcode numbering

    [Test]
    public void DdlPrerequisiteOpcodesAreAppendedAfterTheExistingBytecodeTail()
    {
        ((int)VdbeOpcode.VRename).Should().Be(118);
        ((int)VdbeOpcode.MakeRecord).Should().Be(119);
        ((int)VdbeOpcode.NewRowid).Should().Be(120);
        ((int)VdbeOpcode.CreateBtree).Should().Be(121);
        ((int)VdbeOpcode.ClearBtree).Should().Be(122);
        ((int)VdbeOpcode.Destroy).Should().Be(123);
        ((int)VdbeOpcode.ReadCookie).Should().Be(124);
        ((int)VdbeOpcode.SetCookie).Should().Be(125);
        ((int)VdbeOpcode.ParseSchema).Should().Be(126);
        ((int)VdbeOpcode.DropTable).Should().Be(127);
        ((int)VdbeOpcode.DropView).Should().Be(128);
        ((int)VdbeOpcode.DropIndex).Should().Be(129);
        ((int)VdbeOpcode.DropTrigger).Should().Be(130);
        ((int)VdbeOpcode.RenameTable).Should().Be(131);
        ((int)VdbeOpcode.AddColumn).Should().Be(132);
        ((int)VdbeOpcode.DropColumn).Should().Be(133);
        ((int)VdbeOpcode.AlterColumn).Should().Be(134);
        ((int)VdbeOpcode.IndexBuild).Should().Be(135);

        Enum.GetValues<VdbeOpcode>().Max(static opcode => (int)opcode).Should().Be(135);
    }

    [Test]
    public void CookieAndBtreeFlagValuesMatchTheUpstreamNumbering()
    {
        ((int)VdbeSchemaCookie.SchemaVersion).Should().Be(1);
        ((int)VdbeSchemaCookie.DatabaseFormat).Should().Be(2);
        ((int)VdbeSchemaCookie.DefaultPageCacheSize).Should().Be(3);
        ((int)VdbeSchemaCookie.LargestRootPageNumber).Should().Be(4);
        ((int)VdbeSchemaCookie.DatabaseTextEncoding).Should().Be(5);
        ((int)VdbeSchemaCookie.UserVersion).Should().Be(6);
        ((int)VdbeSchemaCookie.IncrementalVacuum).Should().Be(7);
        ((int)VdbeSchemaCookie.ApplicationId).Should().Be(8);

        ((int)VdbeCreateBtreeFlags.Table).Should().Be(0b0001);
        ((int)VdbeCreateBtreeFlags.Index).Should().Be(0b0010);
    }

    // ---------------------------------------------------------------- MakeRecord

    [Test]
    public void MakeRecordPacksARegisterBlockIntoARecordRegisterWithoutFakingAScalar()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(7)),
            new LoadConstantInstruction(new Register(1), SqlValue.Text("hello")),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 2), new Register(3)));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations));

        statement.Step().Should().Be(StatementStepResult.Done);

        var record = statement.GetRecordRegister(new Register(3));
        record.Should().NotBeNull();
        record!.Count.Should().Be(2);
        record[0].Should().Be(SqlValue.Integer(7));
        record[1].Should().Be(SqlValue.Text("hello"));
        record.ToArray().Should().Equal(SqlValue.Integer(7), SqlValue.Text("hello"));

        // The scalar view of a record register is NULL: no fabricated blob, no new SqlValueKind.
        statement.GetRegister(new Register(3)).Should().Be(SqlValue.Null);
        statement.GetRegister(new Register(3)).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void MakeRecordRefusesToPackARegisterThatAlreadyHoldsARecord()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(2)),
            new MakeRecordInstruction(new RegisterRange(new Register(2), 1), new Register(4)));
        using var statement = Statement(program, Context(new RecordingSchemaOperations()));

        Action step = () => statement.Step();

        step.Should().Throw<InvalidOperationException>()
            .WithMessage("MakeRecord cannot pack register 2, which holds a record rather than a scalar.");
        statement.State.Should().Be(ResumableStatementState.Faulted);
    }

    [Test]
    public void MakeRecordRejectsADestinationThatOverlapsItsSourceRange()
    {
        Action build = () => Program(
            new MakeRecordInstruction(new RegisterRange(new Register(1), 3), new Register(2)));

        build.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*writes its record to register 2, which overlaps its source range r[1..3]*");
    }

    [Test]
    public void MakeRecordRejectsAnEmptyRangeAndAnEmptyIndexName()
    {
        Action emptyRange = () => Program(
            new MakeRecordInstruction(new RegisterRange(new Register(0), 0), new Register(3)));
        Action emptyIndexName = () => Program(
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(3), string.Empty));

        emptyRange.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*builds a record from an empty register range*");
        emptyIndexName.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*empty MakeRecord index name*");
    }

    [Test]
    public void CopyMovesARecordAndAScalarWriteInvalidatesIt()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(5)),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(2)),
            new CopyInstruction(new Register(2), new Register(3)),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("scalar")));
        using var statement = Statement(program, Context(new RecordingSchemaOperations()));

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRecordRegister(new Register(3)).Should().NotBeNull();
        statement.GetRecordRegister(new Register(3))![0].Should().Be(SqlValue.Integer(5));
        // Overwriting the source register with a scalar drops its record rather than leaving a stale tuple.
        statement.GetRecordRegister(new Register(2)).Should().BeNull();
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Text("scalar"));
    }

    [Test]
    public void RollbackToSavepointRestoresRecordRegistersAlongsideScalars()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(11)),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(2)),
            new SavepointInstruction("staged"),
            new LoadConstantInstruction(new Register(2), SqlValue.Text("clobbered")),
            new RollbackToSavepointInstruction("staged"));
        using var statement = Statement(program, Context(new RecordingSchemaOperations()));

        statement.Step().Should().Be(StatementStepResult.Done);

        var record = statement.GetRecordRegister(new Register(2));
        record.Should().NotBeNull();
        record![0].Should().Be(SqlValue.Integer(11));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Null);
    }

    [Test]
    public void RollbackTransactionRestoresARecordRegisterCreatedInsideTheTransaction()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(3)),
            new BeginTransactionInstruction(),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(2)),
            new RollbackTransactionInstruction());
        using var statement = Statement(program, Context(new RecordingSchemaOperations()));

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRecordRegister(new Register(2)).Should().BeNull();
    }

    [Test]
    public void ResetClearsRecordRegisters()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(9)),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(2)));
        using var statement = Statement(program, Context(new RecordingSchemaOperations()));

        statement.Step().Should().Be(StatementStepResult.Done);
        statement.GetRecordRegister(new Register(2)).Should().NotBeNull();

        statement.Reset();

        statement.GetRecordRegister(new Register(2)).Should().BeNull();
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Null);
    }

    [Test]
    public void MakeRecordExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new MakeRecordInstruction(new RegisterRange(new Register(2), 3), new Register(6)))
            .Should().Be((2L, 3L, 6L, null, "r[6]=mkrec(r[2..4])"));
        VdbeExplain.Describe(new MakeRecordInstruction(new RegisterRange(new Register(2), 3), new Register(6), "by_value"))
            .Should().Be((2L, 3L, 6L, "by_value", "r[6]=mkrec(r[2..4]); for by_value"));
    }

    // ---------------------------------------------------------------- NewRowid

    [Test]
    public void NewRowidAllocatesOnePastTheLargestExistingRowid()
    {
        var program = Program(
            new OpenReadCursorInstruction(new Cursor(0), "entries", 1),
            new NewRowidInstruction(new Cursor(0), new Register(1), new Register(2)));
        var source = new VdbeCursorSource(
            [[SqlValue.Integer(10)], [SqlValue.Integer(20)]],
            [4L, 9L]);
        using var statement = Statement(program, Context(new RecordingSchemaOperations()), [source]);

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(10));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(9));
    }

    [Test]
    public void NewRowidStartsAtOneForAnEmptyCursor()
    {
        var program = Program(
            new OpenReadCursorInstruction(new Cursor(0), "entries", 1),
            new NewRowidInstruction(new Cursor(0), new Register(1), new Register(2)));
        var source = new VdbeCursorSource([], []);
        using var statement = Statement(program, Context(new RecordingSchemaOperations()), [source]);

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(1));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void NewRowidAdvancesTheLargestNegativeRowidWithoutClampingToZero()
    {
        var program = Program(
            new OpenReadCursorInstruction(new Cursor(0), "entries", 1),
            new NewRowidInstruction(new Cursor(0), new Register(1), new Register(2)));
        var source = new VdbeCursorSource(
            [[SqlValue.Integer(10)], [SqlValue.Integer(20)]],
            [-7L, -3L]);
        using var statement = Statement(program, Context(new RecordingSchemaOperations()), [source]);

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(-2));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(-3));
    }

    [Test]
    public void NewRowidUsesAnUnusedPositiveRandomRowidAfterTheMaximum()
    {
        var program = Program(
            new OpenReadCursorInstruction(new Cursor(0), "entries", 1),
            new NewRowidInstruction(new Cursor(0), new Register(1), new Register(2)));
        var source = new VdbeCursorSource([[SqlValue.Integer(10)]], [long.MaxValue]);
        using var statement = Statement(program, Context(new RecordingSchemaOperations()), [source]);

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRegister(new Register(1)).AsInteger().Should().BeInRange(1, long.MaxValue >> 1);
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(long.MaxValue));
    }

    [Test]
    public void NewRowidFailsOnAValueOnlyCursorInsteadOfInventingAZero()
    {
        var program = Program(
            new OpenReadCursorInstruction(new Cursor(0), "entries", 1),
            new NewRowidInstruction(new Cursor(0), new Register(1)));
        var source = new VdbeCursorSource([[SqlValue.Integer(1)]]);
        using var statement = Statement(program, Context(new RecordingSchemaOperations()), [source]);

        Action step = () => statement.Step();

        step.Should().Throw<InvalidOperationException>()
            .WithMessage("NewRowid requires cursor 0 to expose rowids, but its source is value-only.");
    }

    [Test]
    public void NewRowidRejectsAnUnopenedCursorAndAnAliasedDestination()
    {
        Action unopened = () => Program(new NewRowidInstruction(new Cursor(0), new Register(1)));
        Action aliased = () => Program(
            new OpenReadCursorInstruction(new Cursor(0), "entries", 1),
            new NewRowidInstruction(new Cursor(0), new Register(1), new Register(1)));

        unopened.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*uses cursor 0 before opening it*");
        aliased.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*writes the new and previous largest rowid to the same register 1*");
    }

    [Test]
    public void NewRowidExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new NewRowidInstruction(new Cursor(3), new Register(5), new Register(6)))
            .Should().Be((3L, 5L, 6L, null, "r[5]=rowid"));
        VdbeExplain.Describe(new NewRowidInstruction(new Cursor(3), new Register(5)))
            .Should().Be((3L, 5L, 0L, null, "r[5]=rowid"));
    }

    // ---------------------------------------------------------------- root pages

    [Test]
    public void CreateBtreeWritesTheAllocatedRootAndStagesItOnTheContext()
    {
        var program = Program(
            new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table),
            new CreateBtreeInstruction(0, new Register(2), VdbeCreateBtreeFlags.Index));
        var operations = new RecordingSchemaOperations();
        var context = Context(operations);
        using var statement = Statement(program, context);

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(2));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(3));
        operations.Calls.Should().Equal("CreateBtree(0,Table)", "CreateBtree(0,Index)");
        context.ReservedRootPages.Should().Equal(2L, 3L);
    }

    [Test]
    public void ResettingTheOwningStatementDiscardsTheStagedRootReservations()
    {
        var program = Program(new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table));
        var context = Context(new RecordingSchemaOperations());
        using var statement = Statement(program, context);

        statement.Step().Should().Be(StatementStepResult.Done);
        context.ReservedRootPages.Should().Equal(2L);

        statement.Reset();

        context.ReservedRootPages.Should().BeEmpty();
        context.ReclaimedRootPages.Should().BeEmpty();
    }

    [Test]
    public void DisposingTheOwningStatementDiscardsTheStagedRootReservations()
    {
        var context = Context(new RecordingSchemaOperations());
        var statement = Statement(
            Program(new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table)),
            context);

        statement.Step().Should().Be(StatementStepResult.Done);
        context.ReservedRootPages.Should().Equal(2L);

        statement.Dispose();

        context.ReservedRootPages.Should().BeEmpty();
        context.ReclaimedRootPages.Should().BeEmpty();
    }

    [Test]
    public void CreateBtreeRejectsAnUnsetOrCombinedFlagWord()
    {
        Action none = () => Program(new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.None));
        Action both = () => Program(new CreateBtreeInstruction(
            0,
            new Register(1),
            VdbeCreateBtreeFlags.Table | VdbeCreateBtreeFlags.Index));

        none.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*must create exactly one of a table or index b-tree*");
        both.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*must create exactly one of a table or index b-tree*");
    }

    [Test]
    public void ClearBtreeAndDestroyForwardTheirRootsAndDestroyReportsTheMovedRoot()
    {
        var program = Program(
            new ClearBtreeInstruction(0, 4),
            new DestroyInstruction(0, 7, new Register(1)),
            new DestroyInstruction(0, 8, new Register(2), IsTemporary: true));
        var operations = new RecordingSchemaOperations { MovedRootPage = 12 };
        var context = Context(operations);
        using var statement = Statement(program, context);

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal("ClearBtree(0,4)", "Destroy(0,7,False)", "Destroy(0,8,True)");
        statement.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(12));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(12));
        context.ReclaimedRootPages.Should().Equal(7L, 8L);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(-3)]
    public void ClearBtreeAndDestroyRejectNonAllocatableRootPages(long rootPage)
    {
        Action clear = () => Program(new ClearBtreeInstruction(0, rootPage));
        Action destroy = () => Program(new DestroyInstruction(0, rootPage, new Register(1)));

        clear.Should().Throw<VdbeProgramValidationException>()
            .WithMessage($"*addresses root page {rootPage}, which is not an allocatable b-tree root*");
        destroy.Should().Throw<VdbeProgramValidationException>()
            .WithMessage($"*addresses root page {rootPage}, which is not an allocatable b-tree root*");
    }

    [Test]
    public void CreateBtreeFailsWhenTheOperationsHandOutAReservedPage()
    {
        var program = Program(new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table));
        var context = new VdbeSchemaExecutionContext(new HeaderPageAllocator());
        using var statement = Statement(program, context);

        Action step = () => statement.Step();

        step.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("CreateBtree allocated root page 1, which is not an allocatable b-tree root.");
    }

    [Test]
    public void RootPageOpcodeExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new CreateBtreeInstruction(1, new Register(4), VdbeCreateBtreeFlags.Index))
            .Should().Be((1L, 4L, 2L, null, "r[4]=root iDb=1 flags=2"));
        VdbeExplain.Describe(new ClearBtreeInstruction(1, 9))
            .Should().Be((9L, 1L, 0L, null, "root=9 iDb=1"));
        VdbeExplain.Describe(new DestroyInstruction(1, 9, new Register(5), IsTemporary: true))
            .Should().Be((9L, 5L, 1L, null, "root=9 iDb=1 former_root=5 is_temp=1"));
        // A root discovered at run time is reported as the register that carries it, so EXPLAIN never
        // claims a page number the program does not have.
        VdbeExplain.Describe(new DestroyInstruction(1, 0, new Register(5), IsTemporary: false, new Register(6)))
            .Should().Be((6L, 5L, 0L, null, "root=r[6] iDb=1 former_root=5 is_temp=0"));
    }

    // ---------------------------------------------------------------- cookies

    [Test]
    public void SetCookieStagesAValueThatReadCookieObserves()
    {
        var program = Program(
            new SetCookieInstruction(0, VdbeSchemaCookie.SchemaVersion, 42),
            new ReadCookieInstruction(0, new Register(1), VdbeSchemaCookie.SchemaVersion),
            new ReadCookieInstruction(0, new Register(2), VdbeSchemaCookie.UserVersion));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations));

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(42));
        statement.GetRegister(new Register(2)).Should().Be(SqlValue.Integer(0));
        operations.Calls.Should().Equal(
            "SetCookie(0,SchemaVersion,42)",
            "ReadCookie(0,SchemaVersion)",
            "ReadCookie(0,UserVersion)");
    }

    [Test]
    public void CookieOpcodesRejectAnUndefinedCookieNumberAndANegativeFlagWord()
    {
        Action read = () => Program(new ReadCookieInstruction(0, new Register(1), (VdbeSchemaCookie)99));
        Action set = () => Program(new SetCookieInstruction(0, (VdbeSchemaCookie)0, 1));
        Action negativeP5 = () => Program(
            new SetCookieInstruction(0, VdbeSchemaCookie.SchemaVersion, 1, P5: -1));

        read.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*addresses an undefined header cookie 99*");
        set.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*addresses an undefined header cookie 0*");
        negativeP5.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*negative SetCookie P5 flag word*");
    }

    [Test]
    public void ReadCookieIsAllowedOnAReadOnlyContextButSetCookieIsNot()
    {
        var operations = new RecordingSchemaOperations();
        operations.Cookies[(0, VdbeSchemaCookie.UserVersion)] = 17;
        using var read = Statement(
            Program(new ReadCookieInstruction(0, new Register(1), VdbeSchemaCookie.UserVersion)),
            Context(operations, isReadOnly: true));
        using var write = Statement(
            Program(new SetCookieInstruction(0, VdbeSchemaCookie.UserVersion, 18)),
            Context(operations, isReadOnly: true));

        read.Step().Should().Be(StatementStepResult.Done);
        read.GetRegister(new Register(1)).Should().Be(SqlValue.Integer(17));

        Action step = () => write.Step();
        step.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("SetCookie cannot run against a read-only schema context.");
    }

    [Test]
    public void CookieOpcodeExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new ReadCookieInstruction(1, new Register(4), VdbeSchemaCookie.SchemaVersion))
            .Should().Be((1L, 4L, 1L, null, "r[4]=cookie[1] iDb=1"));
        VdbeExplain.Describe(new SetCookieInstruction(1, VdbeSchemaCookie.UserVersion, 12, P5: 3))
            .Should().Be((1L, 6L, 12L, null, "cookie[6]=12 iDb=1 p5=3"));
    }

    // ---------------------------------------------------------------- ParseSchema

    [Test]
    public void ParseSchemaForwardsItsClauseAndTriggerTargetDatabase()
    {
        var program = Program(
            new ParseSchemaInstruction(0),
            new ParseSchemaInstruction(1, "tbl_name='entries'"),
            new ParseSchemaInstruction(1, "type='trigger'", TriggerTargetDatabase: 0));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations, databaseCount: 2));

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal(
            "ParseSchema(0,NULL,NULL)",
            "ParseSchema(1,tbl_name='entries',NULL)",
            "ParseSchema(1,type='trigger',0)");
    }

    [Test]
    public void ParseSchemaRejectsABlankClauseAndANegativeTriggerTargetDatabase()
    {
        Action blank = () => Program(new ParseSchemaInstruction(0, "   "));
        Action negativeTarget = () => Program(new ParseSchemaInstruction(0, "type='trigger'", -1));

        blank.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*blank ParseSchema where clause; use null to reparse the whole schema*");
        negativeTarget.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*addresses a negative database index -1*");
    }

    [Test]
    public void ParseSchemaExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new ParseSchemaInstruction(1, "tbl_name='entries'"))
            .Should().Be((1L, 0L, 0L, "tbl_name='entries'", "tbl_name='entries'"));
        VdbeExplain.Describe(new ParseSchemaInstruction(0))
            .Should().Be((0L, 0L, 0L, "NULL", "NULL"));
    }

    // ---------------------------------------------------------------- Drop opcodes

    [Test]
    public void DropOpcodesEvictTheirOwnObjectKind()
    {
        var program = Program(
            new DropTableInstruction(0, "entries"),
            new DropViewInstruction(0, "entries_view"),
            new DropIndexInstruction(0, "entries_value"),
            new DropTriggerInstruction(0, "entries_audit"));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations));

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal(
            "DropObject(0,Table,entries)",
            "DropObject(0,View,entries_view)",
            "DropObject(0,Index,entries_value)",
            "DropObject(0,Trigger,entries_audit)");
    }

    [Test]
    public void DropOpcodesRejectBlankNames()
    {
        Action table = () => Program(new DropTableInstruction(0, "  "));
        Action view = () => Program(new DropViewInstruction(0, ""));
        Action index = () => Program(new DropIndexInstruction(0, null!));
        Action trigger = () => Program(new DropTriggerInstruction(0, "\t"));

        table.Should().Throw<VdbeProgramValidationException>().WithMessage("*blank DropTable name*");
        view.Should().Throw<VdbeProgramValidationException>().WithMessage("*blank DropView name*");
        index.Should().Throw<VdbeProgramValidationException>().WithMessage("*blank DropIndex name*");
        trigger.Should().Throw<VdbeProgramValidationException>().WithMessage("*blank DropTrigger name*");
    }

    [Test]
    public void DropOpcodeExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new DropTableInstruction(1, "entries"))
            .Should().Be((1L, 0L, 0L, "entries", "DROP TABLE entries"));
        VdbeExplain.Describe(new DropViewInstruction(1, "entries_view"))
            .Should().Be((1L, 0L, 0L, "entries_view", "DROP VIEW entries_view"));
        // Upstream renders DropIndex with a zeroed P1, so the database index is not reported.
        VdbeExplain.Describe(new DropIndexInstruction(1, "entries_value"))
            .Should().Be((0L, 0L, 0L, "entries_value", "DROP INDEX entries_value"));
        VdbeExplain.Describe(new DropTriggerInstruction(1, "entries_audit"))
            .Should().Be((1L, 0L, 0L, "entries_audit", "DROP TRIGGER entries_audit"));
    }

    // ---------------------------------------------------------------- ALTER opcodes

    [Test]
    public void AlterOpcodesForwardTheirSchemaRewrites()
    {
        var program = Program(
            new RenameTableInstruction(0, "entries", "records"),
            new AddColumnInstruction(0, "records", "note", "note TEXT DEFAULT 'x'"),
            new DropColumnInstruction(0, "records", 2),
            new AlterColumnInstruction(0, "records", 1, "value INTEGER", Rename: true));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations));

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal(
            "RenameTable(0,entries,records)",
            "AddColumn(0,records,note,note TEXT DEFAULT 'x')",
            "DropColumn(0,records,2)",
            "AlterColumn(0,records,1,value INTEGER,True)");
    }

    [Test]
    public void AlterOpcodesRejectBlankNamesAndNegativeColumnIndexes()
    {
        Action rename = () => Program(new RenameTableInstruction(0, "entries", " "));
        Action add = () => Program(new AddColumnInstruction(0, "entries", "note", "  "));
        Action drop = () => Program(new DropColumnInstruction(0, "entries", -1));
        Action alter = () => Program(new AlterColumnInstruction(0, "entries", -2, "value INTEGER"));

        rename.Should().Throw<VdbeProgramValidationException>().WithMessage("*blank RenameTable name*");
        add.Should().Throw<VdbeProgramValidationException>().WithMessage("*blank AddColumn name*");
        drop.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*addresses a negative column index -1*");
        alter.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*addresses a negative column index -2*");
    }

    [Test]
    public void AlterOpcodeExplainMatchesTheUpstreamShape()
    {
        VdbeExplain.Describe(new RenameTableInstruction(0, "entries", "records"))
            .Should().Be((0L, 0L, 0L, null, "rename_table(entries, records)"));
        VdbeExplain.Describe(new AddColumnInstruction(0, "records", "note", "note TEXT"))
            .Should().Be((0L, 0L, 0L, null, "add_column(records, note TEXT)"));
        VdbeExplain.Describe(new DropColumnInstruction(0, "records", 2))
            .Should().Be((0L, 0L, 0L, null, "drop_column(records, 2)"));
        VdbeExplain.Describe(new AlterColumnInstruction(0, "records", 1, "value INTEGER", Rename: true))
            .Should().Be((0L, 0L, 0L, null, "alter_column(records, 1, value INTEGER, true)"));
    }

    // ---------------------------------------------------------------- context binding

    [Test]
    public void EveryDatabaseOwnedOpcodeFailsExplicitlyWithoutASchemaContext()
    {
        var instructions = new VdbeInstruction[]
        {
            new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table),
            new ClearBtreeInstruction(0, 4),
            new DestroyInstruction(0, 4, new Register(1)),
            new ReadCookieInstruction(0, new Register(1), VdbeSchemaCookie.SchemaVersion),
            new SetCookieInstruction(0, VdbeSchemaCookie.SchemaVersion, 1),
            new ParseSchemaInstruction(0),
            new DropTableInstruction(0, "entries"),
            new DropViewInstruction(0, "entries_view"),
            new DropIndexInstruction(0, "entries_value"),
            new DropTriggerInstruction(0, "entries_audit"),
            new RenameTableInstruction(0, "entries", "records"),
            new AddColumnInstruction(0, "entries", "note", "note TEXT"),
            new DropColumnInstruction(0, "entries", 0),
            new AlterColumnInstruction(0, "entries", 0, "id INTEGER"),
        };

        foreach (var instruction in instructions)
        {
            // The public constructor cannot supply a schema context, so these programs are exactly what a
            // caller outside the engine can build.
            using var statement = new ResumableStatement(Program(instruction));
            Action step = () => statement.Step();

            step.Should().Throw<VdbeSchemaExecutionException>()
                .WithMessage($"{instruction.Opcode} requires a schema execution context, but the statement was created without one.");
            statement.State.Should().Be(ResumableStatementState.Faulted);
        }
    }

    [Test]
    public void ScalarOpcodesStillRunWithoutASchemaContext()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(4)),
            new MakeRecordInstruction(new RegisterRange(new Register(0), 1), new Register(2)));
        using var statement = new ResumableStatement(program);

        statement.Step().Should().Be(StatementStepResult.Done);

        statement.GetRecordRegister(new Register(2)).Should().NotBeNull();
        statement.SchemaContext.Should().BeNull();
    }

    [Test]
    public void SchemaOpcodesRejectADatabaseTheContextIsNotBoundTo()
    {
        var program = Program(new ParseSchemaInstruction(2));
        using var statement = Statement(program, Context(new RecordingSchemaOperations(), databaseCount: 2));

        Action step = () => statement.Step();

        step.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("ParseSchema addresses database 2, but the schema context is bound to 2 database(s).");
    }

    [Test]
    public void ParseSchemaRejectsATriggerTargetDatabaseTheContextIsNotBoundTo()
    {
        var program = Program(new ParseSchemaInstruction(0, "type='trigger'", TriggerTargetDatabase: 3));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations, databaseCount: 2));

        Action step = () => statement.Step();

        step.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("ParseSchema addresses database 3, but the schema context is bound to 2 database(s).");
        operations.Calls.Should().BeEmpty();
    }

    [Test]
    public void AFailedSchemaOpcodeFaultsTheStatementAndLeavesLaterOpcodesUnexecuted()
    {
        var program = Program(
            new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table),
            new SetCookieInstruction(0, VdbeSchemaCookie.SchemaVersion, 5),
            new ParseSchemaInstruction(0));
        var operations = new RecordingSchemaOperations
        {
            FailOn = call => call.StartsWith("SetCookie", StringComparison.Ordinal)
                ? new InvalidOperationException("staged cookie rejected")
                : null,
        };
        var context = Context(operations);
        using var statement = Statement(program, context);

        Action step = () => statement.Step();

        step.Should().Throw<InvalidOperationException>().WithMessage("staged cookie rejected");
        statement.State.Should().Be(ResumableStatementState.Faulted);
        operations.Calls.Should().Equal("CreateBtree(0,Table)", "SetCookie(0,SchemaVersion,5)");
        // The reservation the failed run made is still visible until the statement is reset, so the owning
        // transaction can discard it deliberately rather than losing track of it.
        context.ReservedRootPages.Should().Equal(2L);
    }

    [Test]
    public void ANestedSubprogramSharesTheCallersSchemaContextWithoutOwningIt()
    {
        var nested = new VdbeSubprogram(new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new CreateBtreeInstruction(0, new Register(0), VdbeCreateBtreeFlags.Index),
                new SetCookieInstruction(0, VdbeSchemaCookie.SchemaVersion, 3),
                new HaltInstruction(),
            ]));
        var program = Program(
            new CreateBtreeInstruction(0, new Register(1), VdbeCreateBtreeFlags.Table),
            new ProgramInstruction([], nested),
            new ParseSchemaInstruction(0));
        var operations = new RecordingSchemaOperations();
        var context = Context(operations);
        using var statement = Statement(program, context);

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal(
            "CreateBtree(0,Table)",
            "CreateBtree(0,Index)",
            "SetCookie(0,SchemaVersion,3)",
            "ParseSchema(0,NULL,NULL)");
        // Both the caller's and the nested program's reservations land on the one shared context.
        context.ReservedRootPages.Should().Equal(2L, 3L);
    }

    [Test]
    public void ANestedSubprogramWithoutASchemaContextFailsInsteadOfSilentlySucceeding()
    {
        var nested = new VdbeSubprogram(new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [new ParseSchemaInstruction(0), new HaltInstruction()]));
        var program = Program(new ProgramInstruction([], nested));
        using var statement = new ResumableStatement(program);

        Action step = () => statement.Step();

        step.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("ParseSchema requires a schema execution context, but the statement was created without one.");
    }

    [Test]
    public void TheSchemaContextValidatesItsOwnConstructionAndReadOnlyGuard()
    {
        var operations = new RecordingSchemaOperations();

        Action nullOperations = () => new VdbeSchemaExecutionContext(null!);
        Action zeroDatabases = () => new VdbeSchemaExecutionContext(operations, databaseCount: 0);

        nullOperations.Should().Throw<ArgumentNullException>();
        zeroDatabases.Should().Throw<ArgumentOutOfRangeException>();

        var readOnly = Context(operations, isReadOnly: true);
        readOnly.IsReadOnly.Should().BeTrue();
        readOnly.DatabaseCount.Should().Be(1);

        Action create = () => readOnly.CreateBtree(0, VdbeCreateBtreeFlags.Table);
        create.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("CreateBtree cannot run against a read-only schema context.");
        operations.Calls.Should().BeEmpty();
    }

    [Test]
    public void DestroyRejectsANegativeMovedRootPage()
    {
        var context = new VdbeSchemaExecutionContext(new NegativeMoveOperations());

        Action destroy = () => context.Destroy(0, 4, isTemporary: false);

        destroy.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("Destroy reported a negative moved root page -1; use zero when no root moved.");
        context.ReclaimedRootPages.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Destroy from a register

    [Test]
    public void DestroyReclaimsTheRootPageHeldInItsRootRegister()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Integer(9)),
            new DestroyInstruction(0, 0, new Register(1), IsTemporary: false, new Register(0)));
        var operations = new RecordingSchemaOperations();
        var context = Context(operations);
        using var statement = Statement(program, context);

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal("Destroy(0,9,False)");
        context.ReclaimedRootPages.Should().Equal(9L);
        statement.GetRegister(new Register(1)).AsInteger().Should().Be(0);
    }

    [Test]
    public void DestroyFaultsWhenItsRootRegisterHoldsSomethingOtherThanAPageNumber()
    {
        var program = Program(
            new LoadConstantInstruction(new Register(0), SqlValue.Null),
            new DestroyInstruction(0, 0, new Register(1), IsTemporary: false, new Register(0)));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations));

        Action step = () => statement.Step();

        step.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("Destroy reads its root page from r[0], which holds Null instead of an integer.");
        statement.State.Should().Be(ResumableStatementState.Faulted);
        operations.Calls.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- IndexBuild

    [Test]
    public void IndexBuildForwardsItsIndexIdentityAndUniqueness()
    {
        var program = Program(
            new IndexBuildInstruction(0, "entries", "entries_value"),
            new IndexBuildInstruction(0, "entries", "entries_key", Unique: true));
        var operations = new RecordingSchemaOperations();
        using var statement = Statement(program, Context(operations));

        statement.Step().Should().Be(StatementStepResult.Done);

        operations.Calls.Should().Equal(
            "IndexBuild(0,entries,entries_value,non-unique)",
            "IndexBuild(0,entries,entries_key,unique)");
    }

    [Test]
    public void IndexBuildFaultsTheStatementWhenTheRefillFails()
    {
        var program = Program(new IndexBuildInstruction(0, "entries", "entries_key", Unique: true));
        var operations = new RecordingSchemaOperations
        {
            FailOn = call => call.StartsWith("IndexBuild", StringComparison.Ordinal)
                ? new EmbeddedSqlException("UNIQUE constraint failed: entries.key")
                : null,
        };
        using var statement = Statement(program, Context(operations));

        Action step = () => statement.Step();

        step.Should().Throw<EmbeddedSqlException>().WithMessage("UNIQUE constraint failed: entries.key");
        statement.State.Should().Be(ResumableStatementState.Faulted);
    }

    [Test]
    public void IndexBuildIsRejectedByAReadOnlySchemaContext()
    {
        var operations = new RecordingSchemaOperations();
        var readOnly = Context(operations, isReadOnly: true);

        Action build = () => readOnly.BuildIndex(0, "entries", "entries_key", unique: false);

        build.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("IndexBuild cannot run against a read-only schema context.");
        operations.Calls.Should().BeEmpty();
    }

    private sealed class HeaderPageAllocator : RecordingSchemaOperationsBase
    {
        public override long CreateBtree(int database, VdbeCreateBtreeFlags flags) => 1;
    }

    private sealed class NegativeMoveOperations : RecordingSchemaOperationsBase
    {
        public override long Destroy(int database, long rootPage, bool isTemporary) => -1;
    }

    /// <summary>
    /// A base that throws for every operation a focused test does not exercise, so a test can never pass
    /// because an unexpected call quietly succeeded.
    /// </summary>
    private abstract class RecordingSchemaOperationsBase : IVdbeSchemaOperations
    {
        public virtual long CreateBtree(int database, VdbeCreateBtreeFlags flags) => throw Unexpected();

        public virtual void ClearBtree(int database, long rootPage) => throw Unexpected();

        public virtual long Destroy(int database, long rootPage, bool isTemporary) => throw Unexpected();

        public virtual long ReadCookie(int database, VdbeSchemaCookie cookie) => throw Unexpected();

        public virtual void SetCookie(int database, VdbeSchemaCookie cookie, long value) => throw Unexpected();

        public virtual void ParseSchema(int database, string? whereClause, int? triggerTargetDatabase)
            => throw Unexpected();

        public virtual void BuildIndex(int database, string tableName, string indexName, bool unique)
            => throw Unexpected();

        public virtual void DropObject(int database, VdbeSchemaObjectKind kind, string name) => throw Unexpected();

        public virtual void RenameTable(int database, string from, string to) => throw Unexpected();

        public virtual void AddColumn(
            int database,
            string table,
            string columnName,
            string columnDefinition,
            string? columnSql)
            => throw Unexpected();

        public virtual void DropColumn(int database, string table, int columnIndex) => throw Unexpected();

        public virtual void AlterColumn(
            int database,
            string table,
            int columnIndex,
            string columnDefinition,
            bool rename,
            bool quoteNewName)
            => throw Unexpected();

        private static InvalidOperationException Unexpected()
            => new("The test bound schema operation was not expected to be invoked.");
    }
}
