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
    {
        try
        {
            _connection.Close();
        }
        catch
        {
            throw new InvalidOperationException("The Turso Cloud connection could not be closed.");
        }
    }

    public override void Open()
    {
        try
        {
            _connection.Open();
        }
        catch
        {
            throw new InvalidOperationException("The Turso Cloud connection could not be opened.");
        }
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw new InvalidOperationException("The Turso Cloud connection could not be opened.");
        }
    }

    public override string ToString() => $"{nameof(AhtolaCloudConnection)} ({_endpoint})";

    internal Ahtola.AhtolaSyncResult Synchronize()
    {
        if (!IsReplica)
            throw new NotSupportedException("Sync requires a managed embedded replica connection.");

        try
        {
            return _connection.Sync(new Ahtola.AhtolaSyncOptions());
        }
        catch
        {
            throw new InvalidOperationException("The Turso Cloud replica synchronization failed.");
        }
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _connection.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => _connection.CreateCommand();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _connection.Dispose();
            }
            catch
            {
                throw new InvalidOperationException("The Turso Cloud connection could not be disposed.");
            }
        }
        base.Dispose(disposing);
    }
}
