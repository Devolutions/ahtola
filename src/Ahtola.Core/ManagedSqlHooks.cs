namespace Ahtola.Core;

/// <summary>
/// The row operation reported to an update hook, mirroring SQLite's
/// <c>SQLITE_INSERT</c>, <c>SQLITE_UPDATE</c> and <c>SQLITE_DELETE</c> codes.
/// </summary>
public enum SqliteChangeOperation
{
    /// <summary>A row was inserted.</summary>
    Insert = 18,

    /// <summary>A row was updated.</summary>
    Update = 23,

    /// <summary>A row was deleted.</summary>
    Delete = 9,
}

/// <summary>
/// A single row change reported to an update hook. <see cref="RowId"/> is the rowid of the
/// affected row after the change, matching <c>sqlite3_update_hook</c>.
/// </summary>
/// <param name="Operation">The row operation that produced the notification.</param>
/// <param name="Database">The schema the table belongs to: <c>main</c>, <c>temp</c>, or an ATTACH alias.</param>
/// <param name="Table">The table that changed.</param>
/// <param name="RowId">The rowid of the changed row.</param>
public readonly record struct SqliteRowChange(
    SqliteChangeOperation Operation,
    string Database,
    string Table,
    long RowId);

/// <summary>
/// A row change with managed before/after values. This is intentionally internal: it preserves
/// the public SQLite-compatible update-hook contract while allowing managed subsystems such as
/// replica synchronization to retain a delete's stable key after the row is gone.
/// </summary>
internal readonly record struct ManagedSqliteRowChange(
    SqliteChangeOperation Operation,
    string Database,
    string Table,
    long RowId,
    IReadOnlyList<SqlValue>? BeforeValues,
    IReadOnlyList<SqlValue>? AfterValues);

/// <summary>
/// The decision an authorizer callback returns, mirroring SQLite's <c>SQLITE_OK</c>,
/// <c>SQLITE_DENY</c> and <c>SQLITE_IGNORE</c>.
/// </summary>
public enum SqliteAuthorizerResult
{
    /// <summary>Allow the action.</summary>
    Ok = 0,

    /// <summary>Reject the action, failing statement preparation.</summary>
    Deny = 1,

    /// <summary>
    /// Silently neutralize the action: a column read yields NULL, and an INSERT, UPDATE
    /// column assignment or SELECT becomes a no-op. A DELETE still proceeds, matching SQLite.
    /// </summary>
    Ignore = 2,
}

/// <summary>
/// The action codes reported to an authorizer callback. The numeric values match SQLite's
/// <c>SQLITE_*</c> authorizer action codes so they can be compared against native constants.
/// </summary>
public enum SqliteAuthorizerAction
{
    /// <summary><c>SQLITE_CREATE_INDEX</c></summary>
    CreateIndex = 1,

    /// <summary><c>SQLITE_CREATE_TABLE</c></summary>
    CreateTable = 2,

    /// <summary><c>SQLITE_CREATE_TEMP_INDEX</c></summary>
    CreateTempIndex = 3,

    /// <summary><c>SQLITE_CREATE_TEMP_TABLE</c></summary>
    CreateTempTable = 4,

    /// <summary><c>SQLITE_CREATE_TEMP_TRIGGER</c></summary>
    CreateTempTrigger = 5,

    /// <summary><c>SQLITE_CREATE_TEMP_VIEW</c></summary>
    CreateTempView = 6,

    /// <summary><c>SQLITE_CREATE_TRIGGER</c></summary>
    CreateTrigger = 7,

    /// <summary><c>SQLITE_CREATE_VIEW</c></summary>
    CreateView = 8,

    /// <summary><c>SQLITE_DELETE</c></summary>
    Delete = 9,

    /// <summary><c>SQLITE_DROP_INDEX</c></summary>
    DropIndex = 10,

    /// <summary><c>SQLITE_DROP_TABLE</c></summary>
    DropTable = 11,

    /// <summary><c>SQLITE_DROP_TEMP_INDEX</c></summary>
    DropTempIndex = 12,

    /// <summary><c>SQLITE_DROP_TEMP_TABLE</c></summary>
    DropTempTable = 13,

    /// <summary><c>SQLITE_DROP_TEMP_TRIGGER</c></summary>
    DropTempTrigger = 14,

