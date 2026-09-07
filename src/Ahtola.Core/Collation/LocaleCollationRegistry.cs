namespace Ahtola.Core.Collation;

/// <summary>
/// Process-wide cache of parsed locale collation tags, mirroring Turso's
/// <c>LocaleCollationRegistry</c> (<c>turso-src/core/translate/collate.rs</c>):
/// resolving a BCP-47 tag string to a comparator is attempted lazily, once, and
/// the result is cached by the case-folded tag text so repeated <c>COLLATE
/// 'locale-tag'</c> usages do not re-parse or re-build the weight lookup.
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
/// <b>Only successfully-resolved (accepted-profile) results are cached.</b> A
/// review (2026-09-07) correctly flagged that the original implementation
/// cached EVERY distinct name ever queried, including ones that failed to
/// parse or were rejected by <see cref="LocaleCollationTag"/>'s accepted-
/// profile allowlist. Since <c>COLLATE '&lt;arbitrary text&gt;'</c> can appear
/// in ordinary SQL text and this cache is process-wide and never evicted, that
/// let an attacker (or a buggy application generating novel collation names
/// per query) grow the dictionary without bound simply by supplying a stream
/// of distinct garbage strings — an unbounded-memory-growth vector. The set of
/// names that can ever resolve successfully is intrinsically bounded (it must
/// pass <see cref="LocaleCollationTag.TryParse"/>'s finite accepted-profile
/// allowlist), so only positive resolutions are cached; a failed parse/
/// rejection is recomputed on every call instead of being remembered. Parsing
/// is a single cheap linear pass over the tag string with no allocation beyond
/// the (rejected, therefore discarded) <see cref="LocaleCollationTag"/> record,
/// so this trades an unbounded memory leak for a bounded amount of repeated,
/// inexpensive work on the (rare, already-erroring) rejection path.
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
    /// keyword with an invalid value, or a syntactically valid tag outside
    /// <see cref="LocaleCollationTag"/>'s accepted-profile allowlist) — the
    /// caller should treat that exactly like any other unresolvable collation
    /// name ("no such collation sequence"). Only a successful resolution is
    /// cached; see the type remarks for why a failed/rejected lookup is
    /// deliberately never cached.
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
                return true;
            }

            if (!LocaleCollationTag.TryParse(name, out var tag) || tag is null)
                return false;

            var collator = new LocaleCollator(tag);
            Func<string, string, int> resolved = collator.Compare;
            Cache[key] = resolved;
            compare = resolved;
            return true;
        }
    }
}
