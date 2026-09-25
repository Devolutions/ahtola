using System.Runtime.ExceptionServices;
using Ahtola.Core.Execution;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private sealed class WindowInputScope : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public void Own(IDisposable resource) => _owned.Add(resource);

        public void Dispose()
        {
            Exception? failure = null;
            foreach (var resource in _owned)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                }
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class SpilledWindowInputList : IReadOnlyList<WindowFunctionInput>, IDisposable
    {
        private static readonly SqlValue[] Included = [SqlValue.Integer(1)];
        private static readonly SqlValue[] Excluded = [SqlValue.Integer(0)];
        private static readonly SqlValue[] NullArgument = [SqlValue.Null];

        private readonly VdbeWindowEvaluationResources _resources;
        private readonly VdbeMemoryReservation _infrastructure;
        private readonly int _count;
        private readonly int _argumentCount;
        private VdbeTemporaryFile? _dataFile;
        private VdbeTemporaryFile? _indexFile;
        private WindowFunctionInput _cached;
        private long _cachedBytes;
        private long _writePosition;
        private int _cachedIndex = -1;
        private int _written;
        private bool _disposed;

        public SpilledWindowInputList(
            int count,
            int argumentCount,
            VdbeWindowEvaluationResources resources)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfNegative(argumentCount);
            _count = count;
            _argumentCount = argumentCount;
            _resources = resources;
            _infrastructure = VdbeMemoryReservation.Create(
                resources.Memory,
                VdbeManagedFootprint.EstimateWindowBufferSpillInfrastructure(
                    resources.Options.TemporaryDirectory));
            try
            {
                _dataFile = VdbeTemporaryFile.Create(resources.Options, "window-input");
                _indexFile = VdbeTemporaryFile.Create(resources.Options, "window-input-index");
                _writePosition = VdbeSpillRecordCodec.InitializeFile(
                    _dataFile.File, VdbeSpillFileKind.WindowInput, resources.Options.Metrics);
                resources.Options.Metrics.WindowInputSpilled();
            }
            catch (Exception original)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException(original, cleanup);
                }
                throw;
            }
        }

        public int Count => _count;

        public void Append(WindowFunctionInput input)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_written >= _count
                || input.Arguments.Length != (input.Included ? _argumentCount : 0))
                throw new InvalidOperationException("Window input spill has an invalid row shape.");
            _resources.CancellationToken.ThrowIfCancellationRequested();
            var memory = _resources.Memory;
            var bytes = checked(
                VdbeManagedFootprint.EstimateSorterRow(input.Arguments)
                + VdbeManagedFootprint.EstimateWindowTupleSlots(1));
            memory.RetainOrThrow(bytes, rows: 0);
            try
            {
                var file = _dataFile!.File;
                var start = _writePosition;
                var recordStart = VdbeSpillRecordCodec.BeginRecord(ref _writePosition);
                VdbeSpillRecordCodec.WriteValues(
                    file, ref _writePosition, input.Included ? Included : Excluded,
                    _resources.Options.Metrics);
                if (input.Included)
                {
                    VdbeSpillRecordCodec.WriteValues(
                        file, ref _writePosition, input.Arguments, _resources.Options.Metrics);
                }
                else
                {
                    for (var index = 0; index < _argumentCount; index++)
                        VdbeSpillRecordCodec.WriteValues(
                            file, ref _writePosition, NullArgument, _resources.Options.Metrics);
                }
                VdbeSpillRecordCodec.CompleteRecord(
                    file, recordStart, _writePosition, _resources.Options.Metrics);
                var indexPosition = checked((long)_written * sizeof(long));
                VdbeSpillRecordCodec.WriteInt64(
                    _indexFile!.File, ref indexPosition, start, _resources.Options.Metrics);
                _written++;
            }
            finally
            {
                memory.Release(bytes, rows: 0);
            }
        }

        public WindowFunctionInput this[int index]
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _resources.CancellationToken.ThrowIfCancellationRequested();
                if ((uint)index >= (uint)_written)
                    throw new ArgumentOutOfRangeException(nameof(index));
                if (_cachedIndex == index)
                    return _cached;
                ReleaseCache();

                var indexPosition = checked((long)index * sizeof(long));
                var file = _dataFile!.File;
                var position = VdbeSpillRecordCodec.ReadInt64(
                    _indexFile!.File, ref indexPosition, _resources.Options.Metrics);
                if (position < VdbeSpillRecordCodec.FileHeaderSize || position >= file.Length)
                    throw new InvalidDataException("Window input spill index contains an invalid offset.");
                var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
                    file, ref position, _resources.Options.Metrics);
                var bytes = checked(
                    VdbeManagedFootprint.EstimateSorterRowFromEncodedLength(
                        recordEnd - position, _argumentCount + 1)
                    + VdbeManagedFootprint.EstimateWindowTupleSlots(0));
                _resources.Memory.RetainOrThrow(bytes, rows: 0);
                try
                {
                    var marker = VdbeSpillRecordCodec.ReadValues(
                        file, ref position, 1, recordEnd,
                        _resources.Options.Metrics, _resources.CancellationToken)[0];
                    if (marker.Kind != SqlValueKind.Integer
                        || marker.AsInteger() is not (0 or 1))
                        throw new InvalidDataException("Window input spill contains an invalid filter marker.");
                    var arguments = VdbeSpillRecordCodec.ReadValues(
                        file, ref position, _argumentCount, recordEnd,
                        _resources.Options.Metrics, _resources.CancellationToken);
                    VdbeSpillRecordCodec.RequireRecordEnd(position, recordEnd);
                    _cached = new WindowFunctionInput(
                        marker.AsInteger() == 1,
                        marker.AsInteger() == 1 ? arguments : []);
                    _cachedIndex = index;
                    _cachedBytes = bytes;
                    return _cached;
                }
                catch
                {
                    _resources.Memory.Release(bytes, rows: 0);
                    throw;
                }
            }
        }

        public IEnumerator<WindowFunctionInput> GetEnumerator()
        {
            for (var index = 0; index < _count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        private void ReleaseCache()
        {
            if (_cachedIndex < 0)
                return;
            _cached = default;
            _cachedIndex = -1;
            _resources.Memory.Release(_cachedBytes, rows: 0);
            _cachedBytes = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            try
            {
                ReleaseCache();
                _indexFile?.Dispose();
            }
            finally
            {
                try
                {
                    _dataFile?.Dispose();
                }
                finally
                {
                    _infrastructure.Dispose();
                }
            }
            _disposed = true;
        }
    }
}
