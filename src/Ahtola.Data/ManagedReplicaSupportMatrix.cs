namespace Ahtola;

/// <summary>
/// Enforces the managed Cloud-replica support boundary before a local replica is opened.
/// </summary>
internal static class ManagedReplicaSupportMatrix
{
    public static void ValidateOptions(AhtolaReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.PartialBootstrap is not null)
        {
            throw new NotSupportedException(
                "Managed embedded replicas support only a complete raw 4 KiB page bootstrap; partial, query, and lazy bootstrap are not supported.");
        }

        if (options.PullBytesThreshold is not null)
        {
            throw new NotSupportedException(
                "Managed embedded replicas support only a single complete raw 4 KiB page bootstrap; chunked bootstrap is not supported.");
        }

        if (options.RemoteEncryption is not null)
            ManagedReplicaEncryption.EnsureSupportedCipher(options.RemoteEncryption.Cipher);
    }
}
