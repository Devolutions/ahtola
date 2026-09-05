namespace Ahtola.Core;

/// <summary>
/// The per-connection sequence state: the <c>currval</c> session map Turso keeps on its connection
/// (<c>sequence_currvals</c>), plus the pending nextval/setval allocations the statement has produced.
/// </summary>
/// <remarks>
/// <para>
/// The session object flows into <c>EmbeddedDatabase.Execute</c> exactly the way
/// <see cref="ChangeDataCaptureSession"/> does, so every evaluation site — the evaluator's scalar
/// dispatch, a trigger body, a view — reaches the same connection-owned state.
/// </para>
/// <para>
/// <c>currval</c> recording is immediate, matching upstream's <c>SetSequenceCurrval</c> opcode: a
/// <c>nextval</c>/<c>setval</c> that produced a value establishes this connection's session entry even
/// if the surrounding transaction later rolls back, which is PostgreSQL-compatible and what Turso's
/// per-connection map does. The backing-table watermark, by contrast, is ordinary table state: it only
/// becomes durable when the statement's transaction commits, so a rolled-back allocation can be
/// re-emitted by a later <c>nextval</c> — Turso's documented behavior.
/// </para>
/// </remarks>
internal sealed class ManagedSequenceSession
{
    private readonly Dictionary<string, long> _currvals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the value a nextval/setval call produced for the named sequence.</summary>
    public void SetCurrval(string name, long value) => _currvals[name] = value;

    /// <summary>The last value nextval/setval produced for the named sequence on this connection.</summary>
    public bool TryGetCurrval(string name, out long value) => _currvals.TryGetValue(name, out value);

    /// <summary>
    /// Drops the session entry, so a recreated same-name sequence cannot inherit the stale currval of
    /// the one that went away (upstream <c>clear_sequence_currval</c> on DROP SEQUENCE).
    /// </summary>
    public void ClearCurrval(string name) => _currvals.Remove(name);
}