    /// <summary><c>SQLITE_DROP_TEMP_VIEW</c></summary>
    DropTempView = 15,

    /// <summary><c>SQLITE_DROP_TRIGGER</c></summary>
    DropTrigger = 16,

    /// <summary><c>SQLITE_DROP_VIEW</c></summary>
    DropView = 17,

    /// <summary><c>SQLITE_INSERT</c></summary>
    Insert = 18,

    /// <summary><c>SQLITE_PRAGMA</c></summary>
    Pragma = 19,

    /// <summary><c>SQLITE_READ</c></summary>
    Read = 20,

    /// <summary><c>SQLITE_SELECT</c></summary>
    Select = 21,

    /// <summary><c>SQLITE_TRANSACTION</c></summary>
    Transaction = 22,

    /// <summary><c>SQLITE_UPDATE</c></summary>
    Update = 23,

    /// <summary><c>SQLITE_ATTACH</c></summary>
    Attach = 24,

    /// <summary><c>SQLITE_DETACH</c></summary>
    Detach = 25,

    /// <summary><c>SQLITE_ALTER_TABLE</c></summary>
    AlterTable = 26,

    /// <summary><c>SQLITE_REINDEX</c></summary>
    Reindex = 27,

    /// <summary><c>SQLITE_ANALYZE</c></summary>
    Analyze = 28,

    /// <summary><c>SQLITE_CREATE_VTABLE</c></summary>
    CreateVTable = 29,

    /// <summary><c>SQLITE_DROP_VTABLE</c></summary>
    DropVTable = 30,

    /// <summary><c>SQLITE_FUNCTION</c></summary>
    Function = 31,

    /// <summary><c>SQLITE_SAVEPOINT</c></summary>
    Savepoint = 32,

    /// <summary><c>SQLITE_RECURSIVE</c></summary>
    Recursive = 33,
}

/// <summary>
/// The arguments passed to an authorizer callback. The four string arguments mirror the
/// <c>sqlite3_set_authorizer</c> callback parameters; their meaning depends on
/// <see cref="Action"/>. Arguments SQLite leaves empty are reported as <c>null</c>.
/// </summary>
/// <param name="Action">The action being authorized.</param>
/// <param name="Argument0">
/// The first action-specific argument: the table name for INSERT, UPDATE, DELETE and READ,
/// the object name for CREATE/DROP, the pragma name for PRAGMA, the operation keyword for
/// TRANSACTION, or the function name for FUNCTION.
/// </param>
/// <param name="Argument1">
/// The second action-specific argument: the column name for READ and UPDATE, or the pragma
/// argument for PRAGMA.
/// </param>
/// <param name="Database">The schema the action targets, or <c>null</c> when it has no schema.</param>
/// <param name="TriggerOrView">
/// The innermost trigger or view the action originates from, or <c>null</c> for a top-level action.
/// </param>
public readonly record struct SqliteAuthorizerContext(
    SqliteAuthorizerAction Action,
    string? Argument0,
    string? Argument1,
    string? Database,
    string? TriggerOrView);

/// <summary>
/// Per-connection callback registrations shared between <see cref="EmbeddedConnection"/> and the
/// ADO.NET facade. Every member is optional; a null callback means the hook is not installed.
/// </summary>
public sealed class ManagedConnectionHooks
{
    /// <summary>Invoked once per changed row, after the row has been written.</summary>
    public Action<SqliteRowChange>? UpdateHook { get; set; }

    /// <summary>
    /// Internal detailed counterpart to <see cref="UpdateHook"/>. The assembly-visible hook retains
    /// immutable before/after value snapshots for managed infrastructure without changing the
    /// public SQLite-compatible update-hook surface.
    /// </summary>
    internal Action<ManagedSqliteRowChange>? DetailedUpdateHook { get; set; }

    /// <summary>
    /// Invoked immediately before a transaction is committed. Returning <c>false</c> vetoes the
    /// commit, turning it into a rollback.
    /// </summary>
    public Func<bool>? CommitHook { get; set; }

    /// <summary>Invoked when a transaction is rolled back.</summary>
    public Action? RollbackHook { get; set; }

    /// <summary>Invoked at statement preparation for each authorizable action.</summary>
    public Func<SqliteAuthorizerContext, SqliteAuthorizerResult>? Authorizer { get; set; }

