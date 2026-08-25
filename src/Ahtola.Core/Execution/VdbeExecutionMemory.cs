namespace Ahtola.Core.Execution;

/// <summary>
/// Raised when an execution intermediate cannot stay within its configured retained-memory budget
/// and temporary-file spill is disabled.
/// </summary>
public sealed class VdbeMemoryLimitExceededException : InvalidOperationException
{
    internal VdbeMemoryLimitExceededException(long limitBytes, long requestedBytes)
        : base($"The managed execution memory limit of {limitBytes} bytes cannot retain a {requestedBytes}-byte intermediate.")
    {
        LimitBytes = limitBytes;
        RequestedBytes = requestedBytes;
    }

    public long LimitBytes { get; }

    public long RequestedBytes { get; }
}

/// <summary>Statement-local high-water and spill metrics for managed VDBE execution.</summary>
public sealed class VdbeExecutionMetrics
{
    public long CurrentRetainedBytes { get; private set; }

    public long PeakRetainedBytes { get; private set; }

    public long CurrentRetainedRows { get; private set; }

    public long PeakRetainedRows { get; private set; }

    public long SpillFilesCreated { get; private set; }

    public long ActiveSpillFiles { get; private set; }

    public long SpillBytesWritten { get; private set; }

    public long SpillBytesRead { get; private set; }

    public long SorterRunsWritten { get; private set; }

    public long HashPartitionsCreated { get; private set; }

    public long HashPartitionScans { get; private set; }

    public long HashPartitionLoads { get; private set; }

    public long HashPartitionFallbackScans { get; private set; }

    internal void Retain(long bytes, long rows)
    {
        CurrentRetainedBytes = checked(CurrentRetainedBytes + bytes);
        CurrentRetainedRows = checked(CurrentRetainedRows + rows);
        PeakRetainedBytes = Math.Max(PeakRetainedBytes, CurrentRetainedBytes);
        PeakRetainedRows = Math.Max(PeakRetainedRows, CurrentRetainedRows);
    }

    internal void Release(long bytes, long rows)
    {
        CurrentRetainedBytes = checked(CurrentRetainedBytes - bytes);
        CurrentRetainedRows = checked(CurrentRetainedRows - rows);
        if (CurrentRetainedBytes < 0 || CurrentRetainedRows < 0)
            throw new InvalidOperationException("Execution memory accounting was released below zero.");
    }

    internal void SpillFileOpened()
    {
        SpillFilesCreated = checked(SpillFilesCreated + 1);
        ActiveSpillFiles = checked(ActiveSpillFiles + 1);
    }

    internal void SpillFileClosed()
    {
        ActiveSpillFiles = checked(ActiveSpillFiles - 1);
        if (ActiveSpillFiles < 0)
            throw new InvalidOperationException("Execution spill-file accounting was released below zero.");
    }

    internal void AddSpillBytesWritten(long bytes) =>
        SpillBytesWritten = checked(SpillBytesWritten + bytes);

    internal void AddSpillBytesRead(long bytes) =>
        SpillBytesRead = checked(SpillBytesRead + bytes);

    internal void SorterRunWritten() => SorterRunsWritten = checked(SorterRunsWritten + 1);

    internal void HashPartitionCreated() => HashPartitionsCreated = checked(HashPartitionsCreated + 1);

    internal void HashPartitionScanned() => HashPartitionScans = checked(HashPartitionScans + 1);

    internal void HashPartitionLoaded() => HashPartitionLoads = checked(HashPartitionLoads + 1);

    internal void HashPartitionFallbackScan() =>
        HashPartitionFallbackScans = checked(HashPartitionFallbackScans + 1);
}

internal sealed class VdbeExecutionMemory(long limitBytes, VdbeExecutionMetrics metrics)
{
    private long _retainedBytes;
    private long _retainedRows;

    public long LimitBytes { get; } = limitBytes;

    public long AvailableBytes => LimitBytes - _retainedBytes;

    public bool TryRetain(long bytes, long rows = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        if (bytes > LimitBytes - _retainedBytes)
            return false;

        _retainedBytes = checked(_retainedBytes + bytes);
        _retainedRows = checked(_retainedRows + rows);
        metrics.Retain(bytes, rows);
        return true;
    }

    public void RetainOrThrow(long bytes, long rows = 1)
    {
        if (!TryRetain(bytes, rows))
            throw new VdbeMemoryLimitExceededException(LimitBytes, bytes);
    }

    public void Release(long bytes, long rows = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        _retainedBytes = checked(_retainedBytes - bytes);
        _retainedRows = checked(_retainedRows - rows);
        if (_retainedBytes < 0 || _retainedRows < 0)
            throw new InvalidOperationException("Execution memory accounting was released below zero.");
        metrics.Release(bytes, rows);
    }
}
