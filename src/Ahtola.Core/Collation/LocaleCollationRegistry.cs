namespace Ahtola.Core.Collation;

/// <summary>
/// Process-wide cache of parsed locale collation tags, mirroring Turso's
/// <c>LocaleCollationRegistry</c> (<c>turso-src/core/translate/collate.rs</c>):
/// resolving a BCP-47 tag string to a comparator is attempted lazily, once, and
/// the result is cached by the case-folded tag text so repeated <c>COLLATE
/// 'locale-tag'</c> usages do not re-parse or re-build the weight lookup.
/// </summary>
/// <remarks>
/// This is the single entry point every collation-resolution call site in
/// <c>Ahtola.Core</c> should consult once a name is not one of the three
/// built-ins (BINARY/NOCASE/RTRIM) and not a registered application-defined
/// callback: <see cref="Storage.SqliteKeyCollation.FromName"/> (persisted index
/// key comparisons), <c>EmbeddedDatabase.Compare</c> and
/// <c>EmbeddedDatabase.ValidateCollation</c> (scalar/ORDER BY/GROUP BY
/// comparisons), and the REINDEX-target / expression-index validity checks
/// (<c>EmbeddedDatabase.HasCollation</c>, <c>IsCollationResolvable</c>). Keeping
/// the resolution logic in one place is what makes a locale name behave
/// consistently everywhere a collation name is accepted, without threading a
/// new "kind of collation" through the many existing call sites individually —
/// matching Turso's own single point of dispatch through <c>CollationSeq::new</c>.
/// </remarks>
public static class LocaleCollationRegistry
{
    private static readonly Dictionary<string, Func<string, string, int>?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Attempts to resolve <paramref name="name"/> as a locale collation tag.
    /// Returns <see langword="false"/> for <see langword="null"/>/blank input,
    /// for the three built-in names (which callers must handle themselves), and
    /// for a tag this port cannot parse (structurally malformed, or a recognized
    /// keyword with an invalid value) — the caller should treat that exactly like
    /// any other unresolvable collation name ("no such collation sequence").
    /// </summary>
    public static bool TryResolve(string? name, out Func<string, string, int>? compare)
    {
        compare = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (string.Equals(name, "BINARY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "NOCASE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "RTRIM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var key = name.ToLowerInvariant();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                compare = cached;
                return cached is not null;
            }

            Func<string, string, int>? resolved = null;
            if (LocaleCollationTag.TryParse(name, out var tag) && tag is not null)
            {
                var collator = new LocaleCollator(tag);
                resolved = collator.Compare;
            }

            Cache[key] = resolved;
            compare = resolved;
            return resolved is not null;
        }
    }
}
