namespace Ahtola.Core.Collation;

/// <summary>Where uppercase sorts relative to lowercase at the tertiary (case) level.</summary>
public enum LocaleCaseFirst
{
    /// <summary>No explicit case-first tailoring: lowercase sorts before uppercase (UCA root default).</summary>
    Off,

    /// <summary>Uppercase sorts before lowercase (BCP-47 <c>kf=upper</c>).</summary>
    Upper,

    /// <summary>Lowercase sorts before uppercase, spelled out explicitly (BCP-47 <c>kf=lower</c>).</summary>
    Lower,
}

/// <summary>
/// A parsed BCP-47 locale tag carrying the subset of Unicode locale extension
/// ("-u-") keywords this collation port interprets: <c>co</c> (collation type),
/// <c>kf</c> (case-first), and <c>kn</c> (numeric ordering).
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the surface Turso exposes through <c>icu_locale::Locale</c> +
/// <c>icu_collator::Collator</c> (see <c>turso-src/core/translate/collate.rs</c>,
/// <c>LocaleCollationRegistry::get_or_register</c>), scoped to what this managed
/// port actually implements. The <c>ks</c> (collation strength) keyword is parsed
/// and its value validated against the BCP-47 enumeration (<c>level1</c>..<c>level4</c>,
/// <c>identic</c>) for syntax fidelity, but has no effect on comparison strength
/// in this port: comparisons always run through the tertiary (case) level
/// regardless of the requested <c>ks</c> value.
/// </para>
/// <para>
/// <b>Evidence for the <c>ks</c> claim (not a proof of general ICU/UCA strength
/// semantics).</b> This was checked against the pinned crate versions
/// (Cargo.lock-resolved <c>icu_collator</c>/<c>icu_locale</c> 2.3.1 for the
/// <c>turso-src</c> <c>v0.8.0-pre.7</c> tag) with a small, concrete matrix, not
/// a single hand-picked pair: for EACH of the five enumerated <c>ks</c> values
/// (<c>level1</c>, <c>level2</c>, <c>level3</c>, <c>level4</c>, <c>identic</c>),
/// three probes were run — a primary-only difference (<c>'a'</c> vs <c>'b'</c>),
/// a secondary-only (accent) difference (<c>'a'</c> vs <c>'á'</c>), and a
/// tertiary-only (case) difference (<c>'a'</c> vs <c>'A'</c>) — and every one of
/// the fifteen results was identical to the untailored default (case and accent
/// differences were distinguished under every <c>ks</c> value, none of them
/// collapsed to primary-only comparison). The same three probes were repeated
/// with <c>kf=upper</c> also applied for all five <c>ks</c> values (case
/// ordering correctly reversed under every one), confirming the two keywords
/// still compose correctly regardless of the (inert) <c>ks</c> value. This is
/// evidence for the SPECIFIC claim "this crate version does not vary strength
/// for these keyword combinations, for these probe pairs" — it is not a formal
/// proof that every possible pair of strings behaves identically across every
/// <c>ks</c> value, nor a general statement about ICU/UCA strength semantics.
/// If a future <c>icu_collator</c> upgrade changes this, this port's own
/// behavior (always comparing through tertiary) would then diverge from the
/// upgraded reference and must be re-verified against the new pinned version
/// before claiming continued parity.
/// </para>
/// <para>
/// <b>Evidence for <c>kn</c> (numeric ordering) semantics.</b> Checked against
/// the same pinned crate with ten varied digit-run cases (leading zeros of
/// different lengths, single- vs. multi-digit magnitude comparisons, digit runs
/// embedded mid-string at different positions, and digit runs separated by a
/// non-digit character, e.g. version-like text), each compared once under plain
/// <c>en-u-kn-true</c> and once under the exact compound tag the pinned
/// upstream sqltest corpus uses
/// (<c>en-u-kn-true-kf-upper-ks-level2</c>) to confirm numeric folding composes
/// correctly with case-first and the (inert) strength keyword rather than only
/// being verified in isolation. All ten cases matched this port's
/// (length-then-text, leading-zeros-stripped) numeric comparison in both forms.
/// </para>
/// <para>
/// Any unrecognized <c>-u-</c> keyword, any Unicode locale extension
/// "attribute" subtag (BCP-47 permits free-form 3-8 alphanumeric attributes
/// before the first keyword), and any singleton extension other than <c>u</c>
/// (including private-use <c>-x-...</c>) are REJECTED by <see cref="TryParse"/>
/// rather than tolerated/skipped. A review (2026-09-07) correctly found that
/// silently tolerating such content — while syntactically legal BCP-47 — let
/// an essentially unbounded family of distinct raw tag strings (e.g.
/// <c>en-u-zzzzzzzz-kf-upper</c> with an arbitrary ignored attribute,
/// <c>en-x-aaaaaaaa</c> with an arbitrary private-use suffix, or
/// <c>en-u-zz-abcdefgh</c> with an arbitrary unrecognized keyword) all resolve
/// successfully to an otherwise-ordinary accepted profile. Combined with
/// caching by raw input text, that reopened the same unbounded-cache-growth
/// vector the prior fix closed for outright-rejected names — except this time
/// via inputs that still "worked". See <see cref="LocaleCollationRegistry"/>'s
/// remarks for the complete fix (a canonical, semantics-only cache key).
/// </para>
/// <para>
/// <b>Accepted profiles are an explicit, conservative allowlist — not full ICU
/// parity.</b> Only the base locale/region combinations this port has actually
/// implemented and verified against the pinned reference collator are accepted:
/// bare <c>en</c>, bare <c>es</c> (with or without <c>co=trad</c>), and
/// <c>fr-FR</c> exactly. A script subtag, a variant subtag, any other region
/// (including <c>fr-CA</c> — real BCP-47, but not a profile this port has
/// verified), and any other base language (including <c>sv</c> Swedish, whose
/// å/ä/ö sort as distinct primary letters after 'z' rather than as accented
/// a/o; <c>tr</c> Turkish, whose dotted/dotless I forms are distinct base
/// letters rather than a case pair; and <c>de</c> German, whose <c>co=phonebk</c>
/// tailoring expands ö/ü/ä into two collation elements) are all REJECTED by
/// <see cref="TryParse"/> rather than silently falling back to a generic Latin
/// comparator that would produce a plausible-looking but factually wrong order
/// for those locales. Likewise, <c>co</c> is accepted only as the literal value
/// <c>trad</c> and only paired with language <c>es</c>; every other <c>co</c>
/// value (including real CLDR types this port has not implemented, such as
/// <c>phonebk</c>, <c>pinyin</c>, <c>stroke</c>, <c>search</c>) is rejected. A
/// syntactically well-formed but entirely fictional language subtag (e.g.
/// <c>"zzz"</c>) is likewise rejected: this port does not attempt to
/// distinguish "real language we have not implemented" from "not a real
/// language at all" and fails closed for both, rather than risk silently
/// approximating a real locale's ordering with generic Latin rules.
/// </para>
/// </remarks>
public sealed record LocaleCollationTag
{
    /// <summary>
    /// The base language/region combinations this port has implemented and
    /// verified end-to-end against the pinned reference collator. Anything
    /// outside this set is rejected by <see cref="TryParse"/> — see the type
    /// remarks for why (known divergent tailoring for languages like Swedish/
    /// Turkish, and unverified regional variants like French Canadian).
    /// </summary>
    private static readonly HashSet<(string Language, string? Region)> AcceptedProfiles = new()
    {
        ("en", null),
        ("es", null),
        ("fr", "fr"),
    };

