using System.Buffers;
using System.Text;

namespace Ahtola.Core.Storage;

/// <summary>
/// Compares SQLite index records using retained term collations and directions.
/// </summary>
/// <remarks>
/// Fields after the retained terms use ascending BINARY order. For an ordinary
/// SQLite index this is the appended rowid field.
/// </remarks>
public sealed class SqliteIndexRecordComparer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, false, true);

    private readonly SqliteIndexComparisonTerm[] _terms;

    /// <summary>Creates a comparer for records encoded using <paramref name="textEncoding"/>.</summary>
    public SqliteIndexRecordComparer(SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8)
        : this(textEncoding, Array.Empty<SqliteIndexComparisonTerm>())
    {
    }

    /// <summary>
    /// Creates a comparer whose listed leading fields use the supplied sort directions.
    /// Fields after the list remain ascending, including the rowid suffix of ordinary indexes.
    /// </summary>
    public SqliteIndexRecordComparer(
        SqliteTextEncoding textEncoding,
        IReadOnlyList<bool> descendingFields)
        : this(
            textEncoding,
            descendingFields,
            Enumerable.Repeat<string?>("BINARY", descendingFields?.Count ?? 0).ToArray())
    {
    }

    /// <summary>
    /// Creates a comparer whose listed leading fields use the supplied sort
    /// directions and built-in SQLite collations.
    /// </summary>
    public SqliteIndexRecordComparer(
        SqliteTextEncoding textEncoding,
        IReadOnlyList<bool> descendingFields,
        IReadOnlyList<string?> collations)
        : this(textEncoding, CreateTerms(descendingFields, collations))
    {
    }

    /// <summary>Creates a comparer for schema-aware leading index terms.</summary>
    public SqliteIndexRecordComparer(
        SqliteTextEncoding textEncoding,
        IReadOnlyList<SqliteIndexComparisonTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        TextEncoding = textEncoding is SqliteTextEncoding.Unset
            ? SqliteTextEncoding.Utf8
            : textEncoding;
        _ = GetTextEncoding(TextEncoding);
        _terms = terms.ToArray();
        foreach (var term in _terms)
        {
            ArgumentNullException.ThrowIfNull(term.Collation);
            if (!term.Collation.IsAvailable)
                throw new NotSupportedException("SQLite index comparison requires concrete collation metadata.");
            if (!term.Collation.IsSupportedByManagedIndexWriter)
            {
                throw new NotSupportedException(
                    $"SQLite index comparison does not support application-defined collation {term.Collation.Name}.");
            }
        }
    }

    /// <summary>The database text encoding used to interpret text record fields.</summary>
    public SqliteTextEncoding TextEncoding { get; }

    /// <summary>Compares two complete SQLite record payloads.</summary>
    public int Compare(ReadOnlySpan<byte> leftRecord, ReadOnlySpan<byte> rightRecord)
        => Compare(SqliteRecordCodec.Decode(leftRecord, TextEncoding), SqliteRecordCodec.Decode(rightRecord, TextEncoding));

    /// <summary>Compares two decoded SQLite records.</summary>
    /// <remarks>
    /// The array overload exists because this is the sort comparator for index
    /// builds; the <see cref="IReadOnlyList{T}"/> form pays an interface dispatch
    /// per element on a path that runs O(n log n) times.
    /// </remarks>
    public int Compare(SqlValue[] left, SqlValue[] right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            var term = index < _terms.Length
                ? _terms[index]
                : SqliteIndexComparisonTerm.BinaryAscending;
            var result = CompareValue(left[index], right[index], term.Collation);
            if (result != 0)
                return term.SortOrder == SqliteKeySortOrder.Descending
                    ? -Math.Sign(result)
                    : result;
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Compares two decoded SQLite records.</summary>
    public int Compare(IReadOnlyList<SqlValue> left, IReadOnlyList<SqlValue> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var count = Math.Min(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            var term = index < _terms.Length
                ? _terms[index]
                : SqliteIndexComparisonTerm.BinaryAscending;
            var result = CompareValue(left[index], right[index], term.Collation);
            if (result != 0)
                return term.SortOrder == SqliteKeySortOrder.Descending
                    ? -Math.Sign(result)
                    : result;
        }

        return left.Count.CompareTo(right.Count);
    }

    /// <summary>Validates that <paramref name="record"/> is a supported index key record.</summary>
    /// <remarks>
    /// A stored NaN is not rejected. SQLite's <c>sqlite3VdbeMemSetDouble</c> refuses to create one
    /// and its <c>serialGet</c> reads one back as NULL, so the managed codec normalises it the same
    /// way and no NaN can reach a comparison.
    /// </remarks>
    public void Validate(ReadOnlySpan<byte> record) => SqliteRecordCodec.Decode(record, TextEncoding);

    /// <summary>
    /// Returns whether a collation name is one of SQLite's built-in persisted
    /// index collations. A missing name means BINARY.
    /// </summary>
    public static bool IsSupportedCollation(string? collation)
        => collation is null
            || string.Equals(collation, "BINARY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase);

    private int CompareValue(SqlValue left, SqlValue right, SqliteKeyCollation collation)
    {
        var leftClass = GetStorageClass(left.Kind);
        var rightClass = GetStorageClass(right.Kind);
        if (leftClass != rightClass)
            return leftClass.CompareTo(rightClass);

        return leftClass switch
        {
            StorageClass.Null => 0,
            StorageClass.Numeric => CompareNumeric(left, right),
            StorageClass.Text => CompareText(left.AsText(), right.AsText(), collation),
            StorageClass.Blob => CompareBinary(left.AsBlob().Span, right.AsBlob().Span),
            _ => throw new InvalidOperationException("SQLite index record has an unknown storage class."),
        };
    }

    private int CompareText(string left, string right, SqliteKeyCollation collation)
    {
        if (collation.IsNoCase)
            return CompareNoCaseText(left, right);

        if (collation.IsRTrim)
            return CompareRTrimText(left, right);

        if (collation.IsBinary)
            return CompareBinaryText(left, right, GetTextEncoding(TextEncoding));

        throw new NotSupportedException(
            $"SQLite index comparison does not support collation {collation.Name}.");
    }

    /// <summary>
    /// Compares two strings as SQLite would compare their encoded bytes, without
    /// allocating a byte array per comparison.
    /// </summary>
    /// <remarks>
    /// This runs O(n log n) times per index build. Encoding both operands with
    /// <c>Encoding.GetBytes</c> allocated two arrays on every comparison, which
    /// dominated both the CPU and the garbage produced by a full rebuild.
    /// For all-ASCII operands the encoded byte order matches ordinal char order
    /// under UTF-8 and both UTF-16 forms, so no encoding is needed at all.
    /// </remarks>
    private static int CompareBinaryText(string left, string right, Encoding encoding)
    {
        if (Ascii.IsValid(left) && Ascii.IsValid(right))
            return string.CompareOrdinal(left, right);

        var leftMaximum = encoding.GetMaxByteCount(left.Length);
        var rightMaximum = encoding.GetMaxByteCount(right.Length);
        var leftBuffer = ArrayPool<byte>.Shared.Rent(leftMaximum);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(rightMaximum);
        try
        {
            var leftLength = encoding.GetBytes(left, leftBuffer);
            var rightLength = encoding.GetBytes(right, rightBuffer);
            return CompareBinary(
                leftBuffer.AsSpan(0, leftLength),
                rightBuffer.AsSpan(0, rightLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    internal static int CompareNoCaseText(string left, string right)
    {
        if (Ascii.IsValid(left) && Ascii.IsValid(right))
        {
            // ASCII folding over ASCII input is exactly the byte-wise fold below,
            // so the encode step and its two arrays are unnecessary.
            var asciiCount = Math.Min(left.Length, right.Length);
            for (var index = 0; index < asciiCount; index++)
            {
                var leftFolded = FoldAscii((byte)left[index]);
                var rightFolded = FoldAscii((byte)right[index]);
                if (leftFolded != rightFolded)
                    return leftFolded.CompareTo(rightFolded);
                if (leftFolded == 0)
                    return left.Length.CompareTo(right.Length);
            }

            return left.Length.CompareTo(right.Length);
        }

        var leftBytes = StrictUtf8.GetBytes(left);
        var rightBytes = StrictUtf8.GetBytes(right);
        var count = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < count; index++)
        {
            var leftByte = FoldAscii(leftBytes[index]);
            var rightByte = FoldAscii(rightBytes[index]);
            if (leftByte == 0 || rightByte == 0)
            {
                return leftByte == rightByte
                    ? leftBytes.Length.CompareTo(rightBytes.Length)
                    : leftByte.CompareTo(rightByte);
            }

            var comparison = leftByte.CompareTo(rightByte);
            if (comparison != 0)
                return comparison;
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    internal static int CompareRTrimText(string left, string right)
    {
        var trimmedLeft = left.TrimEnd(' ');
        var trimmedRight = right.TrimEnd(' ');
        if (Ascii.IsValid(trimmedLeft) && Ascii.IsValid(trimmedRight))
            return string.CompareOrdinal(trimmedLeft, trimmedRight);

        return CompareBinary(
            StrictUtf8.GetBytes(trimmedLeft),
            StrictUtf8.GetBytes(trimmedRight));
    }

    private static byte FoldAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + ((byte)'a' - (byte)'A'))
            : value;

    private static StorageClass GetStorageClass(SqlValueKind kind)
    {
        return kind switch
        {
            SqlValueKind.Null => StorageClass.Null,
            SqlValueKind.Integer or SqlValueKind.Real => StorageClass.Numeric,
            SqlValueKind.Text => StorageClass.Text,
            SqlValueKind.Blob => StorageClass.Blob,
            _ => throw new InvalidOperationException($"Unknown SQL value kind {kind}."),
        };
    }

    private static int CompareNumeric(SqlValue left, SqlValue right)
    {
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
            return left.AsInteger().CompareTo(right.AsInteger());

        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Real)
            return left.AsReal().CompareTo(right.AsReal());

        var integer = left.Kind == SqlValueKind.Integer ? left.AsInteger() : right.AsInteger();
        var real = left.Kind == SqlValueKind.Real ? left.AsReal() : right.AsReal();
        var result = CompareIntegerToReal(integer, real);
        return left.Kind == SqlValueKind.Integer ? result : -result;
    }

    private static int CompareIntegerToReal(long integer, double real)
    {
        // These boundaries are exactly representable doubles. The positive
        // boundary is one past Int64.MaxValue.
        const double MinimumInt64 = -9_223_372_036_854_775_808d;
        const double OnePastMaximumInt64 = 9_223_372_036_854_775_808d;
        if (real < MinimumInt64)
            return 1;
        if (real >= OnePastMaximumInt64)
            return -1;

        var truncated = (long)real;
        var comparison = integer.CompareTo(truncated);
        if (comparison != 0 || real == truncated)
            return comparison;

        return real > 0 ? -1 : 1;
    }

    private static int CompareBinary(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static Encoding GetTextEncoding(SqliteTextEncoding textEncoding)
    {
        return textEncoding switch
        {
            SqliteTextEncoding.Utf8 => StrictUtf8,
            SqliteTextEncoding.Utf16LittleEndian => StrictUtf16LittleEndian,
            SqliteTextEncoding.Utf16BigEndian => StrictUtf16BigEndian,
            _ => throw new ArgumentOutOfRangeException(
                nameof(textEncoding),
                textEncoding,
                "SQLite index records require a concrete supported text encoding."),
        };
    }

    private static SqliteIndexComparisonTerm[] CreateTerms(
        IReadOnlyList<bool> descendingFields,
        IReadOnlyList<string?> collations)
    {
        ArgumentNullException.ThrowIfNull(descendingFields);
        ArgumentNullException.ThrowIfNull(collations);
        if (descendingFields.Count != collations.Count)
        {
            throw new ArgumentException(
                "SQLite index sort-direction and collation metadata must describe the same number of fields.",
                nameof(collations));
        }

        var terms = new SqliteIndexComparisonTerm[descendingFields.Count];
        for (var index = 0; index < terms.Length; index++)
        {
            terms[index] = new SqliteIndexComparisonTerm(
                descendingFields[index] ? SqliteKeySortOrder.Descending : SqliteKeySortOrder.Ascending,
                collations[index] is null
                    ? SqliteKeyCollation.Binary
                    : SqliteKeyCollation.FromName(collations[index]!));
        }

        return terms;
    }

    private enum StorageClass
    {
        Null,
        Numeric,
        Text,
        Blob,
    }
}

/// <summary>Comparison metadata for one leading SQLite index-record field.</summary>
public sealed record SqliteIndexComparisonTerm(
    SqliteKeySortOrder SortOrder,
    SqliteKeyCollation Collation)
{
    internal static SqliteIndexComparisonTerm BinaryAscending { get; } =
        new(SqliteKeySortOrder.Ascending, SqliteKeyCollation.Binary);
}
