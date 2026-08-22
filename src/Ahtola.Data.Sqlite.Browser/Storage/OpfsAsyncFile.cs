using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Interop;

namespace Ahtola.Data.Sqlite.Browser.Storage;

[SupportedOSPlatform("browser")]
internal sealed class OpfsAsyncFile : IAsyncFile
{
    private readonly OpfsWorkerClient _client;
    private readonly string _path;
    private readonly bool _deleteOnClose;
    private int _handleId;

    public OpfsAsyncFile(
        OpfsWorkerClient client,
        string path,
        int handleId,
        bool readOnly,
        bool deleteOnClose)
    {
        _client = client;
        _path = path;
        _handleId = handleId;
        IsReadOnly = readOnly;
        _deleteOnClose = deleteOnClose;
    }

    public bool IsReadOnly { get; }

    public async ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
    {
        var handleId = GetHandleId();
        try
        {
            return await _client.GetLengthAsync(handleId, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, _path, "read the length of");
        }
    }

    public async ValueTask<int> ReadAsync(
        long position,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        var handleId = GetHandleId();
        if (destination.IsEmpty)
            return 0;

        try
        {
            return await _client
                .ReadAsync(handleId, position, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, _path, "read");
        }
    }

    public async ValueTask WriteAsync(
        long position,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot write to a read-only OPFS file.");
        if (source.IsEmpty)
            return;

        var handleId = GetHandleId();
        try
        {
            await _client
                .WriteAsync(handleId, position, source, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, _path, "write");
        }
    }

    public async ValueTask SetLengthAsync(
        long length,
        CancellationToken cancellationToken = default)
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot change the length of a read-only OPFS file.");

        var handleId = GetHandleId();
        try
        {
            await _client.SetLengthAsync(handleId, length, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, _path, "set the length of");
        }
    }

    public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
    {
        var handleId = GetHandleId();
        try
        {
            await _client.FlushAsync(handleId, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, _path, "flush");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var handleId = Interlocked.Exchange(ref _handleId, 0);
        if (handleId == 0)
            return;

        try
        {
            await _client.CloseAsync(handleId, CancellationToken.None).ConfigureAwait(false);
            if (_deleteOnClose)
                await _client.DeleteAsync(_path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, _path, "close");
        }
    }

    private int GetHandleId()
    {
        var handleId = Volatile.Read(ref _handleId);
        ObjectDisposedException.ThrowIf(handleId == 0, this);
        return handleId;
    }
}
