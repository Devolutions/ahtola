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
/// <c>identic</c>) for syntax fidelity, but — matching the empirically observed
/// behavior of the pinned <c>icu_collator</c>/<c>icu_locale</c> 2.3.x crates when
/// constructed via <c>Collator::try_new(locale, CollatorOptions::default())</c> —
/// it has no effect on comparison strength: comparisons always run through the
/// tertiary (case) level regardless of the requested <c>ks</c> value. This was
/// verified directly against the pinned crate versions (Cargo.lock-resolved
/// <c>icu_collator</c>/<c>icu_locale</c> 2.3.1 for the <c>turso-src</c>
/// <c>v0.8.0-pre.7</c> tag): <c>en-u-ks-level1</c> and <c>en-u-ks-level2</c> both
/// still distinguish case and accents exactly like the tertiary-strength default,
/// so an implementation that faithfully reproduces Turso's current behavior must
/// not truncate strength either. Any unrecognized <c>-u-</c> keyword is tolerated
/// and ignored (BCP-47 permits private/registry-specific keywords); an unrecognized
/// singleton extension (e.g. <c>-t-</c>, <c>-a-</c>) is skipped structurally rather
/// than interpreted.
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
    /// The case-folded original tag text, used as the registry cache key. BCP-47
    /// tags are case-insensitive (<c>FR-fr</c> and <c>fr-FR</c> name the same
    /// locale), matching Turso's <c>eq_ignore_ascii_case</c> name comparison in
    /// <c>LocaleCollationRegistry::find</c>.
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
    /// recognized keyword carrying an out-of-enumeration value, OR a
    /// syntactically well-formed tag that does not match one of
    /// <see cref="AcceptedProfiles"/> (see the type remarks: this includes
    /// known-different-tailoring languages like Swedish/Turkish/German
    /// phonebook, unverified regional variants like <c>fr-CA</c>, and any
    /// fictional language). Mirrors <c>icu_locale::Locale::from_str</c>/
    /// <c>icu_collator::Collator::try_new</c> failing closed for malformed
    /// input (empirically verified: a bad <c>ks</c> value is rejected by
    /// <c>Locale::from_str</c> itself with <c>InvalidExtension</c>, and
    /// space/punctuation-bearing garbage is rejected with
    /// <c>InvalidLanguage</c>) — the accepted-profile allowlist is this port's
    /// own additional, more conservative restriction on top of that.
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

            if (string.Equals(singleton, "x", StringComparison.OrdinalIgnoreCase))
            {
                // Private-use extension: the remainder is opaque content we do not
                // interpret. Validate subtag shape only, then stop.
                index++;
                while (index < subtags.Length)
                {
                    if (subtags[index].Length is < 1 or > 8 || !IsAlphaNumeric(subtags[index]))
                        return false;
                    index++;
                }

                break;
            }

            if (string.Equals(singleton, "u", StringComparison.OrdinalIgnoreCase))
            {
                index++;

                // Attributes: subtags of length 3-8 that appear before the first
                // 2-character keyword key.
                while (index < subtags.Length
                    && subtags[index].Length is >= 3 and <= 8
                    && IsAlphaNumeric(subtags[index]))
                {
                    index++;
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
                            // Unrecognized keyword: tolerated, its type subtags are
                            // already consumed above; no interpretation possible.
                            break;
                    }
                }

                continue;
            }

            // Unrecognized extension singleton: skip its content without
            // interpreting it (we do not implement transform/other extensions).
            index++;
            while (index < subtags.Length && subtags[index].Length != 1)
                index++;
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
            tag.ToLowerInvariant(),
            language,
            collationType,
            caseFirst,
            numeric);
        return true;
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