    private LocaleCollationTag(
        string canonicalName,
        string language,
        string? collationType,
        LocaleCaseFirst caseFirst,
        bool numeric)
    {
        CanonicalName = canonicalName;
        Language = language;
        CollationType = collationType;
        CaseFirst = caseFirst;
        Numeric = numeric;
    }

    /// <summary>
    /// A canonical name synthesized ONLY from this tag's parsed semantic
    /// fields (<see cref="Language"/>, region, <see cref="CollationType"/>,
    /// <see cref="CaseFirst"/>, <see cref="Numeric"/>) in a fixed keyword
    /// order — NEVER the caller's raw input text. Used both for display (error
    /// messages) and as <see cref="LocaleCollationRegistry"/>'s cache key. A
    /// review (2026-09-07) found that using the raw (only lowercased) input
    /// text here let two tags with IDENTICAL semantics but different spelling
    /// (different keyword order, an inert differently-valued <c>ks</c>, or
    /// simply different casing) occupy distinct cache entries — effectively
    /// unbounded, since BCP-47 keyword order is unconstrained and <c>ks</c>
    /// alone contributes 5 inert variants. Because this is rebuilt from only
    /// the finite semantic fields every accepted profile can have, the total
    /// number of distinct <see cref="CanonicalName"/> values across ALL
    /// accepted profiles is small and fixed (language/region combination ×
    /// collation type × case-first × numeric — 24 for the three profiles this
    /// port currently accepts), regardless of how creatively a caller spells,
    /// orders, or pads an equivalent input tag.
    /// </summary>
    public string CanonicalName { get; }

    /// <summary>The primary language subtag, lowercased (e.g. <c>"es"</c>, <c>"en"</c>, <c>"fr"</c>).</summary>
    public string Language { get; }

