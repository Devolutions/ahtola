namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Selects which browser connection operations may run synchronously.
/// </summary>
/// <remarks>
/// OPFS itself is always asynchronous. The mirror loads a database into managed
/// memory once during the asynchronous open, so the mode chooses whether the
/// provider is allowed to serve reads directly from that in-memory mirror.
/// </remarks>
public enum AhtolaBrowserSynchronousMode
{
    /// <summary>
    /// Every connection, command, reader, transaction, and lifecycle operation
    /// must use its asynchronous API. This is the default and preserves the
    /// original browser contract exactly.
    /// </summary>
    AsyncOnly = 0,

    /// <summary>
    /// Opts a data source into synchronous reads served entirely from the managed
    /// in-memory mirror.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One asynchronous initialization and open is still required — use
    /// <see cref="AhtolaBrowserDataSource.OpenSynchronousReadConnectionAsync"/> or
    /// <c>OpenConnectionAsync</c>. After that open completes, the whole database
    /// image lives in managed memory, so a statement that is provably read-only
    /// executes synchronously without touching OPFS or the storage worker.
    /// </para>
    /// <para>
    /// Only statements the provider can prove cannot mutate the database are
    /// allowed to run synchronously: <c>SELECT</c>, <c>VALUES</c>, and <c>WITH</c>
    /// whose terminal statement is <c>SELECT</c> or <c>VALUES</c>. Data definition,
    /// data modification, <c>PRAGMA</c>, <c>EXPLAIN</c>, transaction control,
    /// <c>ATTACH</c>/<c>DETACH</c>, writable common table expressions, and any
    /// batch that contains an unproven statement continue to require the
    /// asynchronous API, because their durability depends on an OPFS flush.
    /// Synchronous <c>Close</c> and <c>Dispose</c> are permitted only while no
    /// mutation is pending; otherwise they fail closed and asynchronous cleanup
    /// is required.
    /// </para>
    /// </remarks>
    ReadOnlyMirror = 1,
}
