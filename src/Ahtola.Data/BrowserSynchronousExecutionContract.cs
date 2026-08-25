namespace Ahtola;

/// <summary>
/// Builds the messages that explain why a synchronous browser operation was
/// refused, so both provider facades describe the same contract.
/// </summary>
internal static class BrowserSynchronousExecutionContract
{
    /// <summary>
    /// The statement shapes a synchronous read-mirror connection can prove are
    /// incapable of mutating the database.
    /// </summary>
    internal const string ProvenReadOnlyShapes =
        "SELECT, VALUES, or WITH whose terminal statement is SELECT or VALUES";

    /// <summary>Describes a refused synchronous command execution.</summary>
    /// <param name="asyncAlternative">The asynchronous API to use instead.</param>
    /// <param name="supportsSynchronousReads">
    /// Whether the data source opted into synchronous read-mirror mode, in which
    /// case only the statement shape blocked this command.
    /// </param>
    internal static string DescribeCommandRejection(
        string asyncAlternative,
        bool supportsSynchronousReads)
        => supportsSynchronousReads
            ? "Synchronous command execution requires a statement proven to be read-only ("
              + ProvenReadOnlyShapes
              + "). Data definition, data modification, PRAGMA, EXPLAIN, transaction control, "
              + "ATTACH/DETACH, writable common table expressions, and batches containing any "
              + $"unproven statement must persist through OPFS. Use {asyncAlternative}."
            : "Synchronous command execution is not supported by the browser database source. "
              + $"Use {asyncAlternative}, or open the data source with "
              + "AhtolaBrowserSynchronousMode.ReadOnlyMirror to run proven read-only statements "
              + "against the managed in-memory mirror.";

    /// <summary>Describes a refused synchronous reader transition.</summary>
    internal static string DescribeReaderRejection(bool supportsSynchronousReads)
        => supportsSynchronousReads
            ? "Synchronous reader iteration requires a statement proven to be read-only ("
              + ProvenReadOnlyShapes
              + "). Use ReadAsync or NextResultAsync."
            : "Synchronous reader iteration is not supported by the browser database source. "
              + "Use ReadAsync or NextResultAsync.";

    /// <summary>Describes a refused synchronous reader disposal.</summary>
    internal static string DescribeReaderDisposalRejection(bool supportsSynchronousReads)
        => supportsSynchronousReads
            ? "Synchronous reader disposal requires a statement proven to be read-only ("
              + ProvenReadOnlyShapes
              + "). Use DisposeAsync."
            : "Synchronous reader disposal is not supported by the browser database source. "
              + "Use DisposeAsync.";
}

/// <summary>
/// An immutable decision about whether one execution may run synchronously, captured from the
/// SQL that was actually prepared and executed.
/// </summary>
/// <remarks>
/// <para>
/// A command's <c>CommandText</c> is mutable and a reader outlives the call that produced it, so
/// asking the command again while stepping would authorize the wrong statement: prepare a write,
/// obtain a reader asynchronously, then assign <c>SELECT 1</c> and every subsequent synchronous
/// <c>Read</c>/<c>Close</c>/<c>Dispose</c> would step the *write* across the OPFS boundary.
/// Capturing the decision once, against the executed text, removes that window entirely.
/// </para>
/// <para>
/// Capturing once is also what keeps the classifier off the row hot path: authorization is proven
/// when the reader is constructed and then simply re-read, never re-tokenized per row.
/// </para>
/// </remarks>
internal readonly struct BrowserSynchronousAuthorization
{
    private BrowserSynchronousAuthorization(bool allowsSynchronousExecution, bool supportsSynchronousReads)
    {
        AllowsSynchronousExecution = allowsSynchronousExecution;
        SupportsSynchronousReads = supportsSynchronousReads;
    }

    /// <summary>
    /// Authorization for a connection that has no asynchronous-only restriction at all.
    /// </summary>
    internal static BrowserSynchronousAuthorization Allowed { get; } = new(true, false);

    /// <summary>Whether the captured execution may be driven synchronously.</summary>
    internal bool AllowsSynchronousExecution { get; }

    /// <summary>
    /// Whether the data source offered synchronous read-mirror mode at capture time, which
    /// distinguishes "this statement is not provably read-only" from "this data source is
    /// asynchronous only" in the rejection message.
    /// </summary>
    internal bool SupportsSynchronousReads { get; }

    /// <summary>
    /// Classifies <paramref name="sql"/> once against <paramref name="connection"/>.
    /// </summary>
    internal static BrowserSynchronousAuthorization Capture(
        IBrowserSynchronousExecutionPolicy? connection,
        string? sql)
        => connection is null
            ? Allowed
            : new BrowserSynchronousAuthorization(
                connection.AllowsSynchronousSql(sql),
                connection.SupportsSynchronousReads);

    /// <summary>
    /// Combines two captured decisions: synchronous execution is authorized only when both are.
    /// Used to fold a sequential batch's per-command decisions into one aggregate.
    /// </summary>
    internal BrowserSynchronousAuthorization And(BrowserSynchronousAuthorization other)
        => new(
            AllowsSynchronousExecution && other.AllowsSynchronousExecution,
            SupportsSynchronousReads || other.SupportsSynchronousReads);

    /// <summary>Throws when the captured execution may not be driven synchronously.</summary>
    internal void ThrowIfReaderIterationRejected()
    {
        if (AllowsSynchronousExecution)
            return;

        throw new PlatformNotSupportedException(
            BrowserSynchronousExecutionContract.DescribeReaderRejection(SupportsSynchronousReads));
    }

    /// <summary>Throws when the captured execution may not be disposed synchronously.</summary>
    internal void ThrowIfReaderDisposalRejected()
    {
        if (AllowsSynchronousExecution)
            return;

        throw new PlatformNotSupportedException(
            BrowserSynchronousExecutionContract.DescribeReaderDisposalRejection(SupportsSynchronousReads));
    }
}

/// <summary>
/// The connection-side inputs a synchronous authorization capture needs. Implemented by both
/// provider facades' connections so one capture routine serves both.
/// </summary>
internal interface IBrowserSynchronousExecutionPolicy
{
    /// <summary>Whether the data source opted into synchronous read-mirror mode.</summary>
    bool SupportsSynchronousReads { get; }

    /// <summary>Whether <paramref name="sql"/> may execute synchronously on this connection.</summary>
    bool AllowsSynchronousSql(string? sql);
}
