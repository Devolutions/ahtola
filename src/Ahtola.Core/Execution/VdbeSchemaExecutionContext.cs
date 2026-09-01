namespace Ahtola.Core.Execution;

/// <summary>
/// Raised when a schema opcode cannot run: no schema context is bound to the statement, the context is
/// read-only, or the instruction addresses a database the context is not bound to. It is deliberately
/// distinct from <see cref="VdbeProgramValidationException"/> — a program carrying schema opcodes is
/// structurally valid; whether it may run depends on the runtime binding supplied to the statement.
/// </summary>
public sealed class VdbeSchemaExecutionException : InvalidOperationException
{
    public VdbeSchemaExecutionException(string message) : base(message)
    {
    }
}

/// <summary>
/// The schema effects a DDL program performs, expressed as one narrow interface so the interpreter never
/// reaches into a catalog, a pager, or a file store directly. Turso spreads these across
/// <c>Pager</c>, <c>Schema</c> and the connection; Ahtola keeps them behind this seam so
/// <see cref="ResumableStatement"/> stays a pure state machine and the durable publication boundary
/// remains the existing catalog/persist adapter.
/// </summary>
/// <remarks>
/// Every member is invoked only from an opcode handler and only after
/// <see cref="VdbeSchemaExecutionContext"/> has checked the database index and the read-only guard, so an
/// implementation may assume a routed database and a writable context.
/// </remarks>
internal interface IVdbeSchemaOperations
{
    /// <summary>Allocates a b-tree root in <paramref name="database"/> and returns its page number.</summary>
    long CreateBtree(int database, VdbeCreateBtreeFlags flags);

    /// <summary>Deletes every entry under <paramref name="rootPage"/>, keeping the root allocated.</summary>
    void ClearBtree(int database, long rootPage);

    /// <summary>Destroys <paramref name="rootPage"/>, returning the page number of a root that moved to
    /// fill the hole, or zero when nothing moved.</summary>
    long Destroy(int database, long rootPage, bool isTemporary);

    /// <summary>Reads a header cookie.</summary>
    long ReadCookie(int database, VdbeSchemaCookie cookie);

    /// <summary>Stages a header cookie value for the eventual commit.</summary>
    void SetCookie(int database, VdbeSchemaCookie cookie, long value);

    /// <summary>Reparses the matching <c>sqlite_schema</c> rows into the live schema.</summary>
    void ParseSchema(int database, string? whereClause, int? triggerTargetDatabase);

    /// <summary>
    /// Rebuilds an index of the live schema from its base table's rows, enforcing its uniqueness
    /// constraint when it declares one.
    /// </summary>
    void BuildIndex(int database, string tableName, string indexName, bool unique);

    /// <summary>Evicts one named object from the live schema.</summary>
    void DropObject(int database, VdbeSchemaObjectKind kind, string name);

    /// <summary>Renames a table in the live schema.</summary>
    void RenameTable(int database, string from, string to);

    /// <summary>Appends a column to a table in the live schema.</summary>
    void AddColumn(
        int database,
        string table,
        string columnName,
        string columnDefinition,
        string? columnSql);

    /// <summary>Removes the column at <paramref name="columnIndex"/> from a table in the live schema.</summary>
    void DropColumn(int database, string table, int columnIndex);

    /// <summary>Rewrites the column at <paramref name="columnIndex"/> of a table in the live schema.</summary>
    void AlterColumn(
        int database,
        string table,
        int columnIndex,
        string columnDefinition,
        bool rename,
        bool quoteNewName);
}

/// <summary>
/// Implemented by an <see cref="IVdbeSchemaOperations"/> that stages state of its own and therefore has to
/// discard it when the owning statement resets. <see cref="VdbeSchemaExecutionContext.Reset"/> calls it in
/// the same step it clears its own bookkeeping, so a re-run cannot inherit half a program's schema effects.
/// </summary>
internal interface IVdbeResettableSchemaOperations : IVdbeSchemaOperations
{
    /// <summary>Discards everything the current program run staged, ready to run again.</summary>
    void ResetStagedState();

