using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// The durable store behind <see cref="BrowserMirroredFileSystem"/>. In a browser
/// this is OPFS through the dedicated worker; the seam exists so the mirror,
/// including encrypted persistence, can be exercised end to end off-browser.
/// </summary>
internal interface IBrowserPersistentStore : IAsyncDisposable
{
    /// <summary>Lists every persisted file below <paramref name="directory"/>.</summary>
    ValueTask<IReadOnlyList<string>> ListFilesAsync(
        string directory,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a persisted file.</summary>
    ValueTask<IAsyncFile> OpenFileAsync(
        string path,
        FileOpenMode mode,
        bool readOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a persisted file.</summary>
    ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Atomically publishes <paramref name="sourcePath"/> over <paramref name="destinationPath"/>.</summary>
    ValueTask ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination,
        CancellationToken cancellationToken = default);
}
