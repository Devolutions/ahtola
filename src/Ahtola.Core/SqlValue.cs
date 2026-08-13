namespace Ahtola.Core;

public enum SqlValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

public readonly struct SqlValue : IEquatable<SqlValue>
{
    private readonly long _integer;
    private readonly double _real;
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _blob;
    private readonly bool _isJson;

    private SqlValue(
        SqlValueKind kind,
        long integer,
        double real,
        string? text,
        ReadOnlyMemory<byte> blob,
        bool isJson)
    {
        Kind = kind;
        _integer = integer;
        _real = real;
        _text = text;
        _blob = blob;
        _isJson = isJson;
    }

    public SqlValueKind Kind { get; }

    public static SqlValue Null => default;

    public static SqlValue Integer(long value) => new(SqlValueKind.Integer, value, default, null, default, false);

    /// <summary>Creates a REAL value, or NULL when <paramref name="value"/> is NaN.</summary>
    /// <remarks>
    /// SQLite has no NaN: <c>sqlite3VdbeMemSetDouble</c> stores a NULL instead, and
    /// <c>serialGet</c> reads a stored NaN back as NULL, so <c>0.0/0.0</c> and
    /// <c>1e999 - 1e999</c> are both NULL rather than a real. Folding the substitution into the
    /// factory keeps that invariant on every path that can produce a floating point result.
    /// </remarks>
    public static SqlValue Real(double value)
        => double.IsNaN(value)
            ? Null
            : new(SqlValueKind.Real, default, value, null, default, false);

    public static SqlValue Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlValueKind.Text, default, default, value, default, false);
    }

    internal static SqlValue JsonText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlValueKind.Text, default, default, value, default, true);
    }

    public static SqlValue Blob(ReadOnlySpan<byte> value)
        => new(SqlValueKind.Blob, default, default, null, value.ToArray(), false);

    internal bool IsJson => _isJson;

    internal SqlValue WithoutJsonSubtype()
        => _isJson ? Text(_text!) : this;

    public long AsInteger()
        => Kind == SqlValueKind.Integer
            ? _integer
            : throw InvalidKind(SqlValueKind.Integer);

    public double AsReal()
        => Kind == SqlValueKind.Real
            ? _real
            : throw InvalidKind(SqlValueKind.Real);

    public string AsText()
        => Kind == SqlValueKind.Text
            ? _text!
            : throw InvalidKind(SqlValueKind.Text);

    /// <summary>Returns a copy of this blob's bytes.</summary>
    /// <remarks>The returned memory never shares mutable storage with this SQL value.</remarks>
    public ReadOnlyMemory<byte> AsBlob()
        => Kind == SqlValueKind.Blob
            ? _blob.ToArray()
            : throw InvalidKind(SqlValueKind.Blob);

        /// <summary>Returns the owned blob bytes without allocating a defensive copy.</summary>
        /// <remarks>
        /// Internal hot-path readers (record encode, comparisons) must not mutate the returned
        /// span. Public callers keep <see cref="AsBlob"/> so exposed buffers stay snapshot-isolated.
        /// </remarks>
        internal ReadOnlySpan<byte> AsBlobSpan()
            => Kind == SqlValueKind.Blob
                ? _blob.Span
                : throw InvalidKind(SqlValueKind.Blob);

    public bool Equals(SqlValue other)
    {
        if (Kind != other.Kind)
            return false;

        return Kind switch
        {
            SqlValueKind.Null => true,
            SqlValueKind.Integer => _integer == other._integer,
            SqlValueKind.Real => _real.Equals(other._real),
            SqlValueKind.Text => string.Equals(_text, other._text, StringComparison.Ordinal),
            SqlValueKind.Blob => _blob.Span.SequenceEqual(other._blob.Span),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {Kind}."),
        };
    }

    public override bool Equals(object? obj) => obj is SqlValue value && Equals(value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);

        switch (Kind)
        {
            case SqlValueKind.Null:
                break;
            case SqlValueKind.Integer:
                hash.Add(_integer);
                break;
            case SqlValueKind.Real:
                hash.Add(_real);
                break;
            case SqlValueKind.Text:
                hash.Add(_text, StringComparer.Ordinal);
                break;
            case SqlValueKind.Blob:
                foreach (var value in _blob.Span)
                    hash.Add(value);
                break;
            default:
                throw new InvalidOperationException($"Unknown SQL value kind {Kind}.");
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(SqlValue left, SqlValue right) => left.Equals(right);

    public static bool operator !=(SqlValue left, SqlValue right) => !left.Equals(right);

    private InvalidOperationException InvalidKind(SqlValueKind expected)
        => new($"SQL value has kind {Kind}, not {expected}.");
}
