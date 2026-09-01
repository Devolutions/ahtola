using System.Runtime.ExceptionServices;

namespace Ahtola.Core.Execution;

/// <summary>
/// Statement-local insertion-ordered row set that spills through the shared execution-memory budget.
/// Equality remains caller-defined so SQLite affinity, collation, and NULL semantics stay with the
/// compiler-supplied <see cref="VdbeRowEquality"/>. A fixed-width spill sidecar maps each logical slot
/// to its latest append-log record so replacements and iteration use bounded direct reads.
/// </summary>
internal sealed class VdbeKeyedRowStore : IDisposable
{
    private readonly VdbeExecutionOptions _options;
    private readonly VdbeExecutionMemory _memory;
    private readonly List<SqlValue[]> _rows = [];
    private VdbeTemporaryFile? _temporaryFile;
    private VdbeTemporaryFile? _indexFile;
    private VdbeMemoryReservation? _spillInfrastructure;
    private ReadLease? _current;
    private long _writePosition;
    private long _bufferedBytes;
    private int _columnCount = -1;
    private int _count;
    private int _currentSlot = -1;
    private bool _prepared;
    private bool _disposed;

    public VdbeKeyedRowStore(VdbeExecutionOptions options, VdbeExecutionMemory memory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(memory);
        _options = options;
        _memory = memory;
    }

    public int Count => _count;

    public bool IsSpilled => _temporaryFile is not null;

    public bool TryInsert(
        SqlValue[] candidate,
        VdbeRowEquality equality,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(equality);
        cancellationToken.ThrowIfCancellationRequested();
        if (_prepared)
            throw new InvalidOperationException("Cannot insert into a keyed row set after iteration has started.");

        ValidateWidth(candidate);
        var existingSlot = FindSlot(candidate, equality, cancellationToken);
        if (existingSlot >= 0)
        {
            if (!replaceExisting)
                return false;

            if (_temporaryFile is null && TryReplaceBuffered(existingSlot, candidate))
                return true;

            EnsureSpilled(cancellationToken);
            Append(existingSlot, candidate, cancellationToken);
            return true;
        }

        if (_temporaryFile is null && TryBuffer(candidate))
        {
            _count++;
            return true;
        }

        if (!_options.AllowTemporaryFileSpill)
        {
            throw new VdbeMemoryLimitExceededException(
                _memory.LimitBytes,
                VdbeManagedFootprint.EstimateSorterRow(candidate));
        }

        EnsureSpilled(cancellationToken);
        Append(_count, candidate, cancellationToken);
        _count++;
        return true;
    }

    public bool Contains(
        SqlValue[] candidate,
        VdbeRowEquality equality,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(equality);
        cancellationToken.ThrowIfCancellationRequested();
        if (_columnCount >= 0 && candidate.Length != _columnCount)
            return false;
        return FindSlot(candidate, equality, cancellationToken) >= 0;
    }

