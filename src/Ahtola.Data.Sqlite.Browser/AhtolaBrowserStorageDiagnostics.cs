using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Ahtola.Core;
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
        var managedPersistenceMatches = false;
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

        var managedRoot = $"managed-{id}";
        var managedPath = $"{managedRoot}/main.db";
        await using (var persistent = await OpfsAsyncFileSystem
                         .CreateAsync($"{lockName}-managed", sharedBufferSize, cancellationToken)
                         .ConfigureAwait(false))
        {
            await using var mirror = await BrowserMirroredFileSystem
                .CreateAsync(
                    persistent,
                    managedRoot,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using var database = ManagedDatabaseAdapter.OpenFile(managedPath, mirror);
            var connection = await database.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteToCompletionAsync(
                connection,
                "CREATE TABLE probe(value INTEGER NOT NULL)",
                cancellationToken).ConfigureAwait(false);
            await ExecuteToCompletionAsync(
                connection,
                "INSERT INTO probe(value) VALUES (42)",
                cancellationToken).ConfigureAwait(false);
            await mirror.FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var persistent = await OpfsAsyncFileSystem
                         .CreateAsync($"{lockName}-managed", sharedBufferSize, cancellationToken)
                         .ConfigureAwait(false))
        {
            await using var mirror = await BrowserMirroredFileSystem
                .CreateAsync(
                    persistent,
                    managedRoot,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using var database = ManagedDatabaseAdapter.OpenFile(managedPath, mirror);
            var connection = await database.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using var statement = await connection
                .PrepareAsync("SELECT value FROM probe", cancellationToken)
                .ConfigureAwait(false);
            managedPersistenceMatches =
                await statement.StepAsync(cancellationToken).ConfigureAwait(false)
                    == StatementStepResult.Row
                && statement.GetValue(0).AsInteger() == 42
                && await statement.StepAsync(cancellationToken).ConfigureAwait(false)
                    == StatementStepResult.Done;
            database.Dispose();
            await mirror.DeleteAllPersistentFilesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new AhtolaBrowserStorageDiagnosticResult(
            lockRejected,
            positionalIoMatches,
            atomicReplaceMatches,
            managedPersistenceMatches,
            details);
    }

    /// <summary>
    /// Warms a persistent connection opened in synchronous read-mirror mode, runs
    /// many synchronous point reads, and proves they never crossed into the OPFS
    /// worker.
    /// </summary>
    /// <param name="iterations">The number of synchronous point reads to perform.</param>
    /// <param name="cancellationToken">Cancels setup and teardown.</param>
    /// <remarks>
    /// The proof is the unchanged worker-operation count, not the elapsed time:
    /// timings vary widely across browsers and machines, so they are reported for
    /// diagnostics rather than asserted.
    /// </remarks>
    public static async ValueTask<AhtolaBrowserSynchronousReadDiagnosticResult> RunSynchronousReadAsync(
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        const int rowCount = 64;
        var id = Guid.NewGuid().ToString("N");
        var root = $"sync-read-{id}";
        var databasePath = $"{root}/main.db";
        long before;
        long after;
        long checksum;
        var readerMatched = false;
        double elapsedMilliseconds;

        var source = new AhtolaBrowserDataSource(
            databasePath,
            root,
            AhtolaBrowserOptions.DefaultSharedBufferSize,
            readOnly: false,
            encryption: null,
            synchronousMode: AhtolaBrowserSynchronousMode.ReadOnlyMirror);
        try
        {
            var connection = await source
                .OpenSynchronousReadConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await using (var setup = connection.CreateCommand())
                {
                    setup.CommandText =
                        "CREATE TABLE probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL)";
                    await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var seed = connection.CreateCommand())
                {
                    seed.CommandText =
                        "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < "
                        + rowCount.ToString(CultureInfo.InvariantCulture)
                        + ") INSERT INTO probe(id, value) SELECT n, n * 3 FROM seq";
                    await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // The first synchronous read pays for statement preparation and page
                // materialization, so it is excluded from the measured window.
                using (var warm = connection.CreateCommand())
                {
                    warm.CommandText = "SELECT value FROM probe WHERE id = 1";
                    _ = warm.ExecuteScalar();
                }

                before = source.GetStorageMetrics().PersistentOperations;
                checksum = 0;
                var stopwatch = Stopwatch.StartNew();
                using (var probe = connection.CreateCommand())
                {
                    probe.CommandText = "SELECT value FROM probe WHERE id = $id";
                    var parameter = probe.CreateParameter();
                    parameter.ParameterName = "$id";
                    probe.Parameters.Add(parameter);
                    for (var index = 0; index < iterations; index++)
                    {
                        parameter.Value = (index % rowCount) + 1;
                        checksum += Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }
                }

                stopwatch.Stop();
                elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

                using (var reader = connection.CreateCommand())
                {
                    reader.CommandText = "SELECT id, value FROM probe WHERE id = 7";
                    using var rows = reader.ExecuteReader();
                    readerMatched = rows.Read() && rows.GetInt64(1) == 21 && !rows.Read();
                }

                after = source.GetStorageMetrics().PersistentOperations;

                // Synchronous close is part of the contract: it is allowed only
                // because the mirror owes the persistent store nothing.
                connection.Close();
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            connection.Dispose();
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            await DeletePersistentDirectoryAsync(root).ConfigureAwait(false);
        }

        var expectedChecksum = ExpectedChecksum(iterations, rowCount);
        return new AhtolaBrowserSynchronousReadDiagnosticResult(
            iterations,
            before,
            after,
            elapsedMilliseconds,
            checksum == expectedChecksum && readerMatched,
            $"checksum={checksum};expected={expectedChecksum};reader={readerMatched}");
    }

    private static long ExpectedChecksum(int iterations, int rowCount)
    {
        long expected = 0;
        for (var index = 0; index < iterations; index++)
            expected += ((index % rowCount) + 1) * 3L;
        return expected;
    }

    private static async ValueTask DeletePersistentDirectoryAsync(string root)
    {
        try
        {
            await using var persistent = await OpfsAsyncFileSystem
                .CreateAsync(root, 64 * 1024, CancellationToken.None)
                .ConfigureAwait(false);
            var paths = await persistent
                .ListFilesAsync(root, CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var path in paths)
                await persistent.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AhtolaBrowserDatabaseLockedException)
        {
            // Another context still owns the directory; leaving the diagnostic's
            // scratch files behind must not fail the diagnostic itself.
        }
    }

    private static async ValueTask ExecuteToCompletionAsync(
        IManagedConnectionAdapter connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var statement = await connection
            .PrepareAsync(sql, cancellationToken)
            .ConfigureAwait(false);
        while (await statement.StepAsync(cancellationToken).ConfigureAwait(false)
               == StatementStepResult.Row)
        {
        }
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
    bool ManagedPersistenceMatches,
    string Details)
{
    /// <summary>Whether every OPFS diagnostic check passed.</summary>
    public bool Succeeded =>
        CompetingContextRejected
        && PositionalIoMatches
        && AtomicReplaceMatches
        && ManagedPersistenceMatches;
}

/// <summary>
/// Results from the synchronous read-mirror diagnostic.
/// </summary>
/// <param name="Iterations">The number of synchronous point reads performed.</param>
/// <param name="WorkerOperationsBefore">
/// OPFS worker operations recorded before the measured reads began.
/// </param>
/// <param name="WorkerOperationsAfter">
/// OPFS worker operations recorded after the measured reads completed.
/// </param>
/// <param name="ElapsedMilliseconds">Wall time spent in the measured reads.</param>
/// <param name="ValuesMatched">Whether every read returned the expected value.</param>
/// <param name="Details">Human-readable detail for failure triage.</param>
public readonly record struct AhtolaBrowserSynchronousReadDiagnosticResult(
    int Iterations,
    long WorkerOperationsBefore,
    long WorkerOperationsAfter,
    double ElapsedMilliseconds,
    bool ValuesMatched,
    string Details)
{
    /// <summary>Whether the reads caused no OPFS worker operation at all.</summary>
    public bool WorkerOperationsUnchanged => WorkerOperationsAfter == WorkerOperationsBefore;

    /// <summary>Average wall time per synchronous point read, in microseconds.</summary>
    public double AverageMicrosecondsPerRead
        => Iterations == 0 ? 0 : ElapsedMilliseconds * 1000d / Iterations;

    /// <summary>
    /// Whether the diagnostic proved the synchronous read-mirror contract: correct
    /// values with zero worker crossings.
    /// </summary>
    public bool Succeeded => ValuesMatched && WorkerOperationsUnchanged;
}
