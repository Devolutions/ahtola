namespace Ahtola;

/// <summary>
/// Streaming remote result cursor shared by the Hrana HTTP <c>/v3/cursor</c> NDJSON
/// transport and the Hrana WebSocket <c>open_cursor</c>/<c>fetch_cursor</c> transport.
/// The base owns row buffering and column type inference so both transports expose
/// identical semantics to <see cref="AhtolaRemoteDataReader"/>.
/// </summary>
internal abstract class RemoteCursor : IAsyncDisposable
{
    private const int MaximumTypeInferenceLookaheadRows = 64;

    private readonly Queue<List<RemoteResponseValue>> _bufferedRows = new();
    private readonly HashSet<int> _exhaustedTypeInferenceOrdinals = [];
    private List<RemoteResponseValue>? _pendingRow;

    public List<RemoteColumn> Columns { get; protected set; } = [];

    public int RecordsAffected { get; protected set; }

    /// <summary>Set once the server signalled the end of the result stream.</summary>
    protected bool Terminated { get; set; }

    protected bool Disposed { get; set; }

    /// <summary>
    /// Finds the first non-null value for <paramref name="ordinal"/>, buffering the rows it
    /// had to read so the caller still observes them in order.
    /// </summary>
    public RemoteResponseValue? FindFirstNonNullValue(int ordinal, CancellationToken cancellationToken)
    {
        var collected = new List<List<RemoteResponseValue>>();
        while (_bufferedRows.TryDequeue(out var buffered))
            collected.Add(buffered);
        if (_pendingRow is not null)
        {
            collected.Add(_pendingRow);
            _pendingRow = null;
        }

        try
        {
            foreach (var row in collected)
            {
                if (ordinal < row.Count && row[ordinal].Type != "null")
                    return row[ordinal];
            }

            if (_exhaustedTypeInferenceOrdinals.Contains(ordinal))
                return null;

            var rowsRead = 0;
            while (!Terminated && rowsRead < MaximumTypeInferenceLookaheadRows)
            {
                var row = ReadRowAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                if (row is null)
                    break;
                collected.Add(row);
                rowsRead++;
                if (ordinal < row.Count && row[ordinal].Type != "null")
                    return row[ordinal];
            }
            if (!Terminated && rowsRead == MaximumTypeInferenceLookaheadRows)
                _exhaustedTypeInferenceOrdinals.Add(ordinal);
            return null;
        }
        finally
        {
            foreach (var row in collected)
                _bufferedRows.Enqueue(row);
        }
    }

    public bool EnsureHasRows(CancellationToken cancellationToken)
        => EnsureHasRowsAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

    public async ValueTask<bool> EnsureHasRowsAsync(CancellationToken cancellationToken)
    {
        if (_pendingRow is not null || _bufferedRows.Count > 0)
            return true;

        _pendingRow = await ReadRowAsync(cancellationToken).ConfigureAwait(false);
        return _pendingRow is not null;
    }

    public List<RemoteResponseValue>? ReadRow(CancellationToken cancellationToken)
        => ReadRowAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

    public async ValueTask<List<RemoteResponseValue>?> ReadRowAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_bufferedRows.TryDequeue(out var buffered))
            return buffered;
        if (_pendingRow is not null)
        {
            var pending = _pendingRow;
            _pendingRow = null;
            return pending;
        }
        if (Terminated)
            return null;

        return await FetchRowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the next row from the transport. Implementations return <c>null</c> once the
    /// result stream is exhausted and must set <see cref="Terminated"/> before doing so.
    /// </summary>
    protected abstract ValueTask<List<RemoteResponseValue>?> FetchRowAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    protected static AhtolaException Malformed(string detail)
        => new($"Unable to parse remote cursor response: {detail}.");
}
