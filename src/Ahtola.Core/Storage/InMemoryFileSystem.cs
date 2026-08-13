namespace Ahtola.Core.Storage;

/// <summary>
/// Deterministic fault scheduler for testing storage error paths. Faults are
/// scheduled against a specific <see cref="FileSystemOperation"/> and a 1-based
/// occurrence count, so a test can, for example, fail the third write and assert
/// that higher layers surface the error without corrupting on-disk invariants.
/// This mirrors the fault-injection approach used by the engine's simulator and
/// differential testing harnesses.
/// </summary>
public sealed class DeterministicFaultInjector
{
    private readonly object _gate = new();
    private readonly Dictionary<FileSystemOperation, long> _counts = new();
    private readonly Dictionary<(FileSystemOperation Operation, long Occurrence), string> _scheduled = new();
    private readonly Dictionary<
        (FileSystemOperation Operation, long Occurrence),
        (FileSystemOperation Operation, string Message)> _scheduledAfter = new();

    /// <summary>
    /// Schedules a fault for the <paramref name="occurrence"/>-th (1-based)
    /// invocation of <paramref name="operation"/>.
    /// </summary>
    public void FailOnOccurrence(FileSystemOperation operation, long occurrence, string? message = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);
        lock (_gate)
            _scheduled[(operation, occurrence)] = message ?? DefaultMessage(operation, occurrence);
    }

    /// <summary>Schedules a fault for the next invocation of <paramref name="operation"/>.</summary>
    public void FailNext(FileSystemOperation operation, string? message = null)
    {
        lock (_gate)
        {
            var next = _counts.GetValueOrDefault(operation) + 1;
            _scheduled[(operation, next)] = message ?? DefaultMessage(operation, next);
        }
    }

    /// <summary>
    /// Schedules a fault on the first <paramref name="operation"/> after the next
    /// <paramref name="trigger"/> invocation.
    /// </summary>
    public void FailNextAfter(
        FileSystemOperation trigger,
        FileSystemOperation operation,
        string? message = null)
    {
        lock (_gate)
        {
            var nextTrigger = _counts.GetValueOrDefault(trigger) + 1;
            _scheduledAfter[(trigger, nextTrigger)] = (
                operation,
                message ?? $"Injected {operation} fault after {trigger} occurrence {nextTrigger}.");
        }
    }

    /// <summary>Returns the number of times <paramref name="operation"/> has run.</summary>
    public long GetOperationCount(FileSystemOperation operation)
    {
        lock (_gate)
            return _counts.GetValueOrDefault(operation);
    }

    /// <summary>Removes any scheduled faults without resetting occurrence counters.</summary>
    public void ClearScheduled()
    {
        lock (_gate)
        {
            _scheduled.Clear();
            _scheduledAfter.Clear();
        }
    }

    /// <summary>
    /// Records that <paramref name="operation"/> is about to run and throws an
    /// <see cref="IOException"/> when a fault is scheduled for this occurrence.
    /// Called by deterministic backends before performing the work so a failed
    /// mutation never takes effect.
    /// </summary>
    internal void BeforeOperation(FileSystemOperation operation)
    {
        string message;
        lock (_gate)
        {
            var count = _counts.GetValueOrDefault(operation) + 1;
            _counts[operation] = count;
            if (_scheduledAfter.Remove((operation, count), out var delayed))
            {
                var delayedOccurrence = _counts.GetValueOrDefault(delayed.Operation) + 1;
                _scheduled[(delayed.Operation, delayedOccurrence)] = delayed.Message;
            }

            if (!_scheduled.Remove((operation, count), out message!))
                return;
        }

        throw new IOException(message);
    }

    private static string DefaultMessage(FileSystemOperation operation, long occurrence)
        => $"Injected {operation} fault at occurrence {occurrence}.";
}

