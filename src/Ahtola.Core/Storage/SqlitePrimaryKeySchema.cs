using System.Collections.ObjectModel;

namespace Ahtola.Core.Storage;

/// <summary>
/// The collation metadata available for one primary-key term.
/// </summary>
/// <remarks>
/// <see cref="Unavailable"/> is deliberately distinct from <c>BINARY</c>. A
/// file-store boundary that did not retain a column's declared collation cannot
/// safely assume SQLite's default when materializing an index b-tree.
/// </remarks>
public sealed record SqliteKeyCollation
{
    private SqliteKeyCollation(string? name, Func<string, string, int>? comparison = null)
    {
        Name = name;
        Comparison = comparison;
    }

    /// <summary>A key term whose collation was not retained by its caller.</summary>
    public static SqliteKeyCollation Unavailable { get; } = new(name: null);

    /// <summary>SQLite's bytewise BINARY collation.</summary>
    public static SqliteKeyCollation Binary { get; } = new("BINARY");

    /// <summary>
    /// The canonical SQLite collation name, or <see langword="null"/> when
    /// metadata was unavailable.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// An optional connection/database-bound string comparison delegate that
    /// reproduces an application-defined (non-built-in) collation. When
    /// present, the managed index writer treats this collation as supported
    /// even though it is not one of SQLite's built-in BINARY/NOCASE/RTRIM
    /// sequences. The delegate is never serialized; only <see cref="Name"/>
    /// is persisted to durable schema metadata.
    /// </summary>
    public Func<string, string, int>? Comparison { get; }

    /// <summary>Whether the source supplied a concrete collation name.</summary>
    public bool IsAvailable => Name is not null;

    /// <summary>Whether this is the BINARY collation.</summary>
    public bool IsBinary => string.Equals(Name, "BINARY", StringComparison.Ordinal);

    /// <summary>Whether this is SQLite's built-in NOCASE collation.</summary>
    public bool IsNoCase => string.Equals(Name, "NOCASE", StringComparison.Ordinal);

    /// <summary>Whether this is SQLite's built-in RTRIM collation.</summary>
    public bool IsRTrim => string.Equals(Name, "RTRIM", StringComparison.Ordinal);

    /// <summary>
    /// Whether this collation is one of SQLite's three built-in sequences,
    /// regardless of whether an application has also registered a callback
    /// under the same name (see <see cref="SqliteIndexRecordComparer"/> for
    /// the "overridden built-in" safety rule).
    /// </summary>
    public bool IsBuiltIn => IsBinary || IsNoCase || IsRTrim;

    /// <summary>
    /// Whether the managed index writer can reproduce this collation: either
    /// it is one of SQLite's built-ins, or a bound application-defined
    /// comparison delegate is available for it.
    /// </summary>
    public bool IsSupportedByManagedIndexWriter => IsBuiltIn || Comparison is not null;

    /// <summary>Creates a descriptor for a concrete SQLite collation name.</summary>
    public static SqliteKeyCollation FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return string.Equals(name, "BINARY", StringComparison.OrdinalIgnoreCase)
            ? Binary
            : new SqliteKeyCollation(name.ToUpperInvariant());
    }

    /// <summary>
    /// Returns a copy of this descriptor bound to an application-defined
    /// comparison delegate. Built-in BINARY/NOCASE/RTRIM collations may also
    /// carry a bound delegate when the application has explicitly overridden
    /// them; callers decide whether the override applies (see
    /// <see cref="SqliteIndexRecordComparer"/>).
    /// </summary>
    public SqliteKeyCollation WithComparison(Func<string, string, int> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return new SqliteKeyCollation(Name, comparison);
    }
}

/// <summary>The SQLite sort direction for a primary-key term.</summary>
public enum SqliteKeySortOrder
{
    /// <summary>Ascending order.</summary>
    Ascending,

    /// <summary>Descending order.</summary>
    Descending,
}

/// <summary>One column in a canonical SQLite primary-key schema.</summary>
public sealed record SqlitePrimaryKeyTerm(
    int ColumnIndex,
    string ColumnName,
    SqliteKeySortOrder SortOrder,
    SqliteKeyCollation Collation);

/// <summary>
/// An immutable primary-key descriptor that preserves declaration order, column
/// ordinals, sort direction, and collation metadata.
/// </summary>
/// <remarks>
/// <para>
/// This type can project and encode only a key prefix. It does not describe a
/// complete WITHOUT ROWID table record and does not make a table index b-tree
/// writable.
/// </para>
/// <para>
/// The bounded and WITHOUT ROWID index paths support narrower key shapes than
/// the full persisted-index writer. Call the matching validation method before
/// selecting one of those paths, including
/// <see cref="EnsureSupportedByManagedIndexWriter"/>.
/// </para>
/// </remarks>
public sealed class SqlitePrimaryKeySchema
{
    private readonly ReadOnlyCollection<SqlitePrimaryKeyTerm> _terms;

