namespace Ahtola.Core;

/// <summary>
/// A named sequence created by <c>CREATE SEQUENCE</c>: the immutable descriptor (start, increment, bounds,
/// cycle) plus the runtime state the backing table owns.
/// </summary>
/// <remarks>
/// Mirrors Turso's <c>Sequence</c> (core/schema.rs:695): validation happens at creation with upstream's
/// exact diagnostics, and the runtime watermark is <em>not</em> part of the descriptor — it lives in the
/// backing table row and is read on every <c>nextval</c>/<c>setval</c> call, so rollback and cross-connection
/// visibility fall out of ordinary transaction semantics.
/// </remarks>
internal sealed class ManagedSequence
{
    private ManagedSequence(
        string name,
        long startValue,
        long incrementBy,
        long minValue,
        long maxValue,
        bool cycle)
    {
        Name = name;
        StartValue = startValue;
        IncrementBy = incrementBy;
        MinValue = minValue;
        MaxValue = maxValue;
        Cycle = cycle;
    }

    public string Name { get; }
    public long StartValue { get; }
    public long IncrementBy { get; }
    public long MinValue { get; }
    public long MaxValue { get; }
    public bool Cycle { get; }
    public bool Ascending => IncrementBy > 0;

    /// <summary>
    /// Validates the creation options and applies Turso's defaults (schema.rs <c>Sequence::new</c>):
    /// increment defaults to 1 and may not be zero; min defaults to 1 ascending / <c>i64.MIN</c>
    /// descending; max defaults to <c>i64.MAX</c> ascending / -1 descending; start defaults to min
    /// ascending / max descending and must fall inside the bounds.
    /// </summary>
    public static ManagedSequence Create(
        string name,
        long? start,
        long? increment,
        long? minValue,
        long? maxValue,
        bool cycle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var incrementBy = increment ?? 1;
        if (incrementBy == 0)
            throw new EmbeddedSqlException("INCREMENT must not be zero");

        var minVal = minValue ?? (incrementBy > 0 ? 1 : long.MinValue);
        var maxVal = maxValue ?? (incrementBy > 0 ? long.MaxValue : -1);
        if (minVal >= maxVal)
            throw new EmbeddedSqlException($"MINVALUE ({minVal}) must be less than MAXVALUE ({maxVal})");

        var startVal = start ?? (incrementBy > 0 ? minVal : maxVal);
        if (startVal < minVal)
            throw new EmbeddedSqlException($"START value ({startVal}) cannot be less than MINVALUE ({minVal})");
        if (startVal > maxVal)
            throw new EmbeddedSqlException($"START value ({startVal}) cannot be greater than MAXVALUE ({maxVal})");

        return new ManagedSequence(name, startVal, incrementBy, minVal, maxVal, cycle);
    }

    /// <summary>
    /// The next value Turso's <c>SequenceComputeNext</c> derives from the backing-table row: an empty table
    /// restarts at <c>start</c>, an uncalled watermark returns itself, and a called watermark advances by
    /// the increment — wrapping on <c>CYCLE</c>, exhausting otherwise.
    /// </summary>
    public long ComputeNext(long current, bool isCalled, bool wasEmpty)
    {
        if (wasEmpty)
            return StartValue;
        if (!isCalled)
            return current;

        var exhausted = Ascending
            ? (current > long.MaxValue - IncrementBy) || (checked(current + IncrementBy) > MaxValue)
            : (current < long.MinValue - IncrementBy) || (checked(current + IncrementBy) < MinValue);
        if (exhausted)
        {
            if (Cycle)
                return Ascending ? MinValue : MaxValue;

            throw new EmbeddedSqlException(
                $"nextval: reached {(Ascending ? "maximum" : "minimum")} value of sequence \"{Name}\"");
        }

        return checked(current + IncrementBy);
    }
}
