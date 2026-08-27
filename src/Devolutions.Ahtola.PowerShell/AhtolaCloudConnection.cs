using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Ahtola.PSSqlite;

/// <summary>
/// A PowerShell-safe wrapper for a direct Turso Cloud or managed embedded-replica connection.
/// The provider connection, including its bearer token, remains private.
/// </summary>
public sealed class AhtolaCloudConnection : DbConnection
{
    private readonly Ahtola.AhtolaConnection _connection;
    private readonly string _endpoint;
    private readonly string? _replicaPath;

    internal AhtolaCloudConnection(Ahtola.AhtolaConnection connection, Uri endpoint, string? replicaPath)
    {
        _connection = connection;
        _endpoint = endpoint.AbsoluteUri;
        _replicaPath = string.IsNullOrWhiteSpace(replicaPath) ? null : Path.GetFullPath(replicaPath);
    }

    /// <summary>Gets the remote endpoint. Credentials are never included.</summary>
    public string Endpoint => _endpoint;

    /// <summary>Gets the local replica path, or null for a direct Cloud connection.</summary>
    public string? ReplicaPath => _replicaPath;

    /// <summary>Gets whether this connection uses a managed embedded replica.</summary>
    public bool IsReplica => _replicaPath is not null;

    /// <summary>
    /// Gets a redacted connection description. The underlying connection string is never exposed.
    /// </summary>
    [AllowNull]
    public override string ConnectionString
    {
        get => $"Data Source={_endpoint};Auth Token=***";
        set => throw new NotSupportedException("AhtolaCloudConnection is configured when it is created.");
    }

    public override string Database => _connection.Database;

    public override string DataSource => _endpoint;

    public override string ServerVersion => _connection.ServerVersion;

    public override ConnectionState State => _connection.State;

    public override void ChangeDatabase(string databaseName) => _connection.ChangeDatabase(databaseName);

    public override void Close()
        => _connection.Close();

    public override void Open()
        => _connection.Open();

    public override async Task OpenAsync(CancellationToken cancellationToken)
        => await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

    public override string ToString() => $"{nameof(AhtolaCloudConnection)} ({_endpoint})";

    internal Task<Ahtola.AhtolaSyncResult> SynchronizeAsync(
        Ahtola.AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsReplica)
            throw new NotSupportedException("Sync requires a managed embedded replica connection.");

        return _connection.SyncAsync(options, cancellationToken);
    }

    internal Task<Ahtola.AhtolaReplicaConflictReport?> InspectReplicaConflictAsync(
        CancellationToken cancellationToken)
        => _connection.InspectReplicaConflictAsync(cancellationToken);

    internal Task<Ahtola.AhtolaReplicaConflictResolutionResult> ResolveReplicaConflictAsync(
        Ahtola.AhtolaReplicaConflictResolution resolution,
        Ahtola.AhtolaReplicaConflictResolutionOptions options,
        CancellationToken cancellationToken)
        => _connection.ResolveReplicaConflictAsync(resolution, options, cancellationToken);

    internal Ahtola.AhtolaReplicaChangeCaptureBatch PeekPendingChangeCapture()
        => _connection.PeekPendingChangeCapture();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _connection.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => _connection.CreateCommand();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _connection.Dispose();
        base.Dispose(disposing);
    }
}
