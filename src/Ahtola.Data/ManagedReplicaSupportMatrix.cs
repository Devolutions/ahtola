namespace Ahtola;

/// <summary>
/// Enforces the managed Cloud-replica support boundary before a local replica is opened.
/// </summary>
internal static class ManagedReplicaSupportMatrix
{
    public static void ValidateOptions(AhtolaReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PartialBootstrap is not null)
        {
            throw new NotSupportedException(
                "Managed embedded replicas support only a complete raw 4 KiB page bootstrap; partial, query, and lazy bootstrap are not supported.");
        }

        if (options.RemoteEncryption is not null)
        {
            throw new NotSupportedException(
                "Managed embedded replicas do not support encrypted remote page streams.");
        }
    }
}
