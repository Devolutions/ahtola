using System.Reflection;
using System.Runtime.Loader;

namespace Ahtola;

/// <summary>
/// Registers the optional embedded-replica implementation.
/// </summary>
public static class AhtolaReplicaProvider
{
    private const string ReplicaProviderAssemblyName = "Turso.Data.Sync";
    private const string ReplicaProviderRegistrationTypeName = "Turso.Data.Sync.ReplicaProviderRegistration";
    private static AhtolaReplicaProviderFactory? s_factory;

    /// <summary>
    /// Registers the embedded-replica factory supplied by the optional companion assembly.
    /// </summary>
    public static void Register(AhtolaReplicaProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var registeredFactory = Interlocked.CompareExchange(ref s_factory, factory, null);
        if (registeredFactory is not null && registeredFactory.GetType() != factory.GetType())
        {
            throw new InvalidOperationException(
                $"An embedded replica provider factory of type {registeredFactory.GetType().FullName} is already registered.");
        }
    }

    internal static AhtolaReplicaDatabase OpenReplica(AhtolaReplicaOptions options)
    {
        return GetFactory().OpenReplica(options);
    }

    internal static Task<AhtolaReplicaDatabase> OpenReplicaAsync(
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        return GetFactory().OpenReplicaAsync(options, cancellationToken);
    }

    private static AhtolaReplicaProviderFactory GetFactory()
    {
        var factory = Volatile.Read(ref s_factory);
        if (factory is null)
        {
            TryRegisterCompanion();
            factory = Volatile.Read(ref s_factory);
        }

        return factory
            ?? throw new NotSupportedException(
                "Embedded replica connections are not supported yet by the .NET provider. " +
                "Add the matching Turso.Data.Sqlite.Sync companion package to enable them.");
    }

    private static void TryRegisterCompanion()
    {
        try
        {
            var loadContext = AssemblyLoadContext.GetLoadContext(typeof(AhtolaReplicaProvider).Assembly);
            var assembly = loadContext?.LoadFromAssemblyName(new AssemblyName(ReplicaProviderAssemblyName))
                ?? Assembly.Load(new AssemblyName(ReplicaProviderAssemblyName));
            var registrationType = assembly.GetType(ReplicaProviderRegistrationTypeName, throwOnError: true)!;
            var register = registrationType.GetMethod(
                "Register",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(ReplicaProviderRegistrationTypeName, "Register");
            register.Invoke(null, null);
        }
        catch (FileNotFoundException)
        {
        }
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
        RemoteUri = remoteUri;
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
    /// Gets or initializes the HTTP transport policy.
    /// </summary>
    public AhtolaSyncHttpPolicy HttpPolicy { get; init; } = new();

    internal void Validate()
    {
        if (!RemoteUri.IsAbsoluteUri
            || (!RemoteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !RemoteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Embedded replica remote URLs must use HTTP or HTTPS.", nameof(RemoteUri));
        }

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
