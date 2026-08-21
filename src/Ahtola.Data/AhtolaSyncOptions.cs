using System.Net.Http;

namespace Ahtola;

/// <summary>
/// Selects how an embedded replica is partially bootstrapped.
/// </summary>
public enum AhtolaPartialBootstrapKind
{
    /// <summary>
    /// Bootstrap the pages covered by an initial byte prefix.
    /// </summary>
    Prefix,

    /// <summary>
    /// Bootstrap the pages touched by a server-side SQL query.
    /// </summary>
    Query,
}

/// <summary>
/// Configures partial bootstrap and lazy page loading for an embedded replica.
/// The pure-managed provider supports prefix selection and fetches missing pages on demand
/// from the pinned bootstrap revision. Query selection is not supported by that provider.
/// </summary>
public sealed class AhtolaPartialBootstrapOptions
{
    private AhtolaPartialBootstrapOptions(
        AhtolaPartialBootstrapKind kind,
        int prefixLength,
        string? query,
        long? segmentSize,
        bool prefetch)
    {
        if (segmentSize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSize), segmentSize, "Segment size must be positive.");

        Kind = kind;
        PrefixLength = prefixLength;
        Query = query;
        SegmentSize = segmentSize;
        Prefetch = prefetch;
    }

    /// <summary>
    /// Creates a prefix strategy that bootstraps complete 4 KiB pages within the first
    /// <paramref name="length"/> bytes. The pure-managed provider lazily fetches pages outside
    /// this range when the pager first reads them.
    /// </summary>
    public static AhtolaPartialBootstrapOptions Prefix(
        int length,
        long? segmentSize = null,
        bool prefetch = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new AhtolaPartialBootstrapOptions(
            AhtolaPartialBootstrapKind.Prefix,
            length,
            query: null,
            segmentSize,
            prefetch);
    }

    /// <summary>
    /// Creates a query strategy that bootstraps pages touched by <paramref name="query"/> on the server.
    /// The pure-managed provider rejects this strategy explicitly.
    /// </summary>
    public static AhtolaPartialBootstrapOptions QueryPages(
        string query,
        long? segmentSize = null,
        bool prefetch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return new AhtolaPartialBootstrapOptions(
            AhtolaPartialBootstrapKind.Query,
            prefixLength: 0,
            query,
            segmentSize,
            prefetch);
    }

    /// <summary>
    /// Gets the selected bootstrap strategy.
    /// </summary>
    public AhtolaPartialBootstrapKind Kind { get; }

    /// <summary>
    /// Gets the prefix length in bytes, or zero for a query strategy.
    /// </summary>
    public int PrefixLength { get; }

    /// <summary>
    /// Gets the server-side bootstrap query, or <see langword="null"/> for a prefix strategy.
    /// </summary>
    public string? Query { get; }

    /// <summary>
    /// Gets the lazy-loading segment size in bytes, or <see langword="null"/> for the SDK default.
    /// </summary>
    public long? SegmentSize { get; }

    /// <summary>
    /// Gets whether adjacent pages are prefetched during lazy loading.
    /// </summary>
    public bool Prefetch { get; }
}

/// <summary>
/// Selects the cipher configured for an encrypted Ahtola Cloud database.
/// </summary>
public enum AhtolaRemoteEncryptionCipher
{
    Aes256Gcm,
    Aes128Gcm,
    ChaCha20Poly1305,
    Aegis128L,
    Aegis128X2,
    Aegis128X4,
    Aegis256,
    Aegis256X2,
    Aegis256X4,
}

/// <summary>
/// Configures access to an encrypted Ahtola Cloud database.
/// </summary>
public sealed class AhtolaRemoteEncryptionOptions
{
    /// <summary>
    /// Initializes remote encryption with the base64-encoded key and server-side cipher.
    /// </summary>
    public AhtolaRemoteEncryptionOptions(string base64Key, AhtolaRemoteEncryptionCipher cipher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        Base64Key = base64Key;
        Cipher = cipher;
    }

    /// <summary>
    /// Gets the base64-encoded remote encryption key.
    /// </summary>
    public string Base64Key { get; }

    /// <summary>
    /// Gets the cipher configured on the remote database.
    /// </summary>
    public AhtolaRemoteEncryptionCipher Cipher { get; }

    internal int ReservedBytes => Cipher switch
    {
        AhtolaRemoteEncryptionCipher.Aes256Gcm
            or AhtolaRemoteEncryptionCipher.Aes128Gcm
            or AhtolaRemoteEncryptionCipher.ChaCha20Poly1305 => 28,
        AhtolaRemoteEncryptionCipher.Aegis128L
            or AhtolaRemoteEncryptionCipher.Aegis128X2
            or AhtolaRemoteEncryptionCipher.Aegis128X4 => 32,
        AhtolaRemoteEncryptionCipher.Aegis256
            or AhtolaRemoteEncryptionCipher.Aegis256X2
            or AhtolaRemoteEncryptionCipher.Aegis256X4 => 48,
        _ => throw new ArgumentOutOfRangeException(nameof(Cipher), Cipher, "Unknown remote encryption cipher."),
    };

