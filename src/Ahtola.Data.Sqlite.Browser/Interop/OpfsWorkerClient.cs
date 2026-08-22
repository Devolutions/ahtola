using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ahtola.Data.Sqlite.Browser.Interop;

[SupportedOSPlatform("browser")]
internal sealed class OpfsWorkerClient : IAsyncDisposable
{
    private const long MaximumSafeJavaScriptInteger = 9_007_199_254_740_991;
    private readonly SemaphoreSlim _sharedBufferGate = new(1, 1);
    private readonly int _sharedBufferSize;
    private int _contextId;

    private OpfsWorkerClient(int contextId, int sharedBufferSize)
    {
        _contextId = contextId;
        _sharedBufferSize = sharedBufferSize;
    }

    public static async ValueTask<OpfsWorkerClient> CreateAsync(
        string lockName,
        int sharedBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        ArgumentOutOfRangeException.ThrowIfLessThan(sharedBufferSize, 64 * 1024);
        cancellationToken.ThrowIfCancellationRequested();

        await AhtolaBrowserRuntime.InitializeAsync().ConfigureAwait(false);
        var contextId = await BrowserInterop
            .CreateContextAsync(lockName, sharedBufferSize)
            .ConfigureAwait(false);
        return new OpfsWorkerClient(contextId, sharedBufferSize);
    }

    public async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await BrowserInterop.FileExistsAsync(GetContextId(), path).ConfigureAwait(false);
    }

    public async Task<int> OpenFileAsync(
        string path,
        int mode,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await BrowserInterop
            .OpenFileAsync(GetContextId(), path, mode, readOnly)
            .ConfigureAwait(false);
    }

    public async ValueTask<long> GetLengthAsync(int handleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = await BrowserInterop
            .GetLengthAsync(GetContextId(), handleId)
            .ConfigureAwait(false);
        if (length < 0 || length > MaximumSafeJavaScriptInteger || Math.Truncate(length) != length)
            throw new IOException("The OPFS worker returned an invalid file length.");
        return (long)length;
    }

    public async ValueTask<int> ReadAsync(
        int handleId,
        long position,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ValidatePosition(position);
        if (destination.IsEmpty)
            return 0;

        await _sharedBufferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contextId = GetContextId();
            var total = 0;
            while (total < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(destination.Length - total, _sharedBufferSize);
                var read = await BrowserInterop
                    .ReadFileAsync(contextId, handleId, position + total, count)
                    .ConfigureAwait(false);
                if ((uint)read > (uint)count)
                    throw new IOException("The OPFS worker returned an invalid read length.");
                if (read == 0)
                    break;

                CopyFromSharedBuffer(contextId, destination.Slice(total, read));
                total += read;
                if (read < count)
                    break;
            }
            return total;
        }
        finally
        {
            _sharedBufferGate.Release();
        }
    }

    public async ValueTask WriteAsync(
        int handleId,
        long position,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        ValidatePosition(position);
        if (source.IsEmpty)
            return;

        await _sharedBufferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contextId = GetContextId();
            var total = 0;
            while (total < source.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(source.Length - total, _sharedBufferSize);
                CopyToSharedBuffer(contextId, source.Slice(total, count));
                var written = await BrowserInterop
                    .WriteFileAsync(contextId, handleId, position + total, count)
                    .ConfigureAwait(false);
                if (written != count)
                {
                    throw new IOException(
                        $"The OPFS worker wrote {written} of {count} requested bytes.");
                }
                total += written;
            }
        }
        finally
        {
            _sharedBufferGate.Release();
        }
    }

    public async Task SetLengthAsync(
        int handleId,
        long length,
        CancellationToken cancellationToken)
    {
        ValidatePosition(length);
        cancellationToken.ThrowIfCancellationRequested();
        await BrowserInterop
            .SetLengthAsync(GetContextId(), handleId, length)
            .ConfigureAwait(false);
    }

    public async Task FlushAsync(int handleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await BrowserInterop.FlushFileAsync(GetContextId(), handleId).ConfigureAwait(false);
    }

    public async Task CloseAsync(int handleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await BrowserInterop.CloseFileAsync(GetContextId(), handleId).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await BrowserInterop.DeleteFileAsync(GetContextId(), path).ConfigureAwait(false);
    }

    public async Task ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken)
    {
        await _sharedBufferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contextId = GetContextId();
            var operationId = BrowserInterop.AllocateOperationId(contextId);
            var cancellationState = new CancellationState(contextId, operationId);
            using var registration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var cancellation = (CancellationState)state!;
                    BrowserInterop.CancelOperation(
                        cancellation.ContextId,
                        cancellation.OperationId);
                },
                cancellationState);
            try
            {
                await BrowserInterop
                    .ReplaceFileAtomicallyAsync(
                        contextId,
                        operationId,
                        sourcePath,
                        destinationPath,
                        replaceEmptyDestination)
                    .ConfigureAwait(false);
            }
            catch (System.Runtime.InteropServices.JavaScript.JSException exception) when (
                cancellationToken.IsCancellationRequested
                && exception.Message.Contains("AbortError", StringComparison.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
        finally
        {
            _sharedBufferGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var contextId = Interlocked.Exchange(ref _contextId, 0);
        if (contextId != 0)
            await BrowserInterop.DisposeContextAsync(contextId).ConfigureAwait(false);
        _sharedBufferGate.Dispose();
    }

    private int GetContextId()
    {
        ObjectDisposedException.ThrowIf(_contextId == 0, this);
        return _contextId;
    }

    private static void ValidatePosition(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value > MaximumSafeJavaScriptInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "OPFS offsets must fit exactly in a JavaScript Number.");
        }
    }

    private static void CopyFromSharedBuffer(int contextId, Memory<byte> destination)
    {
        byte[]? temporary = null;
        if (!MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out var segment))
        {
            temporary = new byte[destination.Length];
            segment = new ArraySegment<byte>(temporary);
        }

        var copied = BrowserInterop.CopyFromSharedBuffer(
            contextId,
            segment,
            destination.Length);
        if (copied != destination.Length)
            throw new IOException("The OPFS shared buffer returned an invalid copy length.");
        if (temporary is not null)
            temporary.AsMemory().CopyTo(destination);
    }

    private static void CopyToSharedBuffer(int contextId, ReadOnlyMemory<byte> source)
    {
        byte[]? temporary = null;
        if (!MemoryMarshal.TryGetArray(source, out var segment))
        {
            temporary = source.ToArray();
            segment = new ArraySegment<byte>(temporary);
        }

        var copied = BrowserInterop.CopyToSharedBuffer(contextId, segment);
        if (copied != source.Length)
            throw new IOException("The OPFS shared buffer accepted an invalid copy length.");
    }

    private sealed record CancellationState(int ContextId, int OperationId);
}
