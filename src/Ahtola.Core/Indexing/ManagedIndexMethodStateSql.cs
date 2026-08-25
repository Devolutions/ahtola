using System.Globalization;

namespace Ahtola.Core.Indexing;

/// <summary>
/// Encodes and decodes the versioned, method-owned state envelope carried by a method index's
/// <c>sqlite_schema.sql</c> text.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the shipped managed virtual-table envelope (<c>ManagedVirtualTableSchemaSql</c>):
/// the state rides inside a trailing SQL comment on the ordinary <c>CREATE INDEX</c> text, so it is
/// written and rolled back by exactly the same pager/WAL transaction that writes the rest of
/// <c>sqlite_schema</c>. There is no side file, no custom page type, and no separate durability path.
/// </para>
/// <para>
/// The envelope is an acceleration cache, not the authority: the authority is the ordinary SQLite
/// index b-tree that the file store already builds for the index plus the base-table rows. A missing
/// envelope therefore rebuilds silently, while a malformed or newer-versioned envelope fails closed.
/// </para>
/// <para>
/// The marker is only meaningful on a <c>CREATE INDEX … USING method</c> declaration. An ordinary
/// index whose SQL text happens to end in a comment that looks like an envelope is left alone:
/// <see cref="TrySplit"/> parses the candidate declaration first and refuses to strip a trailing
/// comment from anything that is not a method index.
/// </para>
/// <para>
/// Interoperability: <c>CREATE INDEX … USING …</c> is not parseable by stock SQLite, so a database
/// containing a method index is Ahtola/Turso-only. See docs/managed-index-methods.md.
/// </para>
/// </remarks>
internal static class ManagedIndexMethodStateSql
{
    private const string StateMarker = "/*ahtola-index-method:";
    private const string StateTerminator = "*/";

    /// <summary>Appends the state envelope to an already-formatted CREATE INDEX statement.</summary>
    public static string Append(string createIndexSql, int version, ReadOnlySpan<byte> state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createIndexSql);
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (state.Length == 0)
            throw new EmbeddedSqlException("managed index method state is empty");
        if (state.Length > ManagedIndexMethodLimits.MaxStateBytes)
        {
            throw new EmbeddedSqlException(
                $"managed index method state exceeds {ManagedIndexMethodLimits.MaxStateBytes} bytes");
        }

        return createIndexSql
            + " "
            + StateMarker
            + version.ToString(CultureInfo.InvariantCulture)
            + ":"
            + Convert.ToBase64String(state)
            + StateTerminator;
    }

    /// <summary>
    /// True when <paramref name="sql"/> is shaped like an envelope. This is a syntactic probe only:
    /// it does not establish that the declaration is a method index, so it must never be used on
    /// its own to decide whether to strip a trailing comment.
    /// </summary>
    public static bool HasStateMarker(string? sql)
        => sql is not null
            && sql.EndsWith(StateTerminator, StringComparison.Ordinal)
            && sql.LastIndexOf(StateMarker, StringComparison.Ordinal) > 0;

    /// <summary>
    /// Splits stored index SQL into its declaration and optional state envelope, but only after the
    /// candidate declaration has been parsed and proven to be a <c>USING method</c> index. An
    /// ordinary index whose comment merely resembles an envelope round-trips untouched.
    /// </summary>
    /// <param name="sql">The stored <c>sqlite_schema.sql</c> text.</param>
    /// <param name="isMethodDeclaration">
    /// Predicate that parses the candidate declaration and reports whether it declares a method
    /// index. It must not throw for unparseable input; return false instead.
    /// </param>
    /// <param name="declarationSql">The declaration with the envelope removed, when one was found.</param>
    /// <param name="version">The envelope version.</param>
    /// <param name="state">The decoded envelope bytes.</param>
    public static bool TrySplit(
        string sql,
        Func<string, bool> isMethodDeclaration,
        out string declarationSql,
        out int version,
        out byte[] state)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(isMethodDeclaration);

        declarationSql = sql;
        version = 0;
        state = [];
        if (!HasStateMarker(sql))
            return false;

        var marker = sql.LastIndexOf(StateMarker, StringComparison.Ordinal);
        var candidate = sql[..marker].TrimEnd();
        if (candidate.Length == 0 || !isMethodDeclaration(candidate))
        {
            // Not a method index: the trailing comment belongs to the user's own SQL text and must
            // survive the catalog round-trip byte for byte.
            return false;
        }

        (declarationSql, version, state) = Split(sql);
        return true;
    }

    /// <summary>
    /// Splits a stored method-index SQL text into its declaration and state envelope. Only call
    /// this once the declaration is known to be a method index; prefer <see cref="TrySplit"/>.
    /// </summary>
    public static (string DeclarationSql, int Version, byte[] State) Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (!HasStateMarker(sql))
            return (sql, 0, []);

        var marker = sql.LastIndexOf(StateMarker, StringComparison.Ordinal);
        var declarationSql = sql[..marker].TrimEnd();
        if (declarationSql.Length == 0)
            throw new EmbeddedSqlException("managed index method SQL is missing its CREATE INDEX declaration");

        var encoded = sql.AsSpan(
            marker + StateMarker.Length,
            sql.Length - marker - StateMarker.Length - StateTerminator.Length);
        var separator = encoded.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(
                encoded[..separator],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version)
            || version <= 0)
        {
            throw new EmbeddedSqlException("managed index method state version is invalid");
        }

        var payload = encoded[(separator + 1)..];

        // Bound the *encoded* length before decoding: Convert.FromBase64String allocates a buffer
        // proportional to its input, so checking only the decoded size would let a hostile catalog
        // row force the large allocation first and reject it afterwards.
        if (payload.Length > ManagedIndexMethodLimits.MaxStateEncodedChars)
            throw new EmbeddedSqlException("managed index method state exceeds its maximum size");
        if (payload.Length == 0)
            throw new EmbeddedSqlException("managed index method state is empty");

        byte[] state;
        try
        {
            state = Convert.FromBase64String(payload.ToString());
        }
        catch (FormatException exception)
        {
            throw new EmbeddedSqlException("managed index method state is not valid base64", exception);
        }

        if (state.Length == 0)
            throw new EmbeddedSqlException("managed index method state is empty");
        if (state.Length > ManagedIndexMethodLimits.MaxStateBytes)
            throw new EmbeddedSqlException("managed index method state exceeds its maximum size");

        return (declarationSql, version, state);
    }
}

/// <summary>
/// Collision-proof naming for the auxiliary objects a method index owns. Names embed an infix that
/// <c>IsReservedIndexMethodObjectName</c> rejects in user DDL, so user objects can neither collide
/// with nor drop method-owned state.
/// </summary>
internal static class ManagedIndexMethodNames
{
    /// <summary>The reserved infix that marks an object as owned by a managed index method.</summary>
    public const string ReservedInfix = "_ahtola_idxm_";

    /// <summary>Builds a reserved auxiliary object name for one index and role.</summary>
    public static string Auxiliary(string indexName, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return indexName + ReservedInfix + suffix;
    }

    /// <summary>True when a name is reserved for managed index-method state.</summary>
    public static bool IsReserved(string name)
        => name is not null
            && name.Contains(ReservedInfix, StringComparison.OrdinalIgnoreCase);
}
