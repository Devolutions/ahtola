using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Locks the bytecode <see cref="DdlStatementCompiler"/> emits for the schema-object families — views,
/// triggers and the virtual-table lifecycle — against Turso's <c>translate_create_view</c>,
/// <c>translate_drop_view</c>, <c>translate_create_trigger</c>, <c>translate_drop_trigger</c>,
/// <c>translate_create_virtual_table</c> and the virtual arms of <c>translate_drop_table</c> and
/// <c>translate_rename_virtual_table</c>, together with the compile-time validation that decides whether a
/// program is emitted at all.
/// </summary>
public sealed class SchemaObjectStatementCompilerTests
{
    // ------------------------------------------------------------------ views

    [Test]
    public void CreateViewEmitsTheUpstreamSchemaProgramSequence()
    {
        var compiled = CompileCreateView("CREATE VIEW v AS SELECT a FROM t;");

        compiled.IsNoOp.Should().BeFalse();
        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.NewRowid,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.Insert,
            VdbeOpcode.ParseSchema,
            VdbeOpcode.SetCookie,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
    }

    [Test]
    public void CreateViewWritesARootpageZeroSchemaRowThroughMakeRecordAndNewRowid()
    {
        var compiled = CompileCreateView("CREATE VIEW v AS SELECT a FROM t;");
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
        insert.Flags.Should().Be(VdbeInsertFlags.SkipLastRowid | VdbeInsertFlags.SkipAllChangeCounts);

        var columns = ReadSchemaRecordColumns(compiled, makeRecord);
        columns[0].AsText().Should().Be("view");
        columns[1].AsText().Should().Be("v");
        columns[2].AsText().Should().Be("v");
        // A view has no b-tree, so SQLite stores it with rootpage 0 and nothing is ever allocated.
        columns[3].AsInteger().Should().Be(0);
        columns[4].AsText().Should().Be("CREATE VIEW v AS SELECT a FROM t");
        compiled.Program.Instructions.OfType<CreateBtreeInstruction>().Should().BeEmpty();
    }

    [Test]
    public void CreateViewStagesTheCookieItDeclaredAndAdoptsOnlyItsOwnRow()
    {
        var compiled = CompileCreateView("CREATE VIEW v AS SELECT a FROM t;", schemaVersion: 7);

        compiled.StagedSchemaVersion.Should().Be(8);
        compiled.Program.Instructions
            .OfType<SetCookieInstruction>()
            .Should()
            .ContainSingle(instruction => instruction.Cookie == VdbeSchemaCookie.SchemaVersion)
            .Which.Value.Should().Be(8);

        compiled.Program.Instructions
            .OfType<ParseSchemaInstruction>()
            .Should()
            .ContainSingle()
            .Which.WhereClause.Should().Be("name = 'v' AND type = 'view'");
    }

    [Test]
    public void CreateViewHandsTheDeclaredDefinitionToTheAdoptionItEmits()
    {
        var compiled = CompileCreateView("CREATE VIEW v (x) AS SELECT a FROM t;");

        compiled.PendingObjects.TryGetView("v", out var view).Should().BeTrue();
        view.Name.Should().Be("v");
        view.Columns.Should().Equal("x");
        compiled.PendingObjects.TryGetTrigger("v", out _).Should().BeFalse();
    }