/// <summary>
/// Deterministic, page-backed in-memory <see cref="IFileSystem"/>. Storage is
/// sparse (only written pages are allocated) and reads zero-fill holes, matching
/// the semantics of the engine's in-memory I/O backend. An optional
/// <see cref="DeterministicFaultInjector"/> makes it usable for error-path tests.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem, IAtomicFileSystem
{
    private const int BlockSize = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, InMemoryFile> _files = new(StringComparer.Ordinal);
    private readonly DeterministicFaultInjector? _faults;

    public InMemoryFileSystem(DeterministicFaultInjector? faults = null) => _faults = faults;

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
            return _files.ContainsKey(path);
    }

    public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (readOnly && mode == FileOpenMode.CreateNew)
            throw new ArgumentException("A newly created file cannot be opened read-only.", nameof(readOnly));

        lock (_gate)
        {
            var exists = _files.TryGetValue(path, out var store);
            switch (mode)
            {
                case FileOpenMode.OpenExisting when !exists:
                    throw new FileNotFoundException("The requested in-memory file does not exist.", path);
                case FileOpenMode.CreateNew when exists:
                    throw new IOException($"The in-memory file '{path}' already exists.");
            }

            if (!exists)
            {
                store = new InMemoryFile(this);
                _files[path] = store;
            }

            return store!.OpenView(readOnly);
        }
    }

    public void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
            _files.Remove(path);
    }

    void IAtomicFileSystem.ReplaceFileAtomically(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            throw new IOException("Atomic file replacement requires distinct source and destination paths.");

        _faults?.BeforeOperation(FileSystemOperation.AtomicReplace);
        lock (_gate)
        {
            if (!_files.TryGetValue(sourcePath, out var source))
                throw new FileNotFoundException("The atomic replacement source does not exist.", sourcePath);
            if (_files.TryGetValue(destinationPath, out var destination)
                && (!replaceEmptyDestination || destination.Length != 0))
            {
                throw new IOException("output file already exists");
            }

            _files.Remove(sourcePath);
            _files[destinationPath] = source;
        }
    }

    internal void SignalOperation(FileSystemOperation operation) => _faults?.BeforeOperation(operation);

    /// <summary>
    /// Page-addressed byte store shared by every open view of the same path.
    /// Views are thin handles; the backing bytes live here so that closing one
    /// view does not discard data, mirroring a real file.
    /// </summary>
    internal sealed class InMemoryFile
    {
        private readonly InMemoryFileSystem _owner;
        private readonly object _storeGate = new();
        private readonly SortedDictionary<long, byte[]> _blocks = new();
        private long _length;

        internal InMemoryFile(InMemoryFileSystem owner)
        {
            _owner = owner;
        }

        internal IFile OpenView(bool readOnly) => new View(this, readOnly);

        private int Read(long position, Span<byte> destination)
        {
            _owner.SignalOperation(FileSystemOperation.Read);
            lock (_storeGate)
            {
                if (position >= _length || destination.Length == 0)
                    return 0;

                var toRead = (int)Math.Min(destination.Length, _length - position);
                var remaining = toRead;
                var offset = position;
                var bufferOffset = 0;
                while (remaining > 0)
                {
                    var blockIndex = offset / BlockSize;
                    var blockOffset = (int)(offset % BlockSize);
                    var chunk = Math.Min(remaining, BlockSize - blockOffset);
                    var slice = destination.Slice(bufferOffset, chunk);
                    if (_blocks.TryGetValue(blockIndex, out var block))
                        block.AsSpan(blockOffset, chunk).CopyTo(slice);
                    else
                        slice.Clear();

                    offset += chunk;
                    bufferOffset += chunk;
                    remaining -= chunk;
                }

                return toRead;
            }
        }

        private void Write(long position, ReadOnlySpan<byte> source)
        {
            _owner.SignalOperation(FileSystemOperation.Write);
            if (source.IsEmpty)
                return;

            var end = checked(position + source.Length);
            lock (_storeGate)
            {
                var remaining = source.Length;
                var offset = position;
                var bufferOffset = 0;
                while (remaining > 0)
                {
                    var blockIndex = offset / BlockSize;
                    var blockOffset = (int)(offset % BlockSize);
                    var chunk = Math.Min(remaining, BlockSize - blockOffset);
                    if (!_blocks.TryGetValue(blockIndex, out var block))
                    {
                        block = new byte[BlockSize];
                        _blocks[blockIndex] = block;
                    }

                    source.Slice(bufferOffset, chunk).CopyTo(block.AsSpan(blockOffset, chunk));
                    offset += chunk;
                    bufferOffset += chunk;
                    remaining -= chunk;
                }

                _length = Math.Max(_length, end);
            }
        }

        private void SetLength(long length)
        {
            _owner.SignalOperation(FileSystemOperation.SetLength);
            lock (_storeGate)
            {
                if (length < _length)
                {
                    var keepBlocks = (length + BlockSize - 1) / BlockSize;
                    var stale = _blocks.Keys.Where(index => index >= keepBlocks).ToArray();
                    foreach (var index in stale)
                        _blocks.Remove(index);

                    // Zero any bytes that survive in the final retained block.
                    if (length % BlockSize != 0 && _blocks.TryGetValue(length / BlockSize, out var tail))
                        tail.AsSpan((int)(length % BlockSize)).Clear();
                }

                _length = length;
            }
        }

        internal long Length
        {
            get
            {
                lock (_storeGate)
                    return _length;
            }
        }

        private void FlushToDisk() => _owner.SignalOperation(FileSystemOperation.FlushToDisk);

        private sealed class View : IFile
        {
            private readonly InMemoryFile _file;
            private bool _disposed;

            internal View(InMemoryFile file, bool readOnly)
            {
                _file = file;
                IsReadOnly = readOnly;
            }

            public bool IsReadOnly { get; }

            public long Length
            {
                get
                {
                    ThrowIfDisposed();
                    return _file.Length;
                }
            }

            public int Read(long position, Span<byte> destination)
            {
                ThrowIfDisposed();
                ArgumentOutOfRangeException.ThrowIfNegative(position);
                return _file.Read(position, destination);
            }

            public void Write(long position, ReadOnlySpan<byte> source)
            {
                ThrowIfDisposed();
                ArgumentOutOfRangeException.ThrowIfNegative(position);
                if (IsReadOnly)
                    throw new InvalidOperationException("Cannot write to a file opened read-only.");

                _file.Write(position, source);
            }

            public void SetLength(long length)
            {
                ThrowIfDisposed();
                ArgumentOutOfRangeException.ThrowIfNegative(length);
                if (IsReadOnly)
                    throw new InvalidOperationException("Cannot resize a file opened read-only.");

                _file.SetLength(length);
            }

            public void FlushToDisk()
            {
                ThrowIfDisposed();
                if (IsReadOnly)
                    return;

                _file.FlushToDisk();
            }

            public void Dispose() => _disposed = true;

            private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
