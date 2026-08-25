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
    private readonly bool _ownsOptions;
    private readonly AhtolaBrowserEncryptionOptions? _encryption;
    private readonly string _memoryName = "ahtola-browser-memory-" + Guid.NewGuid().ToString("N");
    private IManagedDatabaseAdapter? _memoryOwner;
    private Task<StorageState>? _initialization;
    private TaskCompletionSource? _connectionsDrained;
    private int _activeConnections;
    private bool _disposed;

    public AhtolaBrowserDataSource(AhtolaBrowserOptions options)
        : this(options ?? throw new ArgumentNullException(nameof(options)), ownsOptions: false)
    {
    }

    private AhtolaBrowserDataSource(AhtolaBrowserOptions options, bool ownsOptions)
    {
        _options = options;
        _ownsOptions = ownsOptions;

        // Options this instance created are disposed with it, so their single copy
        // of the key is the one used and zeroed. Caller-owned options may be
        // disposed at any time, so take an independent snapshot instead.
        _encryption = ownsOptions ? null : options.Encryption?.CreateOwnedCopy();
    }

    /// <summary>
    /// Creates a data source over an explicitly owned OPFS directory, asynchronous only.
    /// </summary>
    /// <remarks>
    /// This overload keeps the exact CLR signature that shipped before synchronous read-mirror
    /// mode existed. Adding an optional parameter to it would have changed the signature and
    /// broken every already-compiled caller at run time, so the mode is offered by the dedicated
    /// overload below instead.
    /// </remarks>
    public AhtolaBrowserDataSource(
        string databasePath,
        string ownedDirectory,
        int sharedBufferSize = AhtolaBrowserOptions.DefaultSharedBufferSize,
        bool readOnly = false,
        AhtolaBrowserEncryptionOptions? encryption = null)
        : this(
            databasePath,
            ownedDirectory,
            sharedBufferSize,
            readOnly,
            encryption,
            AhtolaBrowserSynchronousMode.AsyncOnly)
    {
    }

    /// <summary>
    /// Creates a data source over an explicitly owned OPFS directory, choosing whether provably
    /// read-only statements may also be served synchronously from the managed in-memory mirror.
    /// </summary>
    public AhtolaBrowserDataSource(
        string databasePath,
        string ownedDirectory,
        int sharedBufferSize,
        bool readOnly,
        AhtolaBrowserEncryptionOptions? encryption,
        AhtolaBrowserSynchronousMode synchronousMode)
        : this(
            new AhtolaBrowserOptions(
                databasePath,
                ownedDirectory,
                sharedBufferSize,
                readOnly,
                encryption,
                synchronousMode),
            ownsOptions: true)
    {
    }

    /// <summary>
    /// Creates a data source that owns the database file's parent directory, asynchronous only.
    /// </summary>
    /// <inheritdoc
    ///     cref="AhtolaBrowserDataSource(string, string, int, bool, AhtolaBrowserEncryptionOptions)"
    ///     path="/remarks"/>
    public AhtolaBrowserDataSource(
        string databasePath,
        int sharedBufferSize = AhtolaBrowserOptions.DefaultSharedBufferSize,
        bool readOnly = false,
        AhtolaBrowserEncryptionOptions? encryption = null)
        : this(
            databasePath,
            sharedBufferSize,
            readOnly,
            encryption,
            AhtolaBrowserSynchronousMode.AsyncOnly)
    {
    }

    /// <summary>
    /// Creates a data source that owns the database file's parent directory, choosing whether
    /// provably read-only statements may also be served synchronously from the managed in-memory
    /// mirror.
    /// </summary>
    public AhtolaBrowserDataSource(
        string databasePath,
        int sharedBufferSize,
        bool readOnly,
        AhtolaBrowserEncryptionOptions? encryption,
        AhtolaBrowserSynchronousMode synchronousMode)
        : this(
            new AhtolaBrowserOptions(
                databasePath,
                sharedBufferSize,
                readOnly,
                encryption,
                synchronousMode),
            ownsOptions: true)
    {
    }

    public AhtolaBrowserOptions Options => _options;

    public override string ConnectionString => _options.ConnectionString;

    string IManagedDatabaseFactory.DataSource => _options.DatabasePath;

    bool IManagedDatabaseFactory.IsReadOnly => _options.IsReadOnly;

    bool IManagedDatabaseFactory.IsSharedMemory => _options.IsInMemory;

    /// <summary>
    /// Synchronous reads are served from the managed in-memory mirror, so they are
    /// offered only when the caller opted into
    /// <see cref="AhtolaBrowserSynchronousMode.ReadOnlyMirror"/>.
    /// </summary>
    bool IManagedDatabaseFactory.SupportsSynchronousReads => _options.AllowsSynchronousReads;

    /// <summary>
    /// Whether the OPFS mirror still owes the persistent store work. Synchronous
    /// teardown fails closed while it does, so no mutation can be dropped.
    /// </summary>
    bool IManagedDatabaseFactory.HasPendingDurableWork => HasPendingDurableWork;

    private bool HasPendingDurableWork
    {
        get
        {
            Task<StorageState>? initialization;
            lock (_gate)
                initialization = _initialization;

            // Nothing is owed before the mirror exists, and a failed or still
            // running initialization has no durable state to settle.
            return initialization is { IsCompletedSuccessfully: true }
                   && initialization.Result.Mirror.HasUnflushedWork;
        }
    }

    /// <summary>
    /// Snapshots how much durable-transport work this data source has performed
    /// and still owes.
    /// </summary>
    /// <remarks>
    /// <see cref="AhtolaBrowserStorageMetrics.PersistentOperations"/> counts OPFS
    /// worker round trips, so a workload that only performs supported synchronous
    /// reads leaves it unchanged. An in-memory data source never reaches OPFS and
    /// always reports zero.
    /// <see cref="AhtolaBrowserStorageMetrics.PendingMutations"/> and
    /// <see cref="AhtolaBrowserStorageMetrics.HasUnflushedWork"/> report the same owed work that
    /// synchronous close and disposal fail closed on, including work already handed to a running
    /// flush.
    /// </remarks>
    public AhtolaBrowserStorageMetrics GetStorageMetrics()
    {
        Task<StorageState>? initialization;
        lock (_gate)
            initialization = _initialization;

        if (initialization is not { IsCompletedSuccessfully: true })
            return default;

        var metrics = initialization.Result.Mirror.GetMetrics();
        return new AhtolaBrowserStorageMetrics(
            metrics.PersistentOperations,
            metrics.PendingMutations,
            metrics.HasUnflushedWork);
    }

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

    /// <summary>
    /// Performs the one required asynchronous initialization and open, then returns
    /// a connection intended for synchronous reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Available only when the data source was configured with
    /// <see cref="AhtolaBrowserSynchronousMode.ReadOnlyMirror"/>. Once this task
    /// completes, the database image lives in managed memory, so statements proven
    /// incapable of mutating it — <c>SELECT</c>, <c>VALUES</c>, and <c>WITH</c>
    /// whose terminal statement is <c>SELECT</c> or <c>VALUES</c> — execute
    /// synchronously without any OPFS or worker operation.
    /// </para>
    /// <para>
    /// Mutations, transactions, blobs, backups, <c>PRAGMA</c>, and <c>EXPLAIN</c>
    /// still require the asynchronous API, because their durability depends on an
    /// OPFS flush. The returned connection may be closed or disposed synchronously
    /// only while no mutation is pending.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The data source is asynchronous only.
    /// </exception>
    public async ValueTask<SqliteConnection> OpenSynchronousReadConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfSynchronousReadsNotEnabled();
        return await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the one required asynchronous initialization and open, then returns
    /// an <see cref="global::Ahtola.AhtolaConnection"/> intended for synchronous reads.
    /// </summary>
    /// <inheritdoc cref="OpenSynchronousReadConnectionAsync" path="/remarks"/>
    /// <exception cref="InvalidOperationException">
    /// The data source is asynchronous only.
    /// </exception>
    public async ValueTask<global::Ahtola.AhtolaConnection> OpenSynchronousReadAhtolaConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfSynchronousReadsNotEnabled();
        var connection = CreateAhtolaConnection();
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

    private void ThrowIfSynchronousReadsNotEnabled()
    {
        ThrowIfDisposed();
        if (_options.AllowsSynchronousReads)
            return;

        throw new InvalidOperationException(
            "Synchronous reads require a data source created with "
            + $"{nameof(AhtolaBrowserSynchronousMode)}.{nameof(AhtolaBrowserSynchronousMode.ReadOnlyMirror)}.");
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
        if (_options.IsInMemory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureMemoryDatabase();
            AddConnectionReference();
            try
            {
                var inner = global::Ahtola.ManagedSharedMemoryDatabase.Open(_memoryName);
                return new BrowserInMemoryManagedDatabaseAdapter(
                    inner,
                    ReleaseConnection,
                    allowSynchronousTeardown: _options.AllowsSynchronousReads);
            }
            catch
            {
                ReleaseConnection();
                throw;
            }
        }

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
        AddConnectionReference();

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
        IManagedDatabaseAdapter? memoryOwner;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            initialization = _initialization;
            memoryOwner = _memoryOwner;
            _memoryOwner = null;
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
        {
            try
            {
                if (memoryOwner is not null)
                    await memoryOwner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseKeyMaterial();
            }
            return;
        }

        StorageState storage;
        try
        {
            storage = await initialization.ConfigureAwait(false);
        }
        catch
        {
            ReleaseKeyMaterial();
            return;
        }

        try
        {
            await storage.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseKeyMaterial();
        }
    }

    private AhtolaBrowserEncryptionOptions? EffectiveEncryption => _encryption ?? _options.Encryption;

    /// <summary>
    /// Zeros this instance's key material and, when it created the options itself,
    /// the copy those options hold. Runs on every disposal and failure path.
    /// </summary>
    private void ReleaseKeyMaterial()
    {
        _encryption?.Dispose();
        if (_ownsOptions)
            _options.Dispose();
    }

    private async Task<StorageState> InitializeAsync()
    {
        OpfsAsyncFileSystem? persistent = null;
        BrowserMirroredFileSystem? mirror = null;
        BrowserEncryptedPersistence? encryption = null;
        try
        {
            if (EffectiveEncryption is { } encryptionOptions)
            {
                var cipher = await AhtolaBrowserPageCipherFactory
                    .CreateAsync(encryptionOptions)
                    .ConfigureAwait(false);
                encryption = new BrowserEncryptedPersistence(new AhtolaAsyncPageTransformer(cipher));
                encryption.RegisterDatabase(_options.DatabasePath);
            }

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
                    encryption: encryption,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return new StorageState(persistent, mirror, encryption);
        }
        catch
        {
            if (mirror is not null)
                await mirror.DisposeAsync().ConfigureAwait(false);
            if (encryption is not null)
                await encryption.DisposeAsync().ConfigureAwait(false);
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

    private void AddConnectionReference()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked
            {
                _activeConnections++;
            }
        }
    }

    private void EnsureMemoryDatabase()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _memoryOwner ??= global::Ahtola.ManagedSharedMemoryDatabase.Open(_memoryName);
        }
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
        IBrowserPersistentStore persistent,
        BrowserMirroredFileSystem mirror,
        BrowserEncryptedPersistence? encryption) : IAsyncDisposable
    {
        public BrowserMirroredFileSystem Mirror { get; } = mirror;

        /// <summary>
        /// Drains and persists first, then releases the Web Crypto key, then closes
        /// OPFS. Releasing the key earlier would fail pending encrypted writes, and
        /// closing OPFS earlier would drop bytes the mirror still owes.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Mirror.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (encryption is not null)
                        await encryption.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await persistent.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