    /// <summary>Creates and validates a canonical primary-key descriptor.</summary>
    public SqlitePrimaryKeySchema(IEnumerable<SqlitePrimaryKeyTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var snapshot = terms.ToArray();
        if (snapshot.Length == 0)
            throw new ArgumentException("A SQLite primary-key schema requires at least one term.", nameof(terms));

        var columnIndices = new HashSet<int>();
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in snapshot)
        {
            ArgumentNullException.ThrowIfNull(term);
            ArgumentException.ThrowIfNullOrWhiteSpace(term.ColumnName);
            ArgumentNullException.ThrowIfNull(term.Collation);
            ArgumentOutOfRangeException.ThrowIfNegative(term.ColumnIndex);
            if (term.SortOrder is not (SqliteKeySortOrder.Ascending or SqliteKeySortOrder.Descending))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(terms),
                    term.SortOrder,
                    "SQLite primary-key schema has an unknown sort direction.");
            }
            if (!columnIndices.Add(term.ColumnIndex))
            {
                throw new ArgumentException(
                    $"SQLite primary-key schema repeats column ordinal {term.ColumnIndex}.",
                    nameof(terms));
            }
            if (!columnNames.Add(term.ColumnName))
            {
                throw new ArgumentException(
                    $"SQLite primary-key schema repeats column '{term.ColumnName}'.",
                    nameof(terms));
            }
        }

        _terms = Array.AsReadOnly(snapshot);
    }

    /// <summary>The key terms in SQLite declaration order.</summary>
    public IReadOnlyList<SqlitePrimaryKeyTerm> Terms => _terms;

    /// <summary>Projects the key values from a table row in key declaration order.</summary>
    public SqlValue[] ProjectKey(IReadOnlyList<SqlValue> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var values = new SqlValue[_terms.Count];
        for (var termIndex = 0; termIndex < _terms.Count; termIndex++)
        {
            var term = _terms[termIndex];
            if (term.ColumnIndex >= row.Count)
            {
                throw new ArgumentException(
                    $"SQLite primary-key term '{term.ColumnName}' references column {term.ColumnIndex}, but the row has {row.Count} column(s).",
                    nameof(row));
            }

            values[termIndex] = row[term.ColumnIndex];
        }

        return values;
    }

    /// <summary>
    /// Encodes the schema-ordered primary-key prefix as a SQLite record.
    /// </summary>
    public byte[] EncodeKeyPrefix(
        IReadOnlyList<SqlValue> row,
        SqliteTextEncoding textEncoding = SqliteTextEncoding.Utf8)
        => SqliteRecordCodec.Encode(ProjectKey(row), textEncoding);

    /// <summary>
    /// Rejects schema terms that the current BINARY ascending index primitives
    /// cannot compare safely.
    /// </summary>
    public void EnsureSupportedByBinaryAscendingIndexWriter()
    {
        var failures = new List<string>();
        foreach (var term in _terms)
        {
            if (term.SortOrder == SqliteKeySortOrder.Descending)
            {
                failures.Add(
                    $"primary-key term '{term.ColumnName}' is descending, but the managed index writer supports only ascending terms");
            }
            AddBinaryCollationFailure(term, failures);

        }

        if (failures.Count != 0)
            throw new NotSupportedException(string.Join("; ", failures) + ".");
    }

    /// <summary>
    /// Rejects schema terms whose collation cannot be handled by the BINARY index writer.
    /// ASC and DESC directions are both accepted.
    /// </summary>
    public void EnsureSupportedByBinaryIndexWriter()
    {
        var failures = new List<string>();
        foreach (var term in _terms)
            AddBinaryCollationFailure(term, failures);

        if (failures.Count != 0)
            throw new NotSupportedException(string.Join("; ", failures) + ".");
    }

    /// <summary>
    /// Rejects schema terms whose collation cannot be reproduced by the
    /// persisted managed index writer. ASC and DESC are both accepted.
    /// </summary>
    public void EnsureSupportedByPersistedIndexWriter()
        => EnsureSupportedByManagedIndexWriter();

    /// <summary>
    /// Rejects unavailable or application-defined collations. The managed writer
    /// reproduces SQLite's BINARY, NOCASE, and RTRIM collations in either direction.
    /// </summary>
    public void EnsureSupportedByManagedIndexWriter(bool allowDescending = true)
    {
        var failures = new List<string>();
        foreach (var term in _terms)
        {
            if (!allowDescending && term.SortOrder == SqliteKeySortOrder.Descending)
            {
                failures.Add(
                    $"primary-key term '{term.ColumnName}' is descending, but the managed index writer supports only ascending terms");
            }

            if (!term.Collation.IsAvailable)
            {
                failures.Add(
                    $"primary-key term '{term.ColumnName}' has unavailable collation metadata");
            }
            else if (!term.Collation.IsSupportedByManagedIndexWriter)
            {
                failures.Add(
                    $"primary-key term '{term.ColumnName}' uses application-defined collation {term.Collation.Name}, which cannot be restored before the file catalog is loaded");
            }
        }

        if (failures.Count != 0)
            throw new NotSupportedException(string.Join("; ", failures) + ".");
    }

    private static void AddBinaryCollationFailure(
        SqlitePrimaryKeyTerm term,
        ICollection<string> failures)
    {
        if (!term.Collation.IsAvailable)
        {
            failures.Add(
                $"primary-key term '{term.ColumnName}' has unavailable collation metadata");
        }
        else if (!term.Collation.IsBinary)
        {
            failures.Add(
                $"primary-key term '{term.ColumnName}' uses {term.Collation.Name} collation, but the managed index writer supports only BINARY");
        }
    }
}
