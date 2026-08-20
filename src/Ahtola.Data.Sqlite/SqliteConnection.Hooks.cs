using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public partial class SqliteConnection
{
    private readonly ManagedConnectionHooks _hooks = new();

    /// <summary>
    /// Registers a callback invoked once per changed row, mirroring <c>sqlite3_update_hook</c>.
    /// Pass <c>null</c> to remove the hook.
    /// </summary>
    /// <remarks>
    /// The callback runs after the row has been written but before the change is committed, and
    /// it must not use this connection: reentrant use throws. Matching SQLite, no notification is
    /// raised for WITHOUT ROWID tables, for internal <c>sqlite_*</c> tables such as
    /// <c>sqlite_sequence</c>, or for the implicit delete performed by REPLACE conflict
    /// resolution. Unlike SQLite, an unfiltered <c>DELETE FROM t</c> notifies once per row because
    /// the managed engine has no truncate optimization to suppress.
    /// </remarks>
    /// <param name="handler">The callback, or <c>null</c> to clear the hook.</param>
    public void SetUpdateHook(Action<SqliteRowChange>? handler)
    {
        EnsureHooksSupported(handler);
        _hooks.UpdateHook = handler;
        ApplyHooks();
    }

    /// <summary>
    /// Registers a callback invoked immediately before a transaction commits, mirroring
    /// <c>sqlite3_commit_hook</c>. Returning <c>false</c> vetoes the commit, which is then turned
    /// into a rollback and reported as <c>SQLITE_CONSTRAINT</c> (19). Pass <c>null</c> to remove
    /// the hook.
    /// </summary>
    /// <remarks>
    /// The hook is only consulted when the transaction actually changed something, and it runs
    /// after every update-hook notification for that transaction. It is not consulted for changes
    /// that bypass the statement commit path: <c>VACUUM</c>, <c>ATTACH</c>/<c>DETACH</c>,
    /// <c>CREATE TABLE ... AS SELECT</c>, header pragma writes such as
    /// <c>PRAGMA user_version = n</c>, and incremental blob writes.
    /// </remarks>
    /// <param name="handler">The callback, or <c>null</c> to clear the hook.</param>
    public void SetCommitHook(Func<bool>? handler)
    {
        EnsureHooksSupported(handler);
        _hooks.CommitHook = handler;
        ApplyHooks();
    }

    /// <summary>
    /// Registers a callback invoked when a transaction rolls back, mirroring
    /// <c>sqlite3_rollback_hook</c>. Pass <c>null</c> to remove the hook.
    /// </summary>
    /// <remarks>
    /// Matching SQLite, the hook fires for an explicit <c>ROLLBACK</c> even when nothing changed,
    /// for a rollback caused by a vetoing commit hook, for <c>ON CONFLICT ROLLBACK</c>, and for the
    /// implicit rollback of a failed autocommit mutation. It does not fire when a statement inside
    /// an explicit transaction fails without aborting the transaction, nor for
    /// <c>ROLLBACK TO SAVEPOINT</c>.
    /// </remarks>
    /// <param name="handler">The callback, or <c>null</c> to clear the hook.</param>
    public void SetRollbackHook(Action? handler)
    {
        EnsureHooksSupported(handler);
        _hooks.RollbackHook = handler;
        ApplyHooks();
    }

    /// <summary>
    /// Registers an authorizer consulted while a statement is prepared, mirroring
    /// <c>sqlite3_set_authorizer</c>. Pass <c>null</c> to remove it.
    /// </summary>
    /// <remarks>
    /// <see cref="SqliteAuthorizerResult.Deny"/> fails preparation with <c>SQLITE_AUTH</c> (23).
    /// <see cref="SqliteAuthorizerResult.Ignore"/> neutralizes the action instead of failing: a
    /// column read yields NULL wherever it appears, an INSERT becomes a no-op, an UPDATE column
    /// assignment is skipped, a SELECT returns no rows, and -- matching SQLite -- a DELETE still
    /// proceeds. Views and trigger bodies are walked so a policy cannot be bypassed by reading a
    /// base table through a view or writing to it from a trigger.
    /// </remarks>
    /// <param name="handler">The authorizer, or <c>null</c> to clear it.</param>
    public void SetAuthorizer(Func<SqliteAuthorizerContext, SqliteAuthorizerResult>? handler)
    {
        EnsureHooksSupported(handler);
        _hooks.Authorizer = handler;
        ApplyHooks();
    }

    /// <summary>
    /// Registers a callback invoked once per statement execution with the statement's SQL text,
    /// mirroring <c>sqlite3_trace_v2</c>'s <c>SQLITE_TRACE_STMT</c>. Pass <c>null</c> to remove it.
    /// </summary>
    /// <remarks>
    /// The reported text is the prepared SQL as written. Parameters are not expanded, so this
    /// matches <c>sqlite3_trace_v2</c> rather than the legacy <c>sqlite3_trace</c>.
    /// </remarks>
    /// <param name="handler">The callback, or <c>null</c> to clear it.</param>
    public void SetTraceHandler(Action<string>? handler)
    {
        EnsureHooksSupported(handler);
        _hooks.Trace = handler;
        ApplyHooks();
    }

    /// <summary>
    /// Registers a callback invoked periodically while a statement runs, mirroring
    /// <c>sqlite3_progress_handler</c>. Returning <c>true</c> interrupts the statement, which then
    /// fails with <c>SQLITE_INTERRUPT</c> (9). Pass <c>null</c> to remove it.
    /// </summary>
    /// <remarks>
    /// <paramref name="stepInterval"/> counts managed row-execution steps rather than SQLite VDBE
    /// opcodes, because the managed engine has no opcode counter. The cadence is therefore not
    /// comparable to SQLite's, but the interrupt semantics are.
    /// </remarks>
    /// <param name="stepInterval">The number of managed row steps between invocations.</param>
    /// <param name="handler">The callback, or <c>null</c> to clear it.</param>
    public void SetProgressHandler(int stepInterval, Func<bool>? handler)
    {
        EnsureHooksSupported(handler);
        if (handler is not null && stepInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepInterval), Properties.Resources.ProgressIntervalOutOfRange);

        _hooks.ProgressHandler = handler;
        _hooks.ProgressInterval = handler is null ? 0 : stepInterval;
        ApplyHooks();
    }

    private bool HasHooks
        => _hooks.UpdateHook is not null
           || _hooks.CommitHook is not null
           || _hooks.RollbackHook is not null
           || _hooks.Authorizer is not null
           || _hooks.Trace is not null
           || _hooks.ProgressHandler is not null;

    private int _hookSuspensions;

    /// <summary>
    /// Suspends every registered hook for the lifetime of the returned scope.
    /// </summary>
    /// <remarks>
    /// Used by the provider's own metadata probes. Native SQLite answers questions such as
    /// "what is the declared type of this column" through <c>sqlite3_column_decltype</c> without
    /// preparing a statement, so surfacing the managed engine's equivalent <c>PRAGMA</c> queries
    /// to a trace handler or an authorizer would report work the application never asked for --
    /// and a deny-by-default authorizer would break ordinary reads.
    /// </remarks>
    internal HookSuspension SuspendHooks() => new(this);

    internal readonly struct HookSuspension : IDisposable
    {
        private readonly SqliteConnection? _connection;

        internal HookSuspension(SqliteConnection connection)
        {
            if (!connection.HasHooks)
            {
                _connection = null;
                return;
            }

            _connection = connection;
            connection._hookSuspensions++;
            connection.ApplyHooks();
        }

        public void Dispose()
        {
            if (_connection is null)
                return;

            _connection._hookSuspensions--;
            _connection.ApplyHooks();
        }
    }

    private void EnsureHooksSupported(object? handler)
    {
        if (EndpointMode != AhtolaConnectionEndpointMode.Local
            || !Capabilities.SupportsUserDefinedFunctions)
            throw new NotSupportedException("SQLite hooks are supported only for local database connections.");
        if (handler is null)
            return;
        if (IsManagedSharedMemory)
            throw new NotSupportedException(Properties.Resources.ManagedSharedCacheHooksNotSupported);
        if (_database is not null && !IsManagedConnection)
            throw new NotSupportedException(Properties.Resources.HooksRequireManagedProvider);
    }

    /// <summary>
    /// Pushes the cached registrations onto the live managed connection. Registrations survive
    /// <see cref="Close"/>/<see cref="Open"/> the same way scalar functions and collations do.
    /// </summary>
    private void ApplyHooks()
    {
        if (!IsManagedConnection)
            return;

        var target = ManagedConnection.Hooks;
        var suspended = _hookSuspensions > 0;
        target.UpdateHook = suspended ? null : _hooks.UpdateHook;
        target.CommitHook = suspended ? null : _hooks.CommitHook;
        target.RollbackHook = suspended ? null : _hooks.RollbackHook;
        target.Authorizer = suspended ? null : _hooks.Authorizer;
        target.Trace = suspended ? null : _hooks.Trace;
        target.ProgressHandler = suspended ? null : _hooks.ProgressHandler;
        target.ProgressInterval = suspended ? 0 : _hooks.ProgressInterval;
    }

    private void RegisterHooks()
    {
        if (!HasHooks)
            return;
        if (!IsManagedConnection)
            throw new NotSupportedException(Properties.Resources.HooksRequireManagedProvider);
        if (IsManagedSharedMemory)
            throw new NotSupportedException(Properties.Resources.ManagedSharedCacheHooksNotSupported);

        ApplyHooks();
    }
}
