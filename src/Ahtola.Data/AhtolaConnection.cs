using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola;

public class AhtolaConnection : DbConnection, ILocalReaderConnection
{
    internal const int AutomaticSyncMaximumAttempts = 3;
    // Test-only transport seam. Production callers continue to use the default HttpClient transport.
    internal static Func<HttpMessageHandler?>? RemoteMessageHandlerFactory { get; set; }

    private AhtolaNativeDatabase? _nativeDatabase;
    private AhtolaReplicaDatabase? _replicaDatabase;
    private ManagedReplicaConnectionHost? _managedReplicaHost;
    private IManagedDatabaseAdapter? _managedDatabase;
    private ManagedConnectionPoolLease? _managedPoolLease;
    private ManagedConnectionPoolKey? _managedPoolKey;
    private AhtolaRemoteClient? _remoteClient;
    private AhtolaConnectionOptions _connectionOptions;
    private AhtolaReplicaOptions? _replicaOptions;
    private HttpMessageHandler? _ownedReplicaHttpHandler;
    private readonly object _automaticSyncLock = new();
    private CancellationTokenSource? _automaticSyncCancellation;
    private Task? _automaticSyncTask;
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
    private readonly object _commandLock = new();
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
        => AhtolaConnectionCapabilities.ForAhtola(
            _connectionOptions,
            replicaSupportsSync: _connectionOptions.IsReplica);

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

