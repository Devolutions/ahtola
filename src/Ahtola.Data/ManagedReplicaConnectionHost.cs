using Ahtola.Core;

namespace Ahtola;

/// <summary>
/// Owns a managed local database used by an embedded replica connection.
/// Initial raw-page bootstrap is intentionally separate from later incremental
/// pull and push synchronization.
/// </summary>
internal sealed class ManagedReplicaConnectionHost : IDisposable
{
    private IManagedDatabaseAdapter? _database;
    private ManagedReplicaBootstrapper.ManagedReplicaMetadata? _metadata;
    private readonly AhtolaReplicaOptions _options;
    private readonly ManagedReplicaSyncRegistry.Entry _syncEntry;
    private volatile ManagedReplicaChangeJournal _changeJournal;
    private readonly object _changeGate = new();
    private readonly List<ReplicaLocalChange> _statementChanges = [];
    private readonly List<ReplicaLocalChange> _transactionChanges = [];
    private AhtolaConnection? _connection;
    private bool _localTransactionActive;
    private IDisposable? _sqlTransactionOperation;
    private bool _sqlTransactionBeginPending;
    private bool _sqlTransactionCompletionPending;

    private ManagedReplicaConnectionHost(
        IManagedDatabaseAdapter database,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata? metadata,
        AhtolaReplicaOptions options,
        ManagedReplicaChangeJournal changeJournal,
        ManagedReplicaSyncRegistry.Entry syncEntry)
    {
        _database = database;
        _metadata = metadata;
        _options = options;
        _changeJournal = changeJournal;
        _syncEntry = syncEntry;
        InstallChangeCapture(database.Connection);
        _syncEntry.Register(this);
    }

    public IManagedDatabaseAdapter Database
        => _database ?? throw new ObjectDisposedException(nameof(ManagedReplicaConnectionHost));

    public bool SupportsSync => _metadata is not null;

    public IDisposable EnterLocalOperation(CancellationToken cancellationToken)
        => _syncEntry.EnterLocalOperation(cancellationToken);

    public bool HasSqlTransactionOperation
    {
        get
        {
            lock (_changeGate)
                return _sqlTransactionOperation is not null;
        }
    }

    public void BeginSqlTransaction(CancellationToken cancellationToken)
    {
        lock (_changeGate)
        {
            if (_sqlTransactionOperation is not null)
                return;
        }

        var operation = EnterLocalOperation(cancellationToken);
        lock (_changeGate)
        {
            if (_sqlTransactionOperation is null)
            {
                _sqlTransactionOperation = operation;
                _sqlTransactionBeginPending = true;
                return;
            }
        }

        operation.Dispose();
    }

