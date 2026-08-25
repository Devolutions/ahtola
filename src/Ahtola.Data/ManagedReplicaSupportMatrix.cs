using System.Text;

namespace Ahtola;

/// <summary>
/// Enforces the managed Cloud-replica support boundary before a local replica is opened.
/// </summary>
internal static class ManagedReplicaSupportMatrix
{
    /// <summary>
    /// Upper bound on the encoded <c>server_query_selector</c> (Turso <c>PullUpdatesReqProtoBody</c>
    /// tag 7) the managed client is willing to put on the wire. The managed client never parses or
    /// interprets the bootstrap query -- it is a pass-through to the server -- so this is purely a
    /// defensive cap that keeps one request bounded and keeps the length varint small.
    /// </summary>
    internal const int MaximumBootstrapQueryLength = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void ValidateOptions(AhtolaReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // AhtolaReplicaOptions.Validate() owns the cross-option incompatibilities that must stay
        // fail-closed for every partial bootstrap kind: PartialBootstrap + RemoteEncryption, and
        // PullBytesThreshold + Query (the server, not the client, picks the query page set, so the
        // client can never split it across round trips). Keep both files in step -- this matrix
        // only adds per-kind shape validation on top.
        options.Validate();

        if (options.PartialBootstrap is { } partialBootstrap)
        {
            switch (partialBootstrap.Kind)
            {
                case AhtolaPartialBootstrapKind.Prefix:
                    if (partialBootstrap.PrefixLength < ManagedReplicaBootstrapper.PageSize)
                    {
                        throw new NotSupportedException(
                            "Managed embedded replica prefix bootstrap must select at least one complete 4 KiB page.");
                    }

                    break;
                case AhtolaPartialBootstrapKind.Query:
                    ValidateBootstrapQuery(partialBootstrap.Query);
                    break;
                default:
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
        }

        if (options.RemoteEncryption is not null)
            ManagedReplicaEncryption.EnsureSupportedCipher(options.RemoteEncryption.Cipher);
    }

    /// <summary>
    /// Defense-in-depth validation of the bootstrap query. <see cref="AhtolaPartialBootstrapOptions.QueryPages"/>
    /// already rejects a null/whitespace query at construction, so reaching any throw here means the
    /// options object was constructed by other means; fail closed rather than putting an
    /// unrepresentable selector on the wire.
    /// </summary>
    private static void ValidateBootstrapQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new NotSupportedException(
                "Managed embedded replica query bootstrap requires a non-empty server-side query.");
        }

        int encodedLength;
        try
        {
            encodedLength = StrictUtf8.GetByteCount(query);
        }
        catch (EncoderFallbackException exception)
        {
            throw new NotSupportedException(
                "Managed embedded replica query bootstrap requires a query that encodes as valid UTF-8.",
                exception);
        }

        if (encodedLength > MaximumBootstrapQueryLength)
        {
            throw new NotSupportedException(
                "Managed embedded replica query bootstrap requires a query shorter than "
                + $"{MaximumBootstrapQueryLength} UTF-8 bytes.");
        }
    }
}