    /// <summary>Invoked once per statement execution with the statement's SQL text.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Invoked every <see cref="ProgressInterval"/> row steps while a statement runs. Returning
    /// <c>true</c> interrupts the statement.
    /// </summary>
    public Func<bool>? ProgressHandler { get; set; }

    /// <summary>The number of managed row steps between <see cref="ProgressHandler"/> invocations.</summary>
    public int ProgressInterval { get; set; }

    internal bool HasExecutionHooks
        => UpdateHook is not null || DetailedUpdateHook is not null || CommitHook is not null || ProgressHandler is not null;
}

/// <summary>
/// The subset of a connection's hooks that a single statement execution needs. The engine
/// receives this per execution rather than reading connection state, so registrations stay
/// per-connection while execution stays free of shared mutable hook state.
/// </summary>
internal sealed class ManagedStatementHooks
{
    /// <summary>Reports a changed row with optional before/after values.</summary>
    internal Action<SqliteChangeOperation, string, long, SqlValue[]?, SqlValue[]?>? RowChanged { get; init; }

    /// <summary>
    /// Consulted immediately before the engine publishes an autocommit mutation. Returning
    /// <c>false</c> vetoes the commit, discarding the working catalog.
    /// </summary>
    internal Func<bool>? CommitGate { get; init; }

    /// <summary>Drives the connection's progress handler from row-loop checkpoints.</summary>
    internal ManagedProgressCounter? Progress { get; init; }
}

/// <summary>
/// Counts managed row-execution steps and invokes the connection's progress handler every
/// <c>interval</c> steps. A handler that returns <c>true</c> interrupts the statement.
/// </summary>
internal sealed class ManagedProgressCounter(int interval, Func<bool> handler)
{
    private readonly int _interval = interval;
    private int _remaining = interval;

    internal void Step()
    {
        if (--_remaining > 0)
            return;

        _remaining = _interval;
        if (handler())
            throw new EmbeddedInterruptException();
    }
}

/// <summary>
/// Formats a managed engine failure so the ADO.NET facade reports SQLite's own result code
/// rather than the generic <c>SQLITE_ERROR</c>.
/// </summary>
internal static class SqliteErrorMessage
{
    internal const int Interrupt = 9;
    internal const int Constraint = 19;
    internal const int Authorization = 23;

    internal static string Format(int errorCode, string message)
        => $"__ahtola_sqlite_error__:{errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{message}";
}

/// <summary>
/// Thrown when a commit hook vetoes a commit. The managed engine reports this as SQLite's
/// <c>SQLITE_CONSTRAINT</c> (19) with the message <c>constraint failed</c>, matching what
/// <c>sqlite3_step</c> returns when <c>sqlite3_commit_hook</c> returns nonzero.
/// </summary>
public sealed class EmbeddedCommitVetoException : EmbeddedSqlException
{
    /// <summary>Creates the exception with SQLite's commit-hook veto message.</summary>
    public EmbeddedCommitVetoException()
        : base(SqliteErrorMessage.Format(SqliteErrorMessage.Constraint, "constraint failed"))
    {
    }
}

/// <summary>
/// Thrown when a progress handler interrupts a running statement. The managed engine reports
/// this as SQLite's <c>SQLITE_INTERRUPT</c> (9).
/// </summary>
public sealed class EmbeddedInterruptException : EmbeddedSqlException
{
    /// <summary>Creates the exception with SQLite's interrupt message.</summary>
    public EmbeddedInterruptException()
        : base(SqliteErrorMessage.Format(SqliteErrorMessage.Interrupt, "interrupted"))
    {
    }
}

/// <summary>
/// Thrown when an authorizer callback denies an action during statement preparation. The managed
/// engine reports this as SQLite's <c>SQLITE_AUTH</c> (23).
/// </summary>
public sealed class EmbeddedAuthorizationDeniedException : EmbeddedSqlException
{
    /// <summary>Creates the exception with SQLite's authorization-failure message.</summary>
    /// <param name="reason">The bare SQLite message, such as <c>not authorized</c>.</param>
    public EmbeddedAuthorizationDeniedException(string reason)
        : base(SqliteErrorMessage.Format(SqliteErrorMessage.Authorization, reason))
    {
    }
}
