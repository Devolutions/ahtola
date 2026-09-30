namespace Ahtola;

/// <summary>
/// Registers the optional embedded-replica implementation.
/// </summary>
public static class AhtolaReplicaProvider
{
    private static AhtolaReplicaProviderFactory? s_factory;

    /// <summary>
    /// Registers an explicitly supplied embedded-replica factory.
    /// </summary>
    public static void Register(AhtolaReplicaProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var registeredFactory = Interlocked.CompareExchange(ref s_factory, factory, null);
        if (registeredFactory is not null && !ReferenceEquals(registeredFactory, factory))
        {
            throw new InvalidOperationException(
                "An embedded replica provider factory is already registered.");
        }
    }

    internal static bool HasRegisteredFactory => Volatile.Read(ref s_factory) is not null;

    internal static AhtolaReplicaDatabase OpenRegisteredReplica(AhtolaReplicaOptions options)
    {
        return GetFactory().OpenReplica(options);
    }

    internal static Task<AhtolaReplicaDatabase> OpenRegisteredReplicaAsync(
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        return GetFactory().OpenReplicaAsync(options, cancellationToken);
    }

    private static AhtolaReplicaProviderFactory GetFactory()
    {
        return Volatile.Read(ref s_factory)
            ?? throw new NotSupportedException(
                "No embedded replica factory has been registered.");
    }
}

/// <summary>
/// Describes an embedded replica requested through <see cref="AhtolaConnection"/>.
/// </summary>
public sealed class AhtolaReplicaOptions
{
    private AsyncLocal<ApplicationHttpScope?> _applicationHttpScope = new();

    /// <summary>
    /// Initializes embedded-replica connection options.
    /// </summary>
    public AhtolaReplicaOptions(
        string path,
        Uri remoteUri,
        string? authToken)
        : this(path, remoteUri, authToken, bootstrapIfEmpty: true)
    {
    }

    /// <summary>
    /// Initializes embedded-replica connection options.
    /// </summary>
    public AhtolaReplicaOptions(
        string path,
        Uri remoteUri,
        string? authToken,
        bool bootstrapIfEmpty = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(remoteUri);

        Path = path;
        RemoteUri = NormalizeRemoteUri(remoteUri);
        AuthToken = authToken;
        BootstrapIfEmpty = bootstrapIfEmpty;
    }

    /// <summary>
    /// Gets the local path of the replica database.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the normalized HTTP(S) URL of the remote database.
    /// </summary>
    public Uri RemoteUri { get; }

    /// <summary>
    /// Gets the bearer token sent to the remote database, if configured.
    /// </summary>
    public string? AuthToken { get; }

    /// <summary>
    /// Gets whether a missing local replica is bootstrapped from the remote database.
    /// </summary>
    public bool BootstrapIfEmpty { get; }

    /// <summary>
    /// Gets or initializes the server long-poll timeout. A null value disables long polling.
    /// </summary>
    public TimeSpan? LongPollTimeout { get; init; }

    /// <summary>
    /// Gets or initializes partial bootstrap and lazy page loading.
    /// </summary>
    public AhtolaPartialBootstrapOptions? PartialBootstrap { get; init; }

    /// <summary>
    /// Gets or initializes remote database encryption.
    /// </summary>
    public AhtolaRemoteEncryptionOptions? RemoteEncryption { get; init; }

    /// <summary>
    /// Gets or initializes the maximum CDC operation target for one push batch.
    /// </summary>
    public long? PushOperationsThreshold { get; init; }

    /// <summary>
    /// Gets or initializes the target size in bytes for each initial bootstrap pull.
    /// The target is rounded up to complete 4 KiB pages, and the complete database
    /// image is staged eagerly before it is published.
    /// </summary>
    public long? PullBytesThreshold { get; init; }

    /// <summary>
    /// Gets or initializes the managed embedded-replica synchronization interval in seconds.
    /// A positive value starts a background synchronization loop after the connection opens.
    /// </summary>
    public int SyncInterval { get; init; }