    /// <summary>
    /// The <c>co</c> (collation type) keyword value, lowercased, or <see langword="null"/>
    /// when absent. The only value <see cref="TryParse"/> accepts is
    /// <c>"trad"</c> paired with language <c>es</c> (CLDR traditional Spanish
    /// tailoring — the <c>ll</c>/<c>ch</c> digraph ordering); any other value,
    /// or <c>"trad"</c> paired with a different language, is REJECTED by
    /// <see cref="TryParse"/> rather than silently falling back to untailored
    /// ordering — see the type remarks.
    /// </summary>
    public string? CollationType { get; }

    /// <summary>The <c>kf</c> (case-first) keyword, defaulting to <see cref="LocaleCaseFirst.Off"/>.</summary>
    public LocaleCaseFirst CaseFirst { get; }

    /// <summary>The <c>kn</c> (numeric ordering) keyword, defaulting to <see langword="false"/>.</summary>
    public bool Numeric { get; }

    /// <summary>Whether the Spanish traditional digraph tailoring (<c>ll</c>/<c>ch</c>) applies.</summary>
    public bool UsesTraditionalDigraphs
        => string.Equals(CollationType, "trad", StringComparison.Ordinal);

    /// <summary>
    /// Attempts to parse <paramref name="tag"/> as a BCP-47 locale identifier
    /// carrying the Unicode locale extension keywords this port understands.
    /// Returns <see langword="false"/> for structurally malformed tags, a
    /// recognized keyword carrying an out-of-enumeration value, an unrecognized
    /// <c>-u-</c> keyword/attribute or any non-<c>u</c> singleton extension
    /// (including private-use <c>-x-</c> — see the type remarks on why these
    /// are rejected rather than tolerated), OR a syntactically well-formed tag
    /// that does not match one of <see cref="AcceptedProfiles"/> (see the type
    /// remarks: this includes known-different-tailoring languages like
    /// Swedish/Turkish/German phonebook, unverified regional variants like
    /// <c>fr-CA</c>, and any fictional language). Mirrors
    /// <c>icu_locale::Locale::from_str</c>/<c>icu_collator::Collator::try_new</c>
    /// failing closed for malformed input (empirically verified: a bad
    /// <c>ks</c> value is rejected by <c>Locale::from_str</c> itself with
    /// <c>InvalidExtension</c>, and space/punctuation-bearing garbage is
    /// rejected with <c>InvalidLanguage</c>) — the accepted-profile allowlist,
    /// and the rejection of any unrecognized-but-otherwise-legal BCP-47
    /// construct, are this port's own additional, more conservative
    /// restrictions on top of that.
    /// </summary>
    public static bool TryParse(string? tag, out LocaleCollationTag? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var subtags = tag.Split('-');
        var index = 0;

        if (!IsAlpha(subtags[0]) || subtags[0].Length is < 2 or > 8)
            return false;
        var language = subtags[0].ToLowerInvariant();
        index++;

        // Optional script subtag (4 alpha). No accepted profile carries a script
        // subtag, so its mere presence disqualifies the tag below.
        var hadScript = false;
        if (index < subtags.Length && subtags[index].Length == 4 && IsAlpha(subtags[index]))
        {
            hadScript = true;
            index++;
        }

        // Optional region subtag (2 alpha or 3 digit).
        string? region = null;
        if (index < subtags.Length
            && ((subtags[index].Length == 2 && IsAlpha(subtags[index]))
                || (subtags[index].Length == 3 && IsDigits(subtags[index]))))
        {
            region = subtags[index].ToLowerInvariant();
            index++;
        }

        // Variant subtags: 5-8 alphanumeric, or 4 characters starting with a
        // digit. No accepted profile carries a variant subtag either.
        var hadVariant = false;
        while (index < subtags.Length && IsVariant(subtags[index]))
        {
            hadVariant = true;
            index++;
        }

        var collationType = (string?)null;
        var caseFirst = LocaleCaseFirst.Off;
        var numeric = false;

        while (index < subtags.Length)
        {
            var singleton = subtags[index];
            if (singleton.Length != 1 || !IsAlphaNumeric(singleton))
                return false;

            // Only the Unicode locale extension ("-u-") is recognized. Every
            // other singleton extension — including private-use ("-x-...") —
            // is REJECTED outright rather than tolerated/skipped: see the type
            // remarks for why silently skipping arbitrary ignored content here
            // reopened an unbounded-cache-key vector a review found.
            if (!string.Equals(singleton, "u", StringComparison.OrdinalIgnoreCase))
                return false;

            index++;

            // Unicode locale extension "attributes" (free-form 3-8
            // alphanumeric subtags preceding the first keyword key) carry no
            // semantics this port interprets. Reject their presence instead
            // of silently skipping them, for the same reason.
            if (index < subtags.Length
                && subtags[index].Length is >= 3 and <= 8
                && IsAlphaNumeric(subtags[index]))
            {
                return false;
            }

            while (index < subtags.Length && subtags[index].Length != 1)
            {
                var key = subtags[index];
                if (key.Length != 2 || !IsAlphaNumeric(key))
                    return false;
                index++;

                var types = new List<string>();
                while (index < subtags.Length
                    && subtags[index].Length is >= 3 and <= 8
                    && IsAlphaNumeric(subtags[index]))
                {
                    types.Add(subtags[index]);
                    index++;
                }

                switch (key.ToLowerInvariant())
                {
                    case "kf":
                        if (types.Count != 1)
                            return false;
                        switch (types[0].ToLowerInvariant())
                        {
                            case "upper":
                                caseFirst = LocaleCaseFirst.Upper;
                                break;
                            case "lower":
                                caseFirst = LocaleCaseFirst.Lower;
                                break;
                            case "false":
                                caseFirst = LocaleCaseFirst.Off;
                                break;
                            default:
                                return false;
                        }
                        break;
                    case "kn":
                        if (types.Count != 1)
                            return false;
                        switch (types[0].ToLowerInvariant())
                        {
                            case "true":
                                numeric = true;
                                break;
                            case "false":
                                numeric = false;
                                break;
                            default:
                                return false;
                        }
                        break;
                    case "ks":
                        // Parsed and validated for shape fidelity only — see remarks
                        // above on why this has no effect on comparison strength.
                        // Deliberately excluded from CanonicalName/the cache key.
                        if (types.Count != 1)
                            return false;
                        if (types[0].ToLowerInvariant() is not ("level1" or "level2" or "level3" or "level4" or "identic"))
                            return false;
                        break;
                    case "co":
                        if (types.Count != 1)
                            return false;
                        collationType = types[0].ToLowerInvariant();
                        break;
                    default:
                        // Unrecognized keyword: REJECTED, not tolerated — see the
                        // type remarks (e.g. "en-u-zz-abcdefgh").
                        return false;
                }
            }
        }

        // Accepted-profile allowlist: reject anything outside the base
        // locale/region combinations this port has implemented and verified
        // against the pinned reference collator, and any co value other than
        // "trad" paired with "es" — see type remarks for why this is
        // deliberately conservative rather than a best-effort fallback.
        if (hadScript || hadVariant)
            return false;
        if (!AcceptedProfiles.Contains((language, region)))
            return false;
        if (collationType is not null
            && !(string.Equals(collationType, "trad", StringComparison.Ordinal)
                && string.Equals(language, "es", StringComparison.Ordinal)))
        {
            return false;
        }

        result = new LocaleCollationTag(
            BuildCanonicalName(language, region, collationType, caseFirst, numeric),
            language,
            collationType,
            caseFirst,
            numeric);
        return true;
    }

