using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Locks the bytecode <see cref="DdlStatementCompiler"/> emits for <c>CREATE INDEX</c> and
/// <c>DROP INDEX</c> against Turso's <c>translate_create_index</c>/<c>translate_drop_index</c> shape, and
/// the compile-time validation that decides whether a program is emitted at all.
/// </summary>
public sealed class IndexStatementCompilerTests
{
    [Test]
    public void CreateIndexEmitsTheUpstreamSchemaProgramSequence()
    {
        var compiled = CompileCreate("CREATE INDEX idx ON t(a);");

        compiled.IsNoOp.Should().BeFalse();
        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
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
            VdbeOpcode.IndexBuild,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
    }

    [Test]
    public void CreateIndexWritesItsSchemaRowThroughMakeRecordAndNewRowid()
    {
        var compiled = CompileCreate("CREATE INDEX idx ON t(a);");
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
        columns[0].AsText().Should().Be("index");
        columns[1].AsText().Should().Be("idx");
        columns[2].AsText().Should().Be("t");
        columns[4].AsText().Should().Be("CREATE INDEX idx ON t(a)");
    }

    [Test]
    public void CreateIndexAllocatesAnIndexBtreeAndCopiesItsRootIntoTheSchemaRecord()
    {
        var compiled = CompileCreate("CREATE INDEX idx ON t(a);");

        var createBtree = compiled.Program.Instructions
            .OfType<CreateBtreeInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        createBtree.Flags.Should().Be(VdbeCreateBtreeFlags.Index);

        var copy = compiled.Program.Instructions.OfType<CopyInstruction>().Should().ContainSingle().Subject;
        copy.Source.Should().Be(createBtree.RootDestination);
    }

    [Test]
    public void CreateIndexStagesTheSchemaCookieItsCompilationDeclaredAndAdoptsOnlyItsOwnRow()
    {
        var compiled = CompileCreate("CREATE INDEX idx ON t(a);", schemaVersion: 11);

        compiled.StagedSchemaVersion.Should().Be(12);
        compiled.Program.Instructions
            .OfType<SetCookieInstruction>()
            .Should()
            .ContainSingle(instruction => instruction.Cookie == VdbeSchemaCookie.SchemaVersion)
            .Which.Value.Should().Be(12);

        compiled.Program.Instructions
            .OfType<ParseSchemaInstruction>()
            .Should()
            .ContainSingle()
            .Which.WhereClause.Should().Be("name = 'idx' AND type = 'index'");
    }

    [Test]
    public void CreateIndexRefillsTheIndexOnlyAfterTheAdoptionThatPublishesIt()
    {
        var compiled = CompileCreate("CREATE UNIQUE INDEX idx ON t(a);");

        var indexBuild = compiled.Program.Instructions
            .OfType<IndexBuildInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        indexBuild.TableName.Should().Be("t");
        indexBuild.IndexName.Should().Be("idx");
        indexBuild.Unique.Should().BeTrue();

        IndexOf(compiled, VdbeOpcode.IndexBuild).Should().BeGreaterThan(IndexOf(compiled, VdbeOpcode.ParseSchema));
    }

    [Test]
    public void CreateIndexIfNotExistsOnAnExistingIndexCompilesToANoOpProgram()
    {
        var catalog = CreateCatalog();
        var table = AddTable(catalog);
        table.Indexes.Add(EmbeddedIndexFactory.Create("t", table, ParseCreateIndex("CREATE INDEX idx ON t(a);")));

        var compiled = CompileCreate("CREATE INDEX IF NOT EXISTS idx ON t(a);", catalog);

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
    }

    [TestCase("CREATE INDEX idx ON t(a);", "index idx already exists")]
    [TestCase("CREATE INDEX t ON t(a);", "there is already a table named t")]
    [TestCase("CREATE INDEX sqlite_idx ON t(a);", "object name reserved for internal use: sqlite_idx")]
    [TestCase("CREATE INDEX other ON sqlite_schema(a);", "table sqlite_schema may not be indexed")]
    [TestCase("CREATE INDEX other ON sqlite_sequence(a);", "table sqlite_sequence may not be indexed")]
    [TestCase("CREATE INDEX other ON missing(a);", "no such table: missing")]
    [TestCase("CREATE INDEX other ON t(missing);", "no such column: missing")]
    public void CompilationRejectsAnIllegalCreateIndexBeforeEmittingAnything(string sql, string message)
    {
        var catalog = CreateCatalog();
        var table = AddTable(catalog);
        table.Indexes.Add(EmbeddedIndexFactory.Create("t", table, ParseCreateIndex("CREATE INDEX idx ON t(a);")));

        Action compile = () => CompileCreate(sql, catalog);

        compile.Should().Throw<EmbeddedSqlException>().WithMessage(message);
    }