    /// <summary>
    /// Gets or initializes what each automatic synchronization tick does. The default,
    /// <see cref="AhtolaAutomaticSyncMode.PushAndPull"/>, keeps Ahtola's historical full sync;
    /// <see cref="AhtolaAutomaticSyncMode.PullOnly"/> matches Turso's pull-only loop.
    /// </summary>
    public AhtolaAutomaticSyncMode AutomaticSyncMode { get; init; }

    /// <summary>
    /// Gets or initializes a client name used as the prefix of the sync client identifier that a
    /// newly bootstrapped replica records (<c>&lt;name&gt;-&lt;guid&gt;</c>), mirroring Turso's
    /// <c>client_name</c>. <see langword="null"/> keeps the plain GUID identifier. An already
    /// bootstrapped replica keeps the identifier it was created with. Allowed characters are ASCII
    /// letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, up to 64 characters.
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Gets or initializes an optional bearer-token provider. When set it takes precedence over
    /// <see cref="AuthToken"/> and is invoked before every sync HTTP request (bootstrap, pull,
    /// lazy page fetch and push), so a rotated token takes effect without reopening the
    /// connection. A <see langword="null"/> or blank result sends no token. The provider must not
    /// call back into the replica connection.
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? AuthTokenProvider { get; init; }

    /// <summary>
    /// Gets or initializes the HTTP transport policy.
    /// </summary>
    public AhtolaSyncHttpPolicy HttpPolicy { get; init; } = new();

    /// <summary>Resolves the bearer token for one sync HTTP request.</summary>
    internal ValueTask<string?> ResolveAuthTokenAsync(CancellationToken cancellationToken)
        => AhtolaRemoteClient.ResolveAuthTokenAsync(AuthToken, AuthTokenProvider, cancellationToken);

    /// <summary>
    /// Returns the sync client identifier for a new bootstrap: a GUID, prefixed by
    /// <see cref="ClientName"/> when one is configured.
    /// </summary>
    internal string CreateClientId()
    {
        var id = Guid.NewGuid().ToString("N");
        return string.IsNullOrEmpty(ClientName) ? id : string.Concat(ClientName, "-", id);
    }

    /// <summary>
    /// Whether <paramref name="clientId"/> is a valid persisted sync client identifier: a
    /// 32-digit GUID, optionally prefixed by a valid client name and <c>-</c>.
    /// </summary>
    internal static bool IsValidClientId(string clientId)
    {
        if (Guid.TryParseExact(clientId, "N", out _))
            return true;

        var separator = clientId.LastIndexOf('-');
        return separator > 0
               && IsValidClientName(clientId[..separator])
               && Guid.TryParseExact(clientId[(separator + 1)..], "N", out _);
    }

    internal static bool IsValidClientName(string name)
    {
        if (name.Length is 0 or > 64)
            return false;

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(HttpPolicy);
        AhtolaRemoteTransportSecurity.Validate(
            RemoteUri,
            AuthTokenProvider is null ? AuthToken : AhtolaRemoteClient.AuthTokenProviderPlaceholder,
            remoteEncryptionConfigured: RemoteEncryption is not null);
        if (!Enum.IsDefined(AutomaticSyncMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AutomaticSyncMode),
                AutomaticSyncMode,
                "Unknown automatic synchronization mode.");
        }
        if (ClientName is not null && !IsValidClientName(ClientName))
        {
            throw new ArgumentException(
                "Sync client names must be 1 to 64 ASCII letters, digits, '.', '_' or '-'.",
                nameof(ClientName));
        }
        AhtolaRemoteTransportSecurity.ValidateRedirectContract(
            HttpPolicy.MessageHandler is null || HttpPolicy.MessageHandlerDisablesAutomaticRedirects,
            remoteEncryptionConfigured: RemoteEncryption is not null);

