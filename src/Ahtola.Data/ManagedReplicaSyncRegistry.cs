using System.Collections.Concurrent;

namespace Ahtola;

/// <summary>
/// Coordinates managed embedded-replica publication for one normalized database path.
/// Local work holds a shared lease; publication waits for those leases and then closes
/// and reopens every registered host while no local work can begin.
/// </summary>
internal static class ManagedReplicaSyncRegistry
{
    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static Entry Acquire(string path)
    {
        var normalizedPath = NormalizePath(path);
        while (true)
        {
            var entry = Entries.GetOrAdd(normalizedPath, static key => new Entry(key));
            if (entry.TryAddReference())
                return entry;
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal sealed class Entry
    {
        private readonly object _gate = new();
        private readonly string _path;
        private readonly HashSet<ManagedReplicaConnectionHost> _hosts = [];
        private TaskCompletionSource _stateChanged = NewStateChangedSource();
        private Task<AhtolaSyncResult>? _inFlightSync;
        private int _references;
        private int _activeLocalOperations;
        private bool _publicationPending;
        private bool _publicationActive;
        private bool _retired;

        internal Entry(string path)
        {
            _path = path;
        }

        internal bool TryAddReference()
        {
            lock (_gate)
            {
                if (_retired)
                    return false;

                _references++;
                return true;
            }
        }

        public void ReleaseReference()
        {
            lock (_gate)
            {
                if (_references == 0)
                    return;

                _references--;
                RetireIfUnusedNoLock();
            }
        }

        public void Register(ManagedReplicaConnectionHost host)
        {
            ArgumentNullException.ThrowIfNull(host);
            lock (_gate)
                _hosts.Add(host);
        }

        public void Unregister(ManagedReplicaConnectionHost host)
        {
            ArgumentNullException.ThrowIfNull(host);
            lock (_gate)
            {
                _hosts.Remove(host);
                RetireIfUnusedNoLock();
            }
        }

        public IDisposable EnterLocalOperation(CancellationToken cancellationToken)
            => EnterLocalOperationAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

        public async ValueTask<IDisposable> EnterLocalOperationAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task stateChanged;
                lock (_gate)
                {
                    if (!_publicationPending && !_publicationActive)
                    {
                        _activeLocalOperations++;
                        return new LocalOperationLease(this);
                    }

                    stateChanged = _stateChanged.Task;
                }

                await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<AhtolaSyncResult> SynchronizeAsync(
            ManagedReplicaConnectionHost initiator,
            Func<CancellationToken, Task<AhtolaSyncResult>> stagedOperation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(initiator);
            ArgumentNullException.ThrowIfNull(stagedOperation);

            Task<AhtolaSyncResult> syncTask;
            lock (_gate)
            {
                if (_inFlightSync is null)
                {
                    var completion = new TaskCompletionSource<AhtolaSyncResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlightSync = completion.Task;
                    syncTask = completion.Task;
                    _ = CompleteSyncAsync(completion, stagedOperation, cancellationToken);
                }
                else
                {
                    syncTask = _inFlightSync;
                }
            }

            return WaitForSynchronizationAsync(syncTask, cancellationToken);
        }

        private static async Task<AhtolaSyncResult> WaitForSynchronizationAsync(
            Task<AhtolaSyncResult> syncTask,
            CancellationToken cancellationToken)
        {
            try
            {
                return await syncTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Publication temporarily closes every host. A cancelled observer must
                // wait for its finally block to reopen them before its caller resumes.
                try
                {
                    await syncTask.ConfigureAwait(false);
                }
                catch
                {
                    // The caller observes cancellation below; publication's result has
                    // served its purpose by completing all reopen work.
                }

                throw;
            }
        }

        private async Task CompleteSyncAsync(
            TaskCompletionSource<AhtolaSyncResult> completion,
            Func<CancellationToken, Task<AhtolaSyncResult>> stagedOperation,
            CancellationToken cancellationToken)
        {
            AhtolaSyncResult? result = null;
            Exception? failure = null;
            var canceled = false;
            try
            {
                // Unlike PublishAsync/PublishExclusiveAsync, this deliberately does NOT itself wrap
                // stagedOperation in one publication window: SyncAsync's staged operation runs its
                // network-bound phases (push, then the long-poll wait for remote changes) entirely
                // without any publication gate held, and calls PublishExclusiveAsync itself --
                // possibly more than once -- only around the short local-mutating phases (push's
                // own rare conflict-restore branch; the actual apply). Coalescing concurrent
                // SyncAsync callers for this path into one shared in-flight task is this method's
                // only job; deciding when to gate is entirely the staged operation's business.
                result = await stagedOperation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                canceled = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            // Clear the in-flight slot BEFORE completing the TaskCompletionSource below.
            // TrySetResult/TrySetException/TrySetCanceled make completion.Task observably complete
            // (IsCompleted becomes true synchronously, even though continuations queued with
            // RunContinuationsAsynchronously run later) the instant they are called. If
            // _inFlightSync stayed pointing at that task past this point, a SyncAsync call
            // arriving in the resulting window would see a non-null _inFlightSync, join it via the
            // read in SynchronizeAsync, and receive this ALREADY-COMPLETED result without
            // performing any new work at all -- silently skipping a sync its caller explicitly
            // requested. The identity check guards against a stale write if this slot were ever
            // touched from elsewhere. Retirement is re-checked here too: with RetireIfUnusedNoLock
            // now refusing to retire while _inFlightSync is set, clearing it may be the very last
            // condition standing between an already-unreferenced entry and retirement.
            lock (_gate)
            {
                if (ReferenceEquals(_inFlightSync, completion.Task))
                    _inFlightSync = null;
                SignalStateChangedNoLock();
                RetireIfUnusedNoLock();
            }

            if (canceled)
                completion.TrySetCanceled(cancellationToken);
            else if (failure is not null)
                completion.TrySetException(failure);
            else
                completion.TrySetResult(result!);
        }

        public Task PublishAsync(
            Func<CancellationToken, Task> stagedOperation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stagedOperation);
            return PublishAsync(
                async token =>
                {
                    await stagedOperation(token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken);
        }

        /// <summary>
        /// Runs <paramref name="stagedOperation"/> as one exclusive publication unit and returns its
        /// result. Unlike <see cref="SynchronizeAsync"/> this never coalesces with an in-flight sync:
        /// callers such as explicit conflict resolution choose a specific action, so joining someone
        /// else's already-running operation and reporting its result would be wrong.
        /// </summary>
        public Task<T> PublishExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> stagedOperation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stagedOperation);
            return PublishAsync(stagedOperation, cancellationToken);
        }

        private async Task<T> PublishAsync<T>(
            Func<CancellationToken, Task<T>> stagedOperation,
            CancellationToken cancellationToken)
        {
            var request = new PublicationRequest();
            try
            {
                await EnterPublicationAsync(request, cancellationToken).ConfigureAwait(false);
                ManagedReplicaFaultInjection.Hit(
                    ManagedReplicaDurableBoundary.ReplicaPublicationOwnershipAcquired);

                // The last consequence-free cancellation point of a publication: ownership is
                // held, no host has been closed, and nothing durable has been touched. Observing
                // cancellation here means the caller sees OperationCanceledException with the
                // replica bit-for-bit unchanged. Past this line the staged operation defines its
                // own irreversible boundaries and every host must be reopened before returning,
                // so cancellation is no longer free.
                cancellationToken.ThrowIfCancellationRequested();

                ManagedReplicaConnectionHost[] hosts;
                lock (_gate)
                    hosts = _hosts.ToArray();

                foreach (var host in hosts)
                    host.CloseForPublication();

                try
                {
                    return await stagedOperation(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Exception? reopenError = null;
                    foreach (var host in hosts)
                    {
                        try
                        {
                            host.ReopenAfterPublication();
                        }
                        catch (Exception exception)
                        {
                            reopenError ??= exception;
                        }
                    }

                    if (reopenError is not null)
                        throw reopenError;
                }
            }
            finally
            {
                if (request.OwnsPublication)
                {
                    lock (_gate)
                    {
                        _publicationActive = false;
                        _publicationPending = false;
                        SignalStateChangedNoLock();
                        RetireIfUnusedNoLock();
                    }
                }
            }
        }

        private async Task EnterPublicationAsync(
            PublicationRequest request,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                Task stateChanged;
                lock (_gate)
                {
                    if (_retired)
                    {
                        // Defense in depth: RetireIfUnusedNoLock now refuses to retire while
                        // _inFlightSync is set, so an entry with a sync (push/wait/apply cycle)
                        // still in flight should never reach retirement in the first place, and no
                        // publication should ever observe _retired here. If it ever does, some
                        // other path let an in-flight operation survive past this entry's removal
                        // from the registry -- failing loudly is far safer than silently closing
                        // and reopening zero coordinated hosts and running the staged operation
                        // (e.g. an apply that mutates the physical file) with no protection against
                        // a brand-new, unrelated Entry that may now be governing the very same
                        // physical replica path.
                        throw new InvalidOperationException(
                            "Managed embedded replica sync coordination entry was already retired and "
                            + "cannot accept new publication work; an operation raced a retired registry "
                            + "entry and must not proceed.");
                    }

                    if (!request.OwnsPublication && !_publicationPending && !_publicationActive)
                    {
                        _publicationPending = true;
                        request.OwnsPublication = true;
                        SignalStateChangedNoLock();
                    }

                    if (request.OwnsPublication && _activeLocalOperations == 0 && !_publicationActive)
                    {
                        _publicationActive = true;
                        return;
                    }

                    stateChanged = _stateChanged.Task;
                }

                await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void ExitLocalOperation()
        {
            lock (_gate)
            {
                _activeLocalOperations--;
                SignalStateChangedNoLock();
                RetireIfUnusedNoLock();
            }
        }

        private void RetireIfUnusedNoLock()
        {
            if (_retired
                || _references != 0
                || _hosts.Count != 0
                || _activeLocalOperations != 0
                || _publicationPending
                || _publicationActive
                || _inFlightSync is not null)
            {
                return;
            }

            _retired = true;
            if (Entries.TryGetValue(_path, out var current) && ReferenceEquals(current, this))
                Entries.TryRemove(_path, out _);
        }

        private void SignalStateChangedNoLock()
        {
            var previous = _stateChanged;
            _stateChanged = NewStateChangedSource();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewStateChangedSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class LocalOperationLease(Entry entry) : IDisposable
        {
            private Entry? _entry = entry;

            public void Dispose()
            {
                var entry = Interlocked.Exchange(ref _entry, null);
                entry?.ExitLocalOperation();
            }
        }

        private sealed class PublicationRequest
        {
            public bool OwnsPublication { get; set; }
        }
    }
}