    /// <summary>
    /// Synthesizes <see cref="CanonicalName"/> from only the parsed semantic
    /// fields, in a fixed keyword order (<c>co</c>, then <c>kf</c>, then
    /// <c>kn</c>), so that every accepted tag with the same semantics —
    /// regardless of input casing, keyword order, or an accompanying (inert)
    /// <c>ks</c> value — produces the exact same string. See
    /// <see cref="CanonicalName"/>'s remarks.
    /// </summary>
    private static string BuildCanonicalName(
        string language,
        string? region,
        string? collationType,
        LocaleCaseFirst caseFirst,
        bool numeric)
    {
        var name = region is null ? language : $"{language}-{region}";

        var extensions = new List<string>(3);
        if (collationType is not null)
            extensions.Add($"co-{collationType}");
        if (caseFirst != LocaleCaseFirst.Off)
            extensions.Add($"kf-{(caseFirst == LocaleCaseFirst.Upper ? "upper" : "lower")}");
        if (numeric)
            extensions.Add("kn-true");

        return extensions.Count == 0 ? name : $"{name}-u-{string.Join('-', extensions)}";
    }

    private static bool IsAlpha(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetter(ch))
                return false;
        }

        return true;
    }

    private static bool IsDigits(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
                return false;
        }

        return true;
    }

    private static bool IsAlphaNumeric(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
                return false;
        }

        return true;
    }

    private static bool IsVariant(string value)
    {
        if (value.Length == 0 || !IsAlphaNumeric(value))
            return false;
        if (value.Length is >= 5 and <= 8)
            return true;
        return value.Length == 4 && char.IsAsciiDigit(value[0]);
    }
}
