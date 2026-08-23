namespace Ahtola.Core.Storage;

/// <summary>
/// Controls when SQLite storage writes are forced to durable media.
/// </summary>
/// <remarks>
/// This follows SQLite's <c>PRAGMA synchronous</c> values. In WAL mode,
/// <see cref="Normal"/> defers the WAL barrier to checkpoint while
/// <see cref="Full"/> and <see cref="Extra"/> also barrier every commit.
/// <see cref="Extra"/> is equivalent to <see cref="Full"/> for WAL and retains
/// the rollback journal's durable invalidation before deletion.
/// </remarks>
public enum SqliteSynchronousMode
{
    Off = 0,
    Normal = 1,
    Full = 2,
    Extra = 3,
}

internal static class SqliteSynchronousModeExtensions
{
    internal static bool SyncsWalCommit(this SqliteSynchronousMode mode)
        => mode is SqliteSynchronousMode.Full or SqliteSynchronousMode.Extra;

    internal static bool SyncsCheckpoint(this SqliteSynchronousMode mode)
        => mode is not SqliteSynchronousMode.Off;

    internal static bool UsesFullRollbackBarriers(this SqliteSynchronousMode mode)
        => mode is SqliteSynchronousMode.Full or SqliteSynchronousMode.Extra;

    internal static void Validate(this SqliteSynchronousMode mode, string parameterName)
    {
        if (mode is < SqliteSynchronousMode.Off or > SqliteSynchronousMode.Extra)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                mode,
                "Unsupported SQLite synchronous mode.");
        }
    }
}
