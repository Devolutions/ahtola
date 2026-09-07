namespace Ahtola.Core.Collation;

/// <summary>
/// Resolves the supported managed locale profiles and caches their comparators,
/// corresponding to Turso's <c>LocaleCollationRegistry</c>
/// (<c>turso-src/core/translate/collate.rs</c>). Each lookup parses the supplied tag;
/// equivalent accepted spellings reuse a comparator through its canonical semantic key.
/// </summary>
/// <remarks>
/// <para>
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
/// </para>
/// <para>
/// Only successful resolutions are cached, keyed by
/// <see cref="LocaleCollationTag.CanonicalName"/> rather than raw input text.
/// The supported profile, collation-type, case-first, and numeric combinations
/// produce at most 24 keys. Raw casing, keyword order, and the parsed-but-inert
/// <c>ks</c> value do not create extra entries. Neither rejected names nor
/// caller-provided spellings are retained. This deliberately restricted subset
/// does not provide general ICU/CLDR locale coverage.
/// </para>
/// </remarks>
public static class LocaleCollationRegistry
{
    private static readonly Dictionary<string, Func<string, string, int>> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Test-only visibility into the cache's current entry count, so a
    /// regression test can assert it stays bounded across many distinct
    /// rejected inputs instead of growing without limit. Not used by any
    /// production code path.
    /// </summary>
    internal static int CachedEntryCount
    {
        get
        {
            lock (Gate)
                return Cache.Count;
        }
    }

    /// <summary>
    /// Attempts to resolve <paramref name="name"/> as a locale collation tag.
    /// Returns <see langword="false"/> for <see langword="null"/>/blank input,
    /// for the three built-in names (which callers must handle themselves), and
    /// for a tag this port cannot parse (structurally malformed, a recognized
    /// keyword with an invalid value, an unrecognized-but-otherwise-legal
    /// BCP-47 construct, or a syntactically valid tag outside
    /// <see cref="LocaleCollationTag"/>'s accepted-profile allowlist) — the
    /// caller should treat that exactly like any other unresolvable collation
    /// name ("no such collation sequence"). Only a successful resolution is
    /// cached, keyed by the resolved tag's canonical semantic identity rather
    /// than the caller's raw input text; see the type remarks for why both
    /// halves of that are necessary to keep this cache provably bounded.
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

        if (!LocaleCollationTag.TryParse(name, out var tag) || tag is null)
            return false;

        lock (Gate)
        {
            if (Cache.TryGetValue(tag.CanonicalName, out var cached))
            {
                compare = cached;
                return true;
            }

            var collator = new LocaleCollator(tag);
            Func<string, string, int> resolved = collator.Compare;
            Cache[tag.CanonicalName] = resolved;
            compare = resolved;
            return true;
        }
    }
}
