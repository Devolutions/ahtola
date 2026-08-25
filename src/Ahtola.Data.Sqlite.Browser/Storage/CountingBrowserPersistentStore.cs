using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Counts every operation that crosses from the managed mirror into the durable
/// store. In a browser that boundary is the OPFS worker, so the count is the
/// number of worker round trips a workload caused.
/// </summary>
/// <remarks>
/// The counter exists so the synchronous read-mirror contract can be proven
/// rather than timed: a workload that only performs supported synchronous reads
/// must leave the count unchanged. Counting here rather than in the mirror covers
/// handle operations too, since a write cannot reach the store without one.
/// </remarks>
internal sealed class CountingBrowserPersistentStore(IBrowserPersistentStore inner)
    : IBrowserPersistentStore
{
    private long _operations;

    /// <summary>The number of operations issued to the durable store so far.</summary>
    internal long OperationCount => Interlocked.Read(ref _operations);

    internal IBrowserPersistentStore Inner => inner;

    public ValueTask<IReadOnlyList<string>> ListFilesAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        Count();
        return inner.ListFilesAsync(directory, cancellationToken);
    }

    public async ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        Count();
        var file = await inner
            .OpenFileAsync(path, mode, readOnly, cancellationToken)
            .ConfigureAwait(false);
        return new CountingAsyncFile(this, file);
    }

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        Count();
        return inner.DeleteFileAsync(path, cancellationToken);
    }

    public ValueTask ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken = default)
    {
        Count();
        return inner.ReplaceFileAtomicallyAsync(
            sourcePath,
            destinationPath,
            replaceEmptyDestination,
            cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Count() => Interlocked.Increment(ref _operations);

    private sealed class CountingAsyncFile(
        CountingBrowserPersistentStore owner,
        IAsyncFile inner) : IAsyncFile
    {
        public bool IsReadOnly => inner.IsReadOnly;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken = default)
        {
            owner.Count();
            return inner.GetLengthAsync(cancellationToken);
        }

        public ValueTask<int> ReadAsync(
            long position,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            owner.Count();
            return inner.ReadAsync(position, destination, cancellationToken);
        }

        public ValueTask WriteAsync(
            long position,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            owner.Count();
            return inner.WriteAsync(position, source, cancellationToken);
        }

        public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
        {
            owner.Count();
            return inner.SetLengthAsync(length, cancellationToken);
        }

        public ValueTask FlushToDiskAsync(CancellationToken cancellationToken = default)
        {
            owner.Count();
            return inner.FlushToDiskAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            owner.Count();
            return inner.DisposeAsync();
        }
    }
}