    public void SortBuffered(VdbeRowComparer comparer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(comparer);
        if (_temporaryFile is not null)
            throw new InvalidOperationException("A spilled keyed row set must sort through the spill-aware sorter.");

        try
        {
            _rows.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var comparison = comparer(left, right);
                cancellationToken.ThrowIfCancellationRequested();
                return comparison;
            });
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is OperationCanceledException cancellation)
        {
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        }
    }

    public bool Rewind(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _prepared = true;
        ReleaseCurrent();
        _currentSlot = -1;
        if (_count == 0)
            return false;

        _currentSlot = 0;
        _current = _temporaryFile is null
            ? ReadLease.Borrowed(_rows[0])
            : ReadSpilledSlot(0, cancellationToken);
        return true;
    }

    public SqlValue[] Current()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _current?.Row
            ?? throw new InvalidOperationException("Keyed row set is not positioned on a row.");
    }

    public SqlValue[] TakeCurrent(out long retainedBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = _current
            ?? throw new InvalidOperationException("Keyed row set is not positioned on a row.");
        _current = null;
        return current.Detach(out retainedBytes);
    }

    public bool MoveNext(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_prepared)
            throw new InvalidOperationException("Keyed row set must be rewound before advancing.");

        ReleaseCurrent();
        _currentSlot++;
        if (_currentSlot >= _count)
            return false;

        _current = _temporaryFile is null
            ? ReadLease.Borrowed(_rows[_currentSlot])
            : ReadSpilledSlot(_currentSlot, cancellationToken);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        List<Exception>? failures = null;
        try
        {
            try
            {
                ReleaseCurrent();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                _indexFile?.Dispose();
                _indexFile = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                _temporaryFile?.Dispose();
                _temporaryFile = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                _spillInfrastructure?.Dispose();
                _spillInfrastructure = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        finally
        {
            if (_bufferedBytes > 0)
                _memory.Release(_bufferedBytes, _rows.Count);
            _rows.Clear();
            _rows.Capacity = 0;
            _bufferedBytes = 0;
            _disposed = failures is null;
        }

        if (failures is [var failure])
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);
    }

    private int FindSlot(
        SqlValue[] candidate,
        VdbeRowEquality equality,
        CancellationToken cancellationToken)
    {
        if (_temporaryFile is null)
        {
            for (var slot = 0; slot < _rows.Count; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var equal = equality(_rows[slot], candidate);
                cancellationToken.ThrowIfCancellationRequested();
                if (equal)
                    return slot;
            }
            return -1;
        }

        for (var slot = 0; slot < _count; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stored = ReadSpilledSlot(slot, cancellationToken);
            var equal = equality(stored.Row, candidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (equal)
                return slot;
        }
        return -1;
    }

    private bool TryBuffer(SqlValue[] candidate)
    {
        var requiredCount = checked(_rows.Count + 1);
        var capacity = VdbeManagedFootprint.GetListCapacityForCount(_rows.Capacity, requiredCount);
        var currentListBytes = VdbeManagedFootprint.EstimateReferenceListStorage(_rows.Capacity);
        var listGrowth = VdbeManagedFootprint.EstimateContainerReplacement(
            currentListBytes,
            VdbeManagedFootprint.EstimateReferenceListStorage(capacity));
        var replacedListBytes = listGrowth > 0 ? currentListBytes : 0;
        var rowBytes = VdbeManagedFootprint.EstimateSorterRow(candidate);
        var retainedBytes = checked(rowBytes + listGrowth);
        if (_rows.Count > 0
            && _options.AllowTemporaryFileSpill
            && retainedBytes > _memory.AvailableBytes - SpillInfrastructureBytes())
        {
            return false;
        }
        if (!_memory.TryRetain(retainedBytes))
            return false;

        try
        {
            if (capacity != _rows.Capacity)
                _rows.Capacity = capacity;
            _rows.Add(candidate);
            if (replacedListBytes > 0)
                _memory.Release(replacedListBytes, rows: 0);
            _bufferedBytes = checked(_bufferedBytes + retainedBytes - replacedListBytes);
            return true;
        }
        catch
        {
            _memory.Release(retainedBytes);
            throw;
        }
    }

    private bool TryReplaceBuffered(int slot, SqlValue[] candidate)
    {
        var previousBytes = VdbeManagedFootprint.EstimateSorterRow(_rows[slot]);
        var replacementBytes = VdbeManagedFootprint.EstimateSorterRow(candidate);
        var growthBytes = Math.Max(0, replacementBytes - previousBytes);
        if (!_memory.TryRetain(growthBytes, rows: 0))
        {
            if (!_options.AllowTemporaryFileSpill)
            {
                throw new VdbeMemoryLimitExceededException(
                    _memory.LimitBytes,
                    growthBytes);
            }
            return false;
        }

        _rows[slot] = candidate;
        if (previousBytes > replacementBytes)
            _memory.Release(previousBytes - replacementBytes, rows: 0);
        _bufferedBytes = checked(_bufferedBytes + replacementBytes - previousBytes);
        return true;
    }

    private void EnsureSpilled(CancellationToken cancellationToken)
    {
        if (_temporaryFile is not null)
            return;
        if (!_options.AllowTemporaryFileSpill)
        {
            throw new VdbeMemoryLimitExceededException(
                _memory.LimitBytes,
                SpillInfrastructureBytes());
        }

        cancellationToken.ThrowIfCancellationRequested();
        VdbeMemoryReservation? infrastructure =
            VdbeMemoryReservation.Create(_memory, SpillInfrastructureBytes());
        VdbeTemporaryFile? temporaryFile = null;
        VdbeTemporaryFile? indexFile = null;
        try
        {
            temporaryFile = VdbeTemporaryFile.Create(_options, "keyed-row-set");
            indexFile = VdbeTemporaryFile.Create(_options, "keyed-row-index");
            _writePosition = VdbeSpillRecordCodec.InitializeFile(
                temporaryFile.File,
                VdbeSpillFileKind.KeyedRowSet,
                _options.Metrics);
            VdbeSpillRecordCodec.InitializeFile(
                indexFile.File,
                VdbeSpillFileKind.KeyedRowSetIndex,
                _options.Metrics);
            _temporaryFile = temporaryFile;
            temporaryFile = null;
            _indexFile = indexFile;
            indexFile = null;
            _spillInfrastructure = infrastructure;
            infrastructure = null;

            for (var slot = 0; slot < _rows.Count; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteRecord(slot, _rows[slot]);
            }

            if (_bufferedBytes > 0)
                _memory.Release(_bufferedBytes, _rows.Count);
            _rows.Clear();
            _rows.Capacity = 0;
            _bufferedBytes = 0;
            _options.Metrics.KeyedRowSetSpilled();
        }
        finally
        {
            indexFile?.Dispose();
            temporaryFile?.Dispose();
            infrastructure?.Dispose();
        }
    }

    private void Append(int slot, SqlValue[] row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retainedBytes = VdbeManagedFootprint.EstimateSorterRow(row);
        _memory.RetainOrThrow(retainedBytes);
        try
        {
            WriteRecord(slot, row);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _memory.Release(retainedBytes);
        }
    }

    private void WriteRecord(int slot, SqlValue[] row)
    {
        var file = _temporaryFile?.File
            ?? throw new InvalidOperationException("Keyed row set has no spill file.");
        var index = _indexFile?.File
            ?? throw new InvalidOperationException("Keyed row set has no spill index.");
        var recordStart = VdbeSpillRecordCodec.BeginRecord(ref _writePosition);
        VdbeSpillRecordCodec.WriteInt32(file, ref _writePosition, slot, _options.Metrics);
        VdbeSpillRecordCodec.WriteValues(file, ref _writePosition, row, _options.Metrics);
        VdbeSpillRecordCodec.CompleteRecord(
            file,
            recordStart,
            _writePosition,
            _options.Metrics);
        var indexPosition = checked(
            (long)VdbeSpillRecordCodec.FileHeaderSize
            + ((long)slot * sizeof(long)));
        VdbeSpillRecordCodec.WriteInt64(
            index,
            ref indexPosition,
            recordStart,
            _options.Metrics);
    }

    private ReadLease ReadSpilledSlot(int targetSlot, CancellationToken cancellationToken)
    {
        var file = _temporaryFile?.File
            ?? throw new InvalidOperationException("Keyed row set has no spill file.");
        var index = _indexFile?.File
            ?? throw new InvalidOperationException("Keyed row set has no spill index.");
        cancellationToken.ThrowIfCancellationRequested();
        VdbeSpillRecordCodec.ValidateFile(
            index,
            VdbeSpillFileKind.KeyedRowSetIndex,
            _options.Metrics);
        var indexPosition = checked(
            (long)VdbeSpillRecordCodec.FileHeaderSize
            + ((long)targetSlot * sizeof(long)));
        var recordStart = VdbeSpillRecordCodec.ReadInt64(
            index,
            ref indexPosition,
            _options.Metrics);
        VdbeSpillRecordCodec.ValidateFile(
            file,
            VdbeSpillFileKind.KeyedRowSet,
            _options.Metrics);
        if (recordStart < VdbeSpillRecordCodec.FileHeaderSize || recordStart >= file.Length)
        {
            throw new InvalidDataException(
                $"Keyed row set spill index points slot {targetSlot} outside the data file.");
        }

        var position = recordStart;
        var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
            file,
            ref position,
            _options.Metrics);
        var slot = VdbeSpillRecordCodec.ReadInt32(
            file,
            ref position,
            _options.Metrics);
        if (slot != targetSlot)
        {
            throw new InvalidDataException(
                $"Keyed row set spill index maps slot {targetSlot} to record {slot}.");
        }

        var found = ReadLease.Read(
            file,
            ref position,
            _columnCount,
            recordEnd,
            _options.Metrics,
            _memory,
            cancellationToken);
        try
        {
            VdbeSpillRecordCodec.RequireRecordEnd(position, recordEnd);
            return found;
        }
        catch
        {
            found.Dispose();
            throw;
        }
    }

    private void ReleaseCurrent()
    {
        _current?.Dispose();
        _current = null;
    }

    private void ValidateWidth(SqlValue[] candidate)
    {
        if (_columnCount < 0)
        {
            _columnCount = candidate.Length;
            return;
        }
        if (candidate.Length != _columnCount)
        {
            throw new InvalidOperationException(
                $"Keyed row set stores {_columnCount}-column rows but received {candidate.Length} values.");
        }
    }

    private long SpillInfrastructureBytes() =>
        VdbeManagedFootprint.EstimateKeyedRowSetSpillInfrastructure(
            _options.TemporaryDirectory);

    private sealed class ReadLease : IDisposable
    {
        private VdbeExecutionMemory? _memory;
        private long _retainedBytes;

        private ReadLease(SqlValue[] row, VdbeExecutionMemory? memory, long retainedBytes)
        {
            Row = row;
            _memory = memory;
            _retainedBytes = retainedBytes;
        }

        public SqlValue[] Row { get; }

        public static ReadLease Borrowed(SqlValue[] row) => new(row, null, 0);

        public static ReadLease Read(
            Storage.IFile file,
            ref long position,
            int columnCount,
            long recordEnd,
            VdbeExecutionMetrics metrics,
            VdbeExecutionMemory memory,
            CancellationToken cancellationToken)
        {
            var retainedBytes = VdbeManagedFootprint.EstimateSorterRowFromEncodedLength(
                recordEnd - position,
                columnCount);
            memory.RetainOrThrow(retainedBytes);
            try
            {
                return new ReadLease(
                    VdbeSpillRecordCodec.ReadValues(
                        file,
                        ref position,
                        columnCount,
                        recordEnd,
                        metrics,
                        cancellationToken),
                    memory,
                    retainedBytes);
            }
            catch
            {
                memory.Release(retainedBytes);
                throw;
            }
        }

        public SqlValue[] Detach(out long retainedBytes)
        {
            retainedBytes = _retainedBytes;
            _retainedBytes = 0;
            _memory = null;
            return Row;
        }

        public void Dispose()
        {
            _memory?.Release(_retainedBytes);
            _retainedBytes = 0;
            _memory = null;
        }
    }
}
