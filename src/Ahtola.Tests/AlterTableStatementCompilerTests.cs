using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Locks the bytecode <see cref="DdlStatementCompiler"/> emits for the five ordinary <c>ALTER TABLE</c>
/// variants against Turso's <c>translate_alter_table</c> (alter.rs:855): the <c>sqlite_schema</c> rows each
/// alteration rewrites, the single schema-cookie bump, the typed schema opcode, and the
/// <c>ParseSchema</c>s that adopt the dependents.
/// </summary>
public sealed class AlterTableStatementCompilerTests
{
    [Test]
    public void RenameTableEmitsTheRowRewriteCookieAndRenameSequence()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        var compiled = CompileRenameTable(database, "ALTER TABLE t RENAME TO u;", schemaVersion: 4);

        compiled.IsNoOp.Should().BeFalse();
        Opcodes(compiled).Should().Equal(
            VdbeOpcode.OpenWriteCursor,
            // The delete scan that finds the table's row and reads the rootpage it must keep.
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
            // The row written back under the new identity, carrying the rootpage the scan captured.
            VdbeOpcode.NewRowid,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.Copy,
            VdbeOpcode.LoadConstant,
            VdbeOpcode.MakeRecord,
            VdbeOpcode.Insert,
            VdbeOpcode.SetCookie,
            VdbeOpcode.RenameTable,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
        compiled.StagedSchemaVersion.Should().Be(5);
    }

    [Test]
    public void EveryAlterVariantBumpsTheSchemaCookieExactlyOnceBeforeItsTypedOpcode()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");

        var compiled = new (CompiledSchemaProgram Program, VdbeOpcode Opcode)[]
        {
            (CompileRenameTable(database, "ALTER TABLE t RENAME TO u;", 9), VdbeOpcode.RenameTable),
            (CompileAddColumn(database, "ALTER TABLE t ADD COLUMN c TEXT;", 9), VdbeOpcode.AddColumn),
            (CompileDropColumn(database, "ALTER TABLE t DROP COLUMN b;", 9), VdbeOpcode.DropColumn),
            (CompileRenameColumn(database, "ALTER TABLE t RENAME COLUMN b TO c;", 9), VdbeOpcode.AlterColumn),
            (CompileAlterColumn(database, "ALTER TABLE t ALTER COLUMN b TO b BLOB;", 9), VdbeOpcode.AlterColumn),
        };