    [Test]
    public void CompilationRejectsAnUnavailableCollationBeforeEmittingAnything()
    {
        Action compile = () => CompileCreate(
            "CREATE INDEX idx ON t(a COLLATE custom);",
            CreateCatalog(withTable: true),
            hasCollation: name => !string.Equals(name, "custom", StringComparison.OrdinalIgnoreCase));

        compile.Should().Throw<EmbeddedSqlException>().WithMessage("no such collation sequence: custom");
    }

    [Test]
    public void CompilationRejectsAnApplicationDefinedFunctionInAnIndexExpression()
    {
        // UPPER is a deterministic built-in, so the structural validator accepts it; the rejection has to
        // come from the connection reporting that the name is also an application-defined registration.
        Action compile = () => CompileCreate(
            "CREATE INDEX idx ON t(upper(b));",
            CreateCatalog(withTable: true),
            isRegisteredScalarFunction: (name, _) => string.Equals(name, "upper", StringComparison.OrdinalIgnoreCase));

        compile.Should().Throw<EmbeddedSqlException>().WithMessage(
            "application-defined functions are prohibited in index expressions and partial index WHERE clauses");
    }

    [Test]
    public void CompilationRejectsAnApplicationDefinedFunctionInAPartialIndexPredicate()
    {
        Action compile = () => CompileCreate(
            "CREATE INDEX idx ON t(a) WHERE upper(b) = 'X';",
            CreateCatalog(withTable: true),
            isRegisteredScalarFunction: (name, _) => string.Equals(name, "upper", StringComparison.OrdinalIgnoreCase));

        compile.Should().Throw<EmbeddedSqlException>().WithMessage(
            "application-defined functions are prohibited in index expressions and partial index WHERE clauses");
    }

    [Test]
    public void CompilationEnforcesTheMaximumPageCountForTheIndexBtree()
    {
        var requested = new List<int>();

        _ = CompileCreate("CREATE INDEX idx ON t(a);", CreateCatalog(withTable: true), enforceMaxPageCount: requested.Add);

        requested.Should().Equal(1);
    }

    [Test]
    public void DropIndexEmitsTheUpstreamScanDeleteAndDestroySequence()
    {
        var compiled = CompileDrop("DROP INDEX idx;");

        compiled.IsNoOp.Should().BeFalse();
        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Column,
            VdbeOpcode.Delete,
            VdbeOpcode.Goto,
            VdbeOpcode.Next,
            VdbeOpcode.SetCookie,
            VdbeOpcode.Destroy,
            VdbeOpcode.DropIndex,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
    }

    [Test]
    public void DropIndexReadsTheRootPageOutOfTheRowItDeletesAndPassesItToDestroy()
    {
        var compiled = CompileDrop("DROP INDEX idx;");
        var instructions = compiled.Program.Instructions;

        var rootColumn = instructions
            .OfType<ColumnInstruction>()
            .Should()
            .ContainSingle(column => column.ColumnIndex == 3)
            .Subject;
        rootColumn.Cursor.Should().Be(compiled.SchemaCursor);

        var destroy = instructions.OfType<DestroyInstruction>().Should().ContainSingle().Subject;
        destroy.RootRegister.Should().Be(rootColumn.Destination);
        destroy.RootPage.Should().Be(0);

        // The root has to be read before the row that carries it is deleted.
        var rootColumnAddress = instructions
            .Select((instruction, address) => (instruction, address))
            .First(entry => ReferenceEquals(entry.instruction, rootColumn))
            .address;
        rootColumnAddress.Should().BeLessThan(IndexOf(compiled, VdbeOpcode.Delete));
    }