    internal AhtolaConnection(string connectionString, AhtolaRemoteClient remoteClient)
        : this(connectionString)
    {
        _remoteClient = remoteClient ?? throw new ArgumentNullException(nameof(remoteClient));
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
        var automaticSyncError = StopAutomaticManagedReplicaSync();
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
            if (automaticSyncError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(automaticSyncError).Throw();
            return;
        }

        _replicaDatabase?.EnsureCanClose();
        var cancellationError = _replicaDatabase?.CancelPendingOperationsForClose();
        try
        {
            var nativeDatabase = _nativeDatabase;
            var managedReplicaHost = _managedReplicaHost;
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
                _managedReplicaHost = null;
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
                        else if (managedReplicaHost is not null)
                        {
                            managedReplicaHost.DetachConnection(this);
                            managedReplicaHost.Dispose();
                        }
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
        catch (Exception cleanupError) when (cancellationError is not null || automaticSyncError is not null)
        {
            var errors = new List<Exception>();
            if (automaticSyncError is not null)
                errors.Add(automaticSyncError);
            if (cancellationError is not null)
                errors.Add(cancellationError);
            errors.Add(cleanupError);
            throw new AggregateException(
                "Embedded replica background synchronization, cancellation, and connection cleanup failed.",
                errors);
        }

        if (automaticSyncError is not null && cancellationError is not null)
        {
            throw new AggregateException(
                "Embedded replica background synchronization and cancellation both failed.",
                automaticSyncError,
                cancellationError);
        }
        if (automaticSyncError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(automaticSyncError).Throw();
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
        if (_managedReplicaHost is { } managedReplicaHost)
            return SyncManagedReplicaAsync(managedReplicaHost, new AhtolaSyncOptions(), cancellationToken);
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
        if (_managedReplicaHost is { } managedReplicaHost)
            return SyncManagedReplicaAsync(managedReplicaHost, options, cancellationToken);
        if (!Capabilities.SupportsSync)
            throw new NotSupportedException("Sync requires an embedded replica connection.");

        return (_replicaDatabase ?? throw new InvalidOperationException("Ahtola database is closed."))
            .SyncAsync(options, cancellationToken);
    }

    internal async Task QuiesceManagedReplicaAsync(
        Func<CancellationToken, Task> stagedOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedOperation);
        if (_managedReplicaHost is not { } host || State != ConnectionState.Open)
            throw new InvalidOperationException("Ahtola database is closed.");
        if (_transaction is not null)
            throw new InvalidOperationException("Managed embedded replica sync cannot run while a transaction is active.");
        lock (_readerLock)
        {
            if (_openReaders.Count != 0)
                throw new InvalidOperationException("Managed embedded replica sync cannot run while a data reader is active.");
        }

        await host.QuiesceAndReopenAsync(stagedOperation, cancellationToken).ConfigureAwait(false);
        _managedDatabase = host.Database;
    }

    private async Task<AhtolaSyncResult> SyncManagedReplicaAsync(
        ManagedReplicaConnectionHost host,
        AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("Managed embedded replica sync cannot run while a transaction is active.");
        lock (_readerLock)
        {
            if (_openReaders.Count != 0)
                throw new InvalidOperationException("Managed embedded replica sync cannot run while a data reader is active.");
        }

        try
        {
            return await host.SyncAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _managedDatabase = host.Database;
        }
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

    internal bool IsManaged => _managedReplicaHost is not null || _managedDatabase is not null;

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
        => _managedReplicaHost?.Database.Connection
           ?? _managedDatabase?.Connection
           ?? throw new InvalidOperationException("Ahtola database is closed.");

    internal IDisposable? EnterManagedReplicaOperation(CancellationToken cancellationToken)
    {
        if (_managedReplicaHost is not { } host)
            return null;

        // The transaction owns the path lease until it completes, so commands in that
        // transaction must be allowed to finish after a sync has begun waiting.
        if (_transaction is { IsCompleted: false } || host.HasSqlTransactionOperation)
            return null;

        return host.EnterLocalOperation(cancellationToken);
    }

    internal IDisposable? EnterManagedReplicaTransaction()
        => _managedReplicaHost?.EnterLocalOperation(CancellationToken.None);

    internal void ManagedReplicaStatementStarted(string sql)
        => _managedReplicaHost?.StatementStarted(sql);

    internal bool BeginManagedReplicaSqlTransaction(string sql, CancellationToken cancellationToken)
    {
        if (SqlTransactionControl.GetFirstKeyword(sql)?.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) != true
            || _managedReplicaHost is not { } host)
        {
            return false;
        }

        host.BeginSqlTransaction(cancellationToken);
        return true;
    }

    internal void ManagedReplicaStatementCompleted(string sql)
        => _managedReplicaHost?.StatementCompleted(sql);

    internal void ManagedReplicaStatementFailed()
        => _managedReplicaHost?.StatementFailed();

    internal void ManagedReplicaStatementClosed()
        => _managedReplicaHost?.StatementClosed();

    /// <summary>
    /// Returns committed local replica changes at a durable, exclusive watermark boundary.
    /// This is an internal hand-off to the following push implementation; it performs no network I/O.
    /// </summary>
    internal ReplicaLocalChangeBatch ReadManagedReplicaLocalChanges(int maximumChanges)
        => (_managedReplicaHost ?? throw new InvalidOperationException(
                "Local replica changes are available only for managed embedded replica connections."))
            .ReadLocalChanges(maximumChanges);

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

    internal void CommandOpened(AhtolaCommand command)
    {
        lock (_commandLock)
            _openCommands.Add(command);
    }

    internal void CommandClosed(AhtolaCommand command)
    {
        lock (_commandLock)
            _openCommands.Remove(command);
    }

    internal void ResetManagedReplicaCommandsForPublication() => ResetOpenCommands();

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
        AhtolaRemoteClient.ValidateParameters(sql, parameters);
        for (var attempt = 0; ; attempt++)
        {
            var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
            var closeAfter = !_connectionOptions.ReadYourWrites && !_remoteTransactionActive;
            try
            {
                return await remoteClient.ExecuteAsync(sql, parameters, wantRows, commandTimeout, closeAfter, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AhtolaRemoteSqlException exception) when (exception.IsStreamExpired)
            {
                if (_remoteTransactionActive)
                {
                    RecordRemoteTransactionFailure(exception);
                    throw;
                }

                ResetRemoteSession();
                if (attempt != 0)
                    throw;
            }
            catch (AhtolaRemoteSqlException)
            {
                throw;
            }
            catch (AhtolaParameterException)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordRemoteTransactionFailure(exception);
                InvalidateRemoteSession();
                throw;
            }
        }
    }

