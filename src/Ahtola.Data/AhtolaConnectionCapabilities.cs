namespace Ahtola;

/// <summary>
/// Identifies the public ADO.NET facade whose behavior a capability contract describes.
/// </summary>
public enum AhtolaConnectionFacade
{
    AhtolaData,
    Sqlite,
}

/// <summary>
/// Identifies the execution mode behind a connection.
/// </summary>
public enum AhtolaConnectionMode
{
    ManagedLocal,
    NativeLocal,
    RemoteHrana,
    EmbeddedReplica,
}

/// <summary>
/// Describes the operations supported by a facade and execution mode.
/// </summary>
public sealed class AhtolaConnectionCapabilities
{
    private static readonly AhtolaConnectionCapabilities AhtolaManagedLocal = new(
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.ManagedLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsAttach: true,
        supportsPooling: true);

    private static readonly AhtolaConnectionCapabilities AhtolaNativeLocal = new(
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.NativeLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsAttach: true);

    private static readonly AhtolaConnectionCapabilities AhtolaRemoteHrana = new(
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.RemoteHrana,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true);

    private static readonly AhtolaConnectionCapabilities AhtolaManagedEmbeddedReplica = new(
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.EmbeddedReplica,
        canCreateBatch: false,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true);

    private static readonly AhtolaConnectionCapabilities AhtolaSyncEmbeddedReplica = new(
        AhtolaConnectionFacade.AhtolaData,
        AhtolaConnectionMode.EmbeddedReplica,
        canCreateBatch: false,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsSync: true);

    private static readonly AhtolaConnectionCapabilities SqliteManagedLocal = new(
        AhtolaConnectionFacade.Sqlite,
        AhtolaConnectionMode.ManagedLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsBackup: true,
        supportsIncrementalBlob: true,
        supportsUserDefinedFunctions: true,
        supportsUserDefinedAggregates: true,
        supportsCustomCollations: true,
        supportsAttach: true,
        supportsPooling: true);

    private static readonly AhtolaConnectionCapabilities SqliteNativeLocal = new(
        AhtolaConnectionFacade.Sqlite,
        AhtolaConnectionMode.NativeLocal,
        canCreateBatch: true,
        supportsAsyncOperations: true,
        supportsTransactions: true,
        supportsSavepoints: true,
        supportsBackup: true,
        supportsIncrementalBlob: true,
        supportsUserDefinedFunctions: true,
        supportsUserDefinedAggregates: true,
        supportsCustomCollations: true,
        supportsExtensions: true,
        supportsAttach: true);

    private AhtolaConnectionCapabilities(
        AhtolaConnectionFacade facade,
        AhtolaConnectionMode mode,
        bool canCreateBatch,
        bool supportsAsyncOperations,
        bool supportsTransactions,
        bool supportsSavepoints,
        bool supportsBackup = false,
        bool supportsIncrementalBlob = false,
        bool supportsUserDefinedFunctions = false,
        bool supportsUserDefinedAggregates = false,
        bool supportsCustomCollations = false,
        bool supportsExtensions = false,
        bool supportsAttach = false,
        bool supportsPooling = false,
        bool supportsSync = false)
    {
        Facade = facade;
        Mode = mode;
        CanCreateBatch = canCreateBatch;
        SupportsAsyncOperations = supportsAsyncOperations;
        SupportsTransactions = supportsTransactions;
        SupportsSavepoints = supportsSavepoints;
        SupportsBackup = supportsBackup;
        SupportsIncrementalBlob = supportsIncrementalBlob;
        SupportsUserDefinedFunctions = supportsUserDefinedFunctions;
        SupportsUserDefinedAggregates = supportsUserDefinedAggregates;
        SupportsCustomCollations = supportsCustomCollations;
        SupportsExtensions = supportsExtensions;
        SupportsAttach = supportsAttach;
        SupportsPooling = supportsPooling;
        SupportsSync = supportsSync;
    }

    public AhtolaConnectionFacade Facade { get; }

    public AhtolaConnectionMode Mode { get; }

    public bool CanCreateBatch { get; }

    public bool SupportsAsyncOperations { get; }

    public bool SupportsTransactions { get; }

    public bool SupportsSavepoints { get; }

    public bool SupportsBackup { get; }

    public bool SupportsIncrementalBlob { get; }

    public bool SupportsUserDefinedFunctions { get; }

    public bool SupportsUserDefinedAggregates { get; }

    public bool SupportsCustomCollations { get; }

    public bool SupportsExtensions { get; }

    public bool SupportsAttach { get; }

    public bool SupportsPooling { get; }

    public bool SupportsSync { get; }

    internal static AhtolaConnectionCapabilities ForAhtola(
        AhtolaConnectionOptions options,
        bool replicaSupportsSync = false)
    {
        if (options.IsReplica)
            return replicaSupportsSync
                ? AhtolaSyncEmbeddedReplica
                : AhtolaManagedEmbeddedReplica;
        if (options.IsRemote)
            return AhtolaRemoteHrana;
        return options.LocalProvider == AhtolaLocalProvider.Managed
            ? AhtolaManagedLocal
            : AhtolaNativeLocal;
    }

    internal static AhtolaConnectionCapabilities ForSqlite(AhtolaLocalProvider provider)
        => provider == AhtolaLocalProvider.Managed
            ? SqliteManagedLocal
            : SqliteNativeLocal;

    internal static bool IsRemoteDataSource(string dataSource)
        => Uri.TryCreate(dataSource, UriKind.Absolute, out var uri)
           && uri.Scheme is "libsql" or "http" or "https" or "ws" or "wss";
}
