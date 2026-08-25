using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Storage;

namespace Ahtola.Data.Sqlite.Browser;

[SupportedOSPlatform("browser")]
internal sealed class BrowserManagedDatabaseAdapter(
    IManagedDatabaseAdapter inner,
    BrowserMirroredFileSystem mirror,
    Action released) : IManagedDatabaseAdapter
{
    private readonly object _gate = new();
    private BrowserManagedConnectionAdapter? _connection;
    private int _disposed;

    public IManagedConnectionAdapter Connect()
        => throw BrowserManagedAdapterErrors.SyncNotSupported("connecting");

    public async ValueTask<IManagedConnectionAdapter> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IManagedConnectionAdapter? connected = null;
        Exception? failure = null;
        try
        {
            connected = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        lock (_gate)
        {
            ThrowIfDisposed();
            return _connection ??= new BrowserManagedConnectionAdapter(connected!, mirror);
        }
    }

    public IManagedConnectionAdapter Connection
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _connection
                    ?? throw new InvalidOperationException("The browser database has not been connected.");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            try
            {
                inner.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            try
            {
                BrowserManagedAdapterErrors.ThrowIfPending(mirror, "disposing the database");
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }
        finally
        {
            released();
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            failure = await BrowserManagedAdapterErrors
                .FlushAndCombineAsync(mirror, failure)
                .ConfigureAwait(false);
        }
        finally
        {
            released();
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserManagedConnectionAdapter(
    IManagedConnectionAdapter inner,
    BrowserMirroredFileSystem mirror) :
    IManagedConnectionAdapter,
    IManagedConnectionAdapterDecorator,
    IManagedConnectionDurabilityBoundary
{
    private int _disposed;

    public bool HasAttachedDatabases
    {
        get
        {
            ThrowIfDisposed();
            return inner.HasAttachedDatabases;
        }
    }

    public TimeSpan BusyTimeout
    {
        get
        {
            ThrowIfDisposed();
            return inner.BusyTimeout;
        }
        set
        {
            ThrowIfDisposed();
            inner.BusyTimeout = value;
        }
    }

    public ManagedConnectionHooks Hooks
    {
        get
        {
            ThrowIfDisposed();
            return inner.Hooks;
        }
    }

    public IManagedStatementAdapter Prepare(string sql)
    {
        ThrowIfDisposed();
        return new BrowserManagedStatementAdapter(inner.Prepare(sql), mirror);
    }

    public async ValueTask<IManagedStatementAdapter> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IManagedStatementAdapter? statement = null;
        Exception? failure = null;
        try
        {
            statement = await inner.PrepareAsync(sql, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            if (statement is not null)
                await statement.DisposeAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new BrowserManagedStatementAdapter(statement!, mirror);
    }

    public void ResetForPooling()
        => throw new PlatformNotSupportedException(
            "Browser-managed connections are owned by their data source and do not support pooling.");

    public IManagedIncrementalBlobAdapter OpenBlob(
        string databaseName,
        string tableName,
        string columnName,
        long rowId,
        bool readOnly = false)
    {
        ThrowIfDisposed();
        return new BrowserManagedIncrementalBlobAdapter(
            inner.OpenBlob(databaseName, tableName, columnName, rowId, readOnly),
            mirror);
    }

    public void RegisterScalarFunction(
        string name,
        int arity,
        Func<IReadOnlyList<SqlValue>, SqlValue> function)
    {
        ThrowIfDisposed();
        inner.RegisterScalarFunction(name, arity, function);
    }

    public int UnregisterScalarFunctions(string name)
    {
        ThrowIfDisposed();
        return inner.UnregisterScalarFunctions(name);
    }

    public void RegisterAggregateFunction(
        string name,
        int arity,
        SqlValue seed,
        Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
        Func<SqlValue, SqlValue> finalize)
    {
        ThrowIfDisposed();
        inner.RegisterAggregateFunction(name, arity, seed, step, finalize);
    }

    public int UnregisterAggregateFunctions(string name)
    {
        ThrowIfDisposed();
        return inner.UnregisterAggregateFunctions(name);
    }

    public void RegisterCollation(string name, Func<string, string, int> compare)
    {
        ThrowIfDisposed();
        inner.RegisterCollation(name, compare);
    }

    public bool UnregisterCollation(string name)
    {
        ThrowIfDisposed();
        return inner.UnregisterCollation(name);
    }

    public void CopySnapshotTo(IManagedConnectionAdapter destination)
        => throw BrowserManagedAdapterErrors.BackupNotSupported();

    public void CopySnapshotTo(
        IManagedConnectionAdapter destination,
        string destinationName,
        string sourceName)
        => throw BrowserManagedAdapterErrors.BackupNotSupported();

    public ValueTask CopySnapshotToAsync(
        IManagedConnectionAdapter destination,
        CancellationToken cancellationToken = default)
        => CopySnapshotToAsync(destination, "main", "main", cancellationToken);

    public async ValueTask CopySnapshotToAsync(
        IManagedConnectionAdapter destination,
        string destinationName,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Exception? failure = null;
        try
        {
            await inner
                .CopySnapshotToAsync(
                    destination,
                    destinationName,
                    sourceName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void ApplySnapshotPragmaHeader(int schemaVersion, int userVersion, int applicationId)
        => throw BrowserManagedAdapterErrors.BackupNotSupported();

    public async ValueTask ApplySnapshotPragmaHeaderAsync(
        int schemaVersion,
        int userVersion,
        int applicationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Exception? failure = null;
        try
        {
            await inner
                .ApplySnapshotPragmaHeaderAsync(
                    schemaVersion,
                    userVersion,
                    applicationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            inner.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            BrowserManagedAdapterErrors.ThrowIfPending(mirror, "disposing the connection");
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal IManagedConnectionAdapter Inner => inner;

    IManagedConnectionAdapter IManagedConnectionAdapterDecorator.InnerConnectionAdapter
        => inner;

    ValueTask IManagedConnectionDurabilityBoundary.SynchronizeAsync()
        => mirror.FlushPendingAsync(CancellationToken.None);

    internal BrowserMirroredFileSystem Mirror => mirror;
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserManagedIncrementalBlobAdapter(
    IManagedIncrementalBlobAdapter inner,
    BrowserMirroredFileSystem mirror) : IManagedIncrementalBlobAdapter
{
    private int _disposed;

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return inner.Length;
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        throw BrowserManagedAdapterErrors.SyncNotSupported("incremental blob reads");
    }

    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        throw BrowserManagedAdapterErrors.SyncNotSupported("incremental blob writes");
    }

    public async ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await inner.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(
        long offset,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Exception? failure = null;
        try
        {
            await inner.WriteAsync(offset, source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        ThrowIfDisposed();
        throw BrowserManagedAdapterErrors.SyncNotSupported("incremental blob disposal");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

[SupportedOSPlatform("browser")]
internal sealed class BrowserManagedStatementAdapter(
    IManagedStatementAdapter inner,
    BrowserMirroredFileSystem mirror) : IManagedStatementAdapter
{
    private int _disposed;

    public int ParameterCount
    {
        get
        {
            ThrowIfDisposed();
            return inner.ParameterCount;
        }
    }

    public ManagedParameterMetadata ParameterMetadata => new(this);

    public int RowsAffected
    {
        get
        {
            ThrowIfDisposed();
            return inner.RowsAffected;
        }
    }

    public void Bind(int index, SqlValue value)
    {
        ThrowIfDisposed();
        inner.Bind(index, value);
    }

    public int GetParameterIndex(string name)
    {
        ThrowIfDisposed();
        return inner.GetParameterIndex(name);
    }

    public StatementStepResult Step()
    {
        ThrowIfDisposed();
        return inner.Step();
    }

    public StatementStepResult Step(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return inner.Step(cancellationToken);
    }

    public async ValueTask<StatementStepResult> StepAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = default(StatementStepResult);
        Exception? failure = null;
        try
        {
            result = await inner.StepAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public bool HasRows()
    {
        ThrowIfDisposed();
        return inner.HasRows();
    }

    public void Reset()
    {
        ThrowIfDisposed();
        inner.Reset();
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Exception? failure = null;
        try
        {
            await inner.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void ClearBindings()
    {
        ThrowIfDisposed();
        inner.ClearBindings();
    }

    public SqlValue GetValue(int ordinal)
    {
        ThrowIfDisposed();
        return inner.GetValue(ordinal);
    }

    public string GetColumnName(int ordinal)
    {
        ThrowIfDisposed();
        return inner.GetColumnName(ordinal);
    }

    public int GetColumnCount()
    {
        ThrowIfDisposed();
        return inner.GetColumnCount();
    }

    public ManagedResultValue GetResultValue(int ordinal)
    {
        ThrowIfDisposed();
        return inner.GetResultValue(ordinal);
    }

    public ManagedResultColumn GetResultColumn(int ordinal)
    {
        ThrowIfDisposed();
        return inner.GetResultColumn(ordinal);
    }

    public int GetResultColumnCount()
    {
        ThrowIfDisposed();
        return inner.GetResultColumnCount();
    }

    public string? GetParameterName(int index)
    {
        ThrowIfDisposed();
        return inner.GetParameterName(index);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            inner.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            BrowserManagedAdapterErrors.ThrowIfPending(mirror, "disposing the statement");
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure = await BrowserManagedAdapterErrors
            .FlushAndCombineAsync(mirror, failure)
            .ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

[SupportedOSPlatform("browser")]
internal static class BrowserManagedAdapterErrors
{
    internal static PlatformNotSupportedException SyncNotSupported(string operation)
        => new(
            $"Synchronous {operation} is not supported by browser-managed databases. "
            + "Use the corresponding asynchronous API.");

    internal static PlatformNotSupportedException BackupNotSupported()
        => new(
            "Synchronous backup and snapshot copying are not supported by browser connections. "
            + "Use the corresponding asynchronous API.");

    internal static void ThrowIfPending(BrowserMirroredFileSystem mirror, string operation)
    {
        if (mirror.HasUnflushedWork)
        {
            throw new PlatformNotSupportedException(
                $"Synchronous {operation} cannot persist pending browser database mutations. "
                + "Use asynchronous disposal.");
        }
    }

    /// <summary>
    /// Flushes the mirror and merges any flush failure with an operation failure.
    /// </summary>
    /// <remarks>
    /// A statement that mutated nothing leaves the mirror with no unflushed work,
    /// so the whole flush is skipped: no semaphore wait, no asynchronous state
    /// machine, and no OPFS worker call. Every successful mutation still leaves
    /// work queued and therefore still flushes before the caller's operation
    /// completes, and a failed flush is still surfaced.
    /// </remarks>
    internal static ValueTask<Exception?> FlushAndCombineAsync(
        BrowserMirroredFileSystem mirror,
        Exception? failure)
        => !mirror.IsDisposed && !mirror.HasUnflushedWork
            ? ValueTask.FromResult(failure)
            : FlushAndCombineCoreAsync(mirror, failure);

    private static async ValueTask<Exception?> FlushAndCombineCoreAsync(
        BrowserMirroredFileSystem mirror,
        Exception? failure)
    {
        try
        {
            await mirror.FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception flushFailure)
        {
            failure = failure is null
                ? flushFailure
                : new AggregateException(
                    "The browser database operation and durable mirror flush both failed.",
                    failure,
                    flushFailure);
        }

        return failure;
    }
}