    internal async Task<IReadOnlyList<RemoteStatementResult>> ExecuteRemoteBatchAsync(
        IReadOnlyList<AhtolaBatchCommand> batchCommands,
        int commandTimeout,
        bool wantRows,
        CancellationToken cancellationToken)
    {
        AhtolaRemoteClient.ValidateParameters(batchCommands);
        for (var attempt = 0; ; attempt++)
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
            catch (AhtolaRemoteSqlException exception) when (exception.IsStreamExpired)
            {
                if (_remoteTransactionActive)
                {
                    RecordRemoteTransactionFailure(exception);
                    throw;
                }

                ResetRemoteSession();
                if (attempt != 0)
                    throw;
            }
            catch (AhtolaRemoteSqlException)
            {
                throw;
            }
            catch (AhtolaParameterException)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordRemoteTransactionFailure(exception);
                InvalidateRemoteSession();
                throw;
            }
        }
    }

    internal void BeginRemoteTransaction(IsolationLevel isolationLevel)
    {
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (_remoteTransactionActive)
            throw new InvalidOperationException("A transaction is already active on this connection.");

        _remoteTransactionActive = true;
        try
        {
            remoteClient
                .ExecuteAsync(GetRemoteBeginSql(isolationLevel), new AhtolaParameterCollection(), wantRows: false, DefaultTimeout, closeAfter: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (AhtolaRemoteSqlException exception)
        {
            if (exception.IsStreamExpired)
                ResetRemoteSession();
            else
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
        var remoteClient = _remoteClient ?? throw new InvalidOperationException("Ahtola database is closed.");
        if (_remoteTransactionActive)
            throw new InvalidOperationException("A transaction is already active on this connection.");

        _remoteTransactionActive = true;
        try
        {
            await remoteClient
                .ExecuteAsync(
                    GetRemoteBeginSql(isolationLevel),
                    new AhtolaParameterCollection(),
                    wantRows: false,
                    DefaultTimeout,
                    closeAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AhtolaRemoteSqlException exception)
        {
            if (exception.IsStreamExpired)
                ResetRemoteSession();
            else
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
        catch (AhtolaRemoteSqlException exception)
        {
            if (exception.IsStreamExpired)
                RecordRemoteTransactionFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            RecordRemoteTransactionFailure(exception);
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
        catch (AhtolaRemoteSqlException exception)
        {
            if (exception.IsStreamExpired)
                RecordRemoteTransactionFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            RecordRemoteTransactionFailure(exception);
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
        catch (AhtolaRemoteSqlException exception)
        {
            if (exception.IsStreamExpired)
                RecordRemoteTransactionFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            RecordRemoteTransactionFailure(exception);
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
        catch (AhtolaRemoteSqlException exception)
        {
            if (exception.IsStreamExpired)
                RecordRemoteTransactionFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            RecordRemoteTransactionFailure(exception);
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
        if (_connectionOptions.IsReplica)
        {
            OpenReplica(GetReplicaOptions());
            return;
        }

        ValidateDirectRemoteLocalProvider();
        var endpoint = _connectionOptions.GetRemoteUri();
        var remoteEncryption = _connectionOptions.GetRemoteEncryptionOptions();
        var handler = RemoteMessageHandlerFactory?.Invoke();
        _remoteClient = handler is null
            ? new AhtolaRemoteClient(endpoint, _connectionOptions.AuthToken, remoteEncryption)
            : new AhtolaRemoteClient(
                new HttpClient(handler, disposeHandler: false),
                endpoint,
                _connectionOptions.AuthToken,
                remoteEncryption,
                disposeHttpClient: true);
    }

    private async Task OpenRemoteReplicaAsync(
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        if (!AhtolaReplicaProvider.HasRegisteredFactory)
        {
            await OpenManagedReplicaAsync(options, cancellationToken).ConfigureAwait(false);
            return;
        }

        var replicaDatabase = await AhtolaReplicaProvider
            .OpenRegisteredReplicaAsync(options, cancellationToken)
            .ConfigureAwait(false);
        SetReplicaDatabase(replicaDatabase);
    }

    private void OpenReplica(AhtolaReplicaOptions options)
    {
        if (AhtolaReplicaProvider.HasRegisteredFactory)
        {
            SetReplicaDatabase(AhtolaReplicaProvider.OpenRegisteredReplica(options));
            return;
        }

        OpenManagedReplica(options);
    }

    private void OpenManagedReplica(AhtolaReplicaOptions options)
    {
        var replicaHost = ManagedReplicaConnectionHost.Open(options);
        try
        {
            SetManagedReplicaHost(replicaHost);
            StartAutomaticManagedReplicaSync(replicaHost);
        }
        catch
        {
            replicaHost.Dispose();
            throw;
        }
    }

    private async Task OpenManagedReplicaAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        var replicaHost = await ManagedReplicaConnectionHost.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            SetManagedReplicaHost(replicaHost);
            StartAutomaticManagedReplicaSync(replicaHost);
        }
        catch
        {
            replicaHost.Dispose();
            throw;
        }
    }

    private void ValidateDirectRemoteLocalProvider()
    {
        if (_connectionOptions.LocalProvider == AhtolaLocalProvider.Managed)
        {
            throw new NotSupportedException(
                "Local Provider=Managed is supported only for local database and embedded replica connections.");
        }
    }

    private AhtolaReplicaOptions GetReplicaOptions()
    {
        if (_connectionOptions.GetEncryptionCipher().HasValue
            || !string.IsNullOrWhiteSpace(_connectionOptions["Encryption Key"]))
        {
            throw new InvalidOperationException(
                "Encryption Cipher and Encryption Key are local database options and cannot be used with remote Ahtola URLs.");
        }

        if (_replicaOptions is not null)
            return _replicaOptions;

        var handler = RemoteMessageHandlerFactory?.Invoke();
        return new AhtolaReplicaOptions(
            _connectionOptions.ReplicaPath,
            _connectionOptions.GetRemoteUri(),
            _connectionOptions.AuthToken)
        {
            SyncInterval = _connectionOptions.SyncInterval,
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };
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

    private void SetManagedReplicaHost(ManagedReplicaConnectionHost replicaHost)
    {
        if (_disposed)
        {
            replicaHost.Dispose();
            throw new ObjectDisposedException(nameof(AhtolaConnection));
        }

        _managedReplicaHost = replicaHost;
        _managedDatabase = replicaHost.Database;
        replicaHost.AttachConnection(this);
    }

    private void ValidateCanOpen()
    {
        _replicaOptions?.ThrowIfApplicationHttpReentrant(closing: false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeDatabase is not null || _managedReplicaHost is not null || _managedDatabase is not null || _remoteClient is not null)
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
        if (_nativeDatabase is null && _managedReplicaHost is null && _managedDatabase is null && _remoteClient is null)
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

        if (syncInterval > 0
            && (!_connectionOptions.IsReplica
                || _connectionOptions.LocalProvider != AhtolaLocalProvider.Managed
                || AhtolaReplicaProvider.HasRegisteredFactory))
        {
            throw new NotSupportedException(
                "Sync Interval requires a managed embedded replica connection.");
        }
    }

    private void StartAutomaticManagedReplicaSync(ManagedReplicaConnectionHost replicaHost)
    {
        var syncInterval = _connectionOptions.SyncInterval;
        if (syncInterval <= 0 || !replicaHost.SupportsSync)
            return;

        var cancellation = new CancellationTokenSource();
        lock (_automaticSyncLock)
        {
            if (_automaticSyncTask is not null)
            {
                cancellation.Dispose();
                throw new InvalidOperationException("Automatic managed replica synchronization is already running.");
            }

            _automaticSyncCancellation = cancellation;
            _automaticSyncTask = RunAutomaticManagedReplicaSyncAsync(
                replicaHost,
                TimeSpan.FromSeconds(syncInterval),
                cancellation.Token);
        }
    }

    private static async Task RunAutomaticManagedReplicaSyncAsync(
        ManagedReplicaConnectionHost replicaHost,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            await SynchronizeManagedReplicaWithRetryAsync(replicaHost, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SynchronizeManagedReplicaWithRetryAsync(
        ManagedReplicaConnectionHost replicaHost,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _ = await replicaHost.SyncAsync(new AhtolaSyncOptions(), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                attempt < AutomaticSyncMaximumAttempts
                && IsTransientAutomaticSyncFailure(exception, cancellationToken))
            {
                await Task.Delay(GetAutomaticSyncRetryDelay(attempt - 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static TimeSpan GetAutomaticSyncRetryDelay(int retryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryIndex);
        return TimeSpan.FromMilliseconds(50 * (1 << Math.Min(retryIndex, 2)));
    }

    internal static bool IsTransientAutomaticSyncFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AhtolaReplicaConflictException || cancellationToken.IsCancellationRequested)
            return false;

        return AhtolaReplicaPushFailure.Classify(exception) == AhtolaReplicaPushFailureKind.TransientTransport;
    }

    private Exception? StopAutomaticManagedReplicaSync()
    {
        CancellationTokenSource? cancellation;
        Task? syncTask;
        lock (_automaticSyncLock)
        {
            cancellation = _automaticSyncCancellation;
            syncTask = _automaticSyncTask;
            _automaticSyncCancellation = null;
            _automaticSyncTask = null;
        }

        if (cancellation is null || syncTask is null)
            return null;

        try
        {
            cancellation.Cancel();
            syncTask.GetAwaiter().GetResult();
            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            cancellation.Dispose();
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

    private void ResetRemoteSession()
    {
        _remoteClient?.ResetSession();
        _remoteTransactionActive = false;
        _readUncommitted = false;
    }

    private void RecordRemoteTransactionFailure(Exception exception)
    {
        if (_remoteTransactionActive)
            _transaction?.RecordFailure(exception);
        if (exception is AhtolaRemoteSqlException { IsStreamExpired: true })
            ResetRemoteSession();
    }

    private static string GetRemoteBeginSql(IsolationLevel isolationLevel)
        => isolationLevel == IsolationLevel.ReadUncommitted ? "BEGIN" : "BEGIN IMMEDIATE";

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
        AhtolaCommand[] commands;
        lock (_commandLock)
            commands = _openCommands.ToArray();
        foreach (var command in commands)
            command.ResetFromConnection();
    }
}
