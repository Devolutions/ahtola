using System.Diagnostics;

namespace Ahtola.Core.Storage;

/// <summary>
/// A fair exclusive gate with independent synchronous and asynchronous wait
/// paths. The returned lease, not a thread, owns the gate.
/// </summary>
internal sealed class AsyncFifoGate : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private bool _held;
    private bool _disposed;

    internal Lease Enter(TimeSpan timeout = default)
    {
        ValidateTimeout(timeout);
        var stopwatch = timeout == Timeout.InfiniteTimeSpan ? null : Stopwatch.StartNew();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_held && _waiters.Count == 0)
            {
                _held = true;
                return new Lease(this);
            }

            var waiter = new Waiter();
            _waiters.Enqueue(waiter);
            try
            {
                while (!waiter.Signaled)
                {
                    ThrowIfDisposed();
                    var remaining = RemainingTimeout(timeout, stopwatch);
                    if (remaining == TimeSpan.Zero)
                        throw new TimeoutException("The asynchronous FIFO gate acquisition timed out.");
                    Monitor.Wait(_gate, remaining);
                }

                return new Lease(this);
            }
            catch
            {
                AbandonLocked(waiter);
                throw;
            }
        }
    }

    internal async ValueTask<Lease> EnterAsync(
        TimeSpan timeout = default,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_held && _waiters.Count == 0)
            {
                _held = true;
                return new Lease(this);
            }

            waiter = new Waiter(async: true);
            _waiters.Enqueue(waiter);
        }

        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await waiter.SignalTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await waiter.SignalTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }

            return new Lease(this);
        }
        catch
        {
            lock (_gate)
                AbandonLocked(waiter);
            throw;
        }
    }

    public void Dispose()
    {
        TaskCompletionSource[] asyncWaiters;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            asyncWaiters = _waiters
                .Where(static waiter => waiter.SignalSource is not null)
                .Select(static waiter => waiter.SignalSource!)
                .ToArray();
            foreach (var waiter in _waiters)
                waiter.Removed = true;
            _waiters.Clear();
            Monitor.PulseAll(_gate);
        }

        foreach (var waiter in asyncWaiters)
            waiter.TrySetException(new ObjectDisposedException(nameof(AsyncFifoGate)));
    }

    private void Release()
    {
        TaskCompletionSource? asyncWaiter = null;
        lock (_gate)
        {
            if (!_held)
                throw new InvalidOperationException("The asynchronous FIFO gate lease was already released.");

            while (_waiters.Count > 0 && _waiters.Peek().Removed)
                _waiters.Dequeue();

            if (_disposed || _waiters.Count == 0)
            {
                _held = false;
                return;
            }

            var waiter = _waiters.Dequeue();
            waiter.Removed = true;
            waiter.Signaled = true;
            asyncWaiter = waiter.SignalSource;
            Monitor.PulseAll(_gate);
        }

        asyncWaiter?.TrySetResult();
    }

    private void AbandonLocked(Waiter waiter)
    {
        if (waiter.Signaled)
        {
            ReleaseHandedOffLocked();
            return;
        }

        waiter.Removed = true;
        while (_waiters.Count > 0 && _waiters.Peek().Removed)
            _waiters.Dequeue();
    }

    private void ReleaseHandedOffLocked()
    {
        while (_waiters.Count > 0 && _waiters.Peek().Removed)
            _waiters.Dequeue();

        if (_disposed || _waiters.Count == 0)
        {
            _held = false;
            return;
        }

        var next = _waiters.Dequeue();
        next.Removed = true;
        next.Signaled = true;
        Monitor.PulseAll(_gate);
        next.SignalSource?.TrySetResult();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AsyncFifoGate));
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative or infinite.");
    }

    private static TimeSpan RemainingTimeout(TimeSpan timeout, Stopwatch? stopwatch)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var remaining = timeout - stopwatch!.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return remaining > TimeSpan.FromMilliseconds(int.MaxValue)
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : remaining;
    }

    private sealed class Waiter
    {
        internal Waiter(bool async = false)
        {
            if (async)
                SignalSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal TaskCompletionSource? SignalSource { get; }

        internal Task SignalTask => SignalSource?.Task
            ?? throw new InvalidOperationException("A synchronous gate waiter has no asynchronous signal.");

        internal bool Signaled { get; set; }

        internal bool Removed { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private AsyncFifoGate? _owner;

        internal Lease(AsyncFifoGate owner) => _owner = owner;

        internal bool IsActive => Volatile.Read(ref _owner) is not null;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