        if (LongPollTimeout is { } longPollTimeout
            && (longPollTimeout < TimeSpan.FromMilliseconds(1)
                || longPollTimeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LongPollTimeout),
                longPollTimeout,
                $"Long-poll timeout must be between 1 and {int.MaxValue} milliseconds.");
        }

        ValidateNativeSize(PushOperationsThreshold, nameof(PushOperationsThreshold));
        ValidateNativeSize(PullBytesThreshold, nameof(PullBytesThreshold));
        ArgumentOutOfRangeException.ThrowIfNegative(SyncInterval);
        if (PartialBootstrap?.SegmentSize is { } segmentSize)
            ValidateNativeSize(segmentSize, nameof(AhtolaPartialBootstrapOptions.SegmentSize));

        if (PartialBootstrap is not null && !BootstrapIfEmpty)
        {
            throw new InvalidOperationException(
                "Partial bootstrap requires BootstrapIfEmpty=True because it configures the initial remote bootstrap.");
        }

        // Cross-option incompatibilities that must stay fail-closed for query bootstrap. These live
        // here, not in ManagedReplicaSupportMatrix, because they are option-shape rules rather than
        // managed-provider capability rules; the matrix calls Validate() first and then adds per-kind
        // shape validation on top. Keep the two files in step when either changes.
        if (PartialBootstrap is not null && RemoteEncryption is not null)
        {
            throw new InvalidOperationException(
                "Partial bootstrap cannot be combined with remote encryption.");
        }

        if (PartialBootstrap?.Kind == AhtolaPartialBootstrapKind.Query && PullBytesThreshold is not null)
        {
            throw new InvalidOperationException(
                "PullBytesThreshold cannot be combined with query partial bootstrap because the server selects the query page set.");
        }
    }
    private static Uri NormalizeRemoteUri(Uri remoteUri)
    {
        if (!remoteUri.IsAbsoluteUri)
            throw new ArgumentException("Embedded replica remote URLs must be absolute.", nameof(remoteUri));

        if (remoteUri.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase)
            || remoteUri.Scheme.Equals("turso", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(remoteUri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = remoteUri.IsDefaultPort ? -1 : remoteUri.Port,
                UserName = string.Empty,
                Password = string.Empty,
            };
            return builder.Uri;
        }

        if (remoteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || remoteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return remoteUri;
        }

        throw new ArgumentException(
            "Embedded replica remote URLs must use libsql, turso, HTTP, or HTTPS.", nameof(remoteUri));
    }

    internal IDisposable EnterApplicationHttpScope()
    {
        var previousScope = _applicationHttpScope.Value;
        var scope = new ApplicationHttpScope();
        _applicationHttpScope.Value = scope;
        return new ApplicationHttpScopeLease(_applicationHttpScope, scope, previousScope);
    }

    internal AhtolaReplicaOptions CloneForConnection()
    {
        return new AhtolaReplicaOptions(Path, RemoteUri, AuthToken, BootstrapIfEmpty)
        {
            LongPollTimeout = LongPollTimeout,
            PartialBootstrap = PartialBootstrap,
            RemoteEncryption = RemoteEncryption,
            PushOperationsThreshold = PushOperationsThreshold,
            PullBytesThreshold = PullBytesThreshold,
            SyncInterval = SyncInterval,
            AutomaticSyncMode = AutomaticSyncMode,
            ClientName = ClientName,
            AuthTokenProvider = AuthTokenProvider,
            HttpPolicy = HttpPolicy,
        };
    }

    /// <summary>
    /// Returns a copy with long polling disabled, used for a one-shot, non-blocking pull (e.g.
    /// the immediate logical catch-up performed right after a fresh managed embedded-replica
    /// bootstrap): it must return whatever is immediately available rather than hold the call
    /// open waiting for future changes.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="_applicationHttpScope"/> with the source instance (rather than starting
    /// a fresh one, as <see cref="CloneForConnection"/> does for a genuinely new connection): the
    /// catch-up pull and the connection's regular pulls are the same logical connection's HTTP
    /// call stack, so a reentrant application HTTP handler must be caught regardless of which of
    /// the two option instances is in scope when the reentrant call happens.
    /// </remarks>
    internal AhtolaReplicaOptions WithoutLongPoll()
    {
        var clone = new AhtolaReplicaOptions(Path, RemoteUri, AuthToken, BootstrapIfEmpty)
        {
            LongPollTimeout = null,
            PartialBootstrap = PartialBootstrap,
            RemoteEncryption = RemoteEncryption,
            PushOperationsThreshold = PushOperationsThreshold,
            PullBytesThreshold = PullBytesThreshold,
            SyncInterval = SyncInterval,
            AutomaticSyncMode = AutomaticSyncMode,
            ClientName = ClientName,
            AuthTokenProvider = AuthTokenProvider,
            HttpPolicy = HttpPolicy,
        };
        clone._applicationHttpScope = _applicationHttpScope;
        return clone;
    }

    internal void ThrowIfApplicationHttpReentrant(bool closing)
    {
        if (_applicationHttpScope.Value?.IsActive != true)
            return;

        throw new InvalidOperationException(closing
            ? "An embedded replica cannot be closed from its HTTP handler or response body."
            : "Embedded replica operations cannot be reentered from its HTTP handler or response body.");
    }

    private static void ValidateNativeSize(long? value, string parameterName)
    {
        if (value is null)
            return;
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be positive.");
        if ((ulong)value > nuint.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, value, "The value exceeds the native platform size.");
    }

    private sealed class ApplicationHttpScope
    {
        private int _isActive = 1;

        public bool IsActive => Volatile.Read(ref _isActive) != 0;

        public void Deactivate() => Interlocked.Exchange(ref _isActive, 0);
    }

    private sealed class ApplicationHttpScopeLease(
        AsyncLocal<ApplicationHttpScope?> currentScope,
        ApplicationHttpScope scope,
        ApplicationHttpScope? previousScope) : IDisposable
    {
        public void Dispose()
        {
            scope.Deactivate();
            currentScope.Value = previousScope;
        }
    }
}

