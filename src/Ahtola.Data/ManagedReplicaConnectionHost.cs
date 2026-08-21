using Ahtola.Core;
using Ahtola.Core.Storage;

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
            EnsureReplicaAvailableAndCaughtUpAsync(syncEntry, options, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var operation = syncEntry.EnterLocalOperation(CancellationToken.None);
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
            await EnsureReplicaAvailableAndCaughtUpAsync(syncEntry, options, cancellationToken).ConfigureAwait(false);
            using var operation = await syncEntry.EnterLocalOperationAsync(cancellationToken).ConfigureAwait(false);
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
    /// Ensures a replica database exists at <paramref name="options"/>'s path, bootstrapping it
    /// from the remote database if missing, and performs the mandatory post-bootstrap logical
    /// catch-up pull (<see cref="RunCatchUpIfMvccLogicalAsync"/>) before returning — as ONE
    /// exclusive publication unit (the same <see cref="ManagedReplicaSyncRegistry.Entry.PublishAsync"/>
    /// mechanism a regular <see cref="SyncAsync(CancellationToken)"/> uses).
    /// </summary>
    /// <remarks>
    /// A durably bootstrapped-but-never-caught-up replica is not a safe state to leave sitting on
    /// disk: its base image is the last durable generation base (the server deliberately never
    /// checkpoints for a bootstrap), so without a guaranteed catch-up it would silently serve
    /// stale data, potentially forever, since a later <c>Open()</c> only sees a durable
    /// (database, metadata) pair and has no way to tell that catch-up never ran. Publication
    /// exclusivity additionally serializes concurrent first-time <c>Open()</c> calls for the same
    /// path so they cannot race each other's downloads, and if catch-up fails after a successful
    /// bootstrap, the whole (database, metadata) pair is rolled back so the next attempt (by this
    /// caller, or a concurrent one that was waiting for its publication turn) retries a clean
    /// bootstrap+catch-up rather than observing a half-finished replica.
    /// </remarks>
    private static async Task EnsureReplicaAvailableAndCaughtUpAsync(
        ManagedReplicaSyncRegistry.Entry syncEntry,
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        if (IsReplicaFilePresent(options.Path))
            return;

        await syncEntry.PublishAsync(
                token => BootstrapAndCatchUpAsync(options, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsReplicaFilePresent(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0;

    private static async Task BootstrapAndCatchUpAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        if (IsReplicaFilePresent(options.Path))
        {
            // Another Open() call already completed bootstrap+catch-up while this one waited
            // its turn for exclusive publication access; nothing left to do.
            return;
        }

        if (File.Exists(options.Path))
        {
            throw new NotSupportedException(
                "Managed embedded replica bootstrap only installs a database at a missing replica path.");
        }

        await ManagedReplicaBootstrapper.BootstrapAsync(options, cancellationToken).ConfigureAwait(false);

        // Captured immediately after a successful bootstrap publish so a failed catch-up's
        // rollback below can verify -- under a freshly (re)acquired apply lease -- that this
        // exact generation is still the one on disk before deleting it. See
        // RollBackFailedCatchUpIfStillThisGenerationAsync.
        var bootstrappedRevision = ManagedReplicaBootstrapper.LoadMetadata(options.Path)?.Revision
            ?? throw new InvalidOperationException(
                "Managed embedded replica bootstrap did not publish metadata.");

        try
        {
            await RunCatchUpIfMvccLogicalAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollBackFailedCatchUpIfStillThisGenerationAsync(options, bootstrappedRevision)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Rolls a bootstrap fully back after its mandatory post-bootstrap catch-up failed -- but
    /// only if the replica is still durably sitting in exactly the broken "bootstrapped, never
    /// caught up" generation identified by <paramref name="bootstrappedRevision"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="RunCatchUpIfMvccLogicalAsync"/> releases the apply lease (via
    /// <c>ManagedReplicaBootstrapper.CheckForUpdatesAsync</c>'s own <c>await using</c> lease
    /// scope) before its failure ever reaches this catch handler, so deleting unconditionally
    /// here -- as this method used to -- is not race-free: any other publisher for the very same
    /// physical replica (most commonly an already-open connection's ordinary
    /// <see cref="SyncAsync(CancellationToken)"/> reaching this path through a differently-aliased
    /// <see cref="ManagedReplicaSyncRegistry.Entry"/> for the same underlying file -- see
    /// <c>ManagedReplicaApplyLock</c>'s physical-identity keying -- but in principle any
    /// concurrent caller of <c>ManagedReplicaBootstrapper.CheckForUpdatesAsync</c> for this path)
    /// could acquire the now-free apply lease, publish a newer, entirely valid revision, and
    /// release, all before this rollback gets around to running. Reacquiring the lease here and
    /// re-verifying the on-disk revision before deleting closes that gap: this rollback only ever
    /// deletes the exact generation it set out to undo, never state a concurrent publisher
    /// legitimately moved past it.
    /// </remarks>
    private static async Task RollBackFailedCatchUpIfStillThisGenerationAsync(
        AhtolaReplicaOptions options,
        string bootstrappedRevision)
    {
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapCatchUpFailureObserved);

        // CancellationToken.None throughout: this cleanup must run to completion regardless of
        // why catch-up failed -- including because the caller's own cancellation token fired --
        // or a canceled catch-up would leave a permanently broken, never-caught-up replica on
        // disk instead of being retried cleanly by the next Open().
        await using var lease = await ManagedReplicaApplyLock
            .AcquireExclusiveAsync(options.Path, CancellationToken.None)
            .ConfigureAwait(false);

        var current = ManagedReplicaBootstrapper.LoadMetadata(options.Path);
        if (current is null)
        {
            // Already gone: a concurrent rollback (or an equivalent failure reachable through a
            // differently-aliased path to the same replica) already cleaned this generation up.
            return;
        }

        if (!string.Equals(current.Value.Revision, bootstrappedRevision, StringComparison.Ordinal))
        {
            // Some other publisher has already moved this replica past the specific bootstrap
            // generation that failed to catch up. That newer state is not this rollback's to
            // judge or destroy -- leave it alone.
            return;
        }

        ManagedReplicaBootstrapper.DeleteBootstrappedReplicaFiles(options.Path);
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
    private static async Task RunCatchUpIfMvccLogicalAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
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

        // Anything still sitting in the change journal after the push batch above (capped by
        // PushOperationsThreshold) has not reached the server, so the pull below must reconcile
        // it rather than let its own remote row-level apply silently overwrite it.
        var pendingLocalChanges = _changeJournal.ReadBatch(int.MaxValue).Changes;
        var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                replicaOptions,
                metadata,
                syncOptions,
                pendingLocalChanges,
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
        hooks.DetailedUpdateHook = change =>
        {
            if (!change.Database.Equals("main", StringComparison.OrdinalIgnoreCase))
                return;
            lock (_changeGate)
            {
                _statementChanges.Add(
                    ReplicaLocalChange.Row(
                        change.Operation,
                        change.Database,
                        change.Table,
                        change.RowId,
                        change.Operation == SqliteChangeOperation.Delete && change.BeforeValues is { } beforeValues
                            ? SqliteRecordCodec.Encode(beforeValues)
                            : null));
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
