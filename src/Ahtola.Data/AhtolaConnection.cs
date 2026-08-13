using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola;

public class AhtolaConnection : DbConnection, ILocalReaderConnection
{
    private AhtolaNativeDatabase? _nativeDatabase;
    private AhtolaReplicaDatabase? _replicaDatabase;
    private IManagedDatabaseAdapter? _managedDatabase;
    private ManagedConnectionPoolLease? _managedPoolLease;
    private ManagedConnectionPoolKey? _managedPoolKey;
    private AhtolaRemoteClient? _remoteClient;
    private AhtolaConnectionOptions _connectionOptions;
    private AhtolaReplicaOptions? _replicaOptions;
    private HttpMessageHandler? _ownedReplicaHttpHandler;
    private AhtolaEncryptionFileSystem? _managedEncryptionFileSystem;
    private AhtolaPageCodecFileSystem? _managedPageCodecFileSystem;
    private IPageCodec? _pageCodec;
    private bool _disposed;
    private bool _readUncommitted;
    private bool _managedSharedMemory;
    private bool _remoteTransactionActive;
    private bool _managedReadOnly;
    private readonly HashSet<IConnectionOwnedReader> _openReaders = [];
    private readonly object _readerLock = new();
    private readonly HashSet<AhtolaCommand> _openCommands = [];
    private AhtolaTransaction? _transaction;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionOptions.GetConnectionString();
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("ConnectionString cannot be set while the connection is open.");