/// <summary>
/// Contract implemented by the optional embedded-replica companion assembly.
/// </summary>
public abstract class AhtolaReplicaProviderFactory
{
    /// <summary>
    /// Opens an embedded replica and its local native SQL connection.
    /// </summary>
    public abstract AhtolaReplicaDatabase OpenReplica(AhtolaReplicaOptions options);

    /// <summary>
    /// Asynchronously opens an embedded replica and its local native SQL connection.
    /// </summary>
    public virtual Task<AhtolaReplicaDatabase> OpenReplicaAsync(
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenReplica(options));
    }
}

/// <summary>
/// A native SQL connection backed by an embedded replica.
/// </summary>
public abstract class AhtolaReplicaDatabase : AhtolaNativeDatabase
{
    /// <summary>
    /// Pushes local changes and pulls and applies remote changes.
    /// </summary>
    public abstract Task SyncAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pushes local changes and pulls and applies remote changes.
    /// </summary>
    public virtual Task<AhtolaSyncResult> SyncAsync(
        AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "This embedded replica provider does not support result-bearing synchronization.");
    }

    /// <summary>
    /// Pulls and applies remote changes without pushing local changes.
    /// </summary>
    public virtual Task<AhtolaSyncResult> PullAsync(
        AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "This embedded replica provider does not support pull-only synchronization.");
    }

    /// <summary>
    /// Pushes local changes without pulling remote changes.
    /// </summary>
    public virtual Task<AhtolaSyncResult> PushAsync(
        AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "This embedded replica provider does not support push-only synchronization.");
    }

    /// <summary>
    /// Checkpoints the local replica's write-ahead log into its main database file.
    /// </summary>
    public virtual Task CheckpointAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "This embedded replica provider does not support explicit checkpoints.");
    }

    /// <summary>
    /// Returns a snapshot of the replica's synchronization statistics.
    /// </summary>
    public virtual Task<AhtolaSyncStatistics> GetSyncStatisticsAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "This embedded replica provider does not report synchronization statistics.");
    }

    internal virtual void EnsureCanClose()
    {
    }

    internal virtual Exception? CancelPendingOperationsForClose() => null;
}
