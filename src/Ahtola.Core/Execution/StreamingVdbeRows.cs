using System.Runtime.ExceptionServices;

namespace Ahtola.Core;

/// <summary>
/// Lazily drains a compiled VDBE program. The runtime remains alive while a result row is
/// positioned so virtual-table cursors have the same lifetime as the prepared statement.
/// </summary>
internal sealed class StreamingVdbeRows(
    Execution.ResumableStatement runtime,
    CancellationToken defaultCancellationToken)
    : IReadOnlyList<SqlValue[]>, IDisposable
{
    private readonly List<SqlValue[]> _rows = [];
    private readonly CancellationToken _defaultCancellationToken = defaultCancellationToken;
    private Execution.ResumableStatement? _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    public int Count => Materialize().Count;

    public SqlValue[] this[int index] => Materialize()[index];

    public bool IsComplete => _runtime is null;

    public bool TryGetRow(int index, CancellationToken cancellationToken, out SqlValue[] row)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index < _rows.Count)
        {
            row = _rows[index];
            return true;
        }
        if (index != _rows.Count || _runtime is null)
        {
            row = [];
            return false;
        }

        try
        {
            while (true)
            {
                switch (_runtime.StepResumable(cancellationToken))
                {
                    case Execution.ResumableStatementStepResult.Row:
                        row = [.. _runtime.CurrentRow!];
                        _rows.Add(row);
                        return true;
                    case Execution.ResumableStatementStepResult.Done:
                        Dispose();
                        row = [];
                        return false;
                    default:
                        throw new EmbeddedSqlException("Compiled program yielded during evaluation.");
                }
            }
        }
        catch (Exception executionFailure)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(executionFailure, cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(executionFailure).Throw();
            throw;
        }
    }

    public bool HasAny() => TryGetRow(0, _defaultCancellationToken, out _);

    public IReadOnlyList<SqlValue[]> Materialize()
    {
        while (TryGetRow(_rows.Count, _defaultCancellationToken, out _))
        {
        }

        return _rows;
    }

    public IEnumerator<SqlValue[]> GetEnumerator() => Materialize().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref _runtime, null);
        owned?.Dispose();
    }
}
