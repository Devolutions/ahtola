using System.Runtime.Versioning;
using Ahtola.Core;

namespace Ahtola.Data.Sqlite.Browser;

[SupportedOSPlatform("browser")]
internal sealed class BrowserInMemoryManagedDatabaseAdapter(
    IManagedDatabaseAdapter inner,
    Action released) : IManagedDatabaseAdapter
{
    private int _disposed;

    public IManagedConnectionAdapter Connect()
        => throw BrowserManagedAdapterErrors.SyncNotSupported("connecting");

    public async ValueTask<IManagedConnectionAdapter> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public IManagedConnectionAdapter Connection
    {
        get
        {
            ThrowIfDisposed();
            return inner.Connection;
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        throw BrowserManagedAdapterErrors.SyncNotSupported("disposing the in-memory database");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            released();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
