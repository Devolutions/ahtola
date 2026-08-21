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
    private AhtolaEncryptionFileSystem? _encryptionFileSystem;
    private ManagedReplicaBootstrapper.ManagedReplicaMetadata? _metadata;
    private ManagedReplicaPageMaterializationRegistry.Lease? _materializationLease;
    private readonly AhtolaReplicaOptions _options;
    private readonly ManagedReplicaSyncRegistry.Entry _syncEntry;
    private volatile ManagedReplicaChangeJournal _changeJournal;
    private readonly object _changeGate = new();
    private readonly List<ReplicaLocalChange> _statementChanges = [];
    private readonly List<ReplicaLocalChange> _transactionChanges = [];
    private readonly List<SavepointFrame> _savepoints = [];
    private AhtolaConnection? _connection;
    private bool _localTransactionActive;
    private bool _transactionOpenedBySavepoint;
    private IDisposable? _sqlTransactionOperation;
    private bool _sqlTransactionBeginPending;
    private bool _sqlTransactionCompletionPending;

    private ManagedReplicaConnectionHost(
        IManagedDatabaseAdapter database,
        AhtolaEncryptionFileSystem? encryptionFileSystem,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata? metadata,
        ManagedReplicaPageMaterializationRegistry.Lease? materializationLease,
        AhtolaReplicaOptions options,
        ManagedReplicaChangeJournal changeJournal,
        ManagedReplicaSyncRegistry.Entry syncEntry)
    {
        _database = database;
        _encryptionFileSystem = encryptionFileSystem;
        _metadata = metadata;
        _materializationLease = materializationLease;
        _options = options;
        _changeJournal = changeJournal;
        _syncEntry = syncEntry;
        InstallChangeCapture(database.Connection);
        _syncEntry.Register(this);
    }

    public IManagedDatabaseAdapter Database
        => _database ?? throw new ObjectDisposedException(nameof(ManagedReplicaConnectionHost));

    internal bool TryGetDatabase(out IManagedDatabaseAdapter? database)
    {
        database = _database;
        return database is not null;
    }

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

    /// <summary>
    /// Projects the entire currently pending local change batch into Ahtola's public
    /// change-data-capture row contract. Pure read: it does not acknowledge the journal
    /// watermark, so it has no effect on a subsequent push.
    /// </summary>
    /// <exception cref="AhtolaReplicaChangeCaptureException">
    /// A local transaction is currently open on this connection. Projecting an "after" image
    /// while a transaction is in progress could observe writes that are not yet committed - or
    /// that the transaction later rolls back - and silently bake them into the projected row as
    /// if they were committed. Commit or roll back the open transaction first.
    /// </exception>
    public AhtolaReplicaChangeCaptureBatch PeekPendingChangeCapture()
    {
        ThrowIfChangeCaptureTransactionIsActive();

        // Held for the whole call so a concurrent publish's quiesce-and-close cannot interleave
        // with it: EnterLocalOperation blocks that cycle from proceeding past its own "wait for
        // zero active local operations" step until this lease is released, which is exactly what
        // keeps CloseForPublication/ReopenAfterPublication (the database/journal generation
        // swap) from running underneath this projection and mixing generations.
        using var operation = EnterLocalOperation(CancellationToken.None);

        // Recheck after entering: a transaction can start between the precheck and lease
        // acquisition. The precheck prevents publication from deadlocking behind a transaction
        // lease while this thread waits to enter another local operation.
        ThrowIfChangeCaptureTransactionIsActive();

        // Stable locals captured while the lease above is held, so both come from the same
        // database/journal generation for the whole call: Database and _changeJournal are only
        // ever swapped together, by CloseForPublication/ReopenAfterPublication, which that lease
        // blocks for as long as it is held.
        var database = Database;
        var journal = _changeJournal;
        return ManagedReplicaChangeCaptureProjector.Project(database.Connection, journal.ReadBatch(int.MaxValue));
    }

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
                _savepoints.Clear();
                _localTransactionActive = true;
                _transactionOpenedBySavepoint = false;
                _sqlTransactionBeginPending = false;
                return;
            }

            var savepoint = SqlTransactionControl.GetSavepointCommand(sql);
            if (savepoint.Action != SqlSavepointAction.None)
            {
                CompleteSavepointStatement(savepoint);
                return;
            }

            var completion = SqlTransactionControl.GetCompletion(sql);
            if (completion == SqlTransactionCompletion.Rollback)
            {
                _statementChanges.Clear();
                _transactionChanges.Clear();
                _savepoints.Clear();
                _localTransactionActive = false;
                _transactionOpenedBySavepoint = false;
                _sqlTransactionCompletionPending = true;
                return;
            }

            if (completion == SqlTransactionCompletion.Commit)
            {
                _transactionChanges.AddRange(_statementChanges);
                _statementChanges.Clear();
                _changeJournal.AppendCommitted(_transactionChanges);
                _transactionChanges.Clear();
                _savepoints.Clear();
                _localTransactionActive = false;
                _transactionOpenedBySavepoint = false;
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
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(options.Path);
        var (database, encryptionFileSystem, materializationLease) = OpenDatabase(options, metadata);
        try
        {
            _ = database.Connect();
            return new ManagedReplicaConnectionHost(
                database,
                encryptionFileSystem,
                metadata,
                materializationLease,
                options,
                ManagedReplicaChangeJournal.Open(options.Path),
                syncEntry);
        }

        catch
        {
            database.Dispose();
            encryptionFileSystem?.Dispose();
            materializationLease?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the managed database at <paramref name="options"/>'s path, wiring in remote
    /// encryption (see <see cref="ManagedReplicaEncryption.OpenDatabase"/>) when configured. The
    /// returned file system, if any, must be kept alive and disposed alongside the database for
    /// as long as it remains open -- it is consulted on every subsequent page read/write, not
    /// only at open time.
    /// </summary>
    private static (
        IManagedDatabaseAdapter Database,
        AhtolaEncryptionFileSystem? EncryptionFileSystem,
        ManagedReplicaPageMaterializationRegistry.Lease? MaterializationLease) OpenDatabase(
        AhtolaReplicaOptions options,
        ManagedReplicaBootstrapper.ManagedReplicaMetadata? metadata)
    {
        var stateExists = File.Exists(
            options.Path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
        var publication = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(options.Path);
        if (publication.RequiresPageState && !stateExists)
        {
            throw new InvalidDataException(
                "Managed replica bootstrap state requires lazy-page state that is missing.");
        }

        if (stateExists)
        {
            var requiredMetadata = metadata
                ?? throw new InvalidDataException(
                    "Managed replica lazy-page state exists without revision metadata.");
            var materializationLease = ManagedReplicaPageMaterializationRegistry.Acquire(
                PhysicalFileSystem.Instance,
                options.Path,
                requiredMetadata.Revision,
                new ManagedReplicaPullPageSource(options),
                options.PartialBootstrap?.Prefetch ?? false);
            try
            {
                var database = ManagedDatabaseAdapter.OpenFile(
                    options.Path,
                    materializationLease.FileSystem,
                    readOnly: false);
                _ = database.Connect();
                return (database, null, materializationLease);
            }
            catch
            {
                materializationLease.Dispose();
                throw;
            }
        }

        var opened = ManagedReplicaEncryption.OpenDatabase(options.Path, options.RemoteEncryption);
        try
        {
            _ = opened.Database.Connect();
            return (opened.Database, opened.FileSystem, null);
        }
        catch
        {
            opened.Dispose();
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
        {
            await PrepareExistingReplicaForOpenAsync(syncEntry, options.Path, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await syncEntry.PublishAsync(
                token => BootstrapAndCatchUpAsync(options, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task PrepareExistingReplicaForOpenAsync(
        ManagedReplicaSyncRegistry.Entry syncEntry,
        string databasePath,
        CancellationToken cancellationToken)
    {
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(databasePath);
        var hasRecoveryArtifacts = ManagedReplicaRevertWal.GetArtifactPaths(databasePath).Any(File.Exists);
        if (metadata is null)
        {
            if (hasRecoveryArtifacts)
            {
                throw new InvalidDataException(
                    "Managed embedded replica checkpoint recovery artifacts have no matching metadata.");
            }
            return;
        }
        if (!metadata.Value.RevertState.HasValue && !hasRecoveryArtifacts)
            return;

        await syncEntry.PublishAsync(
                cancellationToken =>
                {
                    var current = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
                                  ?? throw new InvalidDataException(
                                      "Managed embedded replica checkpoint recovery metadata is missing.");
                    _ = ManagedReplicaRevertWal.PrepareSynchronization(databasePath, current);
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsReplicaFilePresent(string path)
    {
        var publication = ManagedReplicaBootstrapper.GetBootstrapPublicationInfo(path);
        var metadataExists = File.Exists(path + ManagedReplicaBootstrapper.MetadataSuffix);
        var pageStateExists = File.Exists(
            path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
        if (!File.Exists(path)
            || new FileInfo(path).Length == 0
            || !publication.IsComplete
            || publication.MarkerExists && !metadataExists
            || publication.RequiresPageState && !pageStateExists)
        {
            return false;
        }

        return !pageStateExists || metadataExists;
    }

    private static async Task BootstrapAndCatchUpAsync(AhtolaReplicaOptions options, CancellationToken cancellationToken)
    {
        if (IsReplicaFilePresent(options.Path))
        {
            // Another Open() call already completed bootstrap+catch-up while this one waited
            // its turn for exclusive publication access; nothing left to do.
            return;
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

        value = await ManagedReplicaBootstrapper
            .CompletePartialReplicaAsync(
                options,
                value,
                allowTrackedLocalMutations: false,
                retainedMaterializer: null,
                cancellationToken)
            .ConfigureAwait(false);
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
        if (metadata.JournalBaseWatermark < _changeJournal.RetentionBase)
            metadata = metadata with { JournalBaseWatermark = _changeJournal.RetentionBase };
        metadata = ManagedReplicaBootstrapper.EnsureLegacyRemoteBaseSnapshot(
            replicaOptions.Path,
            metadata);
        metadata = ManagedReplicaRevertWal.PrepareSynchronization(replicaOptions.Path, metadata);
        var hasTrackedLocalChanges = _changeJournal.ReadBatch(int.MaxValue).Changes.Count != 0
            || _changeJournal.ReadAcknowledged(metadata.JournalBaseWatermark).Count != 0;
        var retainedMaterializer = _materializationLease?.FileSystem;
        var push = await PushLocalChangesAsync(
                replicaOptions,
                metadata,
                syncOptions,
                retainedMaterializer,
                cancellationToken)
            .ConfigureAwait(false);
        metadata = push.Metadata;
        var pushedChangeCount = push.ChangeCount;

        metadata = await ManagedReplicaBootstrapper
            .CompletePartialReplicaAsync(
                replicaOptions,
                metadata,
                allowTrackedLocalMutations: hasTrackedLocalChanges,
                retainedMaterializer,
                cancellationToken)
            .ConfigureAwait(false);
        _metadata = metadata;

        var pendingLocalChanges = _changeJournal.ReadBatch(int.MaxValue).Changes;
        var acknowledgedLocalChanges = _changeJournal.ReadAcknowledged(metadata.JournalBaseWatermark);
        var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                replicaOptions,
                metadata,
                syncOptions,
                pendingLocalChanges,
                acknowledgedLocalChanges,
                cancellationToken)
            .ConfigureAwait(false);
        _metadata = ManagedReplicaBootstrapper.LoadMetadata(replicaOptions.Path);
        if (result.Outcome == AhtolaSyncOutcome.RemoteChangesApplied && _metadata is { } published)
            _changeJournal.PruneAcknowledged(published.JournalBaseWatermark);
        return result with
        {
            Statistics = result.Statistics with
            {
                CdcOperations = checked(result.Statistics.CdcOperations + pushedChangeCount),
                LastPush = pushedChangeCount == 0 ? result.Statistics.LastPush : DateTimeOffset.UtcNow,
            },
        };
    }

    public void Dispose()
    {
        try
        {
            // An explicit SQL transaction already owns a long-lived local-operation lease.
            // Release it before waiting for a new disposal lease, otherwise a pending
            // publication waits for the transaction while blocking this acquisition forever.
            ReleaseSqlTransactionOperation();
            using var operation = EnterLocalOperation(CancellationToken.None);
            _syncEntry.Unregister(this);
            Interlocked.Exchange(ref _connection, null);
            var database = Interlocked.Exchange(ref _database, null);
            try
            {
                database?.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _encryptionFileSystem, null)?.Dispose();
                Interlocked.Exchange(ref _materializationLease, null)?.Dispose();
            }
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
        Interlocked.Exchange(ref _encryptionFileSystem, null)?.Dispose();
    }

    internal void ReopenAfterPublication()
    {
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(_options.Path);
        if (metadata is { } value)
        {
            if (value.RevertState is null
                || value.RevertState is
                {
                    Phase: ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.Captured
                    or ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted
                    or ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreOriginal,
                })
            {
                value = ManagedReplicaRevertWal.PrepareSynchronization(_options.Path, value);
            }
            metadata = value;
            _metadata = value;
        }
        else if (ManagedReplicaRevertWal.GetArtifactPaths(_options.Path).Any(File.Exists))
        {
            throw new InvalidDataException(
                "Managed embedded replica checkpoint recovery artifacts have no matching metadata.");
        }

        var retainedLease = _materializationLease;
        IManagedDatabaseAdapter? database = null;
        AhtolaEncryptionFileSystem? encryptionFileSystem = null;
        ManagedReplicaPageMaterializationRegistry.Lease? materializationLease = null;
        var reusedRetainedLease = retainedLease is not null
            && File.Exists(_options.Path + ManagedReplicaPageMaterializingFileSystem.StateSuffix);
        try
        {
            if (reusedRetainedLease)
            {
                database = ManagedDatabaseAdapter.OpenFile(
                    _options.Path,
                    retainedLease!.FileSystem,
                    readOnly: false);
                materializationLease = retainedLease;
            }
            else
            {
                if (retainedLease is not null)
                {
                    _materializationLease = null;
                    retainedLease.Dispose();
                }
                var opened = OpenDatabase(_options, metadata);
                database = opened.Database;
                encryptionFileSystem = opened.EncryptionFileSystem;
                materializationLease = opened.MaterializationLease;
            }

            var changeJournal = ManagedReplicaChangeJournal.Open(_options.Path);
            InstallChangeCapture(database.Connection);
            if (Interlocked.CompareExchange(ref _database, database, null) is not null)
                throw new InvalidOperationException("Managed embedded replica host reopened more than once.");
            _metadata = metadata;
            _materializationLease = materializationLease;
            _changeJournal = changeJournal;
            _encryptionFileSystem = encryptionFileSystem;
            database = null!;
            encryptionFileSystem = null;
            materializationLease = null;
        }
        finally
        {
            database?.Dispose();
            encryptionFileSystem?.Dispose();
            if (materializationLease is not null)
            {
                if (reusedRetainedLease)
                    _materializationLease = null;
                materializationLease.Dispose();
            }
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
                _savepoints.Clear();
                _localTransactionActive = false;
                _transactionOpenedBySavepoint = false;
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

    private void ThrowIfChangeCaptureTransactionIsActive()
    {
        lock (_changeGate)
        {
            if (!_localTransactionActive)
               return;
        }

        throw new AhtolaReplicaChangeCaptureException(
            "Cannot peek pending change-data-capture while a local transaction is open "
            + "on this connection. Commit or roll back the transaction before peeking.");
    }

    private void CompleteSavepointStatement(SqlSavepointCommand command)
    {
        _statementChanges.Clear();
        var name = command.Name;
        if (string.IsNullOrEmpty(name))
            return;

        if (command.Action == SqlSavepointAction.Savepoint)
        {
            if (!_localTransactionActive)
            {
               _localTransactionActive = true;
               _transactionOpenedBySavepoint = true;
               _sqlTransactionBeginPending = false;
            }

            _savepoints.Add(new SavepointFrame(name, _transactionChanges.Count));
            return;
        }

        var index = _savepoints.FindLastIndex(
            frame => frame.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        if (command.Action == SqlSavepointAction.RollbackTo)
        {
            var retainedChanges = _savepoints[index].ChangeCount;
            if (_transactionChanges.Count > retainedChanges)
            {
               _transactionChanges.RemoveRange(
                   retainedChanges,
                   _transactionChanges.Count - retainedChanges);
            }

            if (_savepoints.Count > index + 1)
               _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);
            return;
        }

        _savepoints.RemoveRange(index, _savepoints.Count - index);
        if (!_transactionOpenedBySavepoint || _savepoints.Count != 0)
            return;

        _changeJournal.AppendCommitted(_transactionChanges);
        _transactionChanges.Clear();
        _localTransactionActive = false;
        _transactionOpenedBySavepoint = false;
        _sqlTransactionCompletionPending = true;
    }

    private readonly record struct SavepointFrame(string Name, int ChangeCount);

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
        ManagedReplicaPageMaterializingFileSystem? retainedMaterializer,
        CancellationToken cancellationToken)
    {
        var recoveringUnknownPush = metadata.RevertState is
        {
            Phase: ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.PushOutcomeUnknown,
        };
        ReplicaLocalChangeBatch batch;
        if (recoveringUnknownPush)
        {
            var state = metadata.RevertState!.Value;
            batch = _changeJournal.ReadBatch(state.AttemptedFirstSequence, state.AttemptedWatermark);
        }
        else
        {
            var maximumChanges = metadata.RevertState.HasValue
                ? int.MaxValue
                : GetPushBatchLimit(replicaOptions);
            batch = _changeJournal.ReadBatch(maximumChanges);
        }
        if (batch.Changes.Count == 0)
        {
            var completedMetadata = ManagedReplicaRevertWal.CompletePreparedCheckpoint(
                replicaOptions.Path,
                metadata);
            _metadata = completedMetadata;
            return new LocalPushResult(0, completedMetadata);
        }

        syncOptions.Progress?.Report(new AhtolaSyncProgress(AhtolaSyncProgressStage.Pushing));
        using var client = replicaOptions.HttpPolicy.CreateHttpClient(replicaOptions.RemoteEncryption is not null);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var remote = new AhtolaRemoteClient(
            client,
            replicaOptions.RemoteUri,
            replicaOptions.AuthToken,
            replicaOptions.RemoteEncryption,
            disposeHttpClient: false,
            automaticRedirectsDisabled: true);

        const long sourcePullGeneration = 0;
        if (recoveringUnknownPush)
        {
            var remoteWatermark = await remote.ReadReplicaPushWatermarkAsync(
                    metadata.ClientId,
                    ToCommandTimeoutSeconds(replicaOptions.HttpPolicy.RequestTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (remoteWatermark is { } watermark)
            {
                if (watermark.PullGeneration > sourcePullGeneration)
                {
                    throw new AhtolaException(
                        "Remote replica push acknowledgement is ahead of the local pull generation.",
                        AhtolaReplicaPushFailureKind.InvalidLocalState);
                }
                if (watermark.PullGeneration == sourcePullGeneration
                    && watermark.ChangeId >= batch.Changes[^1].Sequence)
                {
                    _changeJournal.Acknowledge(batch.Watermark);
                    var acknowledgedMetadata = await ManagedReplicaBootstrapper
                        .RecordLocalPushAsync(
                            replicaOptions,
                            metadata,
                            retainedMaterializer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return new LocalPushResult(batch.Changes.Count, acknowledgedMetadata);
                }
                if (watermark.PullGeneration == sourcePullGeneration
                    && watermark.ChangeId >= batch.FirstSequence)
                {
                    throw new AhtolaException(
                        "Remote replica push acknowledgement splits the pending local batch.",
                        AhtolaReplicaPushFailureKind.InvalidLocalState);
                }
            }
        }

        metadata = ManagedReplicaRevertWal.MarkPushStarted(replicaOptions.Path, metadata, batch);
        _metadata = metadata;
        try
        {
            await remote.PushReplicaChangesAsync(
                    batch,
                    metadata.ClientId,
                    sourcePullGeneration,
                    ToCommandTimeoutSeconds(replicaOptions.HttpPolicy.RequestTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            metadata.RevertState.HasValue
            && AhtolaReplicaPushFailure.Classify(exception) == AhtolaReplicaPushFailureKind.Conflict)
        {
            ManagedReplicaRevertWal.RestorePendingCheckpoint(
                replicaOptions.Path,
                metadata,
                CancellationToken.None);
            _metadata = ManagedReplicaBootstrapper.LoadMetadata(replicaOptions.Path);
            throw;
        }
        _changeJournal.Acknowledge(batch.Watermark);
        var updatedMetadata = await ManagedReplicaBootstrapper
            .RecordLocalPushAsync(
                replicaOptions,
                metadata,
                retainedMaterializer,
                cancellationToken)
            .ConfigureAwait(false);
        return new LocalPushResult(batch.Changes.Count, updatedMetadata);
    }

    private static int GetPushBatchLimit(AhtolaReplicaOptions replicaOptions)
        => replicaOptions.PushOperationsThreshold is { } threshold
            ? checked((int)Math.Min(threshold, int.MaxValue))
            : 1000;

    private static int ToCommandTimeoutSeconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return 0;
        return checked((int)Math.Max(1, Math.Ceiling(timeout.TotalSeconds)));
    }

    private readonly record struct LocalPushResult(long ChangeCount, ManagedReplicaBootstrapper.ManagedReplicaMetadata Metadata);
}