        foreach (var (program, opcode) in compiled)
        {
            var setCookie = program.Program.Instructions
                .OfType<SetCookieInstruction>()
                .Should()
                .ContainSingle()
                .Subject;
            setCookie.Cookie.Should().Be(VdbeSchemaCookie.SchemaVersion);
            setCookie.Value.Should().Be(10);
            program.StagedSchemaVersion.Should().Be(10);

            // Upstream emits SetCookie immediately before the typed effect in every arm of
            // translate_alter_table, so the published version and the schema change are one step apart.
            var opcodes = Opcodes(program).ToArray();
            Array.IndexOf(opcodes, VdbeOpcode.SetCookie)
                .Should()
                .BeLessThan(Array.IndexOf(opcodes, opcode));
        }
    }

    [Test]
    public void RenameTableRewritesTheRowOfEveryObjectTheNewNameAppearsIn()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");
        Execute(connection, "CREATE TABLE child(a INTEGER REFERENCES t(a));");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;");

        var compiled = CompileRenameTable(database, "ALTER TABLE t RENAME TO u;");

        // One scan per rewritten row: the table, its implicit UNIQUE index, its explicit index, the child
        // whose REFERENCES clause names it, the view over it, and the trigger watching it.
        ScanConstants(compiled).Should().Equal(
            "table:t",
            "index:sqlite_autoindex_t_1",
            "index:t_a",
            "table:child",
            "view:v",
            "trigger:tr");
        compiled.Program.Instructions.OfType<DeleteInstruction>().Should().HaveCount(6);
        compiled.Program.Instructions.OfType<InsertInstruction>().Should().HaveCount(6);
    }

    [Test]
    public void RenameTableAdoptsTheDependentsItRewroteThroughParseSchema()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE TABLE child(a INTEGER REFERENCES t(a));");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;");

        var compiled = CompileRenameTable(database, "ALTER TABLE t RENAME TO u;");

        compiled.Program.Instructions
            .OfType<ParseSchemaInstruction>()
            .Select(instruction => instruction.WhereClause)
            .Should()
            .Equal(
                "name = 'child' AND type = 'table'",
                "name = 'v' AND type = 'view'",
                "name = 'tr' AND type = 'trigger'");
        compiled.PendingObjects.TryGetTable("child", out _).Should().BeTrue();
        compiled.PendingObjects.TryGetView("v", out _).Should().BeTrue();
        compiled.PendingObjects.TryGetTrigger("tr", out _).Should().BeTrue();

        // Every dependent is adopted after the rename has published the new name, because a rewritten
        // trigger's target has to resolve against the schema the rename produced.
        var opcodes = Opcodes(compiled).ToArray();
        Array.IndexOf(opcodes, VdbeOpcode.ParseSchema)
            .Should()
            .BeGreaterThan(Array.IndexOf(opcodes, VdbeOpcode.RenameTable));
    }

    [Test]
    public void RenameTableRewritesTheAutoIncrementBackingTableRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('a');");

        var compiled = CompileRenameTable(database, "ALTER TABLE t RENAME TO u;");

        ScanConstants(compiled).Should().Contain(
            $"table:{EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName("t")}");
        var written = compiled.Program.Instructions
            .OfType<LoadConstantInstruction>()
            .Where(instruction => instruction.Value.Kind == SqlValueKind.Text)
            .Select(instruction => instruction.Value.AsText())
            .ToArray();
        written.Should().Contain(EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName("u"));
    }

    [Test]
    public void AddColumnRewritesOnlyTheTableRowAndCarriesTheAddedColumnText()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        var compiled = CompileAddColumn(database, "ALTER TABLE t ADD COLUMN b TEXT DEFAULT 'x';");

        ScanConstants(compiled).Should().Equal("table:t");
        var addColumn = compiled.Program.Instructions
            .OfType<AddColumnInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        addColumn.TableName.Should().Be("t");
        addColumn.ColumnName.Should().Be("b");
        addColumn.ColumnDefinition.Should().Be("b TEXT DEFAULT 'x'");
        addColumn.ColumnSql.Should().Be("b TEXT DEFAULT 'x'");
        compiled.Program.Instructions.OfType<ParseSchemaInstruction>().Should().BeEmpty();
    }

    [Test]
    public void DropColumnCarriesTheDroppedOrdinalAndRewritesTheTableRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, c BLOB);");

        var compiled = CompileDropColumn(database, "ALTER TABLE t DROP COLUMN b;");

        ScanConstants(compiled).Should().Equal("table:t");
        var dropColumn = compiled.Program.Instructions
            .OfType<DropColumnInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        dropColumn.TableName.Should().Be("t");
        dropColumn.ColumnIndex.Should().Be(1);
    }

    [Test]
    public void RenameColumnCarriesTheQuotingDecisionAndRewritesEveryDependentRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE VIEW v AS SELECT b FROM t;");

        var compiled = CompileRenameColumn(database, "ALTER TABLE t RENAME COLUMN b TO \"c d\";");

        var alterColumn = compiled.Program.Instructions
            .OfType<AlterColumnInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        alterColumn.ColumnIndex.Should().Be(1);
        alterColumn.ColumnDefinition.Should().Be("c d");
        alterColumn.Rename.Should().BeTrue();
        alterColumn.QuoteNewName.Should().BeTrue();
        ScanConstants(compiled).Should().Equal("table:t", "view:v");
    }

    [Test]
    public void AlterColumnCarriesTheReplacementTextVerbatim()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");

        var compiled = CompileAlterColumn(database, "ALTER TABLE t ALTER COLUMN b TO b BLOB NOT NULL DEFAULT x'00';");

        var alterColumn = compiled.Program.Instructions
            .OfType<AlterColumnInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        alterColumn.ColumnIndex.Should().Be(1);
        alterColumn.ColumnDefinition.Should().Be("b BLOB NOT NULL DEFAULT x'00'");
        alterColumn.Rename.Should().BeFalse();
        alterColumn.QuoteNewName.Should().BeFalse();
    }

    [Test]
    public void AlterColumnRetiringAutoIncrementClearsTheWatermarkAndDestroysTheBackingTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "INSERT INTO t(v) VALUES ('a');");

        var compiled = CompileAlterColumn(database, "ALTER TABLE t ALTER COLUMN id TO id INTEGER;");

        // The watermark rows go through their own backward delete scan, exactly as DROP TABLE clears one.
        Opcodes(compiled).Should().Contain(VdbeOpcode.Last);
        Opcodes(compiled).Should().Contain(VdbeOpcode.Prev);
        compiled.TableScans.Should().ContainSingle()
            .Which.TableName.Should().Be(EmbeddedDatabase.SqliteSequenceTableName);
        ScanConstants(compiled).Should().Contain(
            $"table:{EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName("t")}");
        compiled.Program.Instructions.OfType<DestroyInstruction>().Should().ContainSingle();
        compiled.Program.Instructions.OfType<DropTableInstruction>().Should().ContainSingle();
    }

    [Test]
    public void EveryAlterProgramOpensExactlyOneSchemaCursorForWriting()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");

        foreach (var compiled in new[]
        {
            CompileRenameTable(database, "ALTER TABLE t RENAME TO u;"),
            CompileAddColumn(database, "ALTER TABLE t ADD COLUMN c TEXT;"),
            CompileDropColumn(database, "ALTER TABLE t DROP COLUMN b;"),
            CompileRenameColumn(database, "ALTER TABLE t RENAME COLUMN b TO c;"),
            CompileAlterColumn(database, "ALTER TABLE t ALTER COLUMN b TO b BLOB;"),
        })
        {
            var open = compiled.Program.Instructions
                .OfType<OpenWriteCursorInstruction>()
                .Should()
                .ContainSingle()
                .Subject;
            open.TableName.Should().Be("sqlite_schema");
            open.ColumnCount.Should().Be(5);
            open.Cursor.Should().Be(compiled.SchemaCursor);
            compiled.Program.Instructions.OfType<CloseCursorInstruction>().Should().ContainSingle();
        }
    }

    [Test]
    public void AlterRowWritesAreInvisibleToChangesAndLastInsertRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        var compiled = CompileAddColumn(database, "ALTER TABLE t ADD COLUMN b TEXT;");

        compiled.Program.Instructions
            .OfType<InsertInstruction>()
            .Should()
            .AllSatisfy(insert =>
            {
                insert.Flags.Should().HaveFlag(VdbeInsertFlags.SkipLastRowid);
                insert.Flags.Should().HaveFlag(VdbeInsertFlags.SkipAllChangeCounts);
            });
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

    /// <summary>
    /// The <c>type:name</c> pair each <c>sqlite_schema</c> delete scan searches for, in emission order. A
    /// scan loads its expected name and then its expected type, so a name constant is recognized by the
    /// type constant that follows it; a constant loaded for a rewritten row's own columns has no type
    /// after it and is skipped.
    /// </summary>
    private static string[] ScanConstants(CompiledSchemaProgram compiled)
    {
        var constants = compiled.Program.Instructions
            .OfType<LoadConstantInstruction>()
            .Where(instruction => instruction.Value.Kind == SqlValueKind.Text)
            .Select(instruction => instruction.Value.AsText())
            .ToArray();
        var pairs = new List<string>();
        for (var index = 0; index + 1 < constants.Length; index++)
        {
            if (constants[index + 1] is not (ManagedSchemaRow.TableType
                or ManagedSchemaRow.IndexType
                or ManagedSchemaRow.TriggerType
                or ManagedSchemaRow.ViewType))
            {
                continue;
            }

            pairs.Add($"{constants[index + 1]}:{constants[index]}");
            index++;
        }

        return [.. pairs];
    }

    private static CompiledSchemaProgram CompileRenameTable(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => Compile(database, sql, schemaVersion);

    private static CompiledSchemaProgram CompileAddColumn(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => Compile(database, sql, schemaVersion);

    private static CompiledSchemaProgram CompileDropColumn(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => Compile(database, sql, schemaVersion);

    private static CompiledSchemaProgram CompileRenameColumn(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => Compile(database, sql, schemaVersion);

    private static CompiledSchemaProgram CompileAlterColumn(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => Compile(database, sql, schemaVersion);

    /// <summary>
    /// Lowers an <c>ALTER TABLE</c> the way the connection does: the plan is computed against the live
    /// catalog first, and the compiler turns it into bytecode.
    /// </summary>
    private static CompiledSchemaProgram Compile(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion)
    {
        var statement = SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        var catalog = database.LiveCatalog;
        var context = new EmbeddedDatabase.QueryContext(
            catalog.Tables,
            new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
            catalog.Views,
            catalog.Triggers)
        {
            VirtualTables = catalog.VirtualTables,
        };
        return database.CompileAlterTable(
            statement,
            catalog,
            [],
            context,
            new DdlCompilationContext(catalog, schemaVersion, static _ => { }, static _ => { }));
    }
}
