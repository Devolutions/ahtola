namespace Ahtola;

/// <summary>
/// Enforces the managed Cloud-replica support boundary before a local replica is opened.
/// </summary>
internal static class ManagedReplicaSupportMatrix
{
    public static void ValidateOptions(AhtolaReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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

            if (partialBootstrap.SegmentSize is { } segmentSize
                && (segmentSize < ManagedReplicaBootstrapper.PageSize
                    || segmentSize % ManagedReplicaBootstrapper.PageSize != 0))
            {
                throw new NotSupportedException(
                    "Managed embedded replica lazy segment size must be a whole number of 4 KiB pages.");
            }

            if (partialBootstrap.PrefixLength < 4096)
            {
                throw new NotSupportedException(
                    "Managed embedded replica prefix bootstrap must select at least one complete 4 KiB page.");
            }
        }

        if (options.PullBytesThreshold is not null)
        {
            throw new NotSupportedException(
                "Managed embedded replicas do not support chunked bootstrap pulls.");
        }

        if (options.RemoteEncryption is not null)
        {
            throw new NotSupportedException(
                "Managed embedded replicas do not support encrypted remote page streams.");
        }
    }
}
