namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Raised when another browser tab or worker owns the requested OPFS database.
/// </summary>
public sealed class AhtolaBrowserDatabaseLockedException : IOException
{
    internal AhtolaBrowserDatabaseLockedException(string lockName, Exception innerException)
        : base(
            $"The browser database '{lockName}' is already open in another tab or worker.",
            innerException)
    {
        LockName = lockName;
    }

    /// <summary>The logical Web Lock name requested by the data source.</summary>
    public string LockName { get; }
}
