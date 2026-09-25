namespace Ahtola.Core.Storage;

/// <summary>A read-only committed page view for bounded asynchronous traversals.</summary>
internal interface IAsyncBoundedReadSnapshot : IAsyncDisposable
{
    uint PageCount { get; }

    ValueTask<byte[]> ReadPageAsync(uint pageNumber, CancellationToken cancellationToken = default);
}
