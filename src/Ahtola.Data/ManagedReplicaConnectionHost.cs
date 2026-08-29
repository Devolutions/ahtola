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
    /// stale data, potentially forever. The bootstrap therefore records the owed catch-up in the
    /// same durable publication that installs the base (see
    /// <c>ManagedReplicaBootstrapper.BootstrapAsync</c>), and the replica stays non-exposable
    /// until <c>RetireRequiredCatchUp</c> clears it. A crash anywhere in between is recoverable:
    /// this method resumes the owed catch-up on the already-installed base rather than
    /// re-downloading it, so nothing is replayed twice. Publication exclusivity additionally
    /// serializes concurrent first-time <c>Open()</c> calls for the same path so they cannot race
    /// each other's downloads, and if catch-up fails in the same call that performed the
    /// bootstrap, the whole (database, metadata) pair is rolled back so the next attempt retries a
    /// clean bootstrap+catch-up rather than observing a half-finished replica.
    /// </remarks>
    private static async Task EnsureReplicaAvailableAndCaughtUpAsync(
        ManagedReplicaSyncRegistry.Entry syncEntry,
        AhtolaReplicaOptions options,
        CancellationToken cancellationToken)
    {
        if (ManagedReplicaReplacementState.HasArtifacts(options.Path))
        {
            await PrepareExistingReplicaForOpenAsync(syncEntry, options.Path, cancellationToken)
                .ConfigureAwait(false);
        }
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
        var hasReplacementArtifacts = ManagedReplicaReplacementState.HasArtifacts(databasePath);
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(databasePath);
        var hasRecoveryArtifacts = ManagedReplicaRevertWal.GetArtifactPaths(databasePath).Any(File.Exists);
        if (metadata is null)
        {
            if (hasReplacementArtifacts || hasRecoveryArtifacts)
            {
                throw new InvalidDataException(
                    "Managed embedded replica recovery artifacts have no matching metadata.");
            }
            return;
        }
        if (!hasReplacementArtifacts
            && !metadata.Value.RevertState.HasValue
            && !hasRecoveryArtifacts)
            return;

        await syncEntry.PublishAsync(
                token => PrepareSynchronizationWithPublicationLocksAsync(databasePath, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task PrepareSynchronizationWithPublicationLocksAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var pushLease = await ManagedReplicaPushLock
            .AcquireExclusiveAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var applyLease = await ManagedReplicaApplyLock
            .AcquireExclusiveAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        ManagedReplicaReplacementState.Recover(databasePath);
        var current = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
                      ?? throw new InvalidDataException(
                          "Managed embedded replica checkpoint recovery metadata is missing.");
        _ = ManagedReplicaRevertWal.PrepareSynchronization(databasePath, current);
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

        // A replica whose marker still records an owed catch-up was durably installed by an
        // earlier attempt that crashed before finishing it. When its artifacts are all still
        // there, resuming means running only the missing catch-up: re-bootstrapping would
        // re-download an image that is already on disk, and the catch-up pull itself is a
        // resume-token request from the recorded revision, so repeating it applies only what has
        // not been applied yet. When they are not -- an interrupted rollback of an earlier failed
        // catch-up can leave the obligation recorded next to a dismantled replica -- this falls
        // through to a bootstrap, whose own recovery clears the residue and reinstalls cleanly.
        var resumingCatchUp = ManagedReplicaBootstrapper.CanResumeRequiredCatchUp(options.Path);
        if (!resumingCatchUp)
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
        catch when (!resumingCatchUp)
        {
            // Only a bootstrap this very call published gets rolled back. When resuming a
            // previously published one, the durable state predates this call and is still exactly
            // as safe as it was before it (installed, and non-exposable until the catch-up
            // succeeds); destroying it because of a transient failure would throw away a
            // recoverable replica -- and a full re-download with it -- for no safety gain, so that
            // exception simply propagates and the next open resumes again.
            await RollBackFailedCatchUpIfStillThisGenerationAsync(options, bootstrappedRevision)
                .ConfigureAwait(false);
            throw;
        }

        // The catch-up's own metadata is durable; only now may the replica become exposable.
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapCatchUpPublished);
        ManagedReplicaBootstrapper.RetireRequiredCatchUp(options.Path);
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
        ThrowIfReplicaConflictIsPending(options.Path);
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(options.Path);
        if (metadata is not { Protocol: RemotePullProtocol.MvccLogical } value)
            return;

        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.BootstrapCatchUpStarted);

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
        ThrowIfReplicaConflictIsPending(replicaOptions.Path);
        if (metadata.JournalBaseWatermark < _changeJournal.RetentionBase)
            metadata = metadata with { JournalBaseWatermark = _changeJournal.RetentionBase };
        metadata = ManagedReplicaBootstrapper.EnsureLegacyRemoteBaseSnapshot(
            replicaOptions.Path,
            metadata);

        // Push local changes, then (when a partial bootstrap is still catching up) complete the
        // partial image. Both can mutate the local database file directly, so both run gated --
        // every host on this path closed for their duration -- exactly as before the wait/apply
        // split below. Unlike before, that gate is now its own short publication window instead of
        // spanning the network wait for remote changes too. hasTrackedLocalChanges and the
        // retained materializer are read INSIDE PreparePushAndPartialReplicaAsync, once the gate
        // is actually held, not here: acquiring the gate is no longer instantaneous now that the
        // network wait for remote changes runs ungated, so an intervening publication (another
        // host's own sync, bootstrap catch-up, or partial-image completion) can advance the
        // journal or dispose/replace _materializationLease while this call is still waiting its
        // turn. Reading them here, before the gate, would risk handing PreparePushAndPartialReplicaAsync
        // a stale bool or an already-disposed file system.
        var (metadataAfterPush, pushedChangeCount) = await _syncEntry.PublishExclusiveAsync(
                token => PreparePushAndPartialReplicaAsync(replicaOptions, metadata, syncOptions, token),
                cancellationToken)
            .ConfigureAwait(false);
        metadata = metadataAfterPush;
        _metadata = metadata;

        var pendingLocalChanges = _changeJournal.ReadBatch(int.MaxValue).Changes;
        var acknowledgedLocalChanges = _changeJournal.ReadAcknowledged(metadata.JournalBaseWatermark);

        // Wait for remote changes and apply them, retrying (bounded, with backoff) when the staged
        // response turns out to be stale relative to local state. The wait itself runs entirely
        // outside any publication gate -- see ManagedReplicaBootstrapper.WaitAndApplyRemoteChangesAsync
        // -- while the apply runs gated, since it mutates the local database file. Mirrors Turso's
        // wait_changes_from_remote -> apply_changes_from_remote split
        // (turso-src/sync/engine/src/database_sync_engine.rs).
        var outcome = await ManagedReplicaBootstrapper.WaitAndApplyRemoteChangesAsync(
                replicaOptions, metadata, syncOptions, pendingLocalChanges, acknowledgedLocalChanges,
                (staged, token) => _syncEntry.PublishExclusiveAsync(
                    applyToken => ManagedReplicaBootstrapper.ApplyRemoteChangesAsync(
                        replicaOptions, staged, syncOptions, expectedConflictState: null, applyToken),
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        var result = outcome.Result;

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

    /// <summary>
    /// Runs the gated first half of one sync cycle: push local changes to the remote, then (when a
    /// partial bootstrap is still catching up) complete the partial image. Split out of
    /// <see cref="SynchronizeAsync"/> so it can be handed to
    /// <see cref="ManagedReplicaSyncRegistry.Entry.PublishExclusiveAsync{T}"/> as its own
    /// publication unit, distinct from the apply publication unit that follows the ungated wait
    /// for remote changes.
    /// </summary>
    /// <remarks>
    /// Reads <c>_materializationLease</c> and the change journal itself, rather than receiving them
    /// as parameters computed before the publication gate was requested: this method only ever runs
    /// once the gate is actually held, so these reads reflect whatever the most recent prior
    /// publication (another host's own sync, bootstrap catch-up, or partial-image completion) left
    /// in place. Reading them earlier, in <see cref="SynchronizeAsync"/> before the gate is even
    /// requested, could observe a retained materializer that an intervening publication then
    /// disposes before this operation gets its turn -- a use-after-dispose -- or a
    /// tracked-local-changes snapshot that is stale by the time it is actually acted upon.
    /// </remarks>
    private async Task<(ManagedReplicaBootstrapper.ManagedReplicaMetadata Metadata, long PushedChangeCount)>
        PreparePushAndPartialReplicaAsync(
            AhtolaReplicaOptions replicaOptions,
            ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata,
            AhtolaSyncOptions syncOptions,
            CancellationToken cancellationToken)
    {
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

        metadata = await ManagedReplicaBootstrapper
            .CompletePartialReplicaAsync(
                replicaOptions,
                metadata,
                allowTrackedLocalMutations: hasTrackedLocalChanges,
                retainedMaterializer,
                cancellationToken)
            .ConfigureAwait(false);

        return (metadata, push.ChangeCount);
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
        try
        {
            ReopenAfterPublicationCore();
        }
        catch
        {
            // Publication already disposed this host's database. If it cannot be reopened, the
            // public connection must not keep reporting ConnectionState.Open over a disposed
            // database: every command issued through it would fail with an opaque
            // ObjectDisposedException while State claimed the connection was usable. Clear the
            // adapter and transition the connection to Closed so the failure is visible in the
            // one place callers actually check, then rethrow the original cause.
            AbandonAfterFailedPublicationReopen();
            throw;
        }
    }

    /// <summary>
    /// Brings this host to a consistent closed state after a publication reopen failed: no
    /// database, no encryption file system, no materialization lease, and an attached public
    /// connection whose <see cref="System.Data.ConnectionState"/> reports
    /// <see cref="System.Data.ConnectionState.Closed"/>. Never throws: it runs inside a failure
    /// path and must not mask the original error.
    /// </summary>
    private void AbandonAfterFailedPublicationReopen()
    {
        try
        {
            Interlocked.Exchange(ref _database, null)?.Dispose();
        }
        catch
        {
            // Best effort: the adapter is already unusable.
        }

        try
        {
            Interlocked.Exchange(ref _encryptionFileSystem, null)?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            Interlocked.Exchange(ref _materializationLease, null)?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            Volatile.Read(ref _connection)?.InvalidateManagedReplicaDatabase();
        }
        catch
        {
            // Best effort.
        }
    }

    private void ReopenAfterPublicationCore()
    {
        var metadata = ManagedReplicaBootstrapper.LoadMetadata(_options.Path);
        if (metadata is { } value)
        {
            if (ManagedReplicaReplacementState.HasArtifacts(_options.Path)
                || value.RevertState is
                {
                    Phase: ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.Captured
                    or ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreCommitted
                    or ManagedReplicaBootstrapper.ManagedReplicaRevertPhase.RestoreOriginal,
                }
                || ManagedReplicaRevertWal.GetArtifactPaths(_options.Path).Any(File.Exists))
            {
                PrepareSynchronizationWithPublicationLocksAsync(_options.Path, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                value = ManagedReplicaBootstrapper.LoadMetadata(_options.Path)
                    ?? throw new InvalidDataException(
                        "Managed embedded replica checkpoint recovery metadata is missing.");
            }
            metadata = value;
            _metadata = value;
        }
        else if (ManagedReplicaReplacementState.HasArtifacts(_options.Path)
                 || ManagedReplicaRevertWal.GetArtifactPaths(_options.Path).Any(File.Exists))
        {
            throw new InvalidDataException(
                "Managed embedded replica recovery artifacts have no matching metadata.");
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
                var reopened = ManagedDatabaseAdapter.OpenFile(
                    _options.Path,
                    retainedLease!.FileSystem,
                    readOnly: false);
                _ = reopened.Connect();
                database = reopened;
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
            Volatile.Read(ref _connection)?.RestoreManagedReplicaDatabase(database);
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
        await using var pushFlight = await ManagedReplicaPushLock
            .AcquireExclusiveAsync(replicaOptions.Path, cancellationToken)
            .ConfigureAwait(false);
        ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushFlightLockAcquired);
        cancellationToken.ThrowIfCancellationRequested();

        // Every step below that mutates durable local state -- selecting the batch, publishing the
        // push intent, restoring the pre-push image, acknowledging the journal, and publishing the
        // conflict marker -- runs while the exclusive physical apply lease is held (see
        // ManagedReplicaApplyLock: keyed by physical file identity, so a symlink/junction/8.3-name
        // alias resolves to the same key, and backed by an OS byte-range lock, so a second process
        // serializes too). The network round trips are deliberately OUTSIDE the lease -- holding a
        // physical lock across unbounded remote I/O would let one stalled replica block every other
        // participant indefinitely. Releasing it means local state can move underneath this call,
        // so each re-acquisition re-validates the generation (metadata revision, revert phase, and
        // the journal's durable shape) it was negotiated against before publishing anything.
        ReplicaLocalChangeBatch batch;
        bool recoveringUnknownPush;
        ManagedReplicaBootstrapper.ManagedReplicaMetadata selectionMetadata;
        await using (await ManagedReplicaApplyLock
                         .AcquireExclusiveAsync(replicaOptions.Path, cancellationToken)
                         .ConfigureAwait(false))
        {
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushPublicationLockAcquired);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfReplicaConflictIsPending(replicaOptions.Path);
            metadata = ManagedReplicaBootstrapper.LoadMetadata(replicaOptions.Path)
                ?? throw new AhtolaException(
                    "Managed embedded replica metadata was removed before a push could begin.",
                    AhtolaReplicaPushFailureKind.InvalidLocalState);
            metadata = ManagedReplicaRevertWal.PrepareSynchronization(replicaOptions.Path, metadata);
            _metadata = metadata;
            _changeJournal = ManagedReplicaChangeJournal.Open(replicaOptions.Path);
            recoveringUnknownPush = metadata.PushState.HasValue;
            if (recoveringUnknownPush)
            {
                var state = metadata.PushState!.Value;
                batch = _changeJournal.ReadBatch(state.FirstSequence, state.Watermark);
            }
            else
            {
                var maximumChanges = metadata.RevertState.HasValue
                    ? int.MaxValue
                    : GetPushBatchLimit(replicaOptions);
                batch = _changeJournal.ReadBatch(maximumChanges);
            }

            // Proven before anything durable moves: the batch's watermark may only ever retire
            // rows this push actually transmits. A row orphaned from its statement (its SQL entry
            // was discarded after a conflict, for example) fails closed here instead of being
            // acknowledged as if the remote had received it.
            _changeJournal.ValidateBatchIsFullyReplayable(batch);

            if (batch.Changes.Count == 0)
            {
                var completedMetadata = ManagedReplicaRevertWal.CompletePreparedCheckpoint(
                    replicaOptions.Path,
                    metadata);
                _metadata = completedMetadata;
                return new LocalPushResult(0, completedMetadata);
            }

            selectionMetadata = metadata;
        }

        var generation = new PushPublicationGeneration(
            selectionMetadata.Revision,
            selectionMetadata.RevertState?.Phase,
            selectionMetadata.PushState,
            _changeJournal.Generation);

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

        var sourcePullGeneration = selectionMetadata.PushState?.SourcePullGeneration ?? 0;
        if (recoveringUnknownPush)
        {
            var remoteWatermark = await remote.ReadReplicaPushWatermarkAsync(
                    selectionMetadata.ClientId,
                    ToCommandTimeoutSeconds(replicaOptions.HttpPolicy.RequestTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (remoteWatermark is { } watermark)
            {
                if (watermark.PullGeneration != sourcePullGeneration)
                {
                    throw new AhtolaException(
                        watermark.PullGeneration > sourcePullGeneration
                            ? "Remote replica push acknowledgement is ahead of the local pull generation."
                            : "Remote replica push acknowledgement regressed behind the local pull generation.",
                        AhtolaReplicaPushFailureKind.InvalidLocalState);
                }
                if (watermark.ChangeId >= batch.Changes[^1].Sequence)
                {
                    return await PublishPushAcknowledgementAsync(
                            replicaOptions,
                            generation,
                            batch,
                            retainedMaterializer)
                        .ConfigureAwait(false);
                }
                if (watermark.ChangeId >= batch.FirstSequence)
                {
                    throw new AhtolaException(
                        "Remote replica push acknowledgement splits the pending local batch.",
                        AhtolaReplicaPushFailureKind.InvalidLocalState);
                }
            }
        }

        await using (await ManagedReplicaApplyLock
                         .AcquireExclusiveAsync(replicaOptions.Path, cancellationToken)
                         .ConfigureAwait(false))
        {
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushPublicationLockAcquired);
            cancellationToken.ThrowIfCancellationRequested();
            metadata = ValidatePushPublicationGeneration(replicaOptions.Path, generation, batch);
            metadata = ManagedReplicaRevertWal.MarkPushStarted(
                replicaOptions.Path,
                metadata,
                batch,
                sourcePullGeneration);
            _metadata = metadata;
            generation = generation with
            {
                RevertPhase = metadata.RevertState?.Phase,
                PushState = metadata.PushState,
            };
        }

        try
        {
            await remote.PushReplicaChangesAsync(
                    batch,
                    metadata.ClientId,
                    sourcePullGeneration,
                    ToCommandTimeoutSeconds(replicaOptions.HttpPolicy.RequestTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushRemoteCommitObserved);
        }
        catch (Exception exception) when (
            AhtolaReplicaPushFailure.Classify(exception) == AhtolaReplicaPushFailureKind.Conflict)
        {
            // Order matters and is deliberately "restore first, then record". The restore puts the
            // database back to the exact pre-push image; only then is the conflict marker
            // published. A crash in between therefore leaves NO marker, which is safe: the
            // ordinary ManagedReplicaRevertPhase recovery in PrepareSynchronization already
            // handles a half-restored bundle exactly as it does for any other conflict today, and
            // the still-unacknowledged journal means the next push simply re-attempts the same
            // batch and re-observes the same conflict. Recording first would instead risk a marker
            // that blocks synchronization while the database is still mid-restore.
            //
            // A replica without a pending checkpoint bundle (no revert state was captured for this
            // push) has nothing to restore, but its rejected batch is just as durable and must be
            // recorded all the same -- otherwise the page protocol would keep re-pushing a batch
            // the server already refused.
            //
            // CancellationToken.None for the lease: the rejection is already durable remote
            // knowledge, so the local evidence of it must be published even when the caller's token
            // fired during the round trip. Abandoning here would leave a replica that silently
            // re-pushes a batch the server has definitively refused.
            await using (await ManagedReplicaApplyLock
                             .AcquireExclusiveAsync(replicaOptions.Path, CancellationToken.None)
                             .ConfigureAwait(false))
            {
                ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushPublicationLockAcquired);
                var current = ValidatePushPublicationGeneration(replicaOptions.Path, generation, batch);
                if (current.RevertState.HasValue)
                {
                    ManagedReplicaRevertWal.RestorePendingCheckpoint(
                        replicaOptions.Path,
                        current,
                        CancellationToken.None);
                    _metadata = ManagedReplicaBootstrapper.LoadMetadata(replicaOptions.Path);
                }

                current = ManagedReplicaBootstrapper.LoadMetadata(replicaOptions.Path)
                    ?? throw new AhtolaException(
                        "Managed embedded replica metadata was removed while recording a push conflict.",
                        AhtolaReplicaPushFailureKind.InvalidLocalState);
                current = ManagedReplicaRevertWal.ClearPushIntent(replicaOptions.Path, current);
                _metadata = current;
                RecordPushConflict(replicaOptions.Path, batch, exception);
            }

            throw;
        }

        return await PublishPushAcknowledgementAsync(
                replicaOptions,
                generation,
                batch,
                retainedMaterializer)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The durable local state a push publication step was negotiated against. Re-validated on
    /// every re-acquisition of the physical apply lease, because the lease is deliberately released
    /// across the network round trip.
    /// </summary>
    private readonly record struct PushPublicationGeneration(
        string Revision,
        ManagedReplicaBootstrapper.ManagedReplicaRevertPhase? RevertPhase,
        ManagedReplicaBootstrapper.ManagedReplicaPushState? PushState,
        ReplicaJournalGeneration Journal);

    /// <summary>
    /// Durably acknowledges a remote-confirmed push while holding the exclusive physical apply
    /// lease. The generation recorded before the round trip is re-validated first; a batch another
    /// participant already acknowledged completes as a no-op rather than being acknowledged twice.
    /// </summary>
    /// <remarks>
    /// Deliberately uncancellable: the remote has already committed this batch, so failing to
    /// record that locally would re-push writes the server already holds. Cancellation is observed
    /// before this boundary, never inside it.
    /// </remarks>
    private async Task<LocalPushResult> PublishPushAcknowledgementAsync(
        AhtolaReplicaOptions replicaOptions,
        PushPublicationGeneration generation,
        ReplicaLocalChangeBatch batch,
        ManagedReplicaPageMaterializingFileSystem? retainedMaterializer)
    {
        await using (await ManagedReplicaApplyLock
                         .AcquireExclusiveAsync(replicaOptions.Path, CancellationToken.None)
                         .ConfigureAwait(false))
        {
            ManagedReplicaFaultInjection.Hit(ManagedReplicaDurableBoundary.ReplicaPushPublicationLockAcquired);
            if (TryObserveAlreadyAcknowledgedPush(replicaOptions.Path, batch, out var acknowledgedElsewhere))
            {
                _metadata = acknowledgedElsewhere;
                return new LocalPushResult(batch.Changes.Count, acknowledgedElsewhere);
            }

            var metadata = ValidatePushPublicationGeneration(replicaOptions.Path, generation, batch);
            _changeJournal.Acknowledge(batch.Watermark);
            var updatedMetadata = await ManagedReplicaBootstrapper
                .RecordLocalPushAsync(
                    replicaOptions,
                    metadata,
                    retainedMaterializer,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _metadata = updatedMetadata;
            return new LocalPushResult(batch.Changes.Count, updatedMetadata);
        }
    }

    /// <summary>
    /// Detects the one benign way local state may legitimately have moved while this push's lease
    /// was released: another participant (a second process, or a differently-aliased connection to
    /// the same physical replica) already acknowledged at least this batch. Re-acknowledging would
    /// persist a stale in-memory journal over theirs, so this adopts the durable journal and
    /// completes as a no-op instead.
    /// </summary>
    private bool TryObserveAlreadyAcknowledgedPush(
        string databasePath,
        ReplicaLocalChangeBatch batch,
        out ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata)
    {
        metadata = default;
        var durable = ManagedReplicaChangeJournal.Open(databasePath);
        if (durable.AcknowledgedWatermark < batch.Watermark
            || durable.Generation == _changeJournal.Generation)
        {
            return false;
        }

        metadata = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
            ?? throw new AhtolaException(
                "Managed embedded replica metadata was removed while a push was in flight.",
                AhtolaReplicaPushFailureKind.InvalidLocalState);
        _changeJournal = durable;
        return true;
    }

    /// <summary>
    /// Proves the durable local state still matches the generation this push was negotiated
    /// against, and returns the reloaded metadata. Anything else -- a revision that moved, a revert
    /// phase that changed, or a journal another writer advanced -- means publishing from this
    /// call's in-memory state would silently overwrite someone else's durable work, so it fails
    /// closed rather than guessing.
    /// </summary>
    private ManagedReplicaBootstrapper.ManagedReplicaMetadata ValidatePushPublicationGeneration(
        string databasePath,
        PushPublicationGeneration generation,
        ReplicaLocalChangeBatch batch)
    {
        var current = ManagedReplicaBootstrapper.LoadMetadata(databasePath)
            ?? throw new AhtolaException(
                "Managed embedded replica metadata was removed while a push was in flight.",
                AhtolaReplicaPushFailureKind.InvalidLocalState);
        var durable = ManagedReplicaChangeJournal.Open(databasePath);
        if (!string.Equals(current.Revision, generation.Revision, StringComparison.Ordinal)
            || current.RevertState?.Phase != generation.RevertPhase
            || current.PushState != generation.PushState
            || durable.Generation != generation.Journal)
        {
            throw new AhtolaException(
                "Managed embedded replica local state changed while a push of sequences "
                + $"[{batch.FirstSequence}, {batch.Watermark}) was in flight; the push was not "
                + "published locally. Synchronize again.",
                AhtolaReplicaPushFailureKind.InvalidLocalState);
        }

        return current;
    }

    /// <summary>
    /// Durably records a rejected push batch so no later synchronization can silently re-push it.
    /// Written after the database has already been restored to its pre-push image, and before the
    /// typed conflict exception reaches the caller.
    /// </summary>
    private static void RecordPushConflict(
        string databasePath,
        ReplicaLocalChangeBatch batch,
        Exception exception)
    {
        var conflict = exception as AhtolaReplicaConflictException;
        var entries = ManagedReplicaConflictClassifier.Classify(
            batch.Changes,
            conflict?.ConflictKind ?? AhtolaReplicaConflictKind.Unknown,
            conflict?.LocalChangeSequence);
        var unresolved = new List<long>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Eligibility != AhtolaReplicaChangeEligibility.Eligible)
                unresolved.Add(entry.Sequence);
        }

        if (unresolved.Count == 0)
        {
            // Classification always marks at least the rejected step itself unresolved, so this is
            // unreachable by construction. Fail closed rather than publish a marker that claims
            // nothing is wrong: quarantining the whole batch is the only safe fallback.
            foreach (var change in batch.Changes)
                unresolved.Add(change.Sequence);
        }

        ManagedReplicaConflictState.Write(
            databasePath,
            new ManagedReplicaConflictState(
                conflict?.ConflictKind ?? AhtolaReplicaConflictKind.Unknown,
                conflict?.RemoteErrorCode,
                conflict?.LocalChangeSequence,
                batch.FirstSequence,
                batch.Watermark,
                unresolved));
    }

    /// <summary>
    /// Fails closed while an unresolved push conflict is recorded. Mirrors
    /// <see cref="ManagedReplicaRevertWal.EnsureSynchronizationReady"/>'s refusal to start a new
    /// cycle while a checkpoint recovery bundle is pending: presence of the durable marker is
    /// authoritative, and a corrupt marker throws rather than being ignored.
    /// </summary>
    private static void ThrowIfReplicaConflictIsPending(string databasePath)
        => ManagedReplicaConflictState.ThrowIfPending(databasePath);

    /// <summary>
    /// Reads the open push conflict, if any, and classifies every change in the rejected batch.
    /// Pure read: no network access, no local mutation, and safe to call at any time.
    /// </summary>
    public Task<AhtolaReplicaConflictReport?> InspectReplicaConflictAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<AhtolaReplicaConflictReport?>(cancellationToken);

        try
        {
            using var operation = EnterLocalOperation(cancellationToken);
            if (ManagedReplicaConflictState.TryRead(_options.Path) is not { } state)
                return Task.FromResult<AhtolaReplicaConflictReport?>(null);

            var (batch, _, _) = ReadConflictBatch(_changeJournal, state);
            return Task.FromResult<AhtolaReplicaConflictReport?>(BuildReport(state, batch));
        }
        catch (Exception exception)
        {
            return Task.FromException<AhtolaReplicaConflictReport?>(exception);
        }
    }

    /// <summary>
    /// Applies an explicit caller-chosen resolution to the open push conflict.
    /// </summary>
    public Task<AhtolaReplicaConflictResolutionResult> ResolveReplicaConflictAsync(
        AhtolaReplicaConflictResolution resolution,
        AhtolaReplicaConflictResolutionOptions? options,
        CancellationToken cancellationToken)
    {
        if (resolution is not (AhtolaReplicaConflictResolution.PullAndRebaseEligible
            or AhtolaReplicaConflictResolution.DiscardUnresolvedChanges))
        {
            return Task.FromException<AhtolaReplicaConflictResolutionResult>(
                new ArgumentOutOfRangeException(nameof(resolution)));
        }
        if (resolution == AhtolaReplicaConflictResolution.DiscardUnresolvedChanges
            && options?.AcknowledgeDataLoss != true)
        {
            // Checked before any I/O: discarding drops locally committed writes the server will
            // never see, so it must never happen as a side effect of a default-constructed option.
            return Task.FromException<AhtolaReplicaConflictResolutionResult>(
                new InvalidOperationException(
                    "Discarding unresolved managed replica changes permanently drops locally committed "
                    + "writes; set AhtolaReplicaConflictResolutionOptions.AcknowledgeDataLoss to proceed."));
        }
        if (_metadata is null)
        {
            return Task.FromException<AhtolaReplicaConflictResolutionResult>(
                new NotSupportedException(
                    "Managed embedded replica conflict resolution requires bootstrap metadata."));
        }

        return _syncEntry.PublishExclusiveAsync(
            token => ResolveConflictAsync(resolution, options, token),
            cancellationToken);
    }

    private async Task<AhtolaReplicaConflictResolutionResult> ResolveConflictAsync(
        AhtolaReplicaConflictResolution resolution,
        AhtolaReplicaConflictResolutionOptions? options,
        CancellationToken cancellationToken)
    {
        ManagedReplicaConflictState state;
        await using (await ManagedReplicaApplyLock
                         .AcquireExclusiveAsync(_options.Path, cancellationToken)
                         .ConfigureAwait(false))
        {
            ManagedReplicaFaultInjection.Hit(
                ManagedReplicaDurableBoundary.ReplicaConflictResolutionLockAcquired);

            // Cancellation is free of consequence right here and nowhere later in the discard
            // path: everything below this point either publishes durable state or completes a
            // publication that already landed.
            cancellationToken.ThrowIfCancellationRequested();

            state = ManagedReplicaConflictState.TryRead(_options.Path)
                ?? throw new InvalidOperationException(
                    "Managed embedded replica has no unresolved push conflict to resolve.");

            // Read the journal as it is on disk, not as this connection last saw it: another
            // process, or a differently-aliased connection to the same physical replica, may have
            // completed the discard while this one was idle. The exclusive physical apply lease is
            // held, so the durable state observed here cannot move under us.
            var journal = ManagedReplicaChangeJournal.Open(_options.Path);
            _changeJournal = journal;
            var evidence = ReadConflictBatch(journal, state);
            if (evidence.UnresolvedStillJournaled == 0)
            {
                // Idempotent completion of an interrupted discard. ReadConflictBatch has already
                // proven that every one of the marker's unresolved sequences is durably recorded as
                // an explicit discard -- that record is the ONLY thing distinguishing "the discard
                // already landed and the process stopped before the marker was retired" from "the
                // evidence is missing or corrupt", and the latter fails closed above with the
                // marker deliberately left in place rather than silently unblocking synchronization.
                ManagedReplicaConflictState.Delete(_options.Path);
                return new AhtolaReplicaConflictResolutionResult(
                    resolution,
                    conflictCleared: true,
                    rebasedChangeCount: 0,
                    discardedChangeCount: 0,
                    remainingConflict: null,
                    syncResult: null);
            }

            if (resolution == AhtolaReplicaConflictResolution.DiscardUnresolvedChanges)
                return DiscardUnresolvedConflictChanges(journal, state, evidence);
        }

        return await RebaseEligibleConflictChangesAsync(state, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the still-unresolved journal entries. No network access and no metadata change: the
    /// journal watermark deliberately stays put so this can never be mistaken for a remote
    /// acknowledgement, and each discarded sequence is durably recorded so the resulting hole is
    /// provable evidence rather than an unexplained absence. The marker is only retired after the
    /// journal's own atomic replace is durable, so a crash in between simply leaves the conflict
    /// open and the retry idempotent. Callers reach this only after
    /// <see cref="ResolveConflictAsync"/> has already proven, under the exclusive physical apply
    /// lease, that the marker and journal agree.
    /// </summary>
    /// <remarks>
    /// Deliberately uncancellable. Cancellation is checked once, before this boundary, in
    /// <see cref="ResolveConflictAsync"/>; from the moment the journal replace is durable the
    /// resolution has irreversibly happened, so retiring the marker always completes rather than
    /// leaving a marker whose sequences no longer exist.
    /// </remarks>
    private AhtolaReplicaConflictResolutionResult DiscardUnresolvedConflictChanges(
        ManagedReplicaChangeJournal journal,
        ManagedReplicaConflictState state,
        ConflictBatchEvidence evidence)
    {
        // Only the sequences still retained are discarded. Any already recorded as discarded were
        // proven so by ReadConflictBatch, so re-requesting them would fail closed on state that is
        // actually consistent instead of converging.
        var stillRetained = new List<long>(evidence.UnresolvedStillJournaled);
        foreach (var sequence in state.UnresolvedSequences)
        {
            if (!journal.WasDiscarded(sequence))
                stillRetained.Add(sequence);
        }

        var discarded = journal.DiscardUnacknowledged(stillRetained);
        ManagedReplicaConflictState.Delete(_options.Path);
        return new AhtolaReplicaConflictResolutionResult(
            AhtolaReplicaConflictResolution.DiscardUnresolvedChanges,
            conflictCleared: true,
            rebasedChangeCount: 0,
            discardedChangeCount: discarded,
            remainingConflict: null,
            syncResult: null);
    }

    /// <summary>
    /// Pulls a fresh remote base and replays only the provably eligible journaled changes onto it,
    /// through the ordinary transactional logical apply and its existing compensation. The
    /// unresolved changes stay journaled and quarantined, and the marker is deliberately retained
    /// so ordinary synchronization stays blocked until they are explicitly resolved or discarded.
    /// </summary>
    private async Task<AhtolaReplicaConflictResolutionResult> RebaseEligibleConflictChangesAsync(
        ManagedReplicaConflictState state,
        AhtolaReplicaConflictResolutionOptions? options,
        CancellationToken cancellationToken)
    {
        var (batch, _, _) = ReadConflictBatch(_changeJournal, state);
        var quarantined = new HashSet<long>(state.UnresolvedSequences);
        foreach (var change in batch)
        {
            if (quarantined.Contains(change.Sequence) && change.Kind == ReplicaLocalChangeKind.Schema)
            {
                // A quarantined DDL statement has already been applied to the local catalog, so
                // rebuilding the image from the remote base would either silently drop the object
                // or replay a statement whose fate is undecided. Neither is a rebase; fail closed.
                throw new NotSupportedException(
                    "Managed embedded replica cannot rebase while an unresolved local schema change is "
                    + "quarantined. Reconcile the schema explicitly, then discard the conflicting "
                    + "changes.");
            }
        }

        var metadata = ManagedReplicaBootstrapper.LoadMetadata(_options.Path)
            ?? throw new NotSupportedException(
                "Managed embedded replica conflict resolution requires bootstrap metadata.");
        if (metadata.Protocol != RemotePullProtocol.MvccLogical)
        {
            // A page-protocol replica has no per-operation replay mechanism at all: a raw page
            // patch cannot rebase journaled SQL, and a whole-image replacement cannot hold a
            // quarantined subset back. Fail closed before spending a request, rather than
            // discovering it after the round trip.
            throw new NotSupportedException(
                "Managed embedded replica conflict rebase requires the MVCC logical pull protocol; a "
                + "page-protocol replica cannot replay individual journaled changes. Discard the "
                + "conflicting changes explicitly instead.");
        }

        IReadOnlyList<ReplicaLocalChange> pendingLocalChanges;
        IReadOnlyList<ReplicaLocalChange> acknowledgedLocalChanges;

        // The revert-WAL transitions below are durable local publications, so they run under the
        // exclusive physical apply lease. It is released again before CheckForUpdatesAsync, which
        // acquires the very same (non-reentrant) lease internally around its own apply.
        await using (await ManagedReplicaApplyLock
                         .AcquireExclusiveAsync(_options.Path, cancellationToken)
                         .ConfigureAwait(false))
        {
            ManagedReplicaFaultInjection.Hit(
                ManagedReplicaDurableBoundary.ReplicaConflictResolutionLockAcquired);
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata.JournalBaseWatermark < _changeJournal.RetentionBase)
                metadata = metadata with { JournalBaseWatermark = _changeJournal.RetentionBase };
            metadata = ManagedReplicaBootstrapper.EnsureLegacyRemoteBaseSnapshot(_options.Path, metadata);

            // Same two steps the push path takes when it has nothing to push: settle any recovery
            // bundle a previous protected apply left behind, then complete it, so the revert-WAL
            // state machine is entered and left exactly as an ordinary sync would. No push is
            // attempted and no phase is skipped or invented.
            metadata = ManagedReplicaRevertWal.PrepareSynchronization(_options.Path, metadata);
            metadata = ManagedReplicaRevertWal.CompletePreparedCheckpoint(_options.Path, metadata);
            _metadata = metadata;

            pendingLocalChanges = _changeJournal.ReadBatch(int.MaxValue).Changes;
            acknowledgedLocalChanges = _changeJournal.ReadAcknowledged(metadata.JournalBaseWatermark);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pull = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                _options,
                metadata,
                new AhtolaSyncOptions(options?.Progress),
                pendingLocalChanges,
                acknowledgedLocalChanges,
                state,
                cancellationToken)
            .ConfigureAwait(false);

        _metadata = ManagedReplicaBootstrapper.LoadMetadata(_options.Path);
        if (pull.Result.Outcome == AhtolaSyncOutcome.RemoteChangesApplied && _metadata is { } published)
            _changeJournal.PruneAcknowledged(published.JournalBaseWatermark);

        // The marker is NOT deleted here. The eligible changes were replayed locally but never
        // pushed, and every unresolved change is still journaled and quarantined, so
        // synchronization must stay blocked. Rewriting the marker with the same still-unresolved
        // set keeps the durable evidence exact and keeps a retry idempotent.
        var remaining = ManagedReplicaConflictState.TryRead(_options.Path)
            ?? throw new InvalidDataException(
                "Managed embedded replica conflict marker disappeared during resolution.");
        var (remainingBatch, _, _) = ReadConflictBatch(_changeJournal, remaining);
        return new AhtolaReplicaConflictResolutionResult(
            AhtolaReplicaConflictResolution.PullAndRebaseEligible,
            conflictCleared: false,
            // Exactly what the apply replayed onto the rebuilt base -- never a count derived from
            // the journal before the apply decided whether, and how, to run.
            rebasedChangeCount: pull.ReplayedLocalChangeCount,
            discardedChangeCount: 0,
            BuildReport(remaining, remainingBatch),
            pull.Result);
    }

    /// <summary>
    /// Reads exactly the journaled changes the marker still refers to, how many of the recorded
    /// unresolved sequences are still retained, and how many are durably recorded as explicitly
    /// discarded. A marker that names only <em>some</em> of its unresolved sequences is
    /// inconsistent with the journal's atomic replace and fails closed rather than being
    /// reinterpreted against whatever happens to be there now.
    /// </summary>
    private static ConflictBatchEvidence ReadConflictBatch(
        ManagedReplicaChangeJournal journal,
        ManagedReplicaConflictState state)
    {
        var pending = journal.ReadBatch(int.MaxValue).Changes;
        var batch = new List<ReplicaLocalChange>(pending.Count);
        foreach (var change in pending)
        {
            if (change.Sequence >= state.BatchFirstSequence && change.Sequence < state.BatchWatermark)
                batch.Add(change);
        }

        var found = 0;
        foreach (var change in batch)
        {
            if (state.UnresolvedSequences.Contains(change.Sequence))
                found++;
        }

        var provablyDiscarded = 0;
        foreach (var sequence in state.UnresolvedSequences)
        {
            if (journal.WasDiscarded(sequence))
                provablyDiscarded++;
        }

        // Every unresolved sequence the marker names must still be accounted for, either because
        // it is retained or because the journal durably recorded it as an explicit discard. A
        // sequence that is neither is missing evidence: the marker and the journal genuinely
        // disagree and must never be reinterpreted against whatever the journal happens to hold
        // now -- in particular, "it is simply gone" must never be read as "the discard landed".
        if (found + provablyDiscarded != state.UnresolvedSequences.Count)
        {
            throw new InvalidDataException(
                "Managed embedded replica conflict marker references journal changes that are neither "
                + "retained nor recorded as discarded; the recorded conflict and the change journal are "
                + "inconsistent.");
        }

        return new ConflictBatchEvidence(batch, found, provablyDiscarded);
    }

    private readonly record struct ConflictBatchEvidence(
        IReadOnlyList<ReplicaLocalChange> Batch,
        int UnresolvedStillJournaled,
        int UnresolvedProvablyDiscarded);

    private static AhtolaReplicaConflictReport BuildReport(
        ManagedReplicaConflictState state,
        IReadOnlyList<ReplicaLocalChange> batch)
    {
        var classified = ManagedReplicaConflictClassifier.Classify(
            batch,
            state.ConflictKind,
            state.ConflictingSequence);

        // The durable marker, not a re-run of the classifier, is authoritative about what is still
        // unresolved: a previous resolution may already have narrowed the set, and the classifier
        // alone cannot know that. Re-classification only supplies each entry's kind/target.
        var unresolved = new HashSet<long>(state.UnresolvedSequences);
        var entries = new AhtolaReplicaConflictEntry[classified.Count];
        for (var i = 0; i < classified.Count; i++)
        {
            var entry = classified[i];
            var eligibility = unresolved.Contains(entry.Sequence)
                ? entry.Eligibility == AhtolaReplicaChangeEligibility.Eligible
                    ? AhtolaReplicaChangeEligibility.RequiresManualResolution
                    : entry.Eligibility
                : AhtolaReplicaChangeEligibility.Eligible;
            entries[i] = new AhtolaReplicaConflictEntry(
                entry.Sequence,
                entry.Kind,
                entry.Table,
                entry.RowId,
                eligibility);
        }

        return new AhtolaReplicaConflictReport(
            state.ConflictKind,
            state.RemoteErrorCode,
            state.ConflictingSequence,
            state.BatchFirstSequence,
            state.BatchWatermark,
            entries);
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
