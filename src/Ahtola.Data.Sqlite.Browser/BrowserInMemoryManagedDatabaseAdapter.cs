using System.Runtime.Versioning;
using Ahtola.Core;

namespace Ahtola.Data.Sqlite.Browser;

[SupportedOSPlatform("browser")]
internal sealed class BrowserInMemoryManagedDatabaseAdapter(
    IManagedDatabaseAdapter inner,
    Action released,
    bool allowSynchronousTeardown = false) : IManagedDatabaseAdapter
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

    /// <summary>
    /// Disposes the database. An in-memory browser database has no persistent
    /// store to settle, so a data source opted into synchronous read-mirror mode
    /// may tear it down synchronously; otherwise the async-only contract holds.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (!allowSynchronousTeardown)
            throw BrowserManagedAdapterErrors.SyncNotSupported("disposing the in-memory database");
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            inner.Dispose();
        }
        finally
        {
            released();
        }
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
