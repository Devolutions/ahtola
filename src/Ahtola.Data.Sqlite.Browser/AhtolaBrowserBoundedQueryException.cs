namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Thrown when a statement's shape, or a cursor's actual page requirement, is not supported by
/// a bounded scan connection (<see cref="AhtolaBrowserDataSource.OpenBoundedScanConnectionAsync"/>).
/// </summary>
/// <remarks>
/// Never thrown after partial results have been yielded to the caller for a given statement: a
/// shape rejection happens at <c>ExecuteBoundedScanAsync</c>, before any page is read, and a
/// page-budget rejection happens at the exact <c>ReadAsync</c> call that would have exceeded it.
/// A bounded scan connection never falls back to the whole-image mirror on its own — the caller
/// decides whether to retry the same statement against a <c>WholeImage</c> connection instead.
/// </remarks>
public sealed class AhtolaBrowserBoundedQueryException : NotSupportedException
{
    public AhtolaBrowserBoundedQueryException(string message)
        : base(message)
    {
    }

    public AhtolaBrowserBoundedQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
