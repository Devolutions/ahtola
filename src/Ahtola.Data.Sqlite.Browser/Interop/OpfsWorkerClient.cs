using System.Runtime.Versioning;

namespace Ahtola.Data.Sqlite.Browser.Interop;

[SupportedOSPlatform("browser")]
internal sealed class OpfsWorkerClient : IAsyncDisposable
{
    private const long MaximumSafeJavaScriptInteger = 9_007_199_254_740_991;
    private int _contextId;

    private OpfsWorkerClient(int contextId)
    {
        _contextId = contextId;
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
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new OpfsWorkerClient(contextId);
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
        => BrowserInterop.FileExistsAsync(GetContextId(), path).WaitAsync(cancellationToken);

    public Task<int> OpenFileAsync(
        string path,
        int mode,
        bool readOnly,
        CancellationToken cancellationToken)
        => BrowserInterop
            .OpenFileAsync(GetContextId(), path, mode, readOnly)
            .WaitAsync(cancellationToken);

    public async ValueTask<long> GetLengthAsync(int handleId, CancellationToken cancellationToken)
    {
        var length = await BrowserInterop
            .GetLengthAsync(GetContextId(), handleId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (length < 0 || length > MaximumSafeJavaScriptInteger || Math.Truncate(length) != length)
            throw new IOException("The OPFS worker returned an invalid file length.");
        return (long)length;
    }

    public async Task<byte[]> ReadAsync(
        int handleId,
        long position,
        int length,
        CancellationToken cancellationToken)
    {
        ValidatePosition(position);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        using var result = await BrowserInterop
            .ReadFileAsync(GetContextId(), handleId, position, length)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return BrowserInterop.UnwrapByteArray(result);
    }

    public Task<int> WriteAsync(
        int handleId,
        long position,
        byte[] source,
        CancellationToken cancellationToken)
    {
        ValidatePosition(position);
        ArgumentNullException.ThrowIfNull(source);
        return BrowserInterop
            .WriteFileAsync(GetContextId(), handleId, position, source)
            .WaitAsync(cancellationToken);
    }

    public Task SetLengthAsync(
        int handleId,
        long length,
        CancellationToken cancellationToken)
    {
        ValidatePosition(length);
        return BrowserInterop
            .SetLengthAsync(GetContextId(), handleId, length)
            .WaitAsync(cancellationToken);
    }

    public Task FlushAsync(int handleId, CancellationToken cancellationToken)
        => BrowserInterop.FlushFileAsync(GetContextId(), handleId).WaitAsync(cancellationToken);

    public Task CloseAsync(int handleId, CancellationToken cancellationToken)
        => BrowserInterop.CloseFileAsync(GetContextId(), handleId).WaitAsync(cancellationToken);

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
        => BrowserInterop.DeleteFileAsync(GetContextId(), path).WaitAsync(cancellationToken);

    public async Task ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contextId = GetContextId();
        using var registration = cancellationToken.UnsafeRegister(
            static state => BrowserInterop.CancelCurrentOperation((int)state!),
            contextId);
        await BrowserInterop
            .ReplaceFileAtomicallyAsync(
                contextId,
                sourcePath,
                destinationPath,
                replaceEmptyDestination)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var contextId = Interlocked.Exchange(ref _contextId, 0);
        if (contextId != 0)
            await BrowserInterop.DisposeContextAsync(contextId).ConfigureAwait(false);
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
}
