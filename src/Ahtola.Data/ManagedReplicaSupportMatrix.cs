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

        if (options.PartialBootstrap is { } partialBootstrap)
        {
            if (partialBootstrap.Kind == AhtolaPartialBootstrapKind.Query)
            {
                throw new NotSupportedException(
                    "Managed embedded replicas do not support query-selected bootstrap pages.");
            }

            if (partialBootstrap.Kind != AhtolaPartialBootstrapKind.Prefix)
            {
                throw new NotSupportedException(
                    "Managed embedded replicas do not support the selected partial bootstrap mode.");
            }

            if (partialBootstrap.SegmentSize is not null || partialBootstrap.Prefetch)
            {
                throw new NotSupportedException(
                    "Managed embedded replicas support eager prefix bootstrap only; lazy segment loading and prefetch are not supported.");
            }

            if (partialBootstrap.PrefixLength < 4096)
            {
                throw new NotSupportedException(
                    "Managed embedded replica prefix bootstrap must select at least one complete 4 KiB page.");
            }
        }

        if (options.RemoteEncryption is not null)
            ManagedReplicaEncryption.EnsureSupportedCipher(options.RemoteEncryption.Cipher);
    }
}
