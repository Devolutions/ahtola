using Ahtola.Core;
using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Locks the bytecode <see cref="DdlStatementCompiler"/> emits for <c>DROP TABLE</c> against Turso's
/// <c>translate_drop_table</c> (schema.rs:1816) and the sequence cleanup it delegates to
/// (<c>emit_drop_sequence_cleanup</c>, sequence.rs:917), together with the compile-time validation that
/// decides whether a program is emitted at all.
/// </summary>
public sealed class DropTableStatementCompilerTests
{
    [Test]
    public void DropTableEmitsTheUpstreamScanDestroyAndEvictSequence()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        var compiled = CompileDropTable(database, "DROP TABLE t;", schemaVersion: 4);

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
            VdbeOpcode.Destroy,
            VdbeOpcode.DropTable,
            VdbeOpcode.SetCookie,
            VdbeOpcode.CloseCursor,
            VdbeOpcode.Halt);
        compiled.StagedSchemaVersion.Should().Be(5);
    }

    [Test]
    public void DropTableBumpsTheSchemaCookieExactlyOnceAndLast()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");
        Execute(connection, "CREATE INDEX t_v ON t(v);");
        Execute(connection, "CREATE TRIGGER t_after AFTER INSERT ON t BEGIN SELECT 1; END;");

        var compiled = CompileDropTable(database, "DROP TABLE t;", schemaVersion: 9);

        var setCookie = compiled.Program.Instructions
            .OfType<SetCookieInstruction>()
            .Should()
            .ContainSingle()
            .Subject;
        setCookie.Cookie.Should().Be(VdbeSchemaCookie.SchemaVersion);
        setCookie.Value.Should().Be(10);

        // Upstream emits the bump as the final instruction of translate_drop_table, after every eviction
        // the statement performs, so a multi-object drop still publishes one schema version.
        var opcodes = Opcodes(compiled).ToArray();
        Array.IndexOf(opcodes, VdbeOpcode.SetCookie)
            .Should()
            .BeGreaterThan(Array.LastIndexOf(opcodes, VdbeOpcode.DropTable));
    }

    [Test]
    public void DropTableDestroysEveryRootTheScansCapturedInRegisters()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");

        var compiled = CompileDropTable(database, "DROP TABLE t;");

        // One scan per object that owns a b-tree — the table, its explicit index and the implicit UNIQUE
        // index — each reading the rootpage column into its own register before the row is deleted.
        var capturedRoots = compiled.Program.Instructions
            .OfType<ColumnInstruction>()
            .Where(column => column.ColumnIndex == SchemaRootPageColumn)
            .Select(column => column.Destination)
            .ToArray();
        capturedRoots.Should().HaveCount(3);
        capturedRoots.Should().OnlyHaveUniqueItems();

        var destroys = compiled.Program.Instructions.OfType<DestroyInstruction>().ToArray();
        destroys.Should().HaveCount(3);
        destroys.Should().AllSatisfy(destroy =>
        {
            // Ahtola assigns roots at commit, so nothing is a translate-time literal: the register the
            // scan filled is the only place the retiring root is known.
            destroy.RootPage.Should().Be(0);
            destroy.IsTemporary.Should().BeFalse();
            destroy.RootRegister.Should().NotBeNull();
        });
        destroys.Select(destroy => destroy.RootRegister!.Value)
            .Should()
            .BeEquivalentTo(capturedRoots);
    }

    [Test]
    public void DropTableDeletesTheRowOfEveryObjectItTakesWithIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT UNIQUE);");
        Execute(connection, "CREATE INDEX t_a ON t(a);");
        Execute(connection, "CREATE TRIGGER t_after AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "CREATE TABLE other(a INTEGER);");
        Execute(connection, "CREATE TRIGGER other_after AFTER INSERT ON other BEGIN SELECT 1; END;");

        var compiled = CompileDropTable(database, "DROP TABLE t;");

        // The table row, both index rows and the trigger row that watched it; the unrelated table's
        // trigger is left alone.
        ScanConstants(compiled).Should().Equal(
            "table:t",
            "index:sqlite_autoindex_t_1",
            "index:t_a",
            "trigger:t_after");
        compiled.Program.Instructions
            .OfType<DropTriggerInstruction>()
            .Select(instruction => instruction.TriggerName)
            .Should()
            .Equal("t_after");
        compiled.Program.Instructions
            .OfType<DropTableInstruction>()
            .Select(instruction => instruction.TableName)
            .Should()
            .Equal("t");
        // Evicting the table takes its indexes with it, exactly as upstream's DropTable does.
        compiled.Program.Instructions.OfType<DropIndexInstruction>().Should().BeEmpty();
    }

    [Test]
    public void DropTableScansOutTheSequenceWatermarkAndTearsDownItsBackingTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT);");

        var compiled = CompileDropTable(database, "DROP TABLE t;");

        // sqlite_sequence is scanned through its own write cursor: its watermark is an ordinary row, not
        // a schema row.
        var sequenceScan = compiled.TableScans.Should().ContainSingle().Subject;
        sequenceScan.TableName.Should().Be("sqlite_sequence");
        compiled.Program.Instructions
            .OfType<OpenWriteCursorInstruction>()
            .Select(instruction => instruction.TableName)
            .Should()
            .Equal("sqlite_schema", "sqlite_sequence");
        compiled.Program.Instructions.OfType<LastCursorInstruction>()
            .Should()
            .ContainSingle()
            .Which.Cursor.Should().Be(sequenceScan.Cursor);
        compiled.Program.Instructions.OfType<PrevInstruction>().Should().ContainSingle();

        // The implicit backing table is torn down the way a sequence is: row, b-tree, catalog entry.
        var backingName = EmbeddedDatabase.GetAutoIncrementSequenceBackingTableName("t");
        ScanConstants(compiled).Should().Equal("table:t", $"table:{backingName}");
        compiled.Program.Instructions
            .OfType<DropTableInstruction>()
            .Select(instruction => instruction.TableName)
            .Should()
            .Equal("t", backingName);
        compiled.Program.Instructions.OfType<DestroyInstruction>().Should().HaveCount(2);
    }

    [Test]
    public void DropTableScansOutTheChangeCaptureVersionEntryWhenThatTableExists()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "PRAGMA capture_data_changes_conn('full');");

        var compiled = CompileDropTable(database, "DROP TABLE t;");

        compiled.TableScans
            .Select(scan => scan.TableName)
            .Should()
            .Equal("turso_cdc_version");
    }

    [Test]
    public void DropTableOnTheChangeCaptureVersionTableTakesItsOwnRowsWithIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "PRAGMA capture_data_changes_conn('full');");

        var compiled = CompileDropTable(database, "DROP TABLE turso_cdc_version;");

        // Scanning the version table for its own name would be a self-referential no-op: dropping it
        // removes every row it holds.
        compiled.TableScans.Should().BeEmpty();
    }

    [Test]
    public void DropTableDestroysAMethodIndexThroughItsOwnLifecycleOpcode()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        Execute(connection, "CREATE INDEX docs_knn ON docs USING vector (embedding) WITH (dims = 4);");

        var compiled = CompileDropTable(database, "DROP TABLE docs;");

        var opcodes = Opcodes(compiled).ToArray();
        opcodes.Should().Contain(VdbeOpcode.IndexMethodDestroy);
        // The method's own state is retired before its backing b-tree, and both before the table leaves
        // the catalog the deferred binding resolves through.
        Array.IndexOf(opcodes, VdbeOpcode.IndexMethodDestroy)
            .Should()
            .BeLessThan(Array.IndexOf(opcodes, VdbeOpcode.Destroy));
        Array.IndexOf(opcodes, VdbeOpcode.IndexMethodDestroy)
            .Should()
            .BeLessThan(Array.IndexOf(opcodes, VdbeOpcode.DropTable));
        compiled.OperationsSlot.Should().NotBeNull();
    }

    [Test]
    public void DropTableWithoutAMethodIndexNeedsNoDeferredBindings()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        var compiled = CompileDropTable(database, "DROP TABLE t;");

        compiled.OperationsSlot.Should().BeNull();
        compiled.TableScans.Should().BeEmpty();
        compiled.VirtualTableBindings.Should().BeNull();
    }

    [Test]
    public void DropTableSearchesForTheStoredSpellingWhateverCaseTheStatementUsed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE MixedCase(a INTEGER);");

        var compiled = CompileDropTable(database, "DROP TABLE mixedcase;");

        // The scan compares the name column with BINARY semantics, so it has to search for the case the
        // table was declared with.
        ScanConstants(compiled).Should().Equal("table:MixedCase");
        compiled.Program.Instructions
            .OfType<DropTableInstruction>()
            .Should()
            .ContainSingle()
            .Which.TableName.Should().Be("MixedCase");
    }

    [Test]
    public void DropTableIfExistsOnAMissingTableCompilesToANoOpProgram()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        var compiled = CompileDropTable(database, "DROP TABLE IF EXISTS missing;", schemaVersion: 3);

        compiled.IsNoOp.Should().BeTrue();
        Opcodes(compiled).Should().Equal(VdbeOpcode.Halt);
        compiled.StagedSchemaVersion.Should().Be(3);
    }

    [Test]
    public void DropTableRejectsTheObjectsThatAreNotItsToDrop()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT);");
        Execute(connection, "CREATE VIEW v AS SELECT id FROM t;");

        Compile(database, "DROP TABLE missing;")
            .Should().Throw<EmbeddedSqlException>().WithMessage("no such table: missing");
        Compile(database, "DROP TABLE v;")
            .Should().Throw<EmbeddedSqlException>().WithMessage("use DROP VIEW to delete view v");
        Compile(database, "DROP TABLE sqlite_sequence;")
            .Should().Throw<EmbeddedSqlException>().WithMessage("table sqlite_sequence may not be dropped");
        // IF EXISTS forgives a missing table, not a wrong-kind object.
        Compile(database, "DROP TABLE IF EXISTS v;")
            .Should().Throw<EmbeddedSqlException>().WithMessage("use DROP VIEW to delete view v");
    }

    [Test]
    public void DropTableDispatchesTheVirtualArmToItsOwnProgram()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE docs USING fts5(body);");

        var compiled = CompileDropTable(database, "DROP TABLE docs;");

        // Upstream's Table::Virtual match arm: the module's storage goes through VDestroy, and no b-tree
        // root is ever destroyed because a virtual table owns none.
        Opcodes(compiled).Should().Contain(VdbeOpcode.VDestroy);
        compiled.Program.Instructions.OfType<DestroyInstruction>().Should().BeEmpty();
        compiled.VirtualTableBindings.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The <c>sqlite_schema</c> rootpage column a delete scan reads before it deletes the row.</summary>
    private const int SchemaRootPageColumn = 3;

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
    /// type constant that follows it; the lone table-name constant a <c>sqlite_sequence</c> scan loads has
    /// no type after it and is skipped.
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

    private static CompiledSchemaProgram CompileDropTable(
        EmbeddedDatabase database,
        string sql,
        long schemaVersion = 0)
        => DdlStatementCompiler.CompileDropTable(
            (DropTableStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql)),
            new DdlCompilationContext(database.LiveCatalog, schemaVersion, static _ => { }, static _ => { }));

    private static Action Compile(EmbeddedDatabase database, string sql)
        => () => CompileDropTable(database, sql);
}
