using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Interop;

namespace Ahtola.Data.Sqlite.Browser.Storage;

[SupportedOSPlatform("browser")]
internal sealed class OpfsAsyncFileSystem :
    IAsyncFileSystem,
    IAsyncAtomicFileSystem,
    IAsyncTemporaryFileSystem,
    IStoragePathResolver,
    IAsyncDisposable
{
    internal const int DefaultSharedBufferSize = 1024 * 1024;
    private readonly OpfsWorkerClient _client;
    private int _disposed;

    private OpfsAsyncFileSystem(OpfsWorkerClient client)
    {
        _client = client;
    }

    public StringComparer PathComparer => StringComparer.Ordinal;

    public static async ValueTask<OpfsAsyncFileSystem> CreateAsync(
        string lockName,
        int sharedBufferSize = DefaultSharedBufferSize,
        CancellationToken cancellationToken = default)
    {
        var canonicalLockName = Canonicalize(lockName);
        var capabilities = await AhtolaBrowserRuntime.GetCapabilitiesAsync().ConfigureAwait(false);
        if (!capabilities.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Ahtola OPFS requires cross-origin isolation, SharedArrayBuffer, "
                + "Origin Private File System synchronous handles, module workers, and Web Locks.");
        }

        try
        {
            var client = await OpfsWorkerClient
                .CreateAsync(canonicalLockName, sharedBufferSize, cancellationToken)
                .ConfigureAwait(false);
            return new OpfsAsyncFileSystem(client);
        }
        catch (JSException exception) when (
            exception.Message.Contains("NoModificationAllowedError", StringComparison.Ordinal))
        {
            throw new AhtolaBrowserDatabaseLockedException(canonicalLockName, exception);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(
                exception,
                canonicalLockName,
                "initialize browser storage for");
        }
    }

    public string GetCanonicalPath(string path) => Canonicalize(path);

    public async ValueTask<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var canonicalPath = Canonicalize(path);
        try
        {
            return await _client
                .FileExistsAsync(canonicalPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, canonicalPath, "inspect");
        }
    }

    public async ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var canonicalPath = Canonicalize(path);
        try
        {
            var handleId = await _client
                .OpenFileAsync(canonicalPath, (int)mode, readOnly, cancellationToken)
                .ConfigureAwait(false);
            return new OpfsAsyncFile(_client, canonicalPath, handleId, readOnly, deleteOnClose: false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, canonicalPath, "open");
        }
    }

    public async ValueTask<IAsyncFile> OpenTemporaryFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var canonicalPath = Canonicalize(path);
        try
        {
            var handleId = await _client
                .OpenFileAsync(
                    canonicalPath,
                    (int)FileOpenMode.CreateNew,
                    readOnly: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return new OpfsAsyncFile(_client, canonicalPath, handleId, readOnly: false, deleteOnClose: true);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, canonicalPath, "create temporary file");
        }
    }

    public async ValueTask DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var canonicalPath = Canonicalize(path);
        try
        {
            await _client.DeleteAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(exception, canonicalPath, "delete");
        }
    }

    public async ValueTask ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var canonicalSource = Canonicalize(sourcePath);
        var canonicalDestination = Canonicalize(destinationPath);
        if (PathComparer.Equals(canonicalSource, canonicalDestination))
        {
            throw new IOException(
                "Atomic file replacement requires distinct source and destination paths.");
        }

        try
        {
            await _client
                .ReplaceFileAtomicallyAsync(
                    canonicalSource,
                    canonicalDestination,
                    replaceEmptyDestination,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw BrowserStorageExceptionMapper.Map(
                exception,
                $"{canonicalSource}' -> '{canonicalDestination}",
                "atomically replace");
        }
    }

    public ValueTask<FileWriteStamp?> GetWriteStampAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<FileWriteStamp?>(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _client.DisposeAsync().ConfigureAwait(false);
    }

    private static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            throw new ArgumentException("OPFS paths must be relative to the origin-private root.", nameof(path));

        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "OPFS paths cannot contain empty, current, or parent segments.",
                nameof(path));
        }

        return string.Join('/', segments);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