    [Test]
    public void CreateViewIfNotExistsOnAnExistingViewCompilesToANoOpProgram()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "v");

        var compiled = CompileCreateView("CREATE VIEW IF NOT EXISTS v AS SELECT a FROM t;", catalog);

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
        compiled.StagedSchemaVersion.Should().Be(0);
    }

    [Test]
    public void CreateViewRejectsNameConflictsInEveryNamespaceThatCollides()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "existing_view");
        AddTrigger(catalog, "existing_trigger", "t");
        catalog.Tables["t"].Indexes.Add(EmbeddedIndexFactory.Create(
            "t",
            catalog.Tables["t"],
            ParseCreateIndex("CREATE INDEX existing_index ON t(a);")));

        Compile("CREATE VIEW existing_view AS SELECT a FROM t;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("view existing_view already exists");
        Compile("CREATE VIEW t AS SELECT a FROM t;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("there is already a table named t");
        Compile("CREATE VIEW existing_trigger AS SELECT a FROM t;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("there is already a trigger named existing_trigger");
        Compile("CREATE VIEW existing_index AS SELECT a FROM t;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("there is already an index named existing_index");
        Compile("CREATE VIEW sqlite_v AS SELECT a FROM t;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("object name reserved for internal use: sqlite_v");
    }

    [Test]
    public void DropViewEmitsTheUpstreamScanAndDropSequence()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "v");

        var compiled = CompileDropView("DROP VIEW v;", catalog);

        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Delete,
            VdbeOpcode.Goto,
            VdbeOpcode.Next,
            VdbeOpcode.SetCookie,
            VdbeOpcode.DropView,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
        // A rootpage-0 object owns no b-tree, so the scan never reads a rootpage and nothing is destroyed.
        compiled.Program.Instructions.OfType<DestroyInstruction>().Should().BeEmpty();
    }

    [Test]
    public void DropViewAlsoRetiresTheTriggersThatWatchedIt()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "v");
        AddTrigger(catalog, "on_view", "v", TriggerTiming.InsteadOf);
        AddTrigger(catalog, "on_table", "t");

        var compiled = CompileDropView("DROP VIEW v;", catalog);

        compiled.Program.Instructions
            .OfType<DropTriggerInstruction>()
            .Select(instruction => instruction.TriggerName)
            .Should()
            .Equal("on_view");
        compiled.Program.Instructions.OfType<DropViewInstruction>().Should().ContainSingle();
        // Two delete scans: the view row and the trigger row that pointed at it.
        compiled.Program.Instructions.OfType<DeleteInstruction>().Should().HaveCount(2);
    }

    [Test]
    public void DropViewIfExistsOnAMissingViewCompilesToANoOpProgram()
    {
        var compiled = CompileDropView("DROP VIEW IF EXISTS missing;", CreateCatalog(withTable: true));

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
    }

    [Test]
    public void DropViewRejectsAMissingViewAndATableOfTheSameName()
    {
        var catalog = CreateCatalog(withTable: true);

        CompileDrop("DROP VIEW missing;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("no such view: missing");
        CompileDrop("DROP VIEW t;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("use DROP TABLE to delete table t");
    }

    // --------------------------------------------------------------- triggers

    [Test]
    public void CreateTriggerEmitsTheUpstreamSchemaProgramSequence()
    {
        var compiled = CompileCreateTrigger("CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;");

        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.NewRowid,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.Insert,
            VdbeOpcode.SetCookie,
            VdbeOpcode.ParseSchema,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        var makeRecord = compiled.Program.Instructions.OfType<MakeRecordInstruction>().Single();
        var columns = ReadSchemaRecordColumns(compiled, makeRecord);
        columns[0].AsText().Should().Be("trigger");
        columns[1].AsText().Should().Be("tr");
        columns[2].AsText().Should().Be("t");
        columns[3].AsInteger().Should().Be(0);

        compiled.Program.Instructions
            .OfType<ParseSchemaInstruction>()
            .Should()
            .ContainSingle()
            .Which.WhereClause.Should().Be("name = 'tr' AND type = 'trigger'");
    }

    [Test]
    public void CreateTriggerHandsTheDeclaredDefinitionAndItsDeclarationOrderToTheAdoption()
    {
        var catalog = CreateCatalog(withTable: true);
        AddTrigger(catalog, "first", "t", declarationOrder: 4);

        var compiled = CompileCreateTrigger(
            "CREATE TRIGGER second BEFORE UPDATE OF a ON t WHEN NEW.a > 0 BEGIN SELECT 1; END;",
            catalog);

        compiled.PendingObjects.TryGetTrigger("second", out var trigger).Should().BeTrue();
        trigger.DeclarationOrder.Should().Be(5);
        trigger.Timing.Should().Be(TriggerTiming.Before);
        trigger.Event.Should().Be(TriggerEvent.Update);
        trigger.UpdateOfColumns.Should().Equal("a");
        trigger.When.Should().NotBeNull();
        trigger.Body.Should().ContainSingle();
    }

    [Test]
    public void CreateTriggerCarriesTheRoutingFactsTheStoredSqlCannotExpress()
    {
        // A TEMP trigger whose target lives in another schema: the connection resolved both the home and
        // the target schema, and the unqualified ON clause cannot record either.
        var statement = (CreateTriggerStatement)SqlParser.Parse(
            "CREATE TEMP TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;",
            SqlParameterMap.Parse("CREATE TEMP TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;"));
        var routed = statement with { Temporary = true, TargetSchema = "main" };

        var compiled = DdlStatementCompiler.CompileCreateTrigger(routed, Context(CreateCatalog(), 0));

        compiled.PendingObjects.TryGetTrigger("tr", out var trigger).Should().BeTrue();
        trigger.Temporary.Should().BeTrue();
        trigger.TargetSchema.Should().Be("main");
    }

    [Test]
    public void CreateTriggerSharesItsNamespaceOnlyWithOtherTriggers()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "v");
        AddTrigger(catalog, "existing", "t");

        // A trigger may reuse a table's or a view's name; only trigger-vs-trigger collides.
        CompileCreateTrigger("CREATE TRIGGER t AFTER INSERT ON t BEGIN SELECT 1; END;", catalog)
            .IsNoOp.Should().BeFalse();
        CompileCreateTrigger("CREATE TRIGGER v AFTER INSERT ON t BEGIN SELECT 1; END;", catalog)
            .IsNoOp.Should().BeFalse();
        Compile("CREATE TRIGGER existing AFTER INSERT ON t BEGIN SELECT 1; END;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("trigger existing already exists");
        CompileCreateTrigger(
                "CREATE TRIGGER IF NOT EXISTS existing AFTER INSERT ON t BEGIN SELECT 1; END;",
                catalog)
            .IsNoOp.Should().BeTrue();
    }

    [Test]
    public void CreateTriggerEnforcesTheTimingRulesForItsTarget()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "v");

        Compile("CREATE TRIGGER tr INSTEAD OF INSERT ON t BEGIN SELECT 1; END;", catalog)
            .Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot create INSTEAD OF trigger on table: t");
        Compile("CREATE TRIGGER tr AFTER INSERT ON v BEGIN SELECT 1; END;", catalog)
            .Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot create AFTER trigger on view: v");
        Compile("CREATE TRIGGER tr AFTER INSERT ON missing BEGIN SELECT 1; END;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("no such table: missing");
        Compile("CREATE TRIGGER tr INSTEAD OF INSERT ON missing BEGIN SELECT 1; END;", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("no such view: missing");
        Compile("CREATE TRIGGER sqlite_tr AFTER INSERT ON t BEGIN SELECT 1; END;", catalog)
            .Should().Throw<EmbeddedSqlException>()
            .WithMessage("object name reserved for internal use: sqlite_tr");
    }

    [Test]
    public void DropTriggerEmitsTheUpstreamScanAndDropSequence()
    {
        var catalog = CreateCatalog(withTable: true);
        AddTrigger(catalog, "tr", "t");

        var compiled = CompileDropTrigger("DROP TRIGGER tr;", catalog);

        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Delete,
            VdbeOpcode.Goto,
            VdbeOpcode.Next,
            VdbeOpcode.SetCookie,
            VdbeOpcode.DropTrigger,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
    }

    [Test]
    public void DropTriggerIfExistsOnAMissingTriggerCompilesToANoOpProgram()
    {
        var compiled = CompileDropTrigger("DROP TRIGGER IF EXISTS missing;", CreateCatalog(withTable: true));

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
    }

    [Test]
    public void DropTriggerRejectsAMissingTrigger()
        => CompileDrop("DROP TRIGGER missing;", CreateCatalog(withTable: true))
            .Should().Throw<EmbeddedSqlException>().WithMessage("no such trigger: missing");

    // --------------------------------------------------------- virtual tables

    [Test]
    public void CreateVirtualTableEmitsVCreateBeforeTheSchemaRowItRecords()
    {
        var compiled = DdlStatementCompiler.CompileCreateVirtualTable(
            ParseCreateVirtualTable("CREATE VIRTUAL TABLE docs USING fts5(body);"),
            Context(CreateCatalog(), 3));

        Opcodes(compiled).Should().Equal(
            VdbeOpcode.VCreate,
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.NewRowid,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.Insert,
            VdbeOpcode.SetCookie,
            VdbeOpcode.ParseSchema,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        var makeRecord = compiled.Program.Instructions.OfType<MakeRecordInstruction>().Single();
        var columns = ReadSchemaRecordColumns(compiled, makeRecord);
        columns[0].AsText().Should().Be("table");
        columns[1].AsText().Should().Be("docs");
        // SQLite records a virtual table as a table row with rootpage 0.
        columns[3].AsInteger().Should().Be(0);
        columns[4].AsText().Should().Be("CREATE VIRTUAL TABLE \"docs\" USING \"fts5\"(body)");

        compiled.StagedSchemaVersion.Should().Be(4);
        compiled.Program.Instructions
            .OfType<ParseSchemaInstruction>()
            .Should()
            .ContainSingle()
            .Which.WhereClause.Should().Be("tbl_name = 'docs' AND type != 'trigger'");
    }

    [Test]
    public void DescribingCreateVirtualTableNeverResolvesTheModuleItNames()
    {
        // A described program is never bound, so the publish binding's slot stays empty and the module is
        // never reached. Compiling one that names a module nobody registered still succeeds.
        var compiled = DdlStatementCompiler.CompileCreateVirtualTable(
            ParseCreateVirtualTable("CREATE VIRTUAL TABLE ghost USING no_such_module(a);"),
            Context(CreateCatalog(), 0));

        compiled.OperationsSlot.Should().NotBeNull();
        var vCreate = compiled.Program.Instructions.OfType<VCreateInstruction>().Should().ContainSingle().Subject;
        Action publish = () => vCreate.Publish(null!);
        publish.Should().Throw<VdbeSchemaExecutionException>()
            .WithMessage("*resolved before the program was bound to a stage*");
    }

    [Test]
    public void CreateVirtualTableRejectsNameConflictsAndHonorsIfNotExists()
    {
        var catalog = CreateCatalog(withTable: true);
        AddView(catalog, "v");
        AddTrigger(catalog, "tr", "t");

        Compile("CREATE VIRTUAL TABLE t USING fts5(body);", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("there is already a table named t");
        Compile("CREATE VIRTUAL TABLE v USING fts5(body);", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("there is already a view named v");
        Compile("CREATE VIRTUAL TABLE tr USING fts5(body);", catalog)
            .Should().Throw<EmbeddedSqlException>().WithMessage("there is already an object named tr");
        Compile("CREATE VIRTUAL TABLE sqlite_x USING fts5(body);", catalog)
            .Should().Throw<EmbeddedSqlException>()
            .WithMessage("object name reserved for internal use: sqlite_x");
    }

    [Test]
    public void DropVirtualTableDeletesItsRowThenDestroysTheModuleStorage()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        var catalog = database.LiveCatalog;

        var compiled = DdlStatementCompiler.CompileDropVirtualTable(
            (DropTableStatement)SqlParser.Parse("DROP TABLE docs;", SqlParameterMap.Parse("DROP TABLE docs;")),
            catalog.VirtualTables["docs"],
            Context(catalog, 5));

        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Delete,
            VdbeOpcode.Goto,
            VdbeOpcode.Next,
            VdbeOpcode.SetCookie,
            VdbeOpcode.VDestroy,
            VdbeOpcode.DropTable,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
        compiled.StagedSchemaVersion.Should().Be(6);
        compiled.VirtualTableBindings.Should().NotBeNull();
        compiled.VirtualTableBindings!.Count(binding => binding is not null).Should().Be(1);
    }

    [Test]
    public void RenameVirtualTableRenamesTheModuleThenRewritesItsSchemaRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        var catalog = database.LiveCatalog;
        var statement = (AlterTableRenameStatement)SqlParser.Parse(
            "ALTER TABLE docs RENAME TO papers;",
            SqlParameterMap.Parse("ALTER TABLE docs RENAME TO papers;"));

        var compiled = DdlStatementCompiler.CompileRenameVirtualTable(
            statement,
            catalog.VirtualTables["docs"],
            new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase),
            Context(catalog, 2));

        Opcodes(compiled).Should().Equal(
            VdbeOpcode.LoadConstant,
            VdbeOpcode.VRename,
            VdbeOpcode.OpenWriteCursor,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Rewind,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Column,
            VdbeOpcode.Compare,
            VdbeOpcode.JumpIfNotTrue,
            VdbeOpcode.Delete,
            VdbeOpcode.Goto,
            VdbeOpcode.Next,
            VdbeOpcode.NewRowid,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.Insert,
            VdbeOpcode.SetCookie,
            VdbeOpcode.RenameTable,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);

        var rename = compiled.Program.Instructions.OfType<RenameTableInstruction>().Single();
        rename.From.Should().Be("docs");
        rename.To.Should().Be("papers");
        compiled.StagedSchemaVersion.Should().Be(3);
    }

    [Test]
    public void RenameVirtualTableAdoptsItsRewrittenDependentsOnlyAfterTheRename()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");
        var catalog = database.LiveCatalog;
        var statement = (AlterTableRenameStatement)SqlParser.Parse(
            "ALTER TABLE docs RENAME TO papers;",
            SqlParameterMap.Parse("ALTER TABLE docs RENAME TO papers;"));
        const string viewSql = "CREATE VIEW docs_view AS SELECT body FROM papers";
        var view = new ViewDefinition(
            "docs_view",
            null,
            ((CreateViewStatement)SqlParser.Parse(viewSql, SqlParameterMap.Parse(viewSql))).Query,
            viewSql);

        var compiled = DdlStatementCompiler.CompileRenameVirtualTable(
            statement,
            catalog.VirtualTables["docs"],
            new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase) { ["docs_view"] = view },
            new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase),
            Context(catalog, 0));

        var opcodes = Opcodes(compiled).ToArray();
        Array.IndexOf(opcodes, VdbeOpcode.ParseSchema)
            .Should()
            .BeGreaterThan(Array.IndexOf(opcodes, VdbeOpcode.RenameTable));
        compiled.PendingObjects.TryGetView("docs_view", out var pending).Should().BeTrue();
        pending.Sql.Should().Be(viewSql);
        // The dependent's row is rewritten in place: one delete scan and one insert per rewritten object.
        compiled.Program.Instructions.OfType<DeleteInstruction>().Should().HaveCount(2);
        compiled.Program.Instructions.OfType<InsertInstruction>().Should().HaveCount(2);
    }

    // ---------------------------------------------------------------- helpers

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static IEnumerable<VdbeOpcode> Opcodes(CompiledSchemaProgram compiled)
        => compiled.Program.Instructions.Select(instruction => instruction.Opcode);

    private static SqlValue[] ReadSchemaRecordColumns(
        CompiledSchemaProgram compiled,
        MakeRecordInstruction makeRecord)
    {
        var values = new SqlValue[makeRecord.Values.Count];
        foreach (var instruction in compiled.Program.Instructions.OfType<LoadConstantInstruction>())
        {
            var offset = instruction.Destination.Index - makeRecord.Values.Start.Index;
            if (offset >= 0 && offset < values.Length)
                values[offset] = instruction.Value;
        }

        return values;
    }

    private static CreateIndexStatement ParseCreateIndex(string sql)
        => (CreateIndexStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql));

    private static CreateVirtualTableStatement ParseCreateVirtualTable(string sql)
        => (CreateVirtualTableStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql));

    private static void AddView(EmbeddedDatabase.SchemaCatalog catalog, string name)
    {
        var sql = $"CREATE VIEW {name} AS SELECT a FROM t";
        var parsed = (CreateViewStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        catalog.Views.Add(name, new ViewDefinition(name, parsed.Columns, parsed.Query, sql));
    }

    private static void AddTrigger(
        EmbeddedDatabase.SchemaCatalog catalog,
        string name,
        string target,
        TriggerTiming timing = TriggerTiming.After,
        long declarationOrder = 0)
    {
        var timingText = timing == TriggerTiming.InsteadOf ? "INSTEAD OF" : timing.ToString().ToUpperInvariant();
        var sql = $"CREATE TRIGGER {name} {timingText} INSERT ON {target} BEGIN SELECT 1; END";
        var parsed = (CreateTriggerStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        catalog.Triggers.Add(
            name,
            new TriggerDefinition(
                name,
                parsed.Timing,
                parsed.Event,
                parsed.UpdateOfColumns,
                target,
                parsed.When,
                parsed.Body,
                sql,
                declarationOrder));
    }

    private static EmbeddedDatabase.SchemaCatalog CreateCatalog(bool withTable = false)
    {
        var catalog = new EmbeddedDatabase.SchemaCatalog(
            new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase));
        if (withTable)
        {
            catalog.Tables.Add(
                "t",
                new EmbeddedTable(
                    "t",
                    [
                        new EmbeddedColumn("a", "INTEGER", false, false, false, null),
                        new EmbeddedColumn("b", "TEXT", false, false, false, null),
                    ]));
        }

        return catalog;
    }

    private static CompiledSchemaProgram CompileCreateView(
        string sql,
        EmbeddedDatabase.SchemaCatalog? catalog = null,
        long schemaVersion = 0)
        => DdlStatementCompiler.CompileCreateView(
            (CreateViewStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
            Context(catalog ?? CreateCatalog(withTable: true), schemaVersion));

    private static CompiledSchemaProgram CompileDropView(
        string sql,
        EmbeddedDatabase.SchemaCatalog catalog,
        long schemaVersion = 0)
        => DdlStatementCompiler.CompileDropView(
            (DropViewStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
            Context(catalog, schemaVersion));

    private static CompiledSchemaProgram CompileCreateTrigger(
        string sql,
        EmbeddedDatabase.SchemaCatalog? catalog = null,
        long schemaVersion = 0)
        => DdlStatementCompiler.CompileCreateTrigger(
            (CreateTriggerStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
            Context(catalog ?? CreateCatalog(withTable: true), schemaVersion));

    private static CompiledSchemaProgram CompileDropTrigger(
        string sql,
        EmbeddedDatabase.SchemaCatalog catalog,
        long schemaVersion = 0)
        => DdlStatementCompiler.CompileDropTrigger(
            (DropTriggerStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
            Context(catalog, schemaVersion));

    private static Action Compile(string sql, EmbeddedDatabase.SchemaCatalog catalog)
        => () =>
        {
            var statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
            var context = Context(catalog, 0);
            _ = statement switch
            {
                CreateViewStatement view => DdlStatementCompiler.CompileCreateView(view, context),
                CreateTriggerStatement trigger => DdlStatementCompiler.CompileCreateTrigger(trigger, context),
                CreateVirtualTableStatement virtualTable =>
                    DdlStatementCompiler.CompileCreateVirtualTable(virtualTable, context),
                _ => throw new InvalidOperationException($"Unexpected statement {statement.GetType().Name}."),
            };
        };

    private static Action CompileDrop(string sql, EmbeddedDatabase.SchemaCatalog catalog)
        => () =>
        {
            var statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
            var context = Context(catalog, 0);
            _ = statement switch
            {
                DropViewStatement view => DdlStatementCompiler.CompileDropView(view, context),
                DropTriggerStatement trigger => DdlStatementCompiler.CompileDropTrigger(trigger, context),
                _ => throw new InvalidOperationException($"Unexpected statement {statement.GetType().Name}."),
            };
        };

    private static DdlCompilationContext Context(EmbeddedDatabase.SchemaCatalog catalog, long schemaVersion)
        => new(catalog, schemaVersion, static _ => { }, static _ => { });
}