    internal string NativeName => Cipher switch
    {
        AhtolaRemoteEncryptionCipher.Aes256Gcm => "aes256gcm",
        AhtolaRemoteEncryptionCipher.Aes128Gcm => "aes128gcm",
        AhtolaRemoteEncryptionCipher.ChaCha20Poly1305 => "chacha20poly1305",
        AhtolaRemoteEncryptionCipher.Aegis128L => "aegis128l",
        AhtolaRemoteEncryptionCipher.Aegis128X2 => "aegis128x2",
        AhtolaRemoteEncryptionCipher.Aegis128X4 => "aegis128x4",
        AhtolaRemoteEncryptionCipher.Aegis256 => "aegis256",
        AhtolaRemoteEncryptionCipher.Aegis256X2 => "aegis256x2",
        AhtolaRemoteEncryptionCipher.Aegis256X4 => "aegis256x4",
        _ => throw new ArgumentOutOfRangeException(nameof(Cipher), Cipher, "Unknown remote encryption cipher."),
    };
}

/// <summary>
/// Controls HTTP transport ownership and timeouts for one embedded replica.
/// </summary>
public sealed class AhtolaSyncHttpPolicy
{
    private int _handlerOwnershipClaimed;

    /// <summary>
    /// Initializes an HTTP policy.
    /// </summary>
    /// <param name="messageHandler">
    /// Optional application-provided handler. The replica does not dispose it unless
    /// <paramref name="disposeMessageHandler"/> is <see langword="true"/>.
    /// </param>
    /// <param name="requestTimeout">
    /// Per-request timeout. The default is infinite so long polling is governed by
    /// <see cref="AhtolaReplicaOptions.LongPollTimeout"/>.
    /// </param>
    /// <param name="disposeMessageHandler">
    /// Whether the connection owns <paramref name="messageHandler"/>. An owned handler
    /// remains usable across <see cref="AhtolaConnection.Close"/> and reopen cycles and is
    /// disposed with the connection. An ownership-transferring policy can create only
    /// one connection.
    /// </param>
    public AhtolaSyncHttpPolicy(
        HttpMessageHandler? messageHandler = null,
        bool disposeMessageHandler = false,
        TimeSpan? requestTimeout = null)
    {
        var timeout = requestTimeout ?? Timeout.InfiniteTimeSpan;
        if (timeout != Timeout.InfiniteTimeSpan
            && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                timeout,
                $"Request timeout must be between 1 and {int.MaxValue} milliseconds, or infinite.");
        }

        MessageHandler = messageHandler;
        DisposeMessageHandler = disposeMessageHandler;
        RequestTimeout = timeout;
    }

    /// <summary>
    /// Gets the application-provided HTTP message handler, if any.
    /// </summary>
    public HttpMessageHandler? MessageHandler { get; }

    /// <summary>
    /// Gets whether the connection owns and disposes <see cref="MessageHandler"/>.
    /// </summary>
    public bool DisposeMessageHandler { get; }

    /// <summary>
    /// Gets the per-request HTTP timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; }

    internal HttpMessageHandler? ClaimMessageHandlerOwnership()
    {
        if (!DisposeMessageHandler || MessageHandler is null)
            return null;
        if (Interlocked.CompareExchange(ref _handlerOwnershipClaimed, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "This HTTP policy already transferred ownership of its message handler to another connection.");
        }

        return MessageHandler;
    }
}

/// <summary>
/// Identifies a synchronization phase.
/// </summary>
public enum AhtolaSyncProgressStage
{
    Pushing,
    Pulling,
    Applying,
    Completed,
}

/// <summary>
/// Describes progress through one explicit synchronization operation.
/// </summary>
public sealed record AhtolaSyncProgress(AhtolaSyncProgressStage Stage);

/// <summary>
/// Configures one explicit synchronization operation.
/// </summary>
public sealed class AhtolaSyncOptions
{
    /// <summary>
    /// Initializes synchronization options.
    /// </summary>
    public AhtolaSyncOptions(IProgress<AhtolaSyncProgress>? progress = null)
    {
        Progress = progress;
    }

    /// <summary>
    /// Gets the phase progress observer, if any.
    /// </summary>
    public IProgress<AhtolaSyncProgress>? Progress { get; }
}

/// <summary>
/// Identifies the observable result of a successful synchronization.
/// </summary>
public enum AhtolaSyncOutcome
{
    UpToDate,
    RemoteChangesApplied,
}

/// <summary>
/// Contains a snapshot of native sync-engine statistics.
/// </summary>
public sealed record AhtolaSyncStatistics(
    long CdcOperations,
    long MainWalSize,
    long RevertWalSize,
    DateTimeOffset? LastPull,
    DateTimeOffset? LastPush,
    long NetworkSentBytes,
    long NetworkReceivedBytes,
    string? Revision);

/// <summary>
/// Describes a completed explicit synchronization.
/// </summary>
public sealed record AhtolaSyncResult(AhtolaSyncOutcome Outcome, AhtolaSyncStatistics Statistics);
