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
            try
            {
                completion.TrySetResult(
                    await PublishAsync(stagedOperation, cancellationToken, clearInFlightSync: true)
                        .ConfigureAwait(false));
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
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
                cancellationToken,
                clearInFlightSync: false);
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
            return PublishAsync(stagedOperation, cancellationToken, clearInFlightSync: false);
        }

        private async Task<T> PublishAsync<T>(
            Func<CancellationToken, Task<T>> stagedOperation,
            CancellationToken cancellationToken,
            bool clearInFlightSync)
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
                        if (clearInFlightSync)
                            _inFlightSync = null;
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
                || _publicationActive)
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
