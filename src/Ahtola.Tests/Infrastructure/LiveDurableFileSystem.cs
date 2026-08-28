using Ahtola.Core.Storage;

namespace Ahtola.Tests;

internal sealed record PowerLossSnapshot(
    IReadOnlyDictionary<string, byte[]> Files,
    int SelectedMutationCount = 0,
    int DroppedMutationCount = 0);

/// <summary>
/// Test storage with separate process-visible and fsync-acknowledged images.
/// It follows Turso's <c>UnreliableIo</c> model: writes and truncations are
/// volatile until the affected file is flushed.
/// </summary>
internal sealed class LiveDurableFileSystem :
    IFileSystem,
    IAtomicFileSystem,
    IStoragePathResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private TornFlushPlan? _tornFlush;
    private PowerLossSnapshot? _tornFlushSnapshot;
    private long _writeStamp;

    public StringComparer PathComparer => StringComparer.Ordinal;

    public string GetCanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return path;
    }

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
            var exists = _files.TryGetValue(path, out var state);
            switch (mode)
            {
                case FileOpenMode.OpenExisting when !exists:
                    throw new FileNotFoundException("The requested crash-test file does not exist.", path);
                case FileOpenMode.CreateNew when exists:
                    throw new IOException($"The crash-test file '{path}' already exists.");
            }

            if (!exists)
            {
                state = new FileState();
                _files.Add(path, state);
            }

            return new FileView(this, path, state!, readOnly);
        }
    }

    public void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            if (_files.Remove(path))
                _writeStamp++;
        }
    }

    public FileWriteStamp? GetWriteStamp(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            return _files.TryGetValue(path, out var state)
                ? new FileWriteStamp(state.Live.LongLength, DateTimeOffset.UnixEpoch.AddTicks(state.Stamp))
                : null;
        }
    }

    void IAtomicFileSystem.ReplaceFileAtomically(
        string sourcePath,
        string destinationPath,
        bool replaceEmptyDestination)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            throw new IOException("Atomic file replacement requires distinct paths.");

        lock (_gate)
        {
            if (!_files.TryGetValue(sourcePath, out var source))
                throw new FileNotFoundException("The atomic replacement source does not exist.", sourcePath);
            if (_files.TryGetValue(destinationPath, out var destination)
                && (!replaceEmptyDestination || destination.Live.Length != 0))
            {
                throw new IOException("output file already exists");
            }

            _files.Remove(sourcePath);
            _files[destinationPath] = source;
            source.Stamp = ++_writeStamp;
        }
    }

    internal IReadOnlyList<string> EnumerateFilePaths()
    {
        lock (_gate)
            return [.. _files.Keys];
    }

    internal void MarkAllDurable()
    {
        lock (_gate)
        {
            foreach (var state in _files.Values)
                state.PromoteAll();
        }
    }

    internal PowerLossSnapshot CaptureDurableSnapshot()
    {
        lock (_gate)
            return BuildDurableSnapshot();
    }

    internal void ArmTornFlush(string path, params int[] selectedMutationOccurrences)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(selectedMutationOccurrences);
        if (selectedMutationOccurrences.Any(static occurrence => occurrence < 1))
            throw new ArgumentOutOfRangeException(
                nameof(selectedMutationOccurrences),
                "Mutation occurrences are one-based.");

        lock (_gate)
        {
            _tornFlush = new TornFlushPlan(path, selectedMutationOccurrences.ToHashSet());
            _tornFlushSnapshot = null;
        }
    }

    internal PowerLossSnapshot TakeTornFlushSnapshot()
    {
        lock (_gate)
        {
            var snapshot = _tornFlushSnapshot
                ?? throw new InvalidOperationException("The armed file has not flushed pending mutations.");
            _tornFlushSnapshot = null;
            return snapshot;
        }
    }

    internal void RestoreAfterPowerLoss()
        => RestoreAfterPowerLoss(CaptureDurableSnapshot());

    internal void RestoreAfterPowerLoss(PowerLossSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _files.Clear();
            foreach (var (path, bytes) in snapshot.Files)
            {
                var image = bytes.ToArray();
                _files.Add(path, new FileState
                {
                    Live = image.ToArray(),
                    Durable = image,
                    DurableExists = true,
                    Stamp = ++_writeStamp,
                });
            }

            _tornFlush = null;
            _tornFlushSnapshot = null;
        }
    }

    private PowerLossSnapshot BuildDurableSnapshot(
        string? tornPath = null,
        IReadOnlySet<int>? selectedOccurrences = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var selected = 0;
        var dropped = 0;
        foreach (var (path, state) in _files)
        {
            var include = state.DurableExists;
            var image = state.Durable.ToArray();
            if (string.Equals(path, tornPath, StringComparison.Ordinal))
            {
                for (var index = 0; index < state.Pending.Count; index++)
                {
                    if (selectedOccurrences!.Contains(index + 1))
                    {
                        state.Pending[index].Apply(ref image);
                        include = true;
                        selected++;
                    }
                    else
                    {
                        dropped++;
                    }
                }
            }

            if (include)
                files.Add(path, image);
        }

        return new PowerLossSnapshot(files, selected, dropped);
    }

    private void Write(FileState state, long position, ReadOnlySpan<byte> source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        lock (_gate)
        {
            var data = source.ToArray();
            var end = checked((int)position + data.Length);
            if (state.Live.Length < end)
                Array.Resize(ref state.Live, end);
            data.CopyTo(state.Live.AsSpan((int)position));
            state.Pending.Add(new WriteMutation(position, data));
            state.Stamp = ++_writeStamp;
        }
    }

    private int Read(FileState state, long position, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        lock (_gate)
        {
            if (position >= state.Live.LongLength || destination.IsEmpty)
                return 0;
            var count = (int)Math.Min(destination.Length, state.Live.LongLength - position);
            state.Live.AsSpan((int)position, count).CopyTo(destination);
            return count;
        }
    }

    private void SetLength(FileState state, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > int.MaxValue)
            throw new IOException("The crash-test file cannot exceed two gigabytes.");

        lock (_gate)
        {
            Array.Resize(ref state.Live, (int)length);
            state.Pending.Add(new SetLengthMutation(length));
            state.Stamp = ++_writeStamp;
        }
    }

    private void Flush(string path, FileState state)
    {
        lock (_gate)
        {
            if (_tornFlush is { } plan
                && string.Equals(plan.Path, path, StringComparison.Ordinal)
                && state.Pending.Count > 0)
            {
                _tornFlushSnapshot = BuildDurableSnapshot(path, plan.SelectedOccurrences);
                _tornFlush = null;
            }

            state.PromoteAll();
        }
    }

    private sealed class FileView(
        LiveDurableFileSystem owner,
        string path,
        FileState state,
        bool readOnly) : IFile
    {
        private bool _disposed;

        public long Length
        {
            get
            {
                ThrowIfDisposed();
                lock (owner._gate)
                    return state.Live.LongLength;
            }
        }

        public bool IsReadOnly { get; } = readOnly;

        public int Read(long position, Span<byte> destination)
        {
            ThrowIfDisposed();
            return owner.Read(state, position, destination);
        }

        public void Write(long position, ReadOnlySpan<byte> source)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            owner.Write(state, position, source);
        }

        public void SetLength(long length)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            owner.SetLength(state, length);
        }

        public void FlushToDisk()
        {
            ThrowIfDisposed();
            if (!IsReadOnly)
                owner.Flush(path, state);
        }

        public void Dispose() => _disposed = true;

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        private void ThrowIfReadOnly()
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot mutate a file opened read-only.");
        }
    }

    private sealed class FileState
    {
        internal byte[] Live = [];
        internal byte[] Durable = [];
        internal bool DurableExists;
        internal long Stamp;
        internal List<FileMutation> Pending { get; } = [];

        internal void PromoteAll()
        {
            Durable = Live.ToArray();
            DurableExists = true;
            Pending.Clear();
        }
    }

    private abstract record FileMutation
    {
        internal abstract void Apply(ref byte[] image);
    }

    private sealed record WriteMutation(long Position, byte[] Data) : FileMutation
    {
        internal override void Apply(ref byte[] image)
        {
            var end = checked((int)Position + Data.Length);
            if (image.Length < end)
                Array.Resize(ref image, end);
            Data.CopyTo(image.AsSpan((int)Position));
        }
    }

    private sealed record SetLengthMutation(long Length) : FileMutation
    {
        internal override void Apply(ref byte[] image)
            => Array.Resize(ref image, checked((int)Length));
    }

    private sealed record TornFlushPlan(
        string Path,
        IReadOnlySet<int> SelectedOccurrences);
}
