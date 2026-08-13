namespace Ahtola.Core.Execution;

/// <summary>
/// Raised when a transaction/savepoint opcode drives an illegal state-machine transition at run time —
/// committing or rolling back with no active transaction, opening a nested transaction, or referencing a
/// savepoint that is not open. It is the runtime analogue of <see cref="VdbeProgramValidationException"/>,
/// which only rejects structurally malformed instructions (an empty savepoint name) when a program is
/// built; whether a given transition is legal depends on the values that flow through control flow and so
/// can only be decided while the resumable state machine runs.
/// </summary>
public sealed class VdbeTransactionException : InvalidOperationException
{
    public VdbeTransactionException(string message) : base(message)
    {
    }
}

/// <summary>
/// The interpreter's transaction/savepoint state machine, expressed as a stack of savepoint frames over
/// the interpreter's mutable <em>register file</em>. Each frame captures a snapshot of the register array
/// at the moment it was opened; the transition opcodes push, restore from, and pop those snapshots to give
/// BEGIN/COMMIT/ROLLBACK and SAVEPOINT/RELEASE/ROLLBACK&#160;TO faithful, nested semantics.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>not</b> a database transaction. The resumable interpreter does not own any
/// durable store — mutations reach tables only through externally supplied <see cref="VdbeWriteTarget"/>
/// delegates — so a transaction opcode here transacts exactly the state the interpreter <em>does</em> own:
/// its scalar register file. Rolling back restores register values that intervening instructions wrote;
/// nothing is persisted, checkpointed, or unwound in storage. Modelling anything more would either be a
/// no-op that pretends durability or a hand-off to native code, both of which are out of scope.
/// </para>
/// <para>
/// The stack mirrors SQLite's savepoint rules:
/// <list type="bullet">
///   <item><see cref="Begin"/> opens the outermost (anonymous) frame and fails if one is already open.</item>
///   <item><see cref="Savepoint"/> pushes a named frame; opening one outside a transaction implicitly opens
///     the transaction.</item>
///   <item><see cref="Commit"/> discards every frame, keeping the current register values.</item>
///   <item><see cref="Rollback"/> restores the outermost frame's snapshot and discards every frame.</item>
///   <item><see cref="Release"/> removes the named frame and all frames above it without restoring
///     registers — nested savepoints fold into the enclosing scope.</item>
///   <item><see cref="RollbackTo"/> restores the named frame's snapshot and cancels the frames above it, but
///     keeps the named frame so it can be rolled back to again.</item>
/// </list>
/// Savepoint names are matched case-insensitively (ordinal, case-folded) exactly as SQLite compares
/// identifiers, so a frame opened as <c>Foo</c> is released or rolled back to by <c>foo</c> or <c>fOo</c>.
/// The topmost matching frame wins, so reusing a name — in any letter case — nests correctly and resolves
/// unambiguously to the innermost frame that carries it.
/// </para>
/// </remarks>
public sealed class VdbeTransactionContext
{
    private readonly record struct SavepointFrame(string? Name, SqlValue[] Snapshot);

    private readonly List<SavepointFrame> _frames = [];

    /// <summary>
    /// Connection/statement-transaction deferred foreign-key violation counter (SQLite deferred FK).
    /// Survives statement reset while <see cref="InTransaction"/>; cleared on commit/rollback.
    /// </summary>
    public int DeferredForeignKeyViolations { get; set; }

    /// <summary>Whether a transaction is currently open (at least one frame on the stack).</summary>
    public bool InTransaction => _frames.Count > 0;

    /// <summary>The number of open frames: the outermost transaction plus any nested savepoints.</summary>
    public int Depth => _frames.Count;

    /// <summary>The open savepoint names from outermost to innermost; the anonymous <see cref="Begin"/>
    /// root reports <see langword="null"/>. Exposed so callers can observe the state machine directly.</summary>
    public IReadOnlyList<string?> SavepointNames
    {
        get
        {
            var names = new string?[_frames.Count];
            for (var i = 0; i < _frames.Count; i++)
                names[i] = _frames[i].Name;
            return names;
        }
    }

    /// <summary>Opens the outermost transaction, snapshotting <paramref name="registers"/>.</summary>
    public void Begin(SqlValue[] registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (InTransaction)
            throw new VdbeTransactionException("cannot start a transaction within a transaction");

        _frames.Add(new SavepointFrame(null, Snapshot(registers)));
    }

    /// <summary>Opens a named savepoint, snapshotting <paramref name="registers"/>. Implicitly opens a
    /// transaction when none is active.</summary>
    public void Savepoint(string name, SqlValue[] registers)
    {
        RequireName(name);
        ArgumentNullException.ThrowIfNull(registers);
        _frames.Add(new SavepointFrame(name, Snapshot(registers)));
    }

    /// <summary>Commits the active transaction, discarding every frame and keeping current register values.</summary>
    public void Commit()
    {
        if (!InTransaction)
            throw new VdbeTransactionException("cannot commit - no transaction is active");

        if (DeferredForeignKeyViolations != 0)
        {
            throw new EmbeddedSqlException(
                "FOREIGN KEY constraint failed",
                SqliteResultCode.ConstraintForeignKey,
                InsertConflictAlgorithm.Abort);
        }

        _frames.Clear();
        DeferredForeignKeyViolations = 0;
    }

    /// <summary>Rolls the active transaction back, restoring the outermost snapshot into
    /// <paramref name="registers"/> and discarding every frame.</summary>
    public void Rollback(SqlValue[] registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (!InTransaction)
            throw new VdbeTransactionException("cannot rollback - no transaction is active");

        Restore(registers, _frames[0].Snapshot);
        _frames.Clear();
        DeferredForeignKeyViolations = 0;
    }

    /// <summary>Releases the named savepoint and every frame above it without restoring registers.</summary>
    public void Release(string name)
    {
        RequireName(name);
        var index = FindTopmost(name);
        if (index < 0)
            throw new VdbeTransactionException($"no such savepoint: {name}");

        _frames.RemoveRange(index, _frames.Count - index);
    }

    /// <summary>Restores the named savepoint's snapshot into <paramref name="registers"/> and cancels the
    /// frames above it, keeping the named frame itself.</summary>
    public void RollbackTo(string name, SqlValue[] registers)
    {
        RequireName(name);
        ArgumentNullException.ThrowIfNull(registers);
        var index = FindTopmost(name);
        if (index < 0)
            throw new VdbeTransactionException($"no such savepoint: {name}");

        Restore(registers, _frames[index].Snapshot);
        var above = _frames.Count - index - 1;
        if (above > 0)
            _frames.RemoveRange(index + 1, above);
    }

    /// <summary>Clears all frames, abandoning any open transaction. Used by statement reset/dispose.</summary>
    public void Reset()
    {
        _frames.Clear();
        DeferredForeignKeyViolations = 0;
    }

    private int FindTopmost(string name)
    {
        // SQLite matches savepoint identifiers case-insensitively, and the binding compares every other SQL
        // identifier with ordinal case-folding, so RELEASE/ROLLBACK TO resolve regardless of the letter case
        // used to open the frame. Scanning from the top makes the innermost frame carrying the name win, which
        // keeps a reused (nested) name unambiguous.
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_frames[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static SqlValue[] Snapshot(SqlValue[] registers)
    {
        var copy = new SqlValue[registers.Length];
        Array.Copy(registers, copy, registers.Length);
        return copy;
    }

    private static void Restore(SqlValue[] registers, SqlValue[] snapshot)
        => Array.Copy(snapshot, registers, registers.Length);

    private static void RequireName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new VdbeTransactionException("a savepoint name must be a non-empty string");
    }
}
