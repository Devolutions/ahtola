using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Streams rows from one <see cref="AhtolaBrowserBoundedConnection.ExecuteBoundedScanAsync"/>
/// call. Each <see cref="ReadAsync"/> advances the underlying page-bounded async scan cursor by
/// exactly one row, awaiting a page fetch only when the traversal crosses into a page not
/// already resident in its <see cref="BoundedAsyncPageCache"/>.
/// </summary>
public sealed class AhtolaBrowserBoundedReader : IAsyncDisposable
{
    private readonly IAsyncEnumerator<SqlValue[]> _rows;
    private readonly IReadOnlyList<int> _projectedColumnIndexes;
    private readonly IReadOnlyList<string> _columnNames;
    private readonly AsyncSqlitePagerReadTransaction _readTransaction;
    private SqlValue[]? _current;
    private int _disposed;

    internal AhtolaBrowserBoundedReader(
        IAsyncEnumerable<SqlValue[]> rows,
        IReadOnlyList<int> projectedColumnIndexes,
        IReadOnlyList<string> columnNames,
        AsyncSqlitePagerReadTransaction readTransaction,
        CancellationToken cancellationToken)
    {
        _rows = rows.GetAsyncEnumerator(cancellationToken);
        _projectedColumnIndexes = projectedColumnIndexes;
        _columnNames = columnNames;
        _readTransaction = readTransaction;
    }

    /// <summary>The number of projected columns.</summary>
    public int FieldCount => _projectedColumnIndexes.Count;

    /// <summary>The projected column's name at <paramref name="ordinal"/>.</summary>
    public string GetName(int ordinal) => _columnNames[ordinal];

    /// <summary>
    /// Advances to the next row, returning <see langword="false"/> once the scan (and any
    /// <c>LIMIT</c>) is exhausted.
    /// </summary>
    /// <remarks>
    /// The cancellation token supplied to the connection's
    /// <c>ExecuteBoundedScanAsync</c> call governs the whole scan; <paramref name="cancellationToken"/>
    /// here is honored only for an upfront cancellation check, since the underlying async
    /// iterator's cancellation token is fixed for its lifetime.
    /// </remarks>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!await _rows.MoveNextAsync().ConfigureAwait(false))
            {
                _current = null;
                return false;
            }
        }
        catch (SqliteBoundedPageBudgetExceededException exception)
        {
            throw new AhtolaBrowserBoundedQueryException(exception.Message, exception);
        }

        _current = _rows.Current;
        return true;
    }

    /// <summary>Reads the projected column's value at <paramref name="ordinal"/> for the current row.</summary>
    public SqlValue GetValue(int ordinal)
    {
        ThrowIfDisposed();
        if (_current is not { } current)
            throw new InvalidOperationException("Call ReadAsync and observe a true result before reading a value.");

        return current[_projectedColumnIndexes[ordinal]];
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _rows.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _readTransaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