    [Test]
    public void DropIndexEvictsTheIndexAfterRetiringItsStorage()
    {
        var compiled = CompileDrop("DROP INDEX idx;");

        compiled.Program.Instructions
            .OfType<DropIndexInstruction>()
            .Should()
            .ContainSingle()
            .Which.IndexName.Should().Be("idx");
        IndexOf(compiled, VdbeOpcode.DropIndex).Should().BeGreaterThan(IndexOf(compiled, VdbeOpcode.Destroy));
        IndexOf(compiled, VdbeOpcode.SetCookie).Should().BeLessThan(IndexOf(compiled, VdbeOpcode.Destroy));
    }

    [Test]
    public void DropIndexIfExistsOnAMissingIndexCompilesToANoOpProgram()
    {
        var compiled = CompileDrop("DROP INDEX IF EXISTS missing;", CreateCatalog(withTable: true));

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
    }

    [Test]
    public void CompilationRejectsDroppingAMissingIndex()
    {
        Action compile = () => CompileDrop("DROP INDEX missing;", CreateCatalog(withTable: true));

        compile.Should().Throw<EmbeddedSqlException>().WithMessage("no such index: missing");
    }

    [Test]
    public void CompilationRejectsDroppingAConstraintBackedIndex()
    {
        var catalog = CreateCatalog();
        catalog.Tables.Add(
            "u",
            new EmbeddedTable(
                "u",
                [new EmbeddedColumn("a", null, false, false, true, null)]));
        var automatic = catalog.Tables["u"].Indexes.Should().ContainSingle().Subject;

        Action compile = () => CompileDrop($"DROP INDEX {automatic.Name};", catalog);

        compile.Should().Throw<EmbeddedSqlException>().WithMessage(
            $"index associated with UNIQUE or PRIMARY KEY constraint cannot be dropped: {automatic.Name}");
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

    private static CreateIndexStatement ParseCreateIndex(string sql)
        => (CreateIndexStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql));

    private static EmbeddedTable AddTable(EmbeddedDatabase.SchemaCatalog catalog)
    {
        var table = new EmbeddedTable(
            "t",
            [
                new EmbeddedColumn("a", "INTEGER", false, false, false, null),
                new EmbeddedColumn("b", "TEXT", false, false, false, null),
            ]);
        catalog.Tables.Add("t", table);
        return table;
    }

    private static EmbeddedDatabase.SchemaCatalog CreateCatalog(bool withTable = false)
    {
        var catalog = new EmbeddedDatabase.SchemaCatalog(
            new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase));
        if (withTable)
            AddTable(catalog);

        return catalog;
    }

    private static CompiledSchemaProgram CompileCreate(
        string sql,
        EmbeddedDatabase.SchemaCatalog? catalog = null,
        long schemaVersion = 0,
        Action<int>? enforceMaxPageCount = null,
        Func<string, bool>? hasCollation = null,
        Func<string, int, bool>? isRegisteredScalarFunction = null)
        => DdlStatementCompiler.CompileCreateIndex(
            ParseCreateIndex(sql),
            Context(
                catalog ?? CreateCatalog(withTable: true),
                schemaVersion,
                enforceMaxPageCount,
                hasCollation,
                isRegisteredScalarFunction));

    private static CompiledSchemaProgram CompileDrop(
        string sql,
        EmbeddedDatabase.SchemaCatalog? catalog = null,
        long schemaVersion = 0)
    {
        if (catalog is null)
        {
            catalog = CreateCatalog();
            var table = AddTable(catalog);
            table.Indexes.Add(EmbeddedIndexFactory.Create("t", table, ParseCreateIndex("CREATE INDEX idx ON t(a);")));
        }

        var statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        statement.Should().BeOfType<DropIndexStatement>();
        return DdlStatementCompiler.CompileDropIndex(
            (DropIndexStatement)statement,
            Context(catalog, schemaVersion));
    }

    private static DdlCompilationContext Context(
        EmbeddedDatabase.SchemaCatalog catalog,
        long schemaVersion,
        Action<int>? enforceMaxPageCount = null,
        Func<string, bool>? hasCollation = null,
        Func<string, int, bool>? isRegisteredScalarFunction = null)
        => new(
            catalog,
            schemaVersion,
            static _ => { },
            enforceMaxPageCount ?? (static _ => { }),
            Database: 0,
            hasCollation,
            isRegisteredScalarFunction);
}
