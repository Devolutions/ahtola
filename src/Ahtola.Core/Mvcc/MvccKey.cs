using Ahtola.Core.Storage;

namespace Ahtola.Core.Mvcc;

/// <summary>
/// The identity portion of an MVCC row key. A rowid table uses
/// <see cref="Integer"/>; WITHOUT ROWID tables and indexes use a complete SQLite
/// record encoded with the owning object's key descriptor.
/// </summary>
internal readonly struct MvccKey : IEquatable<MvccKey>
{
    private readonly long _integer;
    private readonly byte[]? _record;

    private MvccKey(long integer)
    {
        _integer = integer;
        _record = null;
        Kind = MvccKeyKind.Integer;
    }

    private MvccKey(byte[] record)
    {
        _integer = default;
        _record = record;
        Kind = MvccKeyKind.Record;
    }

    internal MvccKeyKind Kind { get; }

    internal bool IsInteger => Kind == MvccKeyKind.Integer;

    internal long Integer
        => IsInteger
            ? _integer
            : throw new InvalidOperationException("The MVCC key is a SQLite record, not an integer rowid.");

    internal ReadOnlyMemory<byte> Record
        => !IsInteger
            ? _record!
            : throw new InvalidOperationException("The MVCC key is an integer rowid, not a SQLite record.");

    internal static MvccKey FromInteger(long value) => new(value);

    internal static MvccKey FromRecord(ReadOnlySpan<byte> value)
        => new(value.ToArray());

    internal static MvccKey FromPrimaryKey(
        SqlitePrimaryKeySchema schema,
        IReadOnlyList<SqlValue> row,
        SqliteTextEncoding textEncoding)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(row);
        var values = schema.ProjectKey(row);
        for (var index = 0; index < values.Length; index++)
            values[index] = Canonicalize(values[index], schema.Terms[index].Collation);
        return FromRecord(SqliteRecordCodec.Encode(values, textEncoding));
    }

    public bool Equals(MvccKey other)
    {
        if (Kind != other.Kind)
            return false;
        return IsInteger || Record.Span.SequenceEqual(other.Record.Span);
    }

    public override bool Equals(object? obj) => obj is MvccKey other && Equals(other);

    public static bool operator ==(MvccKey left, MvccKey right) => left.Equals(right);

    public static bool operator !=(MvccKey left, MvccKey right) => !left.Equals(right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((byte)Kind);
        if (IsInteger)
        {
            hash.Add(_integer);
        }
        else
        {
            foreach (var value in _record!)
                hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
        => IsInteger ? _integer.ToString() : Convert.ToHexString(_record!);

    private static SqlValue Canonicalize(SqlValue value, SqliteKeyCollation collation)
    {
        if (value.Kind == SqlValueKind.Real)
        {
            var real = value.AsReal();
            if (real >= long.MinValue && real < -(double)long.MinValue)
            {
                var integer = (long)real;
                if (real == integer)
                    return SqlValue.Integer(integer);
            }
        }

        if (value.Kind != SqlValueKind.Text)
            return value;

        var text = value.AsText();
        if (collation.IsNoCase)
        {
            var chars = text.ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                if (chars[index] is >= 'A' and <= 'Z')
                    chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
            return SqlValue.Text(new string(chars));
        }

        return collation.IsRTrim
            ? SqlValue.Text(text.TrimEnd(' '))
            : value;
    }
}

internal enum MvccKeyKind : byte
{
    Integer = 0,
    Record = 1,
}
