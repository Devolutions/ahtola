using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// Builds the instruction stream of a schema (DDL) program: it owns register and cursor allocation, the
/// forward-jump patching a branch needs, and the composite emit helpers whose shape must match Turso's
/// translators exactly.
/// </summary>
/// <remarks>
/// <para>
/// This is the managed counterpart of upstream's <c>ProgramBuilder</c> as the schema translators use it
/// (<c>core/translate/schema.rs</c>). It deliberately knows nothing about catalogs, storage, or
/// validation: it turns an already-decided plan into bytecode. Deciding the plan — resolving names,
/// rejecting duplicates, building the table definition — is <see cref="DdlStatementCompiler"/>'s job.
/// </para>
/// <para>
/// Register allocation is strictly bump-allocated and never reused, exactly as
/// <c>ProgramBuilder::alloc_register</c> is, so an emitted program's register numbering is deterministic
/// and its <c>EXPLAIN</c> is stable across runs.
/// </para>
/// </remarks>
internal sealed class SchemaProgramBuilder
{
    private readonly List<VdbeInstruction> _instructions = [];
    private int _registerCount;
    private int _cursorCount;

    /// <param name="database">The routed database index every schema instruction addresses.</param>
    public SchemaProgramBuilder(int database)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(database);
        Database = database;
    }

    /// <summary>The routed database index this program's schema instructions address.</summary>
    public int Database { get; }

    /// <summary>The offset the next emitted instruction will occupy.</summary>
    public ProgramCounter NextOffset => new(_instructions.Count);

    public Register AllocateRegister() => new(_registerCount++);

    public Cursor AllocateCursor() => new(_cursorCount++);

    public void Emit(VdbeInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        _instructions.Add(instruction);
    }

    /// <summary>Loads a constant into a freshly allocated register (upstream <c>emit_*_new_reg</c>).</summary>
    public Register EmitConstant(SqlValue value)
    {
        var register = AllocateRegister();
        Emit(new LoadConstantInstruction(register, value));
        return register;
    }

    /// <summary>
    /// Emits a jump whose target is not known yet and returns the slot to patch. Upstream allocates a
    /// label and resolves it later; the managed program has no label indirection, so the instruction is
    /// rewritten in place once the target offset exists.
    /// </summary>
    public int EmitForwardJumpIf(Register register)
    {
        var slot = _instructions.Count;
        Emit(new JumpIfInstruction(register, new ProgramCounter(0)));
        return slot;
    }

    /// <summary>Emits a forward <c>Rewind</c> whose empty-target is patched later.</summary>
    public int EmitForwardRewind(Cursor cursor)
    {
        var slot = _instructions.Count;
        Emit(new RewindCursorInstruction(cursor, new ProgramCounter(0)));
        return slot;
    }

    /// <summary>Emits a forward <c>Last</c> whose empty-target is patched later.</summary>
    public int EmitForwardLast(Cursor cursor)
    {
        var slot = _instructions.Count;
        Emit(new LastCursorInstruction(cursor, new ProgramCounter(0)));
        return slot;
    }

    /// <summary>Emits a forward <c>JumpIfNotTrue</c> whose target is patched later.</summary>
    public int EmitForwardJumpIfNotTrue(Register register)
    {
        var slot = _instructions.Count;
        Emit(new JumpIfNotTrueInstruction(register, new ProgramCounter(0)));
        return slot;
    }

    /// <summary>Emits a forward <c>Goto</c> whose target is patched later.</summary>
    public int EmitForwardGoto()
    {
        var slot = _instructions.Count;
        Emit(new GotoInstruction(new ProgramCounter(0)));
        return slot;
    }

    /// <summary>Resolves a jump reserved by one of the <c>EmitForward*</c> helpers.</summary>
    public void PatchJump(int slot, ProgramCounter target)
    {
        _instructions[slot] = _instructions[slot] switch
        {
            JumpIfInstruction jump => jump with { Target = target },
            JumpIfNotTrueInstruction jump => jump with { FalseTarget = target },
            GotoInstruction jump => jump with { Target = target },
            RewindCursorInstruction rewind => rewind with { EmptyTarget = target },
            LastCursorInstruction last => last with { EmptyTarget = target },
            var other => throw new InvalidOperationException(
                $"VDBE instruction {slot} is a {other.Opcode}, which carries no patchable jump target."),
        };
    }

    /// <summary>
    /// Emits the first-write header initialization Turso emits at the head of every <c>CREATE TABLE</c>
    /// (schema.rs:1247-1271): when the database format cookie is still zero the database has never been
    /// written, so the format and text-encoding cookies are established before any b-tree is allocated.
    /// </summary>
    public void EmitDatabaseFormatInitialization()
    {
        var format = AllocateRegister();
        Emit(new ReadCookieInstruction(Database, format, VdbeSchemaCookie.DatabaseFormat));
        var jump = EmitForwardJumpIf(format);
        Emit(new SetCookieInstruction(Database, VdbeSchemaCookie.DatabaseFormat, SqliteSchemaFormat));
        Emit(new SetCookieInstruction(Database, VdbeSchemaCookie.DatabaseTextEncoding, Utf8TextEncoding));
        PatchJump(jump, NextOffset);
    }

    /// <summary>Allocates a b-tree root into a fresh register (upstream <c>Insn::CreateBtree</c>).</summary>
    public Register EmitCreateBtree(VdbeCreateBtreeFlags flags)
    {
        var root = AllocateRegister();
        Emit(new CreateBtreeInstruction(Database, root, flags));
        return root;
    }

    /// <summary>
    /// Emits one <c>sqlite_schema</c> row, mirroring upstream's <c>emit_schema_entry</c>
    /// (schema.rs:1510): allocate the rowid, materialize the five columns into a contiguous register
    /// block, pack them with <c>MakeRecord</c>, and write the record through the schema cursor.
    /// </summary>
    /// <param name="schemaCursor">A cursor opened for writing on <c>sqlite_schema</c>.</param>
    /// <param name="entryType">The row's <c>type</c> column.</param>
    /// <param name="name">The row's <c>name</c> column.</param>
    /// <param name="tableName">The row's <c>tbl_name</c> column.</param>
    /// <param name="rootRegister">
    /// The register holding the root <c>CreateBtree</c> allocated, or <see langword="null"/> for an object
    /// with no b-tree of its own, which SQLite stores with rootpage 0.
    /// </param>
    /// <param name="sql">The row's <c>sql</c> column; <see langword="null"/> for an implicit index.</param>
    public void EmitSchemaEntry(
        Cursor schemaCursor,
        string entryType,
        string name,
        string tableName,
        Register? rootRegister,
        string? sql)
    {
        var rowId = AllocateRegister();
        Emit(new NewRowidInstruction(schemaCursor, rowId));

        var type = EmitConstant(SqlValue.Text(entryType));
        EmitConstant(SqlValue.Text(name));
        EmitConstant(SqlValue.Text(tableName));

        var root = AllocateRegister();
        if (rootRegister is { } source)
            Emit(new CopyInstruction(source, root));
        else
            Emit(new LoadConstantInstruction(root, SqlValue.Integer(0)));

        var sqlRegister = AllocateRegister();
        Emit(new LoadConstantInstruction(sqlRegister, sql is null ? SqlValue.Null : SqlValue.Text(sql)));

        var record = AllocateRegister();
        Emit(new MakeRecordInstruction(
            new RegisterRange(type, ManagedSchemaProgramBindings.SchemaColumnCount),
            record));
        Emit(new InsertInstruction(
            schemaCursor,
            SchemaWriteFlags,
            record,
            rowId,
            ManagedSchemaProgramBindings.SchemaTableName));
    }

    /// <summary>
    /// Emits the per-row population loop shared by <c>CREATE TABLE AS SELECT</c> and the seeded internal
    /// tables: scan <paramref name="sourceCursor"/>, pack each row with <c>MakeRecord</c>, allocate its
    /// rowid with <c>NewRowid</c>, and write it through <paramref name="targetCursor"/>. It is upstream's
    /// <c>emit_ctas_insert</c> body (schema.rs:1046-1091); the managed source cursor plays the role of the
    /// coroutine upstream yields rows from.
    /// </summary>
    public void EmitPopulationLoop(
        Cursor sourceCursor,
        Cursor targetCursor,
        string targetTableName,
        int columnCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTableName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);

        var values = AllocateRegister();
        for (var column = 1; column < columnCount; column++)
            AllocateRegister();
        var record = AllocateRegister();
        var rowId = AllocateRegister();

        var rewind = EmitForwardRewind(sourceCursor);
        var loopStart = NextOffset;
        for (var column = 0; column < columnCount; column++)
            Emit(new ColumnInstruction(sourceCursor, column, new Register(values.Index + column)));

        Emit(new MakeRecordInstruction(new RegisterRange(values, columnCount), record));
        Emit(new NewRowidInstruction(targetCursor, rowId));
        Emit(new InsertInstruction(targetCursor, SchemaWriteFlags, record, rowId, targetTableName));
        Emit(new NextInstruction(sourceCursor, loopStart));
        PatchJump(rewind, NextOffset);
    }

    /// <summary>
    /// Emits the <c>sqlite_schema</c> scan that deletes the single row of type <paramref name="entryType"/>
    /// named <paramref name="name"/>, and returns the register holding that row's <c>rootpage</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is upstream's DROP-INDEX loop (index.rs:1285-1367): rewind the schema cursor, compare the
    /// <c>name</c> and <c>type</c> columns against constants, skip to the next row on a mismatch, and
    /// delete the row that matches. Upstream guards the delete with <c>Once</c> because it keeps scanning;
    /// the managed loop jumps straight out instead, which is equivalent for a namespace where the name is
    /// unique and avoids advancing a cursor over a row set that just shrank underneath it.
    /// </para>
    /// <para>
    /// The <c>rootpage</c> is read <em>before</em> the delete because it is the only place the retiring
    /// b-tree's root is recorded, and Ahtola — which assigns roots at commit — has no translate-time
    /// literal for it.
    /// </para>
    /// </remarks>
    public Register EmitSchemaRowDeleteScan(Cursor schemaCursor, string entryType, string name)
        => EmitSchemaRowDeleteScan(schemaCursor, entryType, name, captureRootPage: true)!.Value;

    /// <summary>
    /// Emits the same delete scan for an object SQLite stores with rootpage 0 — a view, a trigger, or a
    /// virtual table — which has no b-tree for a later <c>Destroy</c> to retire and therefore reads no
    /// <c>rootpage</c> column.
    /// </summary>
    public void EmitSchemaRowDelete(Cursor schemaCursor, string entryType, string name)
        => EmitSchemaRowDeleteScan(schemaCursor, entryType, name, captureRootPage: false);

    private Register? EmitSchemaRowDeleteScan(
        Cursor schemaCursor,
        string entryType,
        string name,
        bool captureRootPage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryType);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var expectedName = EmitConstant(SqlValue.Text(name));
        var expectedType = EmitConstant(SqlValue.Text(entryType));
        var column = AllocateRegister();
        var matches = AllocateRegister();
        Register? rootPage = null;
        if (captureRootPage)
        {
            rootPage = AllocateRegister();
            // A scan that matches nothing leaves the root register holding a value Destroy must reject
            // rather than mistake for page 0.
            Emit(new LoadConstantInstruction(rootPage.Value, SqlValue.Null));
        }

        var rewind = EmitForwardRewind(schemaCursor);
        var loopStart = NextOffset;

        Emit(new ColumnInstruction(schemaCursor, SchemaNameColumn, column));
        Emit(new CompareInstruction(
            matches,
            VdbeComparisonOperator.Equal,
            expectedName,
            column,
            LeftAffinity: null,
            RightAffinity: null,
            Collation: null));
        var nameMismatch = EmitForwardJumpIfNotTrue(matches);

        Emit(new ColumnInstruction(schemaCursor, SchemaTypeColumn, column));
        Emit(new CompareInstruction(
            matches,
            VdbeComparisonOperator.Equal,
            expectedType,
            column,
            LeftAffinity: null,
            RightAffinity: null,
            Collation: null));
        var typeMismatch = EmitForwardJumpIfNotTrue(matches);

        if (rootPage is { } destination)
            Emit(new ColumnInstruction(schemaCursor, SchemaRootPageColumn, destination));
        Emit(new DeleteInstruction(schemaCursor));
        var found = EmitForwardGoto();

        PatchJump(nameMismatch, NextOffset);
        PatchJump(typeMismatch, NextOffset);
        Emit(new NextInstruction(schemaCursor, loopStart));

        PatchJump(rewind, NextOffset);
        PatchJump(found, NextOffset);
        return rootPage;
    }

    /// <summary>
    /// Emits the rewrite of one <c>sqlite_schema</c> row: the row is found and deleted where it stands,
    /// then written back carrying its new identity and text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream expresses the same edit as <c>UPDATE sqlite_schema SET sql = '…' WHERE name = '…'</c>
    /// lowered through <c>translate_update_for_schema_change</c> (alter.rs:1148), and as an explicit
    /// <c>cursor_loop</c> that rebuilds the five columns and re-inserts them under the row's own rowid
    /// (alter.rs:1637-1700). Both reduce to delete-then-insert — upstream emits exactly that pair under
    /// MVCC — which is the single form the managed schema cursor supports, so one helper covers both.
    /// </para>
    /// <para>
    /// <paramref name="ownsRootPage"/> selects which of the two delete scans runs. A table or an index
    /// keeps the b-tree it already had across an <c>ALTER</c>, so its <c>rootpage</c> is read into a
    /// register before the row goes and copied back into the row that replaces it; a view, a trigger or a
    /// virtual table has no root to carry and is stored with rootpage 0.
    /// </para>
    /// </remarks>
    /// <param name="schemaCursor">A cursor opened for writing on <c>sqlite_schema</c>.</param>
    /// <param name="entryType">The row's <c>type</c> column, which never changes across an ALTER.</param>
    /// <param name="currentName">The stored spelling the scan searches for.</param>
    /// <param name="name">The row's <c>name</c> column afterwards.</param>
    /// <param name="tableName">The row's <c>tbl_name</c> column afterwards.</param>
    /// <param name="sql">The row's <c>sql</c> column afterwards; null for an implicit index.</param>
    /// <param name="ownsRootPage">Whether the row's <c>rootpage</c> must be carried across the rewrite.</param>
    public void EmitSchemaRowRewrite(
        Cursor schemaCursor,
        string entryType,
        string currentName,
        string name,
        string tableName,
        string? sql,
        bool ownsRootPage)
    {
        if (!ownsRootPage)
        {
            EmitSchemaRowDelete(schemaCursor, entryType, currentName);
            EmitSchemaEntry(schemaCursor, entryType, name, tableName, rootRegister: null, sql);
            return;
        }

        var rootPage = EmitSchemaRowDeleteScan(schemaCursor, entryType, currentName);
        EmitSchemaEntry(schemaCursor, entryType, name, tableName, rootPage, sql);
    }

    /// <summary>
    /// Emits the scan that deletes every row of an ordinary table whose column
    /// <paramref name="matchColumn"/> equals <paramref name="matchValue"/>, comparing with BINARY
    /// semantics. It is the loop upstream emits over <c>sqlite_sequence</c> when an AUTOINCREMENT table is
    /// dropped (schema.rs:2312-2343) and again over the change-capture version table (schema.rs:2389-2444).
    /// </summary>
    /// <remarks>
    /// The scan runs <em>backward</em> — <c>Last</c>/<c>Prev</c> — where upstream runs forward. A managed
    /// cursor reads a live view of the staged rows, so deleting the row a forward scan is positioned on
    /// shifts the next row into that slot and <c>Next</c> would step straight over it. Walking backward is
    /// unaffected: a deletion at or after the cursor cannot move a row the scan has not visited yet, so
    /// every row is examined exactly once and every match is deleted.
    /// </remarks>
    public void EmitTableRowDeleteScan(Cursor cursor, int matchColumn, string matchValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matchColumn);
        ArgumentNullException.ThrowIfNull(matchValue);

        var expected = EmitConstant(SqlValue.Text(matchValue));
        var column = AllocateRegister();
        var matches = AllocateRegister();

        var last = EmitForwardLast(cursor);
        var loopStart = NextOffset;
        Emit(new ColumnInstruction(cursor, matchColumn, column));
        Emit(new CompareInstruction(
            matches,
            VdbeComparisonOperator.Equal,
            expected,
            column,
            LeftAffinity: null,
            RightAffinity: null,
            Collation: null));
        var mismatch = EmitForwardJumpIfNotTrue(matches);
        Emit(new DeleteInstruction(cursor));

        PatchJump(mismatch, NextOffset);
        Emit(new PrevInstruction(cursor, loopStart));
        PatchJump(last, NextOffset);
    }

    /// <summary>Finishes the program, appending the terminating <c>Halt</c>.</summary>
    public VdbeProgram Build()
    {
        Emit(new HaltInstruction());
        return new VdbeProgram(_registerCount, _cursorCount, _instructions);
    }

    /// <summary>The schema format SQLite writes for a database created by this engine.</summary>
    private const int SqliteSchemaFormat = 4;

    /// <summary>The text-encoding cookie value for UTF-8.</summary>
    private const int Utf8TextEncoding = 1;

    /// <summary>The <c>sqlite_schema</c> column ordinals a schema scan reads.</summary>
    private const int SchemaTypeColumn = 0;
    private const int SchemaNameColumn = 1;
    private const int SchemaRootPageColumn = 3;

    /// <summary>
    /// DDL row writes are invisible to <c>changes()</c> and <c>last_insert_rowid()</c>: creating a table
    /// reports no affected rows and leaves the connection's last inserted rowid alone, which is what the
    /// direct evaluator did and what SQLite does.
    /// </summary>
    private const VdbeInsertFlags SchemaWriteFlags =
        VdbeInsertFlags.SkipLastRowid | VdbeInsertFlags.SkipAllChangeCounts;
}
