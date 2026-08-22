namespace Ahtola;

internal sealed class CommandCancellationController
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeOperation;

    public void Cancel()
    {
        lock (_gate)
            _activeOperation?.Cancel();
    }

    public T Run<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = Begin(cancellationToken);
        lease.Token.ThrowIfCancellationRequested();
        return operation(lease.Token);
    }

    public Task<T> RunAsync<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        return RunSyncAsync(operation, cancellationToken);
    }

    public Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        return RunTaskAsync(operation, cancellationToken);
    }

    public Task<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        return RunValueTaskAsync(operation, cancellationToken);
    }

    private async Task<T> RunTaskAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var lease = Begin(cancellationToken);
        lease.Token.ThrowIfCancellationRequested();
        return await operation(lease.Token).ConfigureAwait(false);
    }

    private async Task<T> RunValueTaskAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var lease = Begin(cancellationToken);
        lease.Token.ThrowIfCancellationRequested();
        try
        {
            return await operation(lease.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async Task<T> RunSyncAsync<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        using var lease = Begin(cancellationToken);
        lease.Token.ThrowIfCancellationRequested();
        return await Task.Run(
                () => operation(lease.Token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private OperationLease Begin(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_activeOperation is not null)
                throw new InvalidOperationException("The command already has an operation in progress.");

            var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeOperation = operation;
            return new OperationLease(this, operation);
        }
    }

    private void End(CancellationTokenSource operation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeOperation, operation))
                _activeOperation = null;
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private CommandCancellationController? _owner;
        private CancellationTokenSource? _operation;

        public OperationLease(CommandCancellationController owner, CancellationTokenSource operation)
        {
            _owner = owner;
            _operation = operation;
        }

        public CancellationToken Token
            => _operation?.Token ?? throw new ObjectDisposedException(nameof(OperationLease));

        public void Dispose()
        {
            var operation = Interlocked.Exchange(ref _operation, null);
            if (operation is null)
                return;

            Interlocked.Exchange(ref _owner, null)?.End(operation);
            operation.Dispose();
        }
    }
}