    /// <summary>
    /// Releases the resources the staged state holds, without preparing it to run again. Disposal uses this
    /// rather than <see cref="ResetStagedState"/>: rebuilding a working catalog nobody will ever run or
    /// release is how a discarded program leaks the virtual-table instances that rebuild connects.
    /// </summary>
    void DiscardStagedState();
}

/// <summary>
/// Binds a running <see cref="ResumableStatement"/> to the schema effects its DDL opcodes may perform. It
/// is the runtime substrate the schema opcodes execute against: it validates the database index every
/// instruction addresses, enforces the read-only guard, records the root pages the current program has
/// reserved and reclaimed, and forwards the actual effect to <see cref="IVdbeSchemaOperations"/>.
/// </summary>
/// <remarks>
/// <para>
/// The staged root bookkeeping is interpreter state, not catalog state: it says what <em>this</em> program
/// run has asked for, so a failed program's reservations are visible and discardable without consulting
/// storage. <see cref="Reset"/> clears it, and the statement that owns the context resets it in lockstep
/// with its own <see cref="ResumableStatement.Reset"/>; a nested subprogram shares the context and must
/// not reset it, exactly as a shared <see cref="VdbeTransactionContext"/> survives a nested reset.
/// </para>
/// <para>
/// Nothing here publishes anything durably. Turning a reservation into a committed root page, or a staged
/// cookie into a written header, remains the responsibility of the outer catalog/persist adapter.
/// </para>
/// </remarks>
internal sealed class VdbeSchemaExecutionContext
{
    private readonly IVdbeSchemaOperations _operations;
    private readonly List<long> _reservedRootPages = [];
    private readonly List<long> _reclaimedRootPages = [];

    /// <param name="operations">The schema effects the bound program may perform.</param>
    /// <param name="databaseCount">How many routed databases the program may address, i.e. the exclusive
    /// upper bound on every instruction's database index.</param>
    /// <param name="isReadOnly">Whether mutating schema opcodes must fail. Reading a cookie stays legal.</param>
    public VdbeSchemaExecutionContext(
        IVdbeSchemaOperations operations,
        int databaseCount = 1,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (databaseCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(databaseCount));

        _operations = operations;
        DatabaseCount = databaseCount;
        IsReadOnly = isReadOnly;
    }

    /// <summary>The exclusive upper bound on the database index a schema opcode may address.</summary>
    public int DatabaseCount { get; }

    /// <summary>Whether mutating schema opcodes are rejected.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Root pages <c>CreateBtree</c> has allocated during the current program run, in order.</summary>
    public IReadOnlyList<long> ReservedRootPages => _reservedRootPages;

    /// <summary>Root pages <c>Destroy</c> has reclaimed during the current program run, in order.</summary>
    public IReadOnlyList<long> ReclaimedRootPages => _reclaimedRootPages;

    /// <summary>
    /// Discards the staged root bookkeeping so a re-run starts from a clean slate, and asks the bound
    /// operations to discard whatever they staged as well when they can.
    /// </summary>
    public void Reset()
    {
        _reservedRootPages.Clear();
        _reclaimedRootPages.Clear();
        if (_operations is IVdbeResettableSchemaOperations resettable)
            resettable.ResetStagedState();
    }

    /// <summary>
    /// Releases the staged state's resources without preparing it to run again. The owning statement calls
    /// this from <see cref="ResumableStatement.Dispose"/>, where <see cref="Reset"/> would pointlessly
    /// rebuild a working catalog that nothing will ever execute or release.
    /// </summary>
    public void Discard()
    {
        _reservedRootPages.Clear();
        _reclaimedRootPages.Clear();
        if (_operations is IVdbeResettableSchemaOperations resettable)
            resettable.DiscardStagedState();
    }

