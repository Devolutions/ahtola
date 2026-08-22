using System.Runtime.Versioning;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>Browser-facing diagnostics for the managed OPFS transport.</summary>
[SupportedOSPlatform("browser")]
public static class AhtolaBrowserStorageDiagnostics
{
    /// <summary>
    /// Verifies feature detection, cross-context locking, chunked positional I/O,
    /// persistence, and crash-safe atomic replacement.
    /// </summary>
    public static async ValueTask<AhtolaBrowserStorageDiagnosticResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var lockName = $"diagnostic-{id}";
        var dataPath = $"{id}/data.db";
        var sourcePath = $"{id}/replace-source.db";
        var destinationPath = $"{id}/replace-destination.db";
        const int sharedBufferSize = 64 * 1024;
        var source = new byte[sharedBufferSize * 2 + 137];
        for (var index = 0; index < source.Length; index++)
            source[index] = (byte)((index * 29 + 7) & 0xff);

        var lockRejected = false;
        var positionalIoMatches = false;
        var atomicReplaceMatches = false;
        var details = string.Empty;
        await using var fileSystem = await OpfsAsyncFileSystem
            .CreateAsync(lockName, sharedBufferSize, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            try
            {
                await using var competing = await OpfsAsyncFileSystem
                    .CreateAsync(lockName, sharedBufferSize, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AhtolaBrowserDatabaseLockedException)
            {
                lockRejected = true;
            }

            await using (var file = await fileSystem
                             .OpenFileAsync(
                                 dataPath,
                                 FileOpenMode.CreateNew,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false))
            {
                await file.WriteAsync(11, source, cancellationToken).ConfigureAwait(false);
                await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
                var destination = new byte[source.Length];
                var read = await file
                    .ReadAsync(11, destination, cancellationToken)
                    .ConfigureAwait(false);
                var actualLength = await file
                    .GetLengthAsync(cancellationToken)
                    .ConfigureAwait(false);
                var mismatch = source.AsSpan().SequenceEqual(destination)
                    ? -1
                    : FindFirstMismatch(source, destination);
                positionalIoMatches =
                    actualLength == source.Length + 11
                    && read == source.Length
                    && mismatch == -1;
                details = $"pos(length={actualLength},read={read},mismatch={mismatch})";
            }

            await using (var sourceFile = await fileSystem
                             .OpenFileAsync(
                                 sourcePath,
                                 FileOpenMode.CreateNew,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false))
            {
                await sourceFile.WriteAsync(0, source, cancellationToken).ConfigureAwait(false);
                await sourceFile.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (await fileSystem
                             .OpenFileAsync(
                                 destinationPath,
                                 FileOpenMode.CreateNew,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false))
            {
            }

            await fileSystem
                .ReplaceFileAtomicallyAsync(
                    sourcePath,
                    destinationPath,
                    replaceEmptyDestination: true,
                    cancellationToken)
                .ConfigureAwait(false);
            await using (var destinationFile = await fileSystem
                             .OpenFileAsync(
                                 destinationPath,
                                 FileOpenMode.OpenExisting,
                                 readOnly: true,
                                 cancellationToken)
                             .ConfigureAwait(false))
            {
                var destination = new byte[source.Length];
                var read = await destinationFile
                    .ReadAsync(0, destination, cancellationToken)
                    .ConfigureAwait(false);
                var sourceExists = await fileSystem
                    .FileExistsAsync(sourcePath, cancellationToken)
                    .ConfigureAwait(false);
                var mismatch = source.AsSpan().SequenceEqual(destination)
                    ? -1
                    : FindFirstMismatch(source, destination);
                atomicReplaceMatches =
                    !sourceExists
                    && read == source.Length
                    && mismatch == -1;
                details += $";atomic(source={sourceExists},read={read},mismatch={mismatch})";
            }
        }
        finally
        {
            await fileSystem.DeleteFileAsync(dataPath, CancellationToken.None).ConfigureAwait(false);
            await fileSystem.DeleteFileAsync(sourcePath, CancellationToken.None).ConfigureAwait(false);
            await fileSystem.DeleteFileAsync(destinationPath, CancellationToken.None).ConfigureAwait(false);
        }

        return new AhtolaBrowserStorageDiagnosticResult(
            lockRejected,
            positionalIoMatches,
            atomicReplaceMatches,
            details);
    }

    private static int FindFirstMismatch(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var count = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < count; index++)
        {
            if (expected[index] != actual[index])
                return index;
        }
        return count;
    }
}

/// <summary>Results from the managed OPFS transport diagnostic.</summary>
public readonly record struct AhtolaBrowserStorageDiagnosticResult(
    bool CompetingContextRejected,
    bool PositionalIoMatches,
    bool AtomicReplaceMatches,
    string Details)
{
    /// <summary>Whether every OPFS diagnostic check passed.</summary>
    public bool Succeeded =>
        CompetingContextRejected
        && PositionalIoMatches
        && AtomicReplaceMatches;
}
