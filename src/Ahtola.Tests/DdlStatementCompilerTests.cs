using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Locks the bytecode <see cref="DdlStatementCompiler"/> emits for <c>CREATE TABLE</c> and
/// <c>CREATE TABLE AS SELECT</c> against Turso's <c>translate_create_table</c> shape, and the
/// compile-time validation that decides whether a program is emitted at all.
/// </summary>
public sealed class DdlStatementCompilerTests
{
    [Test]
    public void CreateTableEmitsTheUpstreamSchemaProgramSequence()
    {
        var compiled = Compile("CREATE TABLE t(a INTEGER, b TEXT);");

        compiled.IsNoOp.Should().BeFalse();
        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.ReadCookie,
            VdbeOpcode.JumpIf,
            VdbeOpcode.SetCookie,
            VdbeOpcode.SetCookie,
            VdbeOpcode.CreateBtree,
            VdbeOpcode.NewRowid,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Copy,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.Insert,
            VdbeOpcode.SetCookie,
            VdbeOpcode.ParseSchema,
            VdbeOpcode.Halt);
    }

    [Test]
    public void CreateTableStagesTheSchemaCookieItsCompilationDeclared()
    {
        var compiled = Compile("CREATE TABLE t(a);", schemaVersion: 7);

        compiled.StagedSchemaVersion.Should().Be(8);
        compiled.Program.Instructions
            .OfType<SetCookieInstruction>()
            .Should()
            .ContainSingle(instruction => instruction.Cookie == VdbeSchemaCookie.SchemaVersion)
            .Which.Value.Should().Be(8);
    }

    [Test]
    public void CreateTableInitializesTheHeaderCookiesOnlyWhenTheDatabaseHasNoFormat()
    {
        var compiled = Compile("CREATE TABLE t(a);");
        var instructions = compiled.Program.Instructions;

        var readCookie = instructions.OfType<ReadCookieInstruction>().Should().ContainSingle().Subject;
        readCookie.Cookie.Should().Be(VdbeSchemaCookie.DatabaseFormat);

        var jumpIndex = IndexOf(compiled, VdbeOpcode.JumpIf);
        var jump = (JumpIfInstruction)instructions[jumpIndex];
        jump.Register.Should().Be(readCookie.Destination);
        // The guarded block is exactly the two initialization cookies, so a database that already has a
        // format never re-asserts one.
        jump.Target.Offset.Should().Be(jumpIndex + 3);
        instructions[jumpIndex + 1].Should().BeOfType<SetCookieInstruction>()
            .Which.Cookie.Should().Be(VdbeSchemaCookie.DatabaseFormat);
        instructions[jumpIndex + 2].Should().BeOfType<SetCookieInstruction>()
            .Which.Cookie.Should().Be(VdbeSchemaCookie.DatabaseTextEncoding);
    }

    [Test]
    public void CreateTableWritesItsSchemaRowThroughMakeRecordAndNewRowid()
    {
        var compiled = Compile("CREATE TABLE t(a INTEGER, b TEXT);");
        var instructions = compiled.Program.Instructions;

        var newRowid = instructions.OfType<NewRowidInstruction>().Should().ContainSingle().Subject;
        newRowid.Cursor.Should().Be(compiled.SchemaCursor);

        var makeRecord = instructions.OfType<MakeRecordInstruction>().Should().ContainSingle().Subject;
        makeRecord.Values.Count.Should().Be(5);

        var insert = instructions.OfType<InsertInstruction>().Should().ContainSingle().Subject;
        insert.Cursor.Should().Be(compiled.SchemaCursor);
        insert.Record.Should().Be(makeRecord.Destination);
        insert.RowId.Should().Be(newRowid.Destination);
        insert.TableName.Should().Be("sqlite_schema");
        // A DDL row write is invisible to changes() and last_insert_rowid().
        insert.Flags.Should().Be(VdbeInsertFlags.SkipLastRowid | VdbeInsertFlags.SkipAllChangeCounts);

        var columns = ReadSchemaRecordColumns(compiled, makeRecord);
        columns[0].AsText().Should().Be("table");
        columns[1].AsText().Should().Be("t");
        columns[2].AsText().Should().Be("t");
        columns[4].AsText().Should().Be("CREATE TABLE t(a INTEGER, b TEXT)");
    }

    [Test]
    public void CreateTableCopiesTheAllocatedRootIntoTheSchemaRecord()
    {
        var compiled = Compile("CREATE TABLE t(a);");
        var createBtree = compiled.Program.Instructions
            .OfType<CreateBtreeInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        createBtree.Flags.Should().Be(VdbeCreateBtreeFlags.Table);

        var copy = compiled.Program.Instructions.OfType<CopyInstruction>().Should().ContainSingle().Subject;
        copy.Source.Should().Be(createBtree.RootDestination);
    }

    [Test]
    public void CreateTableWithoutRowidAllocatesAnIndexBtree()
    {
        var compiled = Compile("CREATE TABLE t(a TEXT PRIMARY KEY, b) WITHOUT ROWID;");

        compiled.Program.Instructions
            .OfType<CreateBtreeInstruction>()
            .First()
            .Flags
            .Should()
            .Be(VdbeCreateBtreeFlags.Index);
    }

    [Test]
    public void CreateTableAllocatesAnIndexBtreeAndSchemaRowForEachImplicitIndex()
    {
        var compiled = Compile("CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT UNIQUE, c TEXT UNIQUE);");
        var roots = compiled.Program.Instructions.OfType<CreateBtreeInstruction>().ToArray();

        roots.Should().HaveCount(3);
        roots[0].Flags.Should().Be(VdbeCreateBtreeFlags.Table);
        roots.Skip(1).Should().OnlyContain(root => root.Flags == VdbeCreateBtreeFlags.Index);

        var records = compiled.Program.Instructions.OfType<MakeRecordInstruction>().ToArray();
        records.Should().HaveCount(3);
        ReadSchemaRecordColumns(compiled, records[1])[0].AsText().Should().Be("index");
        // An implicit constraint index is not declared by SQL, so SQLite stores a null sql column.
        ReadSchemaRecordColumns(compiled, records[1])[4].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void CreateTableWithAutoIncrementCreatesTheSequenceTablesAndSeedsTheBackingRow()
    {
        const string backingTable = "__turso_internal_seq___turso_internal_autoincrement_t";
        var compiled = Compile("CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");

        var names = SchemaRecords(compiled)
            .Select(record => ReadSchemaRecordColumns(compiled, record)[1].AsText())
            .ToArray();
        names.Should().Equal("sqlite_sequence", backingTable, "t");

        compiled.Program.Instructions
            .OfType<ParseSchemaInstruction>()
            .Select(parse => parse.WhereClause)
            .Should()
            .Equal(
                "tbl_name = 'sqlite_sequence' AND type != 'trigger'",
                $"tbl_name = '{backingTable}' AND type != 'trigger'",
                "tbl_name = 't' AND type != 'trigger'");

        // The backing table's seed row is written by the same record/rowid/insert bytecode a CTAS uses,
        // after ParseSchema has adopted the table it lands in.
        var population = compiled.Populations.Should().ContainSingle().Subject;
        population.TargetTableName.Should().Be(backingTable);
        population.Rows.Should().ContainSingle();
    }

    [Test]
    public void CreateTableAsSelectEmitsAPerRowPopulationLoop()
    {
        var compiled = CompileCreateTableAsSelect(
            "copied",
            [new EmbeddedColumn("a", "INT", false, false, false, null)],
            [[SqlValue.Integer(1)], [SqlValue.Integer(2)]]);

        var population = compiled.Populations.Should().ContainSingle().Subject;
        population.TargetTableName.Should().Be("copied");
        population.Rows.Should().HaveCount(2);

        var loop = Opcodes(compiled).SkipWhile(opcode => opcode != VdbeOpcode.ParseSchema).Skip(1).ToArray();
        loop.Should().Equal(
            VdbeOpcode.OpenReadCursor,
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.NewRowid,
            VdbeOpcode.Insert,
            VdbeOpcode.Next,
            VdbeOpcode.Halt);
    }

    [Test]
    public void CreateTableAsSelectPopulatesThroughTheTargetCursorNotTheSchemaCursor()
    {
        var compiled = CompileCreateTableAsSelect(
            "copied",
            [new EmbeddedColumn("a", "INT", false, false, false, null)],
            [[SqlValue.Integer(1)]]);
        var population = compiled.Populations.Should().ContainSingle().Subject;
        var inserts = compiled.Program.Instructions.OfType<InsertInstruction>().ToArray();

        inserts.Should().HaveCount(2);
        inserts[0].Cursor.Should().Be(compiled.SchemaCursor);
        inserts[1].Cursor.Should().Be(population.TargetCursor);
        inserts[1].TableName.Should().Be("copied");

        compiled.Program.Instructions
            .OfType<NextInstruction>()
            .Should()
            .ContainSingle()
            .Which.Cursor.Should().Be(population.SourceCursor);
    }

    [Test]
    public void CreateTableAsSelectStoresTheCompactSchemaSql()
    {
        var compiled = CompileCreateTableAsSelect(
            "copied",
            [new EmbeddedColumn("a", "INT", false, false, false, null)],
            []);
        var record = SchemaRecords(compiled).Should().ContainSingle().Subject;

        ReadSchemaRecordColumns(compiled, record)[4].AsText().Should().Be("CREATE TABLE copied(a INT)");
    }

    [Test]
    public void CreateTableIfNotExistsCompilesToAProgramThatOnlyHalts()
    {
        var catalog = CreateCatalog();
        catalog.Tables.Add("t", new EmbeddedTable("t", [new EmbeddedColumn("a", null, false, false, false, null)]));

        var compiled = Compile("CREATE TABLE IF NOT EXISTS t(a);", catalog);

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
        compiled.Populations.Should().BeEmpty();
    }

    [TestCase("CREATE TABLE t(a);", "table t already exists")]
    [TestCase("CREATE TABLE sqlite_taken(a);", "object name reserved for internal use: sqlite_taken")]
    public void CompilationRejectsConflictingNamesBeforeEmittingAnything(string sql, string message)
    {
        var catalog = CreateCatalog();
        catalog.Tables.Add("t", new EmbeddedTable("t", [new EmbeddedColumn("a", null, false, false, false, null)]));

        Action compile = () => Compile(sql, catalog);

        compile.Should().Throw<EmbeddedSqlException>().WithMessage(message);
    }

    [Test]
    public void CompilationRejectsAWithoutRowidTableThatDeclaresNoPrimaryKey()
    {
        Action compile = () => Compile("CREATE TABLE t(a, b) WITHOUT ROWID;");

        compile.Should().Throw<EmbeddedSqlException>().WithMessage("PRIMARY KEY missing on table t");
    }

    [Test]
    public void CompilationEnforcesTheMaximumPageCountForEveryBtreeItWouldAllocate()
    {
        var requested = new List<int>();

        _ = Compile(
            "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT);",
            CreateCatalog(),
            enforceMaxPageCount: requested.Add);

        // One page for the table plus one for each of the two AUTOINCREMENT bookkeeping tables.
        requested.Should().Equal(3);
    }

    [Test]
    public void CompilationResolvesCheckConstraintFunctionsBeforeEmittingAnything()
    {
        Action compile = () => Compile(
            "CREATE TABLE t(a CHECK (missing_function(a)));",
            CreateCatalog(),
            validateCheckConstraintFunctions: _ => throw new EmbeddedSqlException("no such function: missing_function"));

        compile.Should().Throw<EmbeddedSqlException>().WithMessage("no such function: missing_function");
    }

    /// <summary>
    /// The <c>MakeRecord</c> instructions that build <c>sqlite_schema</c> rows, in emission order. The
    /// population loop builds records too, so a schema assertion must not pick those up.
    /// </summary>
    private static MakeRecordInstruction[] SchemaRecords(CompiledSchemaProgram compiled)
    {
        var schemaRecordRegisters = compiled.Program.Instructions
            .OfType<InsertInstruction>()
            .Where(insert => insert.Cursor == compiled.SchemaCursor && insert.Record is not null)
            .Select(insert => insert.Record!.Value.Index)
            .ToHashSet();
        return compiled.Program.Instructions
            .OfType<MakeRecordInstruction>()
            .Where(record => schemaRecordRegisters.Contains(record.Destination.Index))
            .ToArray();
    }

    private static IReadOnlyList<VdbeOpcode> Opcodes(CompiledSchemaProgram compiled)
        => compiled.Program.Instructions.Select(instruction => instruction.Opcode).ToArray();

    private static int IndexOf(CompiledSchemaProgram compiled, VdbeOpcode opcode)
    {
        for (var index = 0; index < compiled.Program.Instructions.Count; index++)
        {
            if (compiled.Program.Instructions[index].Opcode == opcode)
                return index;
        }

        throw new InvalidOperationException($"The compiled program has no {opcode} instruction.");
    }

    /// <summary>
    /// Reads back the five constants a schema record was built from, so a test asserts the row the program
    /// writes rather than the registers it happens to use.
    /// </summary>
    private static SqlValue[] ReadSchemaRecordColumns(
        CompiledSchemaProgram compiled,
        MakeRecordInstruction makeRecord)
    {
        var values = new SqlValue[makeRecord.Values.Count];
        for (var offset = 0; offset < values.Length; offset++)
        {
            var register = makeRecord.Values.Start.Index + offset;
            values[offset] = compiled.Program.Instructions
                .OfType<LoadConstantInstruction>()
                .Where(load => load.Destination.Index == register)
                .Select(load => load.Value)
                .DefaultIfEmpty(SqlValue.Null)
                .Last();
        }

        return values;
    }

    private static EmbeddedDatabase.SchemaCatalog CreateCatalog()
        => new(
            new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase));

    private static CompiledSchemaProgram Compile(
        string sql,
        EmbeddedDatabase.SchemaCatalog? catalog = null,
        long schemaVersion = 0,
        Action<int>? enforceMaxPageCount = null,
        Action<Expression>? validateCheckConstraintFunctions = null)
    {
        var statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        statement.Should().BeOfType<CreateTableStatement>();
        return DdlStatementCompiler.CompileCreateTable(
            (CreateTableStatement)statement,
            new DdlCompilationContext(
                catalog ?? CreateCatalog(),
                schemaVersion,
                validateCheckConstraintFunctions ?? (static _ => { }),
                enforceMaxPageCount ?? (static _ => { })));
    }

    private static CompiledSchemaProgram CompileCreateTableAsSelect(
        string name,
        IReadOnlyList<EmbeddedColumn> columns,
        IReadOnlyList<SqlValue[]> rows)
        => DdlStatementCompiler.CompileCreateTable(
            new CreateTableStatement(name, columns, IfNotExists: false, Strict: false, InitialRows: rows),
            new DdlCompilationContext(
                CreateCatalog(),
                SchemaVersion: 0,
                static _ => { },
                static _ => { }));
}
