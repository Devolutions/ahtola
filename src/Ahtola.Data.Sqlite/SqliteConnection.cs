using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Globalization;
using System.Security.Cryptography;
using Ahtola;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite;

public partial class SqliteConnection :
    DbConnection,
    ILocalReaderConnection,
    IManagedSchemaConnection,
    IAsyncExecutionConnection
{
    private const int SQLITE_ERROR = 1;
    private const int SQLITE_CANTOPEN = 14;
    private static readonly object SharedMemoryLock = new();
    private static readonly Dictionary<string, int> SharedMemoryReferences = new(StringComparer.OrdinalIgnoreCase);

    private AhtolaNativeDatabase? _database;
    private IManagedDatabaseAdapter? _managedDatabase;
    private readonly IManagedDatabaseFactory? _managedDatabaseFactory;
    private AhtolaConnection? _ahtolaConnection;
    private bool _ahtolaConnectionWasOpen;
    private ManagedConnectionPoolLease? _managedPoolLease;
    private SqliteConnectionStringBuilder _connectionOptions = new();
    private bool _disposed;
    private int? _defaultTimeout;
    private readonly HashSet<IConnectionOwnedReader> _openReaders = [];
    private readonly object _readerGate = new();
    private readonly ManualResetEventSlim _noOpenReaders = new(initialState: true);
    private readonly HashSet<SqliteBlob> _openManagedBlobs = [];
    private readonly HashSet<SqliteCommand> _openCommands = [];
    private string? _dataSource;
    private bool _readOnly;
    private AhtolaEncryptionFileSystem? _managedEncryptionFileSystem;
    private AhtolaPageCodecFileSystem? _managedPageCodecFileSystem;
    private IPageCodec? _pageCodec;
    private bool _recursiveTriggers;
    private bool _readUncommitted;
    private bool _managedSharedMemory;
    private string? _sharedMemoryPath;
    private bool _extensionsEnabled;
    private readonly List<(string File, string? Proc)> _pendingExtensions = [];

    public SqliteConnection()
    {
    }

    public SqliteConnection(string? connectionString)
    {
        ConnectionString = connectionString;
    }

    internal SqliteConnection(IManagedDatabaseAdapter managedDatabase)
        : this("Data Source=:memory:;Local Provider=Managed")
    {
        _managedDatabase = managedDatabase ?? throw new ArgumentNullException(nameof(managedDatabase));
        _dataSource = ":memory:";
    }

    internal SqliteConnection(
        string connectionString,
        IManagedDatabaseFactory managedDatabaseFactory)
        : this(connectionString)
    {
        _managedDatabaseFactory = managedDatabaseFactory
            ?? throw new ArgumentNullException(nameof(managedDatabaseFactory));
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionOptions.ConnectionString;
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException(Properties.Resources.ConnectionStringRequiresClosedConnection);

            _connectionOptions = new SqliteConnectionStringBuilder(value);
            _defaultTimeout = null;
        }
    }

    public override string Database => "main";

    public override string DataSource => _dataSource switch
    {
        // Native SQLite's sqlite3_db_filename returns "" for in-memory databases;
        // Microsoft.Data.Sqlite exposes that empty string via DataSource. EFCore's
        // SqliteDatabaseCreator.Delete branches on !string.IsNullOrEmpty(path) and
        // would otherwise try File.Delete(":memory:"), so match the contract.
        ":memory:" => string.Empty,
        null => _connectionOptions.DataSource,
        var path => path,
    };

    public int DefaultTimeout
    {
        get => _defaultTimeout ?? _connectionOptions.DefaultTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _defaultTimeout = value;
        }
    }

    /// <summary>
    ///     Raw SQLitePCL <c>sqlite3*</c> interop is not supported by the Ahtola-backed provider.
    /// </summary>
    public virtual dynamic? Handle => null;

    public SqliteTransaction? Transaction { get; internal set; }

    /// <summary>
    /// Optional external page codec for managed local databases. Set before
    /// <see cref="Open"/>; cannot be combined with built-in encryption options.
    /// The codec is not owned by the connection.
    /// </summary>
    public IPageCodec? PageCodec
    {
        get => _pageCodec;
        set
        {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("PageCodec cannot be set while the connection is open.");
            if (value is not null && IsRemoteDataSource)
                throw new NotSupportedException("PageCodec is supported only for local database connections.");
            if (value is not null)
                PageCodecId.ValidateNonZero(value.CodecId);
            _pageCodec = value;
        }
    }

    internal bool IsSharedCache => _connectionOptions.Cache == SqliteCacheMode.Shared;

    internal bool ReadUncommitted
    {
        get => _readUncommitted;
        set
        {
            if (value && _managedSharedMemory)
                throw new NotSupportedException(Properties.Resources.ManagedSharedCacheReadUncommittedNotSupported);

            _readUncommitted = value;
        }
    }

    /// <summary>
    /// The SQLite version this engine is wire- and SQL-compatible with. Mirrors what
    /// <c>sqlite_version()</c> returns, so callers that feature-gate on the server
    /// version (e.g. EFCore) see the real compatibility level rather than a stub.
    /// </summary>
    public override string ServerVersion => Ahtola.Core.EmbeddedDatabase.SqliteCompatibilityVersion;

    public override ConnectionState State => _ahtolaConnection?.State
        ?? (_database is null && _managedDatabase is null
        ? ConnectionState.Closed
        : ConnectionState.Open);

    /// <summary>
    /// Gets the execution mode currently configured for this facade.
    /// </summary>
    public AhtolaConnectionMode Mode => Capabilities.Mode;

    /// <summary>
    /// Gets the endpoint classification without requiring the connection to be open.
    /// </summary>
    public AhtolaConnectionEndpointMode EndpointMode => _ahtolaConnection?.Capabilities.Mode switch
    {
        AhtolaConnectionMode.RemoteHrana => AhtolaConnectionEndpointMode.RemoteHrana,
        AhtolaConnectionMode.EmbeddedReplica => AhtolaConnectionEndpointMode.EmbeddedReplica,
        _ => AhtolaConnectionModeClassifier.Classify(
            _connectionOptions.DataSource,
            _connectionOptions.ReplicaPath),
    };

    public AhtolaConnectionCapabilities Capabilities
        => _ahtolaConnection is not null
            ? AhtolaConnectionCapabilities.ForSqliteMode(_ahtolaConnection.Capabilities.Mode)
            : EndpointMode switch
            {
                AhtolaConnectionEndpointMode.RemoteHrana => AhtolaConnectionCapabilities.ForSqliteRemote(isReplica: false),
                AhtolaConnectionEndpointMode.EmbeddedReplica => AhtolaConnectionCapabilities.ForSqliteRemote(isReplica: true),
                _ => AhtolaConnectionCapabilities.ForSqlite(_connectionOptions.EffectiveLocalProvider),
            };

    public override bool CanCreateBatch => Capabilities.CanCreateBatch;

    // Test-only forwarding seam. Keeping it on the facade permits deterministic remote
    // tests without reflection while production callers retain the default transport.
    internal static Func<HttpMessageHandler?>? RemoteMessageHandlerFactory
    {
        get => AhtolaConnection.RemoteMessageHandlerFactory;
        set => AhtolaConnection.RemoteMessageHandlerFactory = value;
    }

    protected override DbProviderFactory DbProviderFactory => SqliteFactory.Instance;

    public override void Open()
    {
        if (_managedDatabaseFactory is not null)
        {
            throw new PlatformNotSupportedException(
                "Synchronous Open is not supported by the browser database source. Use OpenAsync.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_database is not null || _managedDatabase is not null || _ahtolaConnection is not null)
            throw new InvalidOperationException("The connection is already open.");
        if (IsRemoteDataSource)
        {
            ValidateRemoteOpenMode();
            var remoteOriginalState = State;
            try
            {
                _ahtolaConnection = new AhtolaConnection(_connectionOptions.GetAhtolaConnectionString());
                _ahtolaConnection.Open();
                _ahtolaConnectionWasOpen = true;
                _dataSource = _ahtolaConnection.DataSource;
                ApplyReplicaConnectionOptions();
                OnStateChange(new StateChangeEventArgs(remoteOriginalState, State));
                return;
            }
            catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
            {
                _ahtolaConnection?.Dispose();
                _ahtolaConnection = null;
                throw MapRemoteLifecycleException(ex);
            }
            catch
            {
                _ahtolaConnection?.Dispose();
                _ahtolaConnection = null;
                throw;
            }
        }
        ValidateManagedSharedCacheOptions();
        ValidateForeignReadOnlyOptions();
                var useManaged = _connectionOptions.EffectiveLocalProvider == AhtolaLocalProvider.Managed;
                if (!useManaged && !string.IsNullOrEmpty(_connectionOptions.Password))
                    throw new InvalidOperationException(Properties.Resources.EncryptionNotSupported("e_sqlite3"));

                var localOriginalState = State;
                var filename = NormalizeDataSource(_connectionOptions);
                var readOnly = _connectionOptions.Mode == SqliteOpenMode.ReadOnly;
                var managedSharedMemory = IsManagedSharedMemoryConfiguration(_connectionOptions);
                var sharedMemoryPath = IsNativeSharedMemory(_connectionOptions) ? RegisterSharedMemoryFile(filename) : null;
                AhtolaEncryptionOptions? managedEncryption = null;
                try
                {
                    if (useManaged)
                    {
                        managedEncryption = _connectionOptions.CreateManagedEncryptionOptions();
                        if (managedEncryption is not null
                            && (_connectionOptions.Mode == SqliteOpenMode.Memory
                                || filename.Equals(":memory:", StringComparison.Ordinal)))
                        {
                            throw new NotSupportedException(Properties.Resources.ManagedMemoryEncryptionNotSupported);
                        }

                        if (managedSharedMemory)
                        {
                            _managedDatabase = ManagedSharedMemoryDatabase.Open(filename);
                            _managedSharedMemory = true;
                        }
                        else if (CanUseManagedPooling(filename, managedEncryption))
                        {
                            var poolKey = ManagedConnectionPoolKey.Create(filename, readOnly);
                            _managedPoolLease = ManagedConnectionPool.Rent(
                                poolKey,
                                () => OpenManagedDatabase(
                                    filename,
                                    readOnly,
                                    encryption: null,
                                    out _,
                                    out _));
                            _managedDatabase = _managedPoolLease.Database;
                        }
                        else
                        {
                            _managedDatabase = OpenManagedDatabase(
                                filename,
                                readOnly,
                                managedEncryption,
                                out var managedEncryptionFileSystem,
                                out var managedPageCodecFileSystem,
                                _connectionOptions.ForeignReadOnly);
                            _managedEncryptionFileSystem = managedEncryptionFileSystem;
                            _managedPageCodecFileSystem = managedPageCodecFileSystem;
                        }
                    }
                    else
                    {
                        if (_connectionOptions.HasEncryptionOptions)
                        {
                            throw new InvalidOperationException(
                                "Password, Encryption Cipher, and Encryption Key require Local Provider=Managed.");
                        }

                        _database = AhtolaNativeProvider.OpenDatabase(filename, cipher: null, encryptionKey: null);
                    }

                    _dataSource = filename;
                    _readOnly = readOnly;
                    _sharedMemoryPath = sharedMemoryPath;
                    if (IsManagedReadOnly)
                        EnableManagedReadOnly();
                    ApplyExtensionSettings();
                    ApplyConnectionOptions();
                    RegisterScalarFunctions();
                    RegisterAggregateFunctions();
                    RegisterCollations();
                    RegisterHooks();
                    LoadPendingExtensions();
                    OnStateChange(new StateChangeEventArgs(localOriginalState, State));
                }
                catch (AhtolaException ex)
                {
                    CleanupFailedOpen(sharedMemoryPath);
                                    throw MapManagedEncryptionOpenFailure(ToSqliteException(ex), managedEncryption is not null || useManaged);
                }
                catch (SqliteException ex)
                {
                    CleanupFailedOpen(sharedMemoryPath);
                                    throw MapManagedEncryptionOpenFailure(ex, managedEncryption is not null || useManaged);
                }
                catch (InvalidDataException ex)
                {
                    CleanupFailedOpen(sharedMemoryPath);
                    throw MapManagedEncryptionOpenFailure(ex, managedEncryption is not null || useManaged);
                }
                                catch (CryptographicException ex)
                {
                    CleanupFailedOpen(sharedMemoryPath);
                                    throw MapManagedEncryptionOpenFailure(ex, managedEncryption is not null || useManaged);
                }
                                catch (Exception ex) when (LooksLikeEncryptedOrCorruptDatabase(ex))
                {
                                    CleanupFailedOpen(sharedMemoryPath);
                                    throw MapManagedEncryptionOpenFailure(ex, encryptionAttempted: true);
                                }
                                catch
                                {
                                    CleanupFailedOpen(sharedMemoryPath);
                                    throw;
                                }
                                finally
                                {
                                    managedEncryption?.Dispose();
                                }
                    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (_managedDatabaseFactory is not null)
            return OpenManagedFactoryAsync(cancellationToken);

        if (IsRemoteDataSource)
            return OpenRemoteAsync(cancellationToken);

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

    private async Task OpenManagedFactoryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != ConnectionState.Closed)
            throw new InvalidOperationException("The connection is already open.");

        var originalState = State;
        IManagedDatabaseAdapter? database = null;
        try
        {
            database = await _managedDatabaseFactory!
                .OpenDatabaseAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = await database.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _managedDatabase = database;
            _dataSource = _managedDatabaseFactory.DataSource;
            _readOnly = _managedDatabaseFactory.IsReadOnly;
            _managedSharedMemory = _managedDatabaseFactory.IsSharedMemory;
            if (IsManagedReadOnly)
                await ExecuteNonQueryAsync("PRAGMA query_only = ON;", cancellationToken).ConfigureAwait(false);
            ApplyExtensionSettings();
            await ApplyConnectionOptionsAsync(cancellationToken).ConfigureAwait(false);
            RegisterScalarFunctions();
            RegisterAggregateFunctions();
            RegisterCollations();
            RegisterHooks();
            LoadPendingExtensions();
            OnStateChange(new StateChangeEventArgs(originalState, State));
            database = null;
        }
        catch
        {
            _managedDatabase = null;
            _dataSource = null;
            _readOnly = false;
            _managedSharedMemory = false;
            throw;
        }
        finally
        {
            if (database is not null)
                await database.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task OpenRemoteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ahtolaConnection is not null)
            throw new InvalidOperationException("The connection is already open.");
        ValidateRemoteOpenMode();

        var remoteOpenOriginalState = State;
        try
        {
            var connection = new AhtolaConnection(_connectionOptions.GetAhtolaConnectionString());
            _ahtolaConnection = connection;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _ahtolaConnectionWasOpen = true;
            _dataSource = connection.DataSource;
            ApplyReplicaConnectionOptions();
            OnStateChange(new StateChangeEventArgs(remoteOpenOriginalState, State));
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            _ahtolaConnection?.Dispose();
            _ahtolaConnection = null;
            throw MapRemoteLifecycleException(ex);
        }
        catch
        {
            _ahtolaConnection?.Dispose();
            _ahtolaConnection = null;
            throw;
        }
    }

    /// <summary>Synchronizes an embedded replica with its configured remote endpoint.</summary>
    public void Sync()
    {
        var connection = _ahtolaConnection
            ?? throw new NotSupportedException("Sync requires an open embedded replica connection.");
        try
        {
            connection.Sync();
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            ObserveRemoteInvalidation();
            throw MapRemoteLifecycleException(ex);
        }
    }

    /// <summary>Asynchronously synchronizes an embedded replica with its configured remote endpoint.</summary>
    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var connection = _ahtolaConnection
            ?? throw new NotSupportedException("Sync requires an open embedded replica connection.");
        return SyncRemoteAsync(connection, cancellationToken);
    }

    private async Task SyncRemoteAsync(AhtolaConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
        {
            ObserveRemoteInvalidation();
            throw MapRemoteLifecycleException(ex);
        }
    }

    public override void Close()
    {
        if (_managedDatabaseFactory is not null && _managedDatabase is not null)
        {
            throw new PlatformNotSupportedException(
                "Synchronous Close is not supported by the browser database source. Use CloseAsync.");
        }

        if (_ahtolaConnection is not null)
        {
            var remoteCloseOriginalState = State;
            var connection = _ahtolaConnection;
            _ahtolaConnection = null;
            Exception? cleanupError = null;
            try
            {
                CloseOpenReaders();
                Transaction?.Dispose();
                ResetOpenCommands();
                connection.Close();
            }
            catch (Exception ex) when (ex is AhtolaException or HttpRequestException)
            {
                cleanupError = ex;
                throw MapRemoteLifecycleException(ex);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
                throw;
            }
            finally
            {
                try
                {
                    connection.Dispose();
                }
                catch when (cleanupError is not null)
                {
                }
                _ahtolaConnectionWasOpen = false;
                _dataSource = null;
                _readOnly = false;
                OnStateChange(new StateChangeEventArgs(remoteCloseOriginalState, State));
            }

            return;
        }

        if (_database is null && _managedDatabase is null)
            return;

        var originalState = State;
        var reusable = false;
        try
        {
            CloseOpenManagedBlobs();
            CloseOpenReaders();
            Transaction?.Dispose();
            ResetOpenCommands();
            reusable = !HasManagedCallbacks;
        }
        finally
        {
            DisposeDatabaseAndManagedEncryptionFileSystem(reusable);
            _dataSource = null;
            _readOnly = false;
            _recursiveTriggers = false;
            _readUncommitted = false;
            _managedSharedMemory = false;
            if (_sharedMemoryPath is not null)
            {
                ReleaseSharedMemoryFile(_sharedMemoryPath);
                _sharedMemoryPath = null;
            }

            OnStateChange(new StateChangeEventArgs(originalState, State));
        }
    }

    public override Task CloseAsync()
        => _managedDatabaseFactory is null
            ? base.CloseAsync()
            : CloseManagedFactoryAsync();

    private async Task CloseManagedFactoryAsync()
    {
        var database = _managedDatabase;
        if (database is null)
            return;

        var originalState = State;
        Exception? cleanupError = null;
        if (Transaction is { IsCompleted: false } transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }
        }
        if (Transaction is not null)
        {
            try
            {
                await Transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError = CombineCleanupErrors(
                    "Browser transaction rollback and disposal both failed.",
                    cleanupError,
                    exception);
            }
        }
        try
        {
            await CloseOpenManagedBlobsAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupError = CombineCleanupErrors(
                "Browser transaction and incremental blob cleanup both failed.",
                cleanupError,
                exception);
        }
        try
        {
            await CloseOpenReadersAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupError = CombineCleanupErrors(
                "Browser connection cleanup failed.",
                cleanupError,
                exception);
        }
        try
        {
            ResetOpenCommands();
        }
        catch (Exception exception)
        {
            cleanupError = CombineCleanupErrors(
                "Browser command cleanup failed.",
                cleanupError,
                exception);
        }
        finally
        {
            _managedDatabase = null;
            Transaction = null;
            _dataSource = null;
            _readOnly = false;
            _recursiveTriggers = false;
            _readUncommitted = false;
            _managedSharedMemory = false;
            try
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError = CombineCleanupErrors(
                    "Browser connection cleanup and durable disposal both failed.",
                    cleanupError,
                    exception);
            }

            OnStateChange(new StateChangeEventArgs(originalState, State));
        }

        if (cleanupError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("Changing databases is not supported.");
    }

        /// <summary>
        /// Rewrites the open managed database under a new passphrase (or plaintext when
        /// <paramref name="newPassword"/> is null/empty). Exclusive access required.
        /// </summary>
        /// <remarks>
        /// Ahtola AES-256-GCM only (via <see cref="AhtolaPasswordEncryption"/>). Not SEE/SQLCipher.
        /// </remarks>
        public virtual void ChangePassword(string? newPassword)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != ConnectionState.Open)
                throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(nameof(ChangePassword)));
            if (!IsManagedConnection)
                throw new NotSupportedException("ChangePassword requires Local Provider=Managed.");
            if (_readOnly)
                throw new InvalidOperationException("Cannot change the password of a read-only connection.");
            if (_managedSharedMemory)
                throw new NotSupportedException("ChangePassword is not supported for shared-memory databases.");
            if (Transaction is not null || HasOpenReader || _openManagedBlobs.Count > 0)
                throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);

            var path = _dataSource;
            if (string.IsNullOrEmpty(path)
                || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("file:memory:", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("ChangePassword is supported only for file-backed managed databases.");
            }

            RewriteManagedFilePassword(Path.GetFullPath(path), newPassword);
        }

        /// <summary>Clears file encryption by rewriting the managed database as plaintext.</summary>
        public virtual void ClearPassword() => ChangePassword(newPassword: null);

        /// <summary>
        /// Encrypts an open plaintext managed database with <paramref name="password"/>.
        /// Empty/null is a no-op (SDS CreateFile + SetPassword("") compatibility).
        /// </summary>
        public virtual void SetPassword(string? password)
        {
            if (string.IsNullOrEmpty(password))
                return;
            ChangePassword(password);
        }

        public override DataTable GetSchema()
            => GetSchema(DbMetaDataCollectionNames.MetaDataCollections, null);

    public override DataTable GetSchema(string collectionName)
        => GetSchema(collectionName, null);

    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
        => AhtolaSchemaCollections.GetSchema(this, collectionName, restrictionValues);

    public static void ClearAllPools()
    {
        ManagedConnectionPool.ClearAll();
    }

    public static void ClearPool(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.TryGetManagedPoolKey(out var key))
            ManagedConnectionPool.Clear(key);
    }

    public new virtual SqliteTransaction BeginTransaction()
        => BeginTransaction(IsolationLevel.Unspecified);

    public virtual SqliteTransaction BeginTransaction(bool deferred)
        => BeginTransaction(IsolationLevel.Unspecified, deferred);

    public new virtual SqliteTransaction BeginTransaction(IsolationLevel isolationLevel)
        => BeginTransaction(isolationLevel, deferred: isolationLevel == IsolationLevel.ReadUncommitted);

    public virtual SqliteTransaction BeginTransaction(IsolationLevel isolationLevel, bool deferred)
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(nameof(BeginTransaction)));
        if (Transaction is not null)
            throw new InvalidOperationException(Properties.Resources.ParallelTransactionsNotSupported);
        if (RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                "Synchronous transaction creation is not supported by the browser database source. "
                + "Use BeginTransactionAsync.");
        }

        Transaction = new SqliteTransaction(this, isolationLevel, deferred);
        return Transaction;
    }

    public virtual void CreateCollation(string name, Comparison<string>? comparison)
    {
        RegisterCollation(name, comparison is null ? null : (left, right) => comparison(left, right));
    }

    public virtual void CreateCollation<T>(string name, T state, Func<T, string, string, int>? comparison)
    {
        RegisterCollation(name, comparison is null ? null : (left, right) => comparison(state, left, right));
    }

    public virtual void CreateFunction<TResult>(string name, Func<TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 0, isDeterministic, function is null ? null : _ => function());
    }

    public virtual void CreateFunction<T1, TResult>(string name, Func<T1, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 1, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, function, args));
    }

    public virtual void CreateFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 2, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, function, args));
    }

    public virtual void CreateFunction<TState, TResult>(string name, TState state, Func<TState, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 0, isDeterministic, function is null ? null : args => InvokeTypedFunction(state, function, args));
    }

    public virtual void CreateFunction<TState, T1, TResult>(string name, TState state, Func<TState, T1, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 1, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, state, function, args));
    }

    public virtual void CreateFunction<TState, T1, T2, TResult>(string name, TState state, Func<TState, T1, T2, TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, 2, isDeterministic, function is null ? null : args => InvokeTypedFunction(name, state, function, args));
    }

    public virtual void CreateFunction<TResult>(string name, Func<object?[], TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, -1, isDeterministic, function is null ? null : args => function(args));
    }

    public virtual void CreateFunction<TState, TResult>(string name, TState state, Func<TState, object?[], TResult>? function, bool isDeterministic = false)
    {
        RegisterScalarFunction(name, -1, isDeterministic, function is null ? null : args => function(state, args));
    }

    public virtual void CreateAggregate<TAccumulate>(string name, Func<TAccumulate?, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 0, isDeterministic, default(TAccumulate), func is null ? null : (accumulator, args) => InvokeNullableAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<T1, TAccumulate>(string name, Func<TAccumulate?, T1, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 1, isDeterministic, default(TAccumulate), func is null ? null : (accumulator, args) => InvokeNullableAggregateStep(name, func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate>(string name, Func<TAccumulate?, object?[], TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, -1, isDeterministic, default(TAccumulate), func is null ? null : (accumulator, args) => InvokeNullableAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate>(string name, TAccumulate seed, Func<TAccumulate, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 0, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<T1, TAccumulate>(string name, TAccumulate seed, Func<TAccumulate, T1, TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(name, func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate>(string name, TAccumulate seed, Func<TAccumulate, object?[], TAccumulate>? func, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, -1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => accumulator);
    }

    public virtual void CreateAggregate<TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 0, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void CreateAggregate<T1, TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, T1, TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(name, func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void CreateAggregate<T1, T2, TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, T1, T2, TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, 2, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(name, func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void CreateAggregate<TAccumulate, TResult>(string name, TAccumulate seed, Func<TAccumulate, object?[], TAccumulate>? func, Func<TAccumulate, TResult>? resultSelector, bool isDeterministic = false)
    {
        RegisterAggregateFunction(name, -1, isDeterministic, seed, func is null ? null : (accumulator, args) => InvokeSeededAggregateStep(func, accumulator, args), accumulator => InvokeResultSelector(resultSelector!, accumulator));
    }

    public virtual void EnableExtensions(bool enable = true)
    {
        if (enable && !Capabilities.SupportsExtensions)
            throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);

        _extensionsEnabled = enable;
        if (_database is not null)
            SqliteNativeProvider.Current.EnableExtensions(NativeDatabase, enable);
    }

    public virtual void LoadExtension(string file, string? proc = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Capabilities.SupportsExtensions)
            throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);
        if (proc is not null)
            throw new NotSupportedException("Custom extension entry points are not yet supported by the Ahtola SQLite-compatible provider.");

        if (State != ConnectionState.Open)
        {
            _pendingExtensions.Add((file, proc));
            return;
        }

        LoadExtensionCore(file, proc);
    }

    public virtual void BackupDatabase(SqliteConnection destination)
        => BackupDatabase(destination, "main", "main");

    public virtual void BackupDatabase(SqliteConnection destination, string destinationName, string sourceName)
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection("BackupDatabase"));
        ArgumentNullException.ThrowIfNull(destination);
        if (RequiresAsyncExecution || destination.RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                "Synchronous backup is not supported by browser-managed databases. "
                + "Use BackupDatabaseAsync.");
        }
        if (!Capabilities.SupportsBackup || !destination.Capabilities.SupportsBackup)
            throw new NotSupportedException("Backup is supported only for local database connections.");
        if (IsManagedProvider != destination.IsManagedProvider)
            throw new NotSupportedException(Properties.Resources.ManagedBackupMixedProvidersNotSupported);
        if (IsManagedProvider)
        {
            if (destination.State != ConnectionState.Open)
                destination.Open();
            SqliteManagedBackup.Copy(this, destination, destinationName, sourceName);
            return;
        }
        if (Transaction is not null)
            throw new SqliteException(Properties.Resources.SqliteNativeError(5, "database is locked"), 5);
        if (destination.State != ConnectionState.Open)
            destination.Open();

        foreach (var createSql in GetSchemaSql())
            destination.ExecuteNonQuery(createSql);

        foreach (var tableName in GetUserTableNames())
            CopyTableRows(destination, tableName);
    }

    public virtual Task BackupDatabaseAsync(
        SqliteConnection destination,
        CancellationToken cancellationToken = default)
        => BackupDatabaseAsync(destination, "main", "main", cancellationToken);

    public virtual async Task BackupDatabaseAsync(
        SqliteConnection destination,
        string destinationName,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection("BackupDatabase"));
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Capabilities.SupportsBackup || !destination.Capabilities.SupportsBackup)
            throw new NotSupportedException("Backup is supported only for local database connections.");
        if (IsManagedProvider != destination.IsManagedProvider)
            throw new NotSupportedException(Properties.Resources.ManagedBackupMixedProvidersNotSupported);
        if (destination.State != ConnectionState.Open)
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (IsManagedProvider)
        {
            await SqliteManagedBackup
                .CopyAsync(this, destination, destinationName, sourceName, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        BackupDatabase(destination, destinationName, sourceName);
    }

    public virtual SqliteBlob OpenBlob(
        string tableName,
        string columnName,
        long rowid,
        bool readOnly = false)
        => OpenBlob("main", tableName, columnName, rowid, readOnly);

    public virtual SqliteBlob OpenBlob(
        string databaseName,
        string tableName,
        string columnName,
        long rowid,
        bool readOnly = false)
    {
        if (RequiresAsyncExecution)
        {
            throw new PlatformNotSupportedException(
                "Synchronous incremental blob opening is not supported by browser-managed databases. "
                + "Use OpenBlobAsync.");
        }

        return new SqliteBlob(this, databaseName, tableName, columnName, rowid, readOnly);
    }

    public virtual ValueTask<SqliteBlob> OpenBlobAsync(
        string tableName,
        string columnName,
        long rowid,
        CancellationToken cancellationToken)
        => OpenBlobAsync("main", tableName, columnName, rowid, readOnly: false, cancellationToken);

    public virtual ValueTask<SqliteBlob> OpenBlobAsync(
        string tableName,
        string columnName,
        long rowid,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
        => OpenBlobAsync("main", tableName, columnName, rowid, readOnly, cancellationToken);

    public virtual ValueTask<SqliteBlob> OpenBlobAsync(
        string databaseName,
        string tableName,
        string columnName,
        long rowid,
        CancellationToken cancellationToken)
        => OpenBlobAsync(databaseName, tableName, columnName, rowid, readOnly: false, cancellationToken);

    public virtual ValueTask<SqliteBlob> OpenBlobAsync(
        string databaseName,
        string tableName,
        string columnName,
        long rowid,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
        => SqliteBlob.OpenAsync(
            this,
            databaseName,
            tableName,
            columnName,
            rowid,
            readOnly,
            cancellationToken);

    public new virtual SqliteCommand CreateCommand() => new(this) { Transaction = Transaction };

    protected override DbCommand CreateDbCommand() => CreateCommand();

    protected override DbBatch CreateDbBatch()
    {
        if (!CanCreateBatch)
            throw new NotSupportedException("Batch execution is not supported by this embedded replica connection.");
        return new SqliteBatch(this);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => BeginTransaction(isolationLevel);

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Open)
            throw new InvalidOperationException(Properties.Resources.CallRequiresOpenConnection(nameof(BeginTransaction)));
        if (Transaction is not null)
            throw new InvalidOperationException(Properties.Resources.ParallelTransactionsNotSupported);

        return Transaction = await SqliteTransaction
            .CreateAsync(
                this,
                isolationLevel,
                deferred: isolationLevel == IsolationLevel.ReadUncommitted,
                cancellationToken)
            .ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing
            && !_disposed
            && _managedDatabaseFactory is not null
            && State != ConnectionState.Closed)
        {
            throw new PlatformNotSupportedException(
                "Synchronous disposal is not supported by the browser database source. Use DisposeAsync.");
        }

        if (disposing && !_disposed)
        {
            try
            {
                Close();
            }
            catch
            {
                // Dispose must not hide the exception that caused scope unwinding.
            }
            finally
            {
                _noOpenReaders.Dispose();
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_managedDatabaseFactory is null)
        {
            Dispose();
            return;
        }
        if (_disposed)
            return;

        Exception? disposalError = null;
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalError = exception;
        }

        _disposed = true;
        _noOpenReaders.Dispose();
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalError = disposalError is null
                ? exception
                : new AggregateException(
                    "Browser connection close and base disposal both failed.",
                    disposalError,
                    exception);
        }

        if (disposalError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalError).Throw();
    }

    internal AhtolaNativeDatabase NativeDatabase => _database ?? throw new InvalidOperationException("The connection is not open.");

    internal IManagedConnectionAdapter ManagedConnection
        => _managedDatabase?.Connection ?? throw new InvalidOperationException("The connection is not open.");

    IManagedConnectionAdapter IManagedSchemaConnection.ManagedSchemaConnection
        => ManagedConnection;

    internal bool IsManagedConnection => _managedDatabase is not null;

    internal bool RequiresAsyncExecution => _managedDatabaseFactory is not null;

    bool IAsyncExecutionConnection.RequiresAsyncExecution => RequiresAsyncExecution;

    internal bool UsesManagedDatabase => IsManagedConnection;

    internal AhtolaConnection? AhtolaConnection => _ahtolaConnection;

    internal bool IsRemoteConnection => _ahtolaConnection?.IsRemote == true;

    internal bool IsReplicaConnection => _ahtolaConnection?.Capabilities.Mode == AhtolaConnectionMode.EmbeddedReplica;

    internal void ObserveRemoteInvalidation()
    {
        var connection = _ahtolaConnection;
        if (connection is null
            || connection.State == ConnectionState.Open
            || !_ahtolaConnectionWasOpen)
        {
            return;
        }

        _ahtolaConnection = null;
        _ahtolaConnectionWasOpen = false;
        _dataSource = null;
        try
        {
            try
            {
                CloseOpenReaders();
                Transaction?.MarkCompletedExternally(rolledBack: true);
                ResetOpenCommands();
            }
            finally
            {
                connection.Dispose();
            }
        }
        catch
        {
            // The original remote exception is the actionable error.
        }
        finally
        {
            OnStateChange(new StateChangeEventArgs(ConnectionState.Open, ConnectionState.Closed));
        }
    }

    private bool IsRemoteDataSource => EndpointMode != AhtolaConnectionEndpointMode.Local;

    private bool IsReplicaDataSource => EndpointMode == AhtolaConnectionEndpointMode.EmbeddedReplica;

        internal DateTimeKind DateTimeKind => _connectionOptions.DateTimeKind;

        internal bool BinaryGuid => _connectionOptions.BinaryGUID;

    internal bool IsManagedSharedMemory => _managedSharedMemory;

    internal bool HasOpenReader
    {
        get
        {
            lock (_readerGate)
                return _openReaders.Any(static reader => reader is SqliteDataReader);
        }
    }

    internal bool IsReadOnly => _readOnly;

    internal bool IsManagedReadOnly => _readOnly && UsesManagedDatabase;

    internal bool RecursiveTriggers => _recursiveTriggers;

    void ILocalReaderConnection.ReaderOpened(IConnectionOwnedReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_readerGate)
        {
            if (!_openReaders.Add(reader))
                throw new InvalidOperationException("The data reader is already registered with this connection.");
            if (reader is SqliteDataReader)
                _noOpenReaders.Reset();
        }
    }

    void ILocalReaderConnection.ReaderClosed(IConnectionOwnedReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_readerGate)
        {
            if (_openReaders.Remove(reader)
                && reader is SqliteDataReader
                && !_openReaders.Any(static openReader => openReader is SqliteDataReader))
            {
                _noOpenReaders.Set();
            }
        }
    }

    internal bool WaitForNoOpenReader(TimeSpan timeout, CancellationToken cancellationToken)
        => _noOpenReaders.Wait(timeout, cancellationToken);

    internal void ManagedBlobOpened(SqliteBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (!_openManagedBlobs.Add(blob))
            throw new InvalidOperationException("The managed incremental blob is already registered with this connection.");
    }

    internal void ManagedBlobClosed(SqliteBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _openManagedBlobs.Remove(blob);
    }

    internal void CommandOpened(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _openCommands.Add(command);
    }

    internal void CommandClosed(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _openCommands.Remove(command);
    }

    internal void ExecuteNonQuery(string sql)
    {
        using var command = new SqliteCommand(sql, this);
        command.ExecuteNonQuery();
    }

    internal async Task ExecuteNonQueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var command = new SqliteCommand(sql, this);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private List<string> GetSchemaSql()
    {
        var schema = new List<string>();
        using var command = new SqliteCommand("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 WHEN 'view' THEN 2 ELSE 3 END;", this);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            schema.Add(reader.GetString(0));

        return schema;
    }

    private List<string> GetUserTableNames()
    {
        var tables = new List<string>();
        using var command = new SqliteCommand("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;", this);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        return tables;
    }

    private void CopyTableRows(SqliteConnection destination, string tableName)
    {
        using var select = new SqliteCommand("SELECT * FROM " + QuoteIdentifier(tableName) + ";", this);
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            using var insert = destination.CreateCommand();
            var parameterNames = Enumerable.Range(0, reader.FieldCount).Select(i => "$p" + i).ToArray();
            insert.CommandText = "INSERT INTO " + QuoteIdentifier(tableName) + " VALUES (" + string.Join(", ", parameterNames) + ");";
            for (var i = 0; i < reader.FieldCount; i++)
                insert.Parameters.AddWithValue(parameterNames[i], reader.GetValue(i));

            insert.ExecuteNonQuery();
        }
    }

    internal void EnsureOpen()
    {
        if (State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
    }

    private void ApplyConnectionOptions()
    {
        // The e_sqlite3 build Microsoft.Data.Sqlite ships is compiled with
        // SQLITE_DEFAULT_FOREIGN_KEYS=1, so managed connections default to enforcing
        // foreign keys unless the connection string says otherwise. The managed engine
        // itself keeps the SQLite CLI default (OFF).
        if (_connectionOptions.ForeignKeys.HasValue)
            ExecuteNonQuery("PRAGMA foreign_keys = " + (_connectionOptions.ForeignKeys.Value ? "1" : "0") + ";");
        else if (IsManagedConnection)
            ExecuteNonQuery("PRAGMA foreign_keys = 1;");
        if (_connectionOptions.RecursiveTriggers)
            _recursiveTriggers = true;
    }

    private async Task ApplyConnectionOptionsAsync(CancellationToken cancellationToken)
    {
        if (_connectionOptions.ForeignKeys.HasValue)
        {
            await ExecuteNonQueryAsync(
                    "PRAGMA foreign_keys = "
                    + (_connectionOptions.ForeignKeys.Value ? "1" : "0")
                    + ";",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (IsManagedConnection)
        {
            await ExecuteNonQueryAsync(
                    "PRAGMA foreign_keys = 1;",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (_connectionOptions.RecursiveTriggers)
            _recursiveTriggers = true;
    }

    private void ApplyReplicaConnectionOptions()
    {
        if (EndpointMode != AhtolaConnectionEndpointMode.EmbeddedReplica)
            return;

        ExecuteNonQuery("PRAGMA foreign_keys = " + (_connectionOptions.ForeignKeys != false ? "1" : "0") + ";");
        if (_connectionOptions.Mode == SqliteOpenMode.ReadOnly)
        {
            _readOnly = true;
            ExecuteNonQuery("PRAGMA query_only = ON;");
        }
        if (_connectionOptions.RecursiveTriggers)
            _recursiveTriggers = true;
    }

    private void ValidateRemoteOpenMode()
    {
        if (EndpointMode == AhtolaConnectionEndpointMode.RemoteHrana
            && _connectionOptions.Mode == SqliteOpenMode.ReadOnly)
        {
            throw new NotSupportedException(
                "Mode=ReadOnly cannot be enforced for a direct remote Hrana connection. Configure server-side read-only access instead.");
        }
    }

    private SqliteException MapRemoteLifecycleException(Exception exception)
    {
        var mapped = SqliteCommand.ToSqliteException(exception);
        return EndpointMode == AhtolaConnectionEndpointMode.Local
            ? mapped
            : SqliteRemoteExceptionClassifier.From(exception, mapped);
    }

    private void EnableManagedReadOnly()
    {
        using var statement = ManagedConnection.Prepare("PRAGMA query_only = ON;");
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private void ApplyExtensionSettings()
    {
        if (_extensionsEnabled)
        {
            if (UsesManagedDatabase)
                throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);

            SqliteNativeProvider.Current.EnableExtensions(NativeDatabase, enable: true);
        }
    }

    private void LoadPendingExtensions()
    {
        foreach (var (file, proc) in _pendingExtensions)
            LoadExtensionCore(file, proc);
        _pendingExtensions.Clear();
    }

    private void LoadExtensionCore(string file, string? proc)
    {
        if (UsesManagedDatabase)
            throw new NotSupportedException(Properties.Resources.ManagedExtensionsNotSupported);

        try
        {
            SqliteNativeProvider.Current.LoadExtension(NativeDatabase, file);
        }
        catch (AhtolaException ex)
        {
            throw SqliteCommand.ToSqliteException(ex);
        }
    }

    private void CleanupFailedOpen(string? sharedMemoryPath)
    {
        DisposeDatabaseAndManagedEncryptionFileSystem();
        _dataSource = null;
        _readOnly = false;
        _managedSharedMemory = false;
        _sharedMemoryPath = null;
        if (sharedMemoryPath is not null)
            ReleaseSharedMemoryFile(sharedMemoryPath);
    }

    private IManagedDatabaseAdapter OpenManagedDatabase(
        string filename,
        bool readOnly,
        AhtolaEncryptionOptions? encryption,
        out AhtolaEncryptionFileSystem? managedEncryptionFileSystem,
        out AhtolaPageCodecFileSystem? managedPageCodecFileSystem,
        bool foreignReadOnly = false)
    {
        managedEncryptionFileSystem = null;
        managedPageCodecFileSystem = null;
        IManagedDatabaseAdapter? database = null;

        try
        {
            if (encryption is not null && PageCodec is not null)
            {
                throw new InvalidOperationException(
                    "Built-in encryption cannot be combined with an external page codec.");
            }

            if (encryption is null && PageCodec is null && !readOnly)
            {
                database = ManagedDatabaseAdapter.Open(filename);
                _ = database.Connect();
                return database;
            }

            IFileSystem fileSystem = PhysicalFileSystem.Instance;
            if (encryption is not null)
            {
                managedEncryptionFileSystem = new AhtolaEncryptionFileSystem(
                    PhysicalFileSystem.Instance,
                    encryption);
                fileSystem = managedEncryptionFileSystem;
            }
            else if (PageCodec is not null)
            {
                managedPageCodecFileSystem = new AhtolaPageCodecFileSystem(
                    PhysicalFileSystem.Instance,
                    PageCodec);
                fileSystem = managedPageCodecFileSystem;
            }

            database = ManagedDatabaseAdapter.OpenFile(
                filename,
                fileSystem,
                readOnly: readOnly,
                foreignReadOnly: foreignReadOnly);
            _ = database.Connect();
            return database;
        }
        catch
        {
            database?.Dispose();
            managedEncryptionFileSystem?.Dispose();
            managedEncryptionFileSystem = null;
            managedPageCodecFileSystem?.Dispose();
            managedPageCodecFileSystem = null;
            throw;
        }
    }

    private void DisposeDatabaseAndManagedEncryptionFileSystem(bool pooledReusable = false)
    {
        var database = _database;
        var managedDatabase = _managedDatabase;
        var managedPoolLease = _managedPoolLease;
        var managedEncryptionFileSystem = _managedEncryptionFileSystem;
        var managedPageCodecFileSystem = _managedPageCodecFileSystem;
        _database = null;
        _managedDatabase = null;
        _managedPoolLease = null;
        _managedEncryptionFileSystem = null;
        _managedPageCodecFileSystem = null;
        try
        {
            database?.Dispose();
        }
        finally
        {
            try
            {
                if (managedPoolLease is not null)
                    managedPoolLease.Release(pooledReusable);
                else
                    managedDatabase?.Dispose();
            }
            finally
            {
                managedEncryptionFileSystem?.Dispose();
                managedPageCodecFileSystem?.Dispose();
            }
        }
    }

    private void CloseOpenReaders()
    {
        IConnectionOwnedReader[] readers;
        lock (_readerGate)
            readers = _openReaders.ToArray();
        List<Exception>? failures = null;
        foreach (var reader in readers)
        {
            try
            {
                reader.CloseFromConnection();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        ThrowCleanupFailures("One or more readers could not be closed.", failures);
    }

    private async ValueTask CloseOpenReadersAsync()
    {
        IConnectionOwnedReader[] readers;
        lock (_readerGate)
            readers = _openReaders.ToArray();
        List<Exception>? failures = null;
        foreach (var reader in readers)
        {
            try
            {
                await reader.CloseFromConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        ThrowCleanupFailures("One or more readers could not be closed.", failures);
    }

    private void CloseOpenManagedBlobs()
    {
        List<Exception>? failures = null;
        foreach (var blob in _openManagedBlobs.ToArray())
        {
            try
            {
                blob.CloseFromConnection();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        ThrowCleanupFailures("One or more incremental blobs could not be closed.", failures);
    }

    private async ValueTask CloseOpenManagedBlobsAsync()
    {
        List<Exception>? failures = null;
        foreach (var blob in _openManagedBlobs.ToArray())
        {
            try
            {
                await blob.CloseFromConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        ThrowCleanupFailures("One or more incremental blobs could not be closed.", failures);
    }

    private void ResetOpenCommands()
    {
        List<Exception>? failures = null;
        foreach (var command in _openCommands.ToArray())
        {
            try
            {
                command.ResetFromConnection();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        ThrowCleanupFailures("One or more commands could not be reset.", failures);
    }

    private static Exception CombineCleanupErrors(
        string message,
        Exception? existing,
        Exception current)
        => existing is null
            ? current
            : new AggregateException(message, existing, current);

    private static void ThrowCleanupFailures(
        string message,
        List<Exception>? failures)
    {
        if (failures is null)
            return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(message, failures);
    }

    private static string NormalizeDataSource(
        SqliteConnectionStringBuilder options,
        bool validateFileExists = true)
    {
        var dataSource = options.DataSource;
        if (string.IsNullOrEmpty(dataSource))
            return ":memory:";
        if (options.EffectiveLocalProvider == AhtolaLocalProvider.Managed && options.Vfs is { Length: > 0 })
            throw new NotSupportedException(Properties.Resources.ManagedVfsNotSupported);
        if (options.Vfs is { Length: > 0 } vfs && !IsSupportedVfs(vfs))
            throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_ERROR, "no such vfs: " + vfs), SQLITE_ERROR);
        if (dataSource == ":memory:")
            return dataSource;
        if (options.Mode == SqliteOpenMode.Memory)
            return options.Cache == SqliteCacheMode.Shared && dataSource.Length > 0
                ? options.EffectiveLocalProvider == AhtolaLocalProvider.Managed
                    ? dataSource
                    : GetSharedMemoryFile(dataSource)
                : ":memory:";
        if (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return NormalizeUriDataSource(dataSource, validateFileExists);

        const string dataDirectory = "|DataDirectory|";
        if (dataSource.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var baseDirectory = AppDomain.CurrentDomain.GetData("DataDirectory") as string
                                ?? AppContext.BaseDirectory;
            dataSource = Path.Combine(baseDirectory, dataSource[dataDirectory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var filename = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.Combine(AppContext.BaseDirectory, dataSource);

        if (validateFileExists
            && (options.Mode == SqliteOpenMode.ReadOnly || options.Mode == SqliteOpenMode.ReadWrite)
            && !File.Exists(filename))
            throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_CANTOPEN, "unable to open database file"), SQLITE_CANTOPEN);

        return filename;
    }

    private static string NormalizeUriDataSource(string dataSource, bool validateFileExists)
    {
        var queryStart = dataSource.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? dataSource[5..] : dataSource[5..queryStart];
        var query = queryStart < 0 ? string.Empty : dataSource[(queryStart + 1)..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (!pieces[0].Equals("mode", StringComparison.OrdinalIgnoreCase))
                continue;

            var mode = pieces.Length == 2 ? pieces[1] : string.Empty;
            if (!mode.Equals("ro", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("rw", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("rwc", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("memory", StringComparison.OrdinalIgnoreCase))
                throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_ERROR, "no such access mode: " + mode), SQLITE_ERROR);
            if (mode.Equals("memory", StringComparison.OrdinalIgnoreCase))
                return ":memory:";
            if (validateFileExists
                && (mode.Equals("ro", StringComparison.OrdinalIgnoreCase) || mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
                && !File.Exists(path))
                throw new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_CANTOPEN, "unable to open database file"), SQLITE_CANTOPEN);
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private static bool IsSupportedVfs(string vfs)
        => vfs.Equals("win32-longpath", StringComparison.OrdinalIgnoreCase)
           || vfs.Equals("unix-dotfile", StringComparison.OrdinalIgnoreCase);

    private static string GetSharedMemoryFile(string dataSource)
    {
        var sanitized = string.Join("_", dataSource.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (sanitized.Length == 0)
            sanitized = Math.Abs(dataSource.GetHashCode(StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture);

        return Path.Combine(Path.GetTempPath(), "Ahtola-dotnet-shared-" + sanitized + ".db");
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsManagedSharedMemoryConfiguration(SqliteConnectionStringBuilder options)
        => options.EffectiveLocalProvider == AhtolaLocalProvider.Managed
           && options.Mode == SqliteOpenMode.Memory
           && options.Cache == SqliteCacheMode.Shared
           && !string.IsNullOrWhiteSpace(options.DataSource);

    private static bool IsNativeSharedMemory(SqliteConnectionStringBuilder options)
        => options.EffectiveLocalProvider != AhtolaLocalProvider.Managed
           && options.Mode == SqliteOpenMode.Memory
           && options.Cache == SqliteCacheMode.Shared
           && options.DataSource.Length > 0;

    private void ValidateManagedSharedCacheOptions()
    {
        if (_connectionOptions.EffectiveLocalProvider != AhtolaLocalProvider.Managed
            || _connectionOptions.Cache != SqliteCacheMode.Shared)
        {
            return;
        }

        if (!IsManagedSharedMemoryConfiguration(_connectionOptions))
        {
            // Anonymous in-memory databases stay connection-private, and file-backed
            // Cache=Shared opens as an ordinary private file connection: the managed
            // engine cannot emulate SQLite shared-cache semantics for file databases,
            // so it deliberately provides stronger isolation instead of rejecting the
            // keyword outright (matches AhtolaConnectionOptions.GetManagedLocalOpenOptions).
            return;
        }

                // Functions/aggregates/collations are catalog-scoped on shared memory (see
                // Register* paths). Only connection-style hooks remain unsupported.
                if (HasHooks)
                    throw new NotSupportedException(Properties.Resources.ManagedSharedCacheHooksNotSupported);
            }

    private void ValidateForeignReadOnlyOptions()
    {
        if (!_connectionOptions.ForeignReadOnly)
            return;

        if (_connectionOptions.EffectiveLocalProvider != AhtolaLocalProvider.Managed
            || _connectionOptions.Mode != SqliteOpenMode.ReadOnly
            || _connectionOptions.Pooling
            || _connectionOptions.Cache == SqliteCacheMode.Shared
            || _connectionOptions.HasEncryptionOptions
            || !string.IsNullOrEmpty(_connectionOptions.Password)
            || string.IsNullOrWhiteSpace(_connectionOptions.DataSource)
            || _connectionOptions.DataSource.Equals(":memory:", StringComparison.Ordinal))
        {
            throw new NotSupportedException(Properties.Resources.ManagedForeignReadOnlyNotSupported);
        }
    }

    private bool CanUseManagedPooling(string filename, AhtolaEncryptionOptions? encryption)
        => _connectionOptions.Pooling
           && encryption is null
               && PageCodec is null
               && !HasManagedCallbacks
               && !_connectionOptions.ForeignReadOnly
               && _connectionOptions.Mode != SqliteOpenMode.Memory
               && !filename.Equals(":memory:", StringComparison.Ordinal);

        private bool TryGetManagedPoolKey(out ManagedConnectionPoolKey key)
        {
            key = default;
            if (_connectionOptions.EffectiveLocalProvider != AhtolaLocalProvider.Managed
                || !_connectionOptions.Pooling
                || _connectionOptions.HasEncryptionOptions
                || PageCodec is not null
                || _connectionOptions.Mode == SqliteOpenMode.Memory
                || _connectionOptions.Cache == SqliteCacheMode.Shared)
            {
                return false;
            }

        var filename = NormalizeDataSource(_connectionOptions, validateFileExists: false);
        if (filename.Equals(":memory:", StringComparison.Ordinal))
            return false;

        key = ManagedConnectionPoolKey.Create(
            filename,
            _connectionOptions.Mode == SqliteOpenMode.ReadOnly);
        return true;
    }

    private bool HasManagedCallbacks
        => _scalarFunctions.Count != 0
           || _aggregateFunctions.Count != 0
           || _collations.Count != 0
           || HasHooks;

    private static string RegisterSharedMemoryFile(string path)
    {
        lock (SharedMemoryLock)
        {
            if (!SharedMemoryReferences.TryGetValue(path, out var references))
            {
                if (File.Exists(path))
                    File.Delete(path);
                SharedMemoryReferences[path] = 1;
            }
            else
            {
                SharedMemoryReferences[path] = references + 1;
            }

            return path;
        }
    }

    private static void ReleaseSharedMemoryFile(string path)
    {
        lock (SharedMemoryLock)
        {
            if (!SharedMemoryReferences.TryGetValue(path, out var references))
                return;

            if (references > 1)
            {
                SharedMemoryReferences[path] = references - 1;
                return;
            }

            SharedMemoryReferences.Remove(path);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private void RewriteManagedFilePassword(string databasePath, string? newPassword)
    {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrEmpty(directory))
                directory = Path.GetTempPath();

            var tempPath = Path.Combine(directory, $".ahtola-rekey-{Guid.NewGuid():N}.db");
            var previousConnectionString = ConnectionString;
            var reopenConnectionString = BuildPasswordRewriteConnectionString(databasePath, newPassword);

            try
            {
                try
                {
                    ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
                }
                catch
                {
                    // Best-effort; snapshot backup still copies a consistent catalog view.
                }

                var destinationConnectionString = BuildPasswordRewriteConnectionString(tempPath, newPassword);
                using (var destination = new SqliteConnection(destinationConnectionString))
                {
                    destination.Open();
                    BackupDatabase(destination);
                    destination.Close();
                }

                ReleaseManagedHandlesForFileReplace();

                try
                {
                    ReplaceDatabaseFiles(databasePath, tempPath);
                }
                catch
                {
                    ConnectionString = previousConnectionString;
                    Open();
                    throw;
                }

                ConnectionString = reopenConnectionString;
                Open();
            }
            finally
            {
                DeleteDatabaseFiles(tempPath);
            }
        }

        private string BuildPasswordRewriteConnectionString(string dataSource, string? newPassword)
        {
            var builder = new SqliteConnectionStringBuilder(ConnectionString)
            {
                DataSource = dataSource,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                ForeignReadOnly = false,
                LocalProvider = AhtolaLocalProvider.Managed,
                Cache = SqliteCacheMode.Default,
            };

            builder.Remove("Encryption Cipher");
            builder.Remove("Encryption Key");
            if (string.IsNullOrEmpty(newPassword))
            {
                builder.Remove("Password");
                // Scheme without Password is invalid; drop both for plaintext reopen.
                builder.Remove("Password Scheme");
            }
            else
            {
                builder.Password = newPassword;
                // Keep Password Scheme from the source connection string when present.
            }
            return builder.ConnectionString;
        }

        private void ReleaseManagedHandlesForFileReplace()
        {
            CloseOpenManagedBlobs();
            CloseOpenReaders();
            Transaction?.Dispose();
            ResetOpenCommands();
            var originalState = State;
            DisposeDatabaseAndManagedEncryptionFileSystem(pooledReusable: false);
            _dataSource = null;
            _readOnly = false;
            _managedSharedMemory = false;
            if (_sharedMemoryPath is not null)
            {
                ReleaseSharedMemoryFile(_sharedMemoryPath);
                _sharedMemoryPath = null;
            }

            OnStateChange(new StateChangeEventArgs(originalState, State));
        }

        private static void ReplaceDatabaseFiles(string databasePath, string tempPath)
        {
            if (!File.Exists(tempPath))
                throw new FileNotFoundException("Rewrite destination database was not created.", tempPath);

            var backupPath = databasePath + $".ahtola-rekey-bak-{Guid.NewGuid():N}";
            try
            {
                File.Move(databasePath, backupPath);
                try
                {
                    File.Move(tempPath, databasePath);
                }
                catch
                {
                    if (!File.Exists(databasePath) && File.Exists(backupPath))
                        File.Move(backupPath, databasePath);
                    throw;
                }

                DeleteDatabaseSidecars(databasePath);
                MoveSidecarIfPresent(tempPath + "-wal", databasePath + "-wal");
                MoveSidecarIfPresent(tempPath + "-shm", databasePath + "-shm");
                DeleteDatabaseFiles(backupPath);
            }
            catch
            {
                DeleteDatabaseFiles(backupPath);
                throw;
            }
        }

        private static void MoveSidecarIfPresent(string source, string destination)
        {
            if (!File.Exists(source))
                return;
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(source, destination);
        }

        private static void DeleteDatabaseFiles(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }

        private static void DeleteDatabaseSidecars(string databasePath)
        {
            foreach (var candidate in new[] { databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }

        private static SqliteException ToSqliteException(AhtolaException exception)
        {
            var message = exception.Message;
            return new SqliteException(Properties.Resources.SqliteNativeError(SQLITE_ERROR, message), SQLITE_ERROR);
        }

        /// <summary>
        /// Maps managed encryption/open authentication failures to include the SDS-shaped
        /// phrase RDM uses for password-protected file detection.
        /// </summary>
        private static Exception MapManagedEncryptionOpenFailure(Exception exception, bool encryptionAttempted)
        {
            // Configuration mistakes (unknown Password Scheme, bad CS combo) must
            // surface as-is — do not wrap them as "encrypted or not a database".
            if (IsManagedEncryptionConfigurationException(exception))
                return exception;

            // Map when a passphrase or Encryption Key was supplied, or the engine/file already
            // looks encrypted/corrupt — so empty-password open of AHTLA files also gets
            // the classic SDS detection phrase on Exception.Message.
            if (!encryptionAttempted && !LooksLikeEncryptedOrCorruptDatabase(exception))
                return exception;

            if (AhtolaPasswordEncryption.ContainsEncryptedOrNotDatabasePhrase(exception.Message))
                return exception;

            var mapped = AhtolaPasswordEncryption.EnsureEncryptedOrNotDatabasePhrase(exception.Message);
            return exception switch
            {
                SqliteException sqlite => new SqliteException(
                    Properties.Resources.SqliteNativeError(sqlite.SqliteErrorCode, mapped),
                    sqlite.SqliteErrorCode,
                    sqlite.SqliteExtendedErrorCode),
                _ => new InvalidDataException(mapped, exception),
            };
        }

        private static bool IsManagedEncryptionConfigurationException(Exception exception)
            => exception is NotSupportedException
                or InvalidOperationException
                or ArgumentException;

        private static bool LooksLikeEncryptedOrCorruptDatabase(Exception exception)
        {
            if (IsManagedEncryptionConfigurationException(exception))
                return false;

            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (IsManagedEncryptionConfigurationException(current))
                    return false;

                if (current is CryptographicException)
                    return true;

                var message = current.Message;
                if (string.IsNullOrEmpty(message))
                    continue;
                if (AhtolaPasswordEncryption.ContainsEncryptedOrNotDatabasePhrase(message))
                    return true;
                if (message.Contains("failed authentication", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("authentication tag", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not a database", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("AHTLA", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("invalid database header", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("malformed database", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("AhtolaEncryptionOptions", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("plaintext fallback", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        private bool IsManagedProvider => _connectionOptions.EffectiveLocalProvider == AhtolaLocalProvider.Managed;

        }