            _connectionOptions = AhtolaConnectionOptions.Parse(value ?? string.Empty);
            _managedPoolKey = null;
            _replicaOptions = null;
        }
    }

    public override string Database => "main";

    public override string DataSource => _connectionOptions["Data Source"] ?? "";

    public override string ServerVersion => typeof(AhtolaConnection).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public override ConnectionState State => _nativeDatabase is not null || _managedDatabase is not null || _remoteClient is not null
        ? ConnectionState.Open
        : ConnectionState.Closed;

    public AhtolaConnectionCapabilities Capabilities
        => AhtolaConnectionCapabilities.ForAhtola(_connectionOptions);

    public override bool CanCreateBatch => Capabilities.CanCreateBatch;

    /// <summary>
    /// Optional external page codec applied to managed local databases opened by
    /// this connection. Must be set before <see cref="Open"/> and cannot be combined
    /// with built-in encryption options. The codec is not owned by the connection.
    /// </summary>
    public IPageCodec? PageCodec
    {
        get => _pageCodec;
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("PageCodec cannot be set while the connection is open.");
            if (value is not null)
                PageCodecId.ValidateNonZero(value.CodecId);
            _pageCodec = value;
        }
    }

    protected override DbProviderFactory DbProviderFactory => AhtolaFactory.Instance;

    public AhtolaConnection() : this("")
    {
    }

    public AhtolaConnection(string connectionString)
    {
        _connectionOptions = AhtolaConnectionOptions.Parse(connectionString);
    }

    /// <summary>
    /// Creates a connection configured as an embedded replica.
    /// </summary>
    /// <param name="replicaOptions">The embedded replica configuration.</param>
    public static AhtolaConnection CreateReplica(AhtolaReplicaOptions replicaOptions)
    {
        ArgumentNullException.ThrowIfNull(replicaOptions);
        replicaOptions.Validate();
        var ownedHttpHandler = replicaOptions.HttpPolicy.ClaimMessageHandlerOwnership();
        var connectionReplicaOptions = replicaOptions.CloneForConnection();
        return new AhtolaConnection
        {
            _replicaOptions = connectionReplicaOptions,
            _connectionOptions = AhtolaConnectionOptions.FromReplica(connectionReplicaOptions),
            _ownedReplicaHttpHandler = ownedHttpHandler,
        };
    }

    public override void Open()
    {
        ValidateCanOpen();
        OpenCore();
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (_connectionOptions.IsRemote && _connectionOptions.IsReplica)
        {
            ValidateCanOpen();
            ValidateReplicaLocalProvider();
            return OpenRemoteReplicaAsync(GetReplicaOptions(), cancellationToken);
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Open();
                if (!cancellationToken.IsCancellationRequested)
                    return;

                Close();
                cancellationToken.ThrowIfCancellationRequested();
            },
            CancellationToken.None);
    }

    public static void ClearAllPools() => ManagedConnectionPool.ClearAll();

    public static void ClearPool(AhtolaConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection._managedPoolKey is { } key
            || connection._connectionOptions.TryGetManagedPoolKey(out key))
        {
            ManagedConnectionPool.Clear(key);
        }
    }

    public override void Close()
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: true);
        if (_remoteClient is not null)
        {
            try
            {
                _transaction?.Dispose();
            }
            finally
            {
                CloseRemote();
                _transaction = null;
            }
            return;
        }

        _replicaDatabase?.EnsureCanClose();
        var cancellationError = _replicaDatabase?.CancelPendingOperationsForClose();
        try
        {
            var nativeDatabase = _nativeDatabase;
            var managedDatabase = _managedDatabase;
            var managedPoolLease = _managedPoolLease;
            var managedEncryptionFileSystem = _managedEncryptionFileSystem;
            var managedPageCodecFileSystem = _managedPageCodecFileSystem;
            var reusable = false;
            try
            {
                CloseOpenReaders();
                _transaction?.Dispose();
                ResetOpenCommands();
                reusable = true;
            }
            finally
            {
                _nativeDatabase = null;
                _replicaDatabase = null;
                _managedDatabase = null;
                _managedPoolLease = null;
                _managedEncryptionFileSystem = null;
                _managedPageCodecFileSystem = null;
                try
                {
                    nativeDatabase?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (managedPoolLease is not null)
                            managedPoolLease.Release(reusable);
                        else
                            managedDatabase?.Dispose();
                    }
                    finally
                    {
                        managedEncryptionFileSystem?.Dispose();
                        managedPageCodecFileSystem?.Dispose();
                        _readUncommitted = false;
                        _managedSharedMemory = false;
                        _managedReadOnly = false;
                        _transaction = null;
                    }
                }
            }
        }
        catch (Exception cleanupError) when (cancellationError is not null)
        {
            throw new AggregateException(
                "Embedded replica cancellation and connection cleanup both failed.",
                cancellationError,
                cleanupError);
        }

        if (cancellationError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            _disposed = true;
            base.Dispose(disposing);
            return;
        }

        Exception? disposalError = null;
        try
        {
            Close();
        }
        catch (Exception exception)
        {
            if (State != ConnectionState.Closed)
                throw;
            disposalError = exception;
        }

        _disposed = true;
        var ownedReplicaHttpHandler = _ownedReplicaHttpHandler;
        _ownedReplicaHttpHandler = null;
        try
        {
            ownedReplicaHttpHandler?.Dispose();
        }
        catch (Exception exception)
        {
            disposalError = CombineDisposalErrors(disposalError, exception);
        }

        try
        {
            base.Dispose(disposing);
        }
        catch (Exception exception)
        {
            disposalError = CombineDisposalErrors(disposalError, exception);
        }

        if (disposalError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalError).Throw();
    }

    private static Exception CombineDisposalErrors(Exception? existing, Exception next)
        => existing is null
            ? next
            : new AggregateException("Multiple errors occurred while disposing the Ahtola connection.", existing, next);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        ValidateCanBeginTransaction();

        return _transaction = new AhtolaTransaction(this, isolationLevel);
    }

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCanBeginTransaction();
        return _transaction = await AhtolaTransaction
            .CreateAsync(this, isolationLevel, cancellationToken)
            .ConfigureAwait(false);
    }

    protected override DbCommand CreateDbCommand()
    {
        return new AhtolaCommand(this);
    }

    protected override DbBatch CreateDbBatch()
    {
        if (!CanCreateBatch)
            throw new NotSupportedException("Ahtola batch execution is not supported for embedded replica connections.");

        return new AhtolaBatch(this);
    }

    public int ExecuteNonQuery(string sql)
    {
        using var command = CreateCommand();
        command.CommandText = sql;

        return command.ExecuteNonQuery();
    }

    public void Sync()
    {
        SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public AhtolaSyncResult Sync(AhtolaSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SyncAsync(options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        if (State != ConnectionState.Open)
            throw new InvalidOperationException("Ahtola database is closed.");
        if (!Capabilities.SupportsSync)
            throw new NotSupportedException("Sync requires an embedded replica connection.");

        return (_replicaDatabase ?? throw new InvalidOperationException("Ahtola database is closed."))
            .SyncAsync(cancellationToken);
    }

    public Task<AhtolaSyncResult> SyncAsync(
        AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<AhtolaSyncResult>(cancellationToken);
        if (State != ConnectionState.Open)
            throw new InvalidOperationException("Ahtola database is closed.");
        if (!Capabilities.SupportsSync)
            throw new NotSupportedException("Sync requires an embedded replica connection.");

        return (_replicaDatabase ?? throw new InvalidOperationException("Ahtola database is closed."))
            .SyncAsync(options, cancellationToken);
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Ahtola does not support changing the active database.");
    }

    /// <summary>
    /// Returns the <c>MetaDataCollections</c> schema collection.
    /// </summary>
    public override DataTable GetSchema()
        => GetSchema(DbMetaDataCollectionNames.MetaDataCollections, null);

    /// <summary>
    /// Returns the requested schema collection.
    /// </summary>
    /// <param name="collectionName">The name of the collection to return.</param>
    public override DataTable GetSchema(string collectionName)
        => GetSchema(collectionName, null);

    /// <summary>
    /// Returns the requested schema collection, filtered by the supplied restrictions.
    /// </summary>
    /// <param name="collectionName">The name of the collection to return.</param>
    /// <param name="restrictionValues">The restriction values for the collection.</param>
    /// <remarks>
    /// The catalog is read with ordinary SQL on this connection, so remote Hrana and
    /// embedded-replica connections describe the database they are attached to instead of an
    /// empty local catalog. A statement the target rejects surfaces that engine's error.
    /// </remarks>
    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
        => AhtolaSchemaCollections.GetSchema(this, collectionName, restrictionValues);

    internal int DefaultTimeout => _connectionOptions.DefaultTimeout;

    internal bool IsRemote => _remoteClient is not null;

    internal bool IsManagedReadOnly => _managedReadOnly;

    internal bool IsManaged => _managedDatabase is not null;

    internal AhtolaTransaction? Transaction => _transaction;

    internal bool ReadUncommitted
    {
        get => _readUncommitted;
        set
        {
            if (value && _managedSharedMemory)
                throw new NotSupportedException(ManagedSharedCacheContract.ReadUncommittedNotSupportedMessage);

            _readUncommitted = value;
        }
    }

    internal AhtolaNativeDatabase NativeDatabase
    {
        get
        {
            _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
            return _nativeDatabase ?? throw new InvalidOperationException("Ahtola database is closed.");
        }
    }

    internal IManagedConnectionAdapter ManagedConnection
        => _managedDatabase?.Connection ?? throw new InvalidOperationException("Ahtola database is closed.");

    void ILocalReaderConnection.ReaderOpened(IConnectionOwnedReader reader)
    {
        lock (_readerLock)
            _openReaders.Add(reader);
    }

    void ILocalReaderConnection.ReaderClosed(IConnectionOwnedReader reader)
    {
        lock (_readerLock)
            _openReaders.Remove(reader);
    }

    internal void CommandOpened(AhtolaCommand command) => _openCommands.Add(command);

    internal void CommandClosed(AhtolaCommand command) => _openCommands.Remove(command);

    internal void TransactionCompleted(AhtolaTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
            _transaction = null;
    }

    internal void TransactionCompletedExternally(SqlTransactionCompletion completion)
    {
        if (completion == SqlTransactionCompletion.None)
            return;

        _remoteTransactionActive = false;
        _transaction?.MarkCompletedExternally();
        CloseRemoteSessionIfStateless();
    }

    internal void ValidateCommandCapabilities(string sql)
    {
        var keyword = SqlTransactionControl.GetFirstKeyword(sql);
        if (!Capabilities.SupportsAttach
            && (keyword?.Equals("ATTACH", StringComparison.OrdinalIgnoreCase) == true
                || keyword?.Equals("DETACH", StringComparison.OrdinalIgnoreCase) == true))
        {
            throw new NotSupportedException(
                "ATTACH and DETACH are supported only for local database connections.");
        }
    }

    internal async Task<RemoteStatementResult> ExecuteRemoteAsync(
        string sql,
        AhtolaParameterCollection parameters,
        bool wantRows,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        var closeAfter = !_connectionOptions.ReadYourWrites && !_remoteTransactionActive;
        try
        {
            return await remoteClient.ExecuteAsync(sql, parameters, wantRows, commandTimeout, closeAfter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AhtolaRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal async Task<IReadOnlyList<RemoteStatementResult>> ExecuteRemoteBatchAsync(
        IReadOnlyList<AhtolaBatchCommand> batchCommands,
        int commandTimeout,
        bool wantRows,
        CancellationToken cancellationToken)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        var closeAfter = !_connectionOptions.ReadYourWrites && !_remoteTransactionActive;
        try
        {
            return await remoteClient.ExecuteBatchAsync(
                    batchCommands,
                    commandTimeout,
                    wantRows,
                    closeAfter,
                    cancellationToken,
                    step => TransactionCompletedExternally(
                        SqlTransactionControl.GetCompletion(batchCommands[step].CommandText)))
                .ConfigureAwait(false);
        }
        catch (AhtolaRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal void BeginRemoteTransaction(IsolationLevel isolationLevel)
    {
        _ = isolationLevel;
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (_remoteTransactionActive)
            throw new InvalidOperationException("A transaction is already active on this connection.");

        _remoteTransactionActive = true;
        try
        {
            remoteClient
                .ExecuteAsync("BEGIN", new AhtolaParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (AhtolaRemoteSqlException)
        {
            _remoteTransactionActive = false;
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal async Task BeginRemoteTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        _ = isolationLevel;
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (_remoteTransactionActive)
            throw new InvalidOperationException("A transaction is already active on this connection.");

        _remoteTransactionActive = true;
        try
        {
            await remoteClient
                .ExecuteAsync(
                    "BEGIN",
                    new AhtolaParameterCollection(),
                    wantRows: false,
                    DefaultTimeout,
                    closeAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AhtolaRemoteSqlException)
        {
            _remoteTransactionActive = false;
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal void CommitRemoteTransaction()
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (!_remoteTransactionActive)
            throw new InvalidOperationException("No remote transaction is active on this connection.");

        try
        {
            remoteClient
                .ExecuteAsync("COMMIT", new AhtolaParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (AhtolaRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }

        _remoteTransactionActive = false;
    }

    internal async Task CommitRemoteTransactionAsync(CancellationToken cancellationToken)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (!_remoteTransactionActive)
            throw new InvalidOperationException("No remote transaction is active on this connection.");

        try
        {
            await remoteClient
                .ExecuteAsync(
                    "COMMIT",
                    new AhtolaParameterCollection(),
                    wantRows: false,
                    DefaultTimeout,
                    closeAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AhtolaRemoteSqlException)
        {
            throw;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }

        _remoteTransactionActive = false;
    }

    internal void RollbackRemoteTransaction()
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (!_remoteTransactionActive)
            throw new InvalidOperationException("No remote transaction is active on this connection.");

        try
        {
            remoteClient
                .ExecuteAsync("ROLLBACK", new AhtolaParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            _remoteTransactionActive = false;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal async Task RollbackRemoteTransactionAsync(CancellationToken cancellationToken)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (!_remoteTransactionActive)
            throw new InvalidOperationException("No remote transaction is active on this connection.");

        try
        {
            await remoteClient
                .ExecuteAsync(
                    "ROLLBACK",
                    new AhtolaParameterCollection(),
                    wantRows: false,
                    DefaultTimeout,
                    closeAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);
            _remoteTransactionActive = false;
        }
        catch
        {
            InvalidateRemoteSession();
            throw;
        }
    }

    internal void CloseRemoteSessionIfStateless()
    {
        if (_connectionOptions.ReadYourWrites || _remoteClient is not { HasOpenSession: true } remoteClient)
            return;

        try
        {
            remoteClient.CloseAsync(DefaultTimeout, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            InvalidateRemoteSession();
        }
    }

    private void OpenRemote()
    {
        ValidateReplicaLocalProvider();

        if (_connectionOptions.IsReplica)
        {
            SetReplicaDatabase(AhtolaReplicaProvider.OpenReplica(GetReplicaOptions()));
            return;
        }

        _remoteClient = new AhtolaRemoteClient(
            _connectionOptions.GetRemoteUri(),
            _connectionOptions.AuthToken,
            _connectionOptions.GetRemoteEncryptionOptions());
    }

    private async Task OpenRemoteReplicaAsync(
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        var ValidateRemoteLocalProvider = await AhtolaReplicaProvider
            .OpenReplicaAsync(options, cancellationToken)
            .ConfigureAwait(false);
        SetReplicaDatabase(ValidateRemoteLocalProvider);
    }

    private void ValidateReplicaLocalProvider()
    {
        if (_connectionOptions.LocalProvider == AhtolaLocalProvider.Managed)
            throw new NotSupportedException("Local Provider=Managed is supported only for local database connections.");
    }

    private AhtolaReplicaOptions GetReplicaOptions()
    {
        if (_connectionOptions.GetEncryptionCipher().HasValue
            || !string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"]))
        {
            throw new InvalidOperationException(
                "Encryption Cipher and Encryption Key are local database options and cannot be used with remote Ahtola URLs.");
        }

        return _replicaOptions ?? new AhtolaReplicaOptions(
            _connectionOptions.ReplicaPath,
            _connectionOptions.GetRemoteUri(),
            _connectionOptions.AuthToken);
    }

    private void SetReplicaDatabase(AhtolaReplicaDatabase replicaDatabase)
    {
        if (_disposed)
        {
            replicaDatabase.Dispose();
            throw new ObjectDisposedException(nameof(AhtolaConnection));
        }

        _replicaDatabase = replicaDatabase;
        _nativeDatabase = replicaDatabase;
    }

    private void ValidateCanOpen()
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeDatabase is not null || _managedDatabase is not null || _remoteClient is not null)
            throw new InvalidOperationException("The connection is already open.");
        ValidateAutomaticSyncPolicy();
                if (!string.IsNullOrWhiteSpace(_connectionOptions["Password"]))
                {
                    if (_connectionOptions.IsRemote
                        || _connectionOptions.LocalProvider != AhtolaLocalProvider.Managed)
                    {
                        throw new NotSupportedException(
                            "Password requires Local Provider=Managed for file-backed Ahtola AES-GCM databases.");
                    }
                }

                ValidatePoolingOptions();
            }

    private void ValidateCanBeginTransaction()
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        if (_nativeDatabase is null && _managedDatabase is null && _remoteClient is null)
            throw new InvalidOperationException("Ahtola database is closed.");
        if (_transaction is not null)
            throw new InvalidOperationException("Parallel transactions are not supported.");
    }

    private void ValidatePoolingOptions()
    {
        if (!_connectionOptions.Pooling)
            return;

        var dataSource = _connectionOptions.DataSource;
        var mode = _connectionOptions.Mode;
        var eligibleManagedFile = Capabilities.SupportsPooling
            && !string.IsNullOrWhiteSpace(dataSource)
            && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("Memory", StringComparison.OrdinalIgnoreCase)
            && !_connectionOptions.GetEncryptionCipher().HasValue
                        && string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"])
                        && string.IsNullOrWhiteSpace(_connectionOptions["Password"]);
                    if (!eligibleManagedFile)
                    {
                        throw new NotSupportedException(
                            "Pooling=True is supported only for unencrypted managed local file databases.");
                    }
    }

    private void ValidateAutomaticSyncPolicy()
    {
        var syncInterval = _connectionOptions.SyncInterval;
        if (syncInterval < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AhtolaConnectionStringBuilder.SyncInterval),
                syncInterval,
                "Sync Interval cannot be negative.");
        }

        if (syncInterval > 0)
        {
            throw new NotSupportedException(
                "Automatic synchronization is not supported. Sync Interval must be 0. Call Sync or SyncAsync explicitly.");
        }
    }

    private void OpenCore()
    {
        if (_connectionOptions.ForeignReadOnly
            && (_connectionOptions.IsRemote || _connectionOptions.LocalProvider != AhtolaLocalProvider.Managed))
        {
            throw new NotSupportedException(
                "Foreign Read Only requires Local Provider=Managed and a file-backed local Data Source.");
        }

        if (_connectionOptions.IsRemote)
        {
            OpenRemote();
            return;
        }

        ValidateLocalOnlyOptions();

        if (_connectionOptions.LocalProvider == AhtolaLocalProvider.Managed)
        {
            using var managedOptions = _connectionOptions.GetManagedLocalOpenOptions();
            OpenManagedDatabase(managedOptions);

            return;
        }

        var filename = _connectionOptions["Data Source"] ?? ":memory:";
        var cipher = _connectionOptions.GetEncryptionCipher();
        var hexkey = _connectionOptions["Encryption Key"];

        if (cipher.HasValue)
        {
            if (string.IsNullOrWhiteSpace(hexkey))
                throw new InvalidOperationException("Encryption Key is required when Encryption Cipher is specified.");

            _nativeDatabase = AhtolaNativeProvider.OpenDatabase(filename, cipher, hexkey);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(hexkey))
                throw new InvalidOperationException("Encryption Cipher is required when Encryption Key is specified.");

            _nativeDatabase = AhtolaNativeProvider.OpenDatabase(filename, cipher: null, encryptionKey: null);
        }
    }

    private void ValidateLocalOnlyOptions()
    {
        if (!string.IsNullOrWhiteSpace(_connectionOptions.AuthToken))
            throw new InvalidOperationException("Auth Token requires a remote Ahtola URL Data Source.");
        if (!string.IsNullOrWhiteSpace(_connectionOptions.ReplicaPath))
            throw new InvalidOperationException("Replica Path requires a remote Ahtola URL Data Source.");
        if (_connectionOptions.Tls.HasValue)
            throw new InvalidOperationException("Tls requires a remote Ahtola URL Data Source.");
    }

    private void OpenManagedDatabase(ManagedLocalOpenOptions options)
    {
        if (options.SharedMemoryName is not null)
        {
            _managedDatabase = ManagedSharedMemoryDatabase.Open(options.SharedMemoryName);
            _managedSharedMemory = true;
        }
        else if (_connectionOptions.Pooling
            && options.Encryption is null
            && PageCodec is null
            && !options.ForeignReadOnly
            && !options.DataSource.Equals(":memory:", StringComparison.Ordinal))
        {
            var poolKey = ManagedConnectionPoolKey.Create(options.DataSource, options.ReadOnly);
            _managedPoolLease = ManagedConnectionPool.Rent(
                poolKey,
                () => OpenUnencryptedManagedDatabase(poolKey.DataSource, options.ReadOnly));
            _managedDatabase = _managedPoolLease.Database;
            _managedPoolKey = poolKey;
        }
        else if (options.Encryption is null && PageCodec is null && !options.ReadOnly)
        {
            var managedDatabase = ManagedDatabaseAdapter.Open(options.DataSource);
            try
            {
                _ = managedDatabase.Connect();
                _managedDatabase = managedDatabase;
            }
            catch
            {
                managedDatabase.Dispose();
                throw;
            }
        }
        else
        {
            if (options.Encryption is not null && PageCodec is not null)
            {
                throw new InvalidOperationException(
                    "Built-in encryption cannot be combined with an external page codec.");
            }

            AhtolaEncryptionFileSystem? managedEncryptionFileSystem = null;
            AhtolaPageCodecFileSystem? managedPageCodecFileSystem = null;
            IManagedDatabaseAdapter? managedDatabase = null;
            try
            {
                IFileSystem fileSystem = PhysicalFileSystem.Instance;
                if (options.Encryption is not null)
                {
                    managedEncryptionFileSystem = new AhtolaEncryptionFileSystem(
                        PhysicalFileSystem.Instance,
                        options.Encryption);
                    fileSystem = managedEncryptionFileSystem;
                }
                else if (PageCodec is not null)
                {
                    managedPageCodecFileSystem = new AhtolaPageCodecFileSystem(
                        PhysicalFileSystem.Instance,
                        PageCodec);
                    fileSystem = managedPageCodecFileSystem;
                }

                managedDatabase = ManagedDatabaseAdapter.OpenFile(
                    options.DataSource,
                    fileSystem,
                    readOnly: options.ReadOnly,
                    foreignReadOnly: options.ForeignReadOnly);
                try
                {
                    _ = managedDatabase.Connect();
                    _managedDatabase = managedDatabase;
                    managedDatabase = null;
                    _managedEncryptionFileSystem = managedEncryptionFileSystem;
                    managedEncryptionFileSystem = null;
                    _managedPageCodecFileSystem = managedPageCodecFileSystem;
                    managedPageCodecFileSystem = null;
                }
                catch
                {
                    throw;
                }
            }
            finally
            {
                managedDatabase?.Dispose();
                managedEncryptionFileSystem?.Dispose();
                managedPageCodecFileSystem?.Dispose();
            }
        }

        if (!options.ReadOnly)
            return;

        try
        {
            using var command = CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            command.ExecuteNonQuery();
            _managedReadOnly = true;
        }
        catch
        {
            Close();
            throw;
        }
    }

    private static IManagedDatabaseAdapter OpenUnencryptedManagedDatabase(string dataSource, bool readOnly)
    {
        var managedDatabase = readOnly
            ? ManagedDatabaseAdapter.OpenFile(dataSource, PhysicalFileSystem.Instance, readOnly: true)
            : ManagedDatabaseAdapter.Open(dataSource);
        try
        {
            _ = managedDatabase.Connect();
            return managedDatabase;
        }
        catch
        {
            managedDatabase.Dispose();
            throw;
        }
    }

    private void CloseRemote()
    {
        var remoteClient = _remoteClient;
        if (remoteClient is null)
            return;

        Exception? closeError = null;
        try
        {
            CloseOpenReaders();
            ResetOpenCommands();
            if (_remoteTransactionActive)
            {
                remoteClient
                    .ExecuteAsync("ROLLBACK", new AhtolaParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: true, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                remoteClient.CloseAsync(DefaultTimeout, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            closeError = ex;
        }
        finally
        {
            remoteClient.Dispose();
            _remoteClient = null;
            _remoteTransactionActive = false;
            _readUncommitted = false;
            _managedReadOnly = false;
            _transaction?.Dispose();
        }

        if (closeError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeError).Throw();
    }

    private void InvalidateRemoteSession()
    {
        _remoteClient?.Dispose();
        _remoteClient = null;
        _remoteTransactionActive = false;
        _readUncommitted = false;
    }

    private void CloseOpenReaders()
    {
        IConnectionOwnedReader[] readers;
        lock (_readerLock)
            readers = _openReaders.ToArray();
        foreach (var reader in readers)
            reader.CloseFromConnection();
    }

    private void ResetOpenCommands()
    {
        foreach (var command in _openCommands.ToArray())
            command.ResetFromConnection();
    }
}