    public long CreateBtree(int database, VdbeCreateBtreeFlags flags)
    {
        RequireWritableDatabase(database, "CreateBtree");
        var rootPage = _operations.CreateBtree(database, flags);
        if (rootPage <= 1)
        {
            throw new VdbeSchemaExecutionException(
                $"CreateBtree allocated root page {rootPage}, which is not an allocatable b-tree root.");
        }

        _reservedRootPages.Add(rootPage);
        return rootPage;
    }

    public void ClearBtree(int database, long rootPage)
    {
        RequireWritableDatabase(database, "ClearBtree");
        _operations.ClearBtree(database, rootPage);
    }

    public long Destroy(int database, long rootPage, bool isTemporary)
    {
        RequireWritableDatabase(database, "Destroy");
        var formerRoot = _operations.Destroy(database, rootPage, isTemporary);
        if (formerRoot < 0)
        {
            throw new VdbeSchemaExecutionException(
                $"Destroy reported a negative moved root page {formerRoot}; use zero when no root moved.");
        }

        _reclaimedRootPages.Add(rootPage);
        return formerRoot;
    }

    public long ReadCookie(int database, VdbeSchemaCookie cookie)
    {
        RequireDatabase(database, "ReadCookie");
        return _operations.ReadCookie(database, cookie);
    }

    public void SetCookie(int database, VdbeSchemaCookie cookie, long value)
    {
        RequireWritableDatabase(database, "SetCookie");
        _operations.SetCookie(database, cookie, value);
    }

    public void ParseSchema(int database, string? whereClause, int? triggerTargetDatabase)
    {
        RequireWritableDatabase(database, "ParseSchema");
        if (triggerTargetDatabase is { } target)
            RequireDatabase(target, "ParseSchema");
        _operations.ParseSchema(database, whereClause, triggerTargetDatabase);
    }

    public void BuildIndex(int database, string tableName, string indexName, bool unique)
    {
        RequireWritableDatabase(database, "IndexBuild");
        _operations.BuildIndex(database, tableName, indexName, unique);
    }

    public void DropObject(int database, VdbeSchemaObjectKind kind, string name)
    {
        RequireWritableDatabase(database, DropOpcodeName(kind));
        _operations.DropObject(database, kind, name);
    }

    public void RenameTable(int database, string from, string to)
    {
        RequireWritableDatabase(database, "RenameTable");
        _operations.RenameTable(database, from, to);
    }

    public void AddColumn(
        int database,
        string table,
        string columnName,
        string columnDefinition,
        string? columnSql = null)
    {
        RequireWritableDatabase(database, "AddColumn");
        _operations.AddColumn(database, table, columnName, columnDefinition, columnSql);
    }

    public void DropColumn(int database, string table, int columnIndex)
    {
        RequireWritableDatabase(database, "DropColumn");
        _operations.DropColumn(database, table, columnIndex);
    }

    public void AlterColumn(
        int database,
        string table,
        int columnIndex,
        string columnDefinition,
        bool rename,
        bool quoteNewName = false)
    {
        RequireWritableDatabase(database, "AlterColumn");
        _operations.AlterColumn(database, table, columnIndex, columnDefinition, rename, quoteNewName);
    }

    private static string DropOpcodeName(VdbeSchemaObjectKind kind) => kind switch
    {
        VdbeSchemaObjectKind.Table => "DropTable",
        VdbeSchemaObjectKind.View => "DropView",
        VdbeSchemaObjectKind.Index => "DropIndex",
        VdbeSchemaObjectKind.Trigger => "DropTrigger",
        _ => throw new VdbeSchemaExecutionException($"Unknown schema object kind {(int)kind}."),
    };

    private void RequireDatabase(int database, string opcodeName)
    {
        if (database < 0 || database >= DatabaseCount)
        {
            throw new VdbeSchemaExecutionException(
                $"{opcodeName} addresses database {database}, but the schema context is bound to {DatabaseCount} database(s).");
        }
    }

    private void RequireWritableDatabase(int database, string opcodeName)
    {
        RequireDatabase(database, opcodeName);
        if (IsReadOnly)
        {
            throw new VdbeSchemaExecutionException(
                $"{opcodeName} cannot run against a read-only schema context.");
        }
    }
}
