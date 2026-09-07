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
/// </remarks>
public sealed record LocaleCollationTag
{
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
    /// when absent. Only <c>"trad"</c> (CLDR traditional Spanish tailoring — the
    /// <c>ll</c>/<c>ch</c> digraph ordering) changes comparison behavior here; any
    /// other value is accepted syntactically and falls back to the untailored
    /// (root-equivalent) ordering, matching CLDR's own type-fallback rule that an
    /// unrecognized/unavailable collation type falls back to the default tailoring
    /// rather than failing.
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
    /// Returns <see langword="false"/> for structurally malformed tags or a
    /// recognized keyword carrying an out-of-enumeration value — mirroring
    /// <c>icu_locale::Locale::from_str</c>/<c>icu_collator::Collator::try_new</c>
    /// failing closed for the same inputs (empirically verified: a bad <c>ks</c>
    /// value is rejected by <c>Locale::from_str</c> itself with
    /// <c>InvalidExtension</c>, and space/punctuation-bearing garbage is rejected
    /// with <c>InvalidLanguage</c>). A syntactically valid but otherwise unknown
    /// language subtag (e.g. <c>"not-a-real-locale-zzz"</c>) is accepted and
    /// falls back to root-equivalent ordering, exactly as Turso's collator falls
    /// back to the CLDR root collation for an unrecognized language.
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

        // Optional script subtag (4 alpha).
        if (index < subtags.Length && subtags[index].Length == 4 && IsAlpha(subtags[index]))
            index++;

        // Optional region subtag (2 alpha or 3 digit).
        if (index < subtags.Length
            && ((subtags[index].Length == 2 && IsAlpha(subtags[index]))
                || (subtags[index].Length == 3 && IsDigits(subtags[index]))))
        {
            index++;
        }

        // Variant subtags: 5-8 alphanumeric, or 4 characters starting with a digit.
        while (index < subtags.Length && IsVariant(subtags[index]))
            index++;

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