    public void AttachConnection(AhtolaConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Interlocked.CompareExchange(ref _connection, connection, null) is { } existing
            && !ReferenceEquals(existing, connection))
        {
            throw new InvalidOperationException("Managed embedded replica host is already attached to a connection.");
        }
    }

    public void DetachConnection(AhtolaConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Interlocked.CompareExchange(ref _connection, null, connection);
    }

    public ReplicaLocalChangeBatch ReadLocalChanges(int maximumChanges)
        => _changeJournal.ReadBatch(maximumChanges);

    public void AcknowledgeLocalChanges(long watermark)
        => _changeJournal.Acknowledge(watermark);

    public void StatementStarted(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        lock (_changeGate)
        {
            // A previous reader that was abandoned before completion must not make its
            // unproven changes eligible for push.
            _statementChanges.Clear();
        }
    }

    public void StatementCompleted(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        lock (_changeGate)
        {
            var keyword = SqlTransactionControl.GetFirstKeyword(sql);
            if (IsSchemaMutation(keyword) && _statementChanges.Count == 0)
                _statementChanges.Add(ReplicaLocalChange.Schema(sql));

            if (_statementChanges.Count > 0
                && !string.IsNullOrWhiteSpace(sql)
                && _statementChanges[0].Kind == ReplicaLocalChangeKind.Row)
            {
                // One SQL statement can invoke the update hook for many rows. Replay the
                // statement once; the remaining journal rows advance with that statement.
                _statementChanges[0] = _statementChanges[0] with { Sql = sql };
            }

            if (keyword is not null && keyword.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                _statementChanges.Clear();
                _localTransactionActive = true;
                _sqlTransactionBeginPending = false;
                return;
            }

            var completion = SqlTransactionControl.GetCompletion(sql);
            if (completion == SqlTransactionCompletion.Rollback)
            {
                _statementChanges.Clear();
                _transactionChanges.Clear();
                _localTransactionActive = false;
                _sqlTransactionCompletionPending = true;
                return;
            }

            if (completion == SqlTransactionCompletion.Commit)
            {
                _transactionChanges.AddRange(_statementChanges);
                _statementChanges.Clear();
                _changeJournal.AppendCommitted(_transactionChanges);
                _transactionChanges.Clear();
                _localTransactionActive = false;
                _sqlTransactionCompletionPending = true;
                return;
            }

            if (_localTransactionActive)
            {
                _transactionChanges.AddRange(_statementChanges);
                _statementChanges.Clear();
            }
            else
            {
                _changeJournal.AppendCommitted(_statementChanges);
                _statementChanges.Clear();
            }
        }
    }

    public void StatementFailed()
    {
        IDisposable? transactionOperation = null;
        lock (_changeGate)
        {
            _statementChanges.Clear();
            if (_sqlTransactionBeginPending)
            {
                transactionOperation = _sqlTransactionOperation;
                _sqlTransactionOperation = null;
                _sqlTransactionBeginPending = false;
            }
        }

        transactionOperation?.Dispose();
    }

    public void StatementClosed()
    {
        IDisposable? transactionOperation = null;
        lock (_changeGate)
        {
            if (_sqlTransactionCompletionPending)
            {
                transactionOperation = _sqlTransactionOperation;
                _sqlTransactionOperation = null;
                _sqlTransactionCompletionPending = false;
            }
        }

        transactionOperation?.Dispose();
    }

    public async Task QuiesceAndReopenAsync(
        Func<CancellationToken, Task> stagedOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedOperation);
        await _syncEntry.PublishAsync(stagedOperation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> QuiesceAndReopenAsync<T>(
        Func<CancellationToken, Task<T>> stagedOperation,
        CancellationToken cancellationToken)
    {
        T? result = default;
        await QuiesceAndReopenAsync(
                (Func<CancellationToken, Task>)(async token => result = await stagedOperation(token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);
        return result!;
    }

    public static ManagedReplicaConnectionHost Open(AhtolaReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ManagedReplicaSupportMatrix.ValidateOptions(options);
        var syncEntry = ManagedReplicaSyncRegistry.Acquire(options.Path);
        try
        {
            using var operation = syncEntry.EnterLocalOperation(CancellationToken.None);
            var freshBootstrap = EnsureReplicaAvailableAsync(options, CancellationToken.None).GetAwaiter().GetResult();
            if (freshBootstrap)
                CatchUpAfterFreshBootstrapAsync(options, CancellationToken.None).GetAwaiter().GetResult();
            return OpenExisting(options, syncEntry);
        }
        catch
        {
            syncEntry.ReleaseReference();
            throw;
        }
    }

    public static async Task<ManagedReplicaConnectionHost> OpenAsync(
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ManagedReplicaSupportMatrix.ValidateOptions(options);
        var syncEntry = ManagedReplicaSyncRegistry.Acquire(options.Path);
        try
        {
            using var operation = await syncEntry.EnterLocalOperationAsync(cancellationToken).ConfigureAwait(false);
            var freshBootstrap = await EnsureReplicaAvailableAsync(options, cancellationToken).ConfigureAwait(false);
            if (freshBootstrap)
                await CatchUpAfterFreshBootstrapAsync(options, cancellationToken).ConfigureAwait(false);
            return OpenExisting(options, syncEntry);
        }
        catch
        {
            syncEntry.ReleaseReference();
            throw;
        }
    }

    private static ManagedReplicaConnectionHost OpenExisting(
        AhtolaReplicaOptions options,
        ManagedReplicaSyncRegistry.Entry syncEntry)
    {
        var database = OpenDatabase(options.Path);
        try
        {
            _ = database.Connect();
            return new ManagedReplicaConnectionHost(
                database,
                ManagedReplicaBootstrapper.LoadMetadata(options.Path),
                options,
                ManagedReplicaChangeJournal.Open(options.Path),
                syncEntry);
        }

        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static IManagedDatabaseAdapter OpenDatabase(string path)
    {
        var database = ManagedDatabaseAdapter.Open(path);
        try
        {
            _ = database.Connect();
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when this call performed a fresh bootstrap (the replica
    /// path did not exist yet), so the caller can catch it up with one immediate logical pull
    /// before exposing the connection.
    /// </summary>
    private static async Task<bool> EnsureReplicaAvailableAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        if (File.Exists(options.Path) && new FileInfo(options.Path).Length > 0)
            return false;

        if (File.Exists(options.Path))
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap only installs a database at a missing replica path.");
        }

        await ManagedReplicaBootstrapper.BootstrapAsync(options, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Catches a freshly bootstrapped MVCC-protocol replica up to the retained logical log. The
    /// bootstrap page image is the last durable generation base (the server deliberately never
    /// checkpoints for a bootstrap), so without this pull, opening the connection would hand out
    /// a database missing every commit since the last natural checkpoint. Long-polling is
    /// disabled: when the base is already current this must return immediately rather than hold
    /// the open call open waiting for future changes. A no-op for page-protocol replicas, whose
    /// bootstrap is already current.
    /// </summary>
    private static async Task CatchUpAfterFreshBootstrapAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(options.Path);
        if (metadata is not { Protocol: RemotePullProtocol.MvccLogical } value)
            return;

        _ = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                options.WithoutLongPoll(),
                value,
                new AhtolaSyncOptions(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        _ = await SyncAsync(new AhtolaSyncOptions(), cancellationToken).ConfigureAwait(false);
    }

    public Task<AhtolaSyncResult> SyncAsync(
        AhtolaSyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_metadata is null)
        {
            return Task.FromException<AhtolaSyncResult>(
                new NotSupportedException(
                    "Managed embedded replica synchronization requires bootstrap metadata."));
        }

        return _syncEntry.SynchronizeAsync(
            this,
            token =>
            {
                var metadata = ManagedReplicaBootstrapper.LoadMetadata(_options.Path)
                    ?? throw new NotSupportedException(
                        "Managed embedded replica synchronization requires bootstrap metadata.");
                _metadata = metadata;
                return SynchronizeAsync(_options, metadata, options, token);
            },
            cancellationToken);
    }

    private async Task<AhtolaSyncResult> SynchronizeAsync(
        AhtolaReplicaOptions replicaOptions,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        AhtolaSyncOptions syncOptions,
        CancellationToken cancellationToken)
    {
        var push = await PushLocalChangesAsync(replicaOptions, metadata, syncOptions, cancellationToken)
            .ConfigureAwait(false);
        metadata = push.Metadata;
        var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                replicaOptions,
                metadata,
                syncOptions,
                cancellationToken)
            .ConfigureAwait(false);
        _metadata = ManagedReplicaBootstrapper.LoadMetadata(replicaOptions.Path);
        return result with
        {
            Statistics = result.Statistics with
            {
                CdcOperations = checked(result.Statistics.CdcOperations + push.ChangeCount),
                LastPush = push.ChangeCount == 0 ? result.Statistics.LastPush : DateTimeOffset.UtcNow,
            },
        };
    }

    public void Dispose()
    {
        try
        {
            using var operation = EnterLocalOperation(CancellationToken.None);
            _syncEntry.Unregister(this);
            Interlocked.Exchange(ref _connection, null);
            ReleaseSqlTransactionOperation();
            var database = Interlocked.Exchange(ref _database, null);
            database?.Dispose();
        }
        finally
        {
            _syncEntry.ReleaseReference();
        }
    }

    internal void CloseForPublication()
    {
        Volatile.Read(ref _connection)?.ResetManagedReplicaCommandsForPublication();
        var database = Interlocked.Exchange(ref _database, null)
            ?? throw new ObjectDisposedException(nameof(ManagedReplicaConnectionHost));
        database.Dispose();
    }

    internal void ReopenAfterPublication()
    {
        var database = OpenDatabase(_options.Path);
        try
        {
            var changeJournal = ManagedReplicaChangeJournal.Open(_options.Path);
            InstallChangeCapture(database.Connection);
            if (Interlocked.CompareExchange(ref _database, database, null) is not null)
                throw new InvalidOperationException("Managed embedded replica host reopened more than once.");
            _changeJournal = changeJournal;
            database = null!;
        }
        finally
        {
            database?.Dispose();
        }
    }

    private void InstallChangeCapture(IManagedConnectionAdapter connection)
    {
        var hooks = connection.Hooks;
        hooks.UpdateHook = change =>
        {
            if (!change.Database.Equals("main", StringComparison.OrdinalIgnoreCase))
                return;
            lock (_changeGate)
            {
                _statementChanges.Add(
                    ReplicaLocalChange.Row(change.Operation, change.Database, change.Table, change.RowId));
            }
        };
        hooks.RollbackHook = () =>
        {
            lock (_changeGate)
            {
                _statementChanges.Clear();
                _transactionChanges.Clear();
                _localTransactionActive = false;
                if (_sqlTransactionOperation is not null)
                {
                    _sqlTransactionBeginPending = false;
                    _sqlTransactionCompletionPending = true;
                }
            }
        };
    }

    private static bool IsSchemaMutation(string? keyword)
        => keyword is not null
           && (keyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
               || keyword.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
               || keyword.Equals("DROP", StringComparison.OrdinalIgnoreCase)
               || keyword.Equals("REINDEX", StringComparison.OrdinalIgnoreCase)
               || keyword.Equals("VACUUM", StringComparison.OrdinalIgnoreCase));

    private void ReleaseSqlTransactionOperation()
    {
        IDisposable? transactionOperation;
        lock (_changeGate)
        {
            transactionOperation = _sqlTransactionOperation;
            _sqlTransactionOperation = null;
            _sqlTransactionBeginPending = false;
            _sqlTransactionCompletionPending = false;
        }

        transactionOperation?.Dispose();
    }

    private async Task<LocalPushResult> PushLocalChangesAsync(
        AhtolaReplicaOptions replicaOptions,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
        AhtolaSyncOptions syncOptions,
        CancellationToken cancellationToken)
    {
        var maximumChanges = replicaOptions.PushOperationsThreshold is { } threshold
            ? checked((int)Math.Min(threshold, int.MaxValue))
            : 1000;
        var batch = _changeJournal.ReadBatch(maximumChanges);
        if (batch.Changes.Count == 0)
            return new LocalPushResult(0, metadata);

        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Pushing));
        using var client = replicaOptions.HttpPolicy.MessageHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var remote = new AhtolaRemoteClient(
            client,
            replicaOptions.RemoteUri,
            replicaOptions.AuthToken,
            replicaOptions.RemoteEncryption,
            disposeHttpClient: false);

        await remote.PushReplicaChangesAsync(
                batch,
                metadata.ClientId,
                sourcePullGeneration: 0,
                ToCommandTimeoutSeconds(replicaOptions.HttpPolicy.RequestTimeout),
                cancellationToken)
            .ConfigureAwait(false);
        var updatedMetadata = await ManagedReplicaBootstrapper
            .RecordLocalPushAsync(replicaOptions, metadata, cancellationToken)
            .ConfigureAwait(false);
        _changeJournal.Acknowledge(batch.Watermark);
        return new LocalPushResult(batch.Changes.Count, updatedMetadata);
    }

    private static int ToCommandTimeoutSeconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return 0;
        return checked((int)Math.Max(1, Math.Ceiling(timeout.TotalSeconds)));
    }

    private readonly record struct LocalPushResult(long ChangeCount, ManagedReplicaBootstrapper.ManagedReplicaMetadata Metadata);
}
