using System.Data.Common;
using System.Runtime.Versioning;
using Ahtola.Core;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Creates asynchronously opened SQLite-compatible connections backed by browser OPFS.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class AhtolaBrowserDataSource : DbDataSource, IManagedDatabaseFactory
{
    private readonly object _gate = new();
    private readonly AhtolaBrowserOptions _options;
    private Task<StorageState>? _initialization;
    private TaskCompletionSource? _connectionsDrained;
    private int _activeConnections;
    private bool _disposed;

    public AhtolaBrowserDataSource(AhtolaBrowserOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AhtolaBrowserDataSource(
        string databasePath,
        string ownedDirectory,
        int sharedBufferSize = AhtolaBrowserOptions.DefaultSharedBufferSize,
        bool readOnly = false)
        : this(new AhtolaBrowserOptions(databasePath, ownedDirectory, sharedBufferSize, readOnly))
    {
    }

    public AhtolaBrowserDataSource(
        string databasePath,
        int sharedBufferSize = AhtolaBrowserOptions.DefaultSharedBufferSize,
        bool readOnly = false)
        : this(new AhtolaBrowserOptions(databasePath, sharedBufferSize, readOnly))
    {
    }

    public AhtolaBrowserOptions Options => _options;

    public override string ConnectionString => _options.ConnectionString;

    string IManagedDatabaseFactory.DataSource => _options.DatabasePath;

    bool IManagedDatabaseFactory.IsReadOnly => _options.IsReadOnly;

    public new SqliteConnection CreateConnection()
    {
        ThrowIfDisposed();
        return new SqliteConnection(ConnectionString, this);
    }

    public global::Ahtola.AhtolaConnection CreateAhtolaConnection()
    {
        ThrowIfDisposed();
        return new global::Ahtola.AhtolaConnection(ConnectionString, this);
    }

    public new async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override DbConnection CreateDbConnection() => CreateConnection();

    protected override DbConnection OpenDbConnection()
        => throw BrowserSyncNotSupported("opening");

    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(
        CancellationToken cancellationToken = default)
        => await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    async ValueTask<IManagedDatabaseAdapter> IManagedDatabaseFactory.OpenDatabaseAsync(
        CancellationToken cancellationToken)
    {
        Task<StorageState> initialization;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= InitializeAsync();
        }

        StorageState storage;
        try
        {
            storage = await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_initialization, initialization)
                    && initialization.IsFaulted)
                {
                    _initialization = null;
                }
            }
            throw;
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked
            {
                _activeConnections++;
            }
        }

        try
        {
            var inner = ManagedDatabaseAdapter.OpenFile(
                _options.DatabasePath,
                storage.Mirror,
                readOnly: _options.IsReadOnly);
            return new BrowserManagedDatabaseAdapter(
                inner,
                storage.Mirror,
                ReleaseConnection);
        }
        catch
        {
            ReleaseConnection();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
        }

        throw BrowserSyncNotSupported("disposing");
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        Task<StorageState>? initialization;
        Task? drained = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            initialization = _initialization;
            if (_activeConnections != 0)
            {
                _connectionsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _connectionsDrained.Task;
            }
        }

        if (drained is not null)
            await drained.ConfigureAwait(false);
        if (initialization is null)
            return;

        StorageState storage;
        try
        {
            storage = await initialization.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await storage.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<StorageState> InitializeAsync()
    {
        OpfsAsyncFileSystem? persistent = null;
        BrowserMirroredFileSystem? mirror = null;
        try
        {
            persistent = await OpfsAsyncFileSystem
                .CreateAsync(
                    _options.OwnedDirectory,
                    _options.SharedBufferSize,
                    CancellationToken.None)
                .ConfigureAwait(false);
            mirror = await BrowserMirroredFileSystem
                .CreateAsync(
                    persistent,
                    _options.OwnedDirectory,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return new StorageState(persistent, mirror);
        }
        catch
        {
            if (mirror is not null)
                await mirror.DisposeAsync().ConfigureAwait(false);
            if (persistent is not null)
                await persistent.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ReleaseConnection()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_activeConnections <= 0)
                throw new InvalidOperationException("Browser data-source connection accounting underflow.");

            _activeConnections--;
            if (_activeConnections == 0)
            {
                drained = _connectionsDrained;
                _connectionsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static PlatformNotSupportedException BrowserSyncNotSupported(string operation)
        => new(
            $"Synchronous {operation} is not supported by the browser data source. "
            + "Use the corresponding asynchronous API.");

    private sealed class StorageState(
        OpfsAsyncFileSystem persistent,
        BrowserMirroredFileSystem mirror) : IAsyncDisposable
    {
        public BrowserMirroredFileSystem Mirror { get; } = mirror;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Mirror.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await persistent.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
