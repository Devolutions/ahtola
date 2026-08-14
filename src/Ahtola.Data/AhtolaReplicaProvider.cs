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
    private readonly AsyncLocal<ApplicationHttpScope?> _applicationHttpScope = new();

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
    /// Gets or initializes the bootstrap pull chunk target in bytes.
    /// </summary>
    public long? PullBytesThreshold { get; init; }

    /// <summary>
    /// Gets or initializes the managed embedded-replica synchronization interval in seconds.
    /// A positive value starts a background synchronization loop after the connection opens.
    /// </summary>
    public int SyncInterval { get; init; }

    /// <summary>
    /// Gets or initializes the HTTP transport policy.
    /// </summary>
    public AhtolaSyncHttpPolicy HttpPolicy { get; init; } = new();

    internal void Validate()
    {
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
        ArgumentNullException.ThrowIfNull(HttpPolicy);
        ArgumentNullException.ThrowIfNull(HttpPolicy);
    }

    private static Uri NormalizeRemoteUri(Uri remoteUri)
    {
        if (!remoteUri.IsAbsoluteUri)
            throw new ArgumentException("Embedded replica remote URLs must be absolute.", nameof(remoteUri));

        if (remoteUri.Scheme.Equals("libsql", StringComparison.OrdinalIgnoreCase))
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

        throw new ArgumentException("Embedded replica remote URLs must use libsql, HTTP, or HTTPS.", nameof(remoteUri));
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
            HttpPolicy = HttpPolicy,
        };
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

    internal virtual void EnsureCanClose()
    {
    }

    internal virtual Exception? CancelPendingOperationsForClose() => null;
}
