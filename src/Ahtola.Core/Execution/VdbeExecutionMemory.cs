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

internal static class VdbeManagedFootprint
{
    public const long ReferenceBytes = 8;
    public const long ListObjectBytes = 32;
    public const long DictionaryObjectBytes = 64;
    public const long BucketListObjectBytes = 32;

    private const long ArrayHeaderBytes = 24;
    private const long StringHeaderBytes = 24;
    private const long SqlValueSlotBytes = 56;
    private const long NullableInt64SlotBytes = 16;
    private const long JoinRowObjectBytes = 32;
    private const long BuildEntryObjectBytes = 48;
    private const long SpillPayloadObjectBytes = 32;
    private const long DictionaryEntryBytes = 24;
    private const long PriorityQueueObjectBytes = 64;
    private const long PriorityQueueNodeBytes = 48;
    private const long RunReaderObjectBytes = 64;
    private const long RunDescriptorSlotBytes = 24;

    public static long EstimateSorterRow(IReadOnlyList<SqlValue> values)
    {
        var total = EstimateArray(SqlValueSlotBytes, values.Count);
        foreach (var value in values)
            total = checked(total + EstimateValuePayload(value));
        return total;
    }

    public static long EstimateHashBuildEntry(
        IReadOnlyList<SqlValue> values,
        string? key,
        int rowIdCount)
    {
        var total = checked(
            BuildEntryObjectBytes
            + JoinRowObjectBytes
            + EstimateArray(SqlValueSlotBytes, values.Count)
            + EstimateArray(NullableInt64SlotBytes, rowIdCount));
        if (key is not null)
            total = checked(total + EstimateString(key.Length));
        foreach (var value in values)
            total = checked(total + EstimateValuePayload(value));
        return total;
    }

    public static long EstimateSorterRowFromEncodedLength(long payloadLength, int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        return checked(
            EstimateArray(SqlValueSlotBytes, columnCount)
            + (columnCount * SpillPayloadObjectBytes)
            + (payloadLength * sizeof(char)));
    }

    public static long EstimateHashBuildEntryFromEncodedLength(
        long payloadLength,
        int columnCount,
        int rowIdCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rowIdCount);
        return checked(
            BuildEntryObjectBytes
            + JoinRowObjectBytes
            + EstimateArray(SqlValueSlotBytes, columnCount)
            + EstimateArray(NullableInt64SlotBytes, rowIdCount)
            + ((columnCount + 1L) * SpillPayloadObjectBytes)
            + (payloadLength * sizeof(char)));
    }

    public static long EstimateReferenceListStorage(int capacity) =>
        capacity == 0 ? 0 : EstimateArray(ReferenceBytes, capacity);

    public static long EstimateInt32ListStorage(int capacity) =>
        capacity == 0 ? 0 : EstimateArray(sizeof(int), capacity);

    public static long EstimateRunDescriptorListStorage(int capacity) =>
        capacity == 0 ? 0 : EstimateArray(RunDescriptorSlotBytes, capacity);

    public static long EstimateDictionaryStorage(int count)
    {
        if (count == 0)
            return 0;
        var capacity = GetConservativeDictionaryCapacity(count);
        return checked(
            EstimateArray(sizeof(int), capacity)
            + EstimateArray(DictionaryEntryBytes, capacity));
    }

    public static long EstimateBooleanArray(int count) =>
        count == 0 ? 0 : EstimateArray(sizeof(byte), count);

    public static long EstimateSortWorkspace(int count) =>
        count == 0
            ? 0
            : checked(
                EstimateArray(sizeof(int), count)
                + ListObjectBytes
                + EstimateArray(ReferenceBytes, count));

    public static long EstimateMergeInfrastructure(int runCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(runCount);
        if (runCount == 0)
            return 0;
        return checked(
            PriorityQueueObjectBytes
            + EstimateArray(PriorityQueueNodeBytes, runCount)
            + (2 * EstimateArray(ReferenceBytes, runCount))
            + (runCount * RunReaderObjectBytes));
    }

    public static int GetListCapacityForCount(int currentCapacity, int requiredCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredCount);
        if (requiredCount <= currentCapacity)
            return currentCapacity;

        var capacity = currentCapacity == 0 ? 4 : currentCapacity;
        while (capacity < requiredCount)
        {
            var doubled = (long)capacity * 2;
            capacity = doubled >= int.MaxValue ? int.MaxValue : (int)doubled;
            if (capacity < requiredCount && capacity == int.MaxValue)
                throw new OutOfMemoryException("A managed execution container exceeded the supported capacity.");
        }
        return capacity;
    }

    private static long EstimateValuePayload(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null or SqlValueKind.Integer or SqlValueKind.Real => 0,
        SqlValueKind.Text => EstimateString(value.AsText().Length),
        SqlValueKind.Blob => EstimateArray(sizeof(byte), value.AsBlobSpan().Length),
        _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
    };

    private static long EstimateString(int characterCount) =>
        Align(checked(StringHeaderBytes + ((characterCount + 1L) * sizeof(char))));

    private static long EstimateArray(long slotBytes, int count) =>
        Align(checked(ArrayHeaderBytes + (slotBytes * count)));

    private static int GetConservativeDictionaryCapacity(int count)
    {
        var required = checked((long)count * 4);
        var capacity = 4L;
        while (capacity < required)
            capacity = checked(capacity * 2);
        if (capacity > int.MaxValue)
            throw new OutOfMemoryException("A managed execution dictionary exceeded the supported capacity.");
        return (int)capacity;
    }

    private static long Align(long bytes) => checked((bytes + 7) & ~7L);
}
