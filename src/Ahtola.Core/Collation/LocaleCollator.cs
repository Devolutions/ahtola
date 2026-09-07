namespace Ahtola.Core.Collation;

/// <summary>
/// A pure-managed, UCA-inspired string comparator for one parsed
/// <see cref="LocaleCollationTag"/>. See <see cref="LocaleCollationWeights"/> for
/// the underlying per-character weight table and citations, and
/// <see cref="LocaleCollationTag"/> for which BCP-47 keywords are interpreted
/// and why.
/// </summary>
/// <remarks>
/// <para>
/// Comparison runs three passes over a per-string sequence of collation
/// elements: primary (base letter / digit value / digraph / Spanish ñ),
/// secondary (accent), and tertiary (case, oriented by <c>kf</c>). This always
/// compares through the tertiary level regardless of the parsed (but inert)
/// <c>ks</c> value — see <see cref="LocaleCollationTag"/> remarks for why that
/// matches the pinned reference collator's actual behavior rather than a naive
/// reading of the BCP-47 spec text.
/// </para>
/// <para>
/// <b>Scope and fail-closed behavior.</b> Only the following are given a
/// verified collation weight: ASCII letters and the 33 verified ASCII
/// punctuation/whitespace characters (see <see cref="LocaleCollationWeights"/>),
/// ASCII digits (plain or <c>kn</c>-folded numeric runs), the Latin-1
/// Supplement/Extended-A accented letters covered by
/// <see cref="LocaleCollationWeights"/> — recognized in BOTH their precomposed
/// spelling and their canonically-decomposed "base letter + combining mark"
/// spelling, which is required for <c>'é' = 'e'||char(0x301)</c> to hold — and,
/// for the Spanish language tag specifically, <c>ñ</c>/<c>Ñ</c> as a distinct
/// primary letter and the traditional <c>ll</c>/<c>ch</c> digraphs. Any other
/// character (non-Latin scripts, ligatures without a real decomposition such as
/// æ/ß, or any codepoint/combining-mark combination this port has not verified)
/// throws <see cref="NotSupportedException"/> rather than silently falling back
/// to code point order: a previous version of this port did the latter, and a
/// review correctly rejected it as installing wrong persisted index ordering
/// for characters this port cannot faithfully collate. The exception propagates
/// through the same path a registered custom collation callback's exception
/// already does (see <c>EmbeddedDatabase.InvokeManagedCallback</c>).
/// </para>
/// </remarks>
internal sealed class LocaleCollator
{
    private const int GroupPunctuation = 0;
    private const int GroupDigit = 1;
    private const int GroupLetter = 2;

    private static readonly int LetterN = LocaleCollationWeights.LetterN;
    private static readonly int LetterL = LocaleCollationWeights.LetterL;
    private static readonly int LetterC = LocaleCollationWeights.LetterC;
    private static readonly int LetterH = LocaleCollationWeights.LetterH;

    private readonly LocaleCollationTag _tag;

    public LocaleCollator(LocaleCollationTag tag)
    {
        _tag = tag ?? throw new ArgumentNullException(nameof(tag));
    }

    public int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var leftElements = BuildElements(left);
        var rightElements = BuildElements(right);

        var count = Math.Min(leftElements.Count, rightElements.Count);
        for (var i = 0; i < count; i++)
        {
            var comparison = leftElements[i].Primary.CompareTo(rightElements[i].Primary);
            if (comparison != 0)
                return comparison;
        }
        if (leftElements.Count != rightElements.Count)
            return leftElements.Count.CompareTo(rightElements.Count);

        for (var i = 0; i < count; i++)
        {
            var comparison = leftElements[i].Secondary.CompareTo(rightElements[i].Secondary);
            if (comparison != 0)
                return comparison;
        }

        for (var i = 0; i < count; i++)
        {
            var comparison = EffectiveTertiary(leftElements[i].Tertiary).CompareTo(EffectiveTertiary(rightElements[i].Tertiary));
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    // REMOVED: WriteSortKey (byte-key encoding) — bug found by review
    // (2026-09-07, commit ad4dbca chain).
    //
    // A prior version of this file exposed WriteSortKey, claiming it produced
    // a canonical byte key whose byte-wise order always matched Compare's
    // order, including when strings had a different element count. That
    // claim was FALSE: the encoding wrote each collation level (all
    // elements' Group values, then all elements' Major values, then all
    // elements' Minor strings, then all Secondary values, then all Tertiary
    // values) as flat fixed-width runs separated by a single 0x00 byte. When
    // two strings produced a different NUMBER of elements, e.g.
    // Compare("ab", "ab ") (2 elements vs. 3 — the trailing space is its own
    // punctuation element), the flat per-level layout misaligned: "ab"'s
    // single group-level separator byte landed at the exact same byte offset
    // as the LAST BYTE of "ab "'s third group value (which can itself be 0),
    // so byte-wise comparison continued past that point comparing "ab"'s
    // Major level against "ab "'s still-unfinished Group level — an
    // apples-to-oranges comparison with no fixed relationship to the
    // intended order. Concretely, Compare("ab", "ab ") correctly returns
    // "ab" < "ab " (shorter is a true prefix), but byte-wise comparing the
    // two former WriteSortKey outputs returned the opposite result.
    //
    // A structurally correct fix requires a real self-delimiting,
    // order-preserving encoding per collation element (e.g. escaping every
    // raw 0x00 byte within an element's encoded fields and terminating each
    // element with an unescaped 0x00 0x00 marker, so a shorter tied prefix's
    // terminator is always guaranteed to sort before a longer string's
    // continuing element bytes regardless of what secondary/tertiary content
    // follows) — NOT a naive per-level length prefix, which would itself
    // break ordinary lexicographic tie-breaking (e.g. a single-element "b"
    // must still sort AFTER the two-element "aa" because 'b' > 'a' at the
    // very first element, even though "b" has fewer elements than "aa";
    // comparing element counts first would incorrectly reverse that).
    //
    // Nothing in this codebase calls WriteSortKey: index-writer/persisted-
    // ordering paths use the Compare delegate directly (see
    // LocaleCollationRegistry, Storage.SqliteKeyCollation), never a byte-key
    // encoding. Given zero production callers, the safest and most honest
    // fix is to remove the unused, demonstrably-incorrect API and its
    // overclaiming doc comment entirely, rather than ship an intricate
    // escaping scheme for code nothing exercises. If a byte-key encoding is
    // ever actually needed (e.g. a future persisted-index format keyed by
    // raw bytes instead of a comparison delegate), implement the
    // escaped/self-delimiting scheme described above and add the exact
    // "same-prefix, different-length" counterexample this note describes as
    // a regression test before trusting it.
    private int EffectiveTertiary(int caseClass)
        => _tag.CaseFirst == LocaleCaseFirst.Upper ? 1 - caseClass : caseClass;

    private List<CollationElement> BuildElements(string text)
    {
        var elements = new List<CollationElement>(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (_tag.Numeric && char.IsAsciiDigit(text[index]))
            {
                var start = index;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                    index++;

                elements.Add(BuildNumericElement(text.AsSpan(start, index - start)));
                continue;
            }

            if (_tag.UsesTraditionalDigraphs && TryBuildDigraph(text, index, out var digraph, out var digraphConsumed))
            {
                elements.Add(digraph);
                index += digraphConsumed;
                continue;
            }

            elements.Add(BuildElement(text, index, out var consumed));
            index += consumed;
        }

        return elements;
    }

    private static CollationElement BuildNumericElement(ReadOnlySpan<char> digits)
    {
        var start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
            start++;
        var stripped = digits[start..];

        // Numeric runs sort at the start of the "digit" reordering group (before
        // Latin letters, after punctuation), per UTS #35's description of the
        // "kn" keyword and verified against the pinned collator (digits sort
        // after punctuation/whitespace and before letters). Comparing by
        // (digit count, then digit text) after stripping leading zeros
        // reproduces correct numeric magnitude ordering for arbitrarily long
        // digit runs without integer overflow.
        return new CollationElement(
            new PrimaryWeight(GroupDigit, Major: stripped.Length, Minor: stripped.ToString()),
            Secondary: 0,
            Tertiary: 0);
    }

    private static bool TryBuildDigraph(string text, int index, out CollationElement element, out int consumed)
    {
        element = default;
        consumed = 0;

        var codepoint = char.ConvertToUtf32(text, index);
        var charLength = char.IsSurrogatePair(text, index) ? 2 : 1;
        if (!LocaleCollationWeights.TryGetWeight(codepoint, out var baseLetter, out var accent, out var caseClass)
            || accent != LocaleCollationWeights.Accent.None)
        {
            return false;
        }

        var nextIndex = index + charLength;
        if (nextIndex >= text.Length)
            return false;

        var nextCodepoint = char.ConvertToUtf32(text, nextIndex);
        var nextLength = char.IsSurrogatePair(text, nextIndex) ? 2 : 1;
        if (!LocaleCollationWeights.TryGetWeight(nextCodepoint, out var nextBaseLetter, out var nextAccent, out var nextCaseClass)
            || nextAccent != LocaleCollationWeights.Accent.None)
        {
            return false;
        }

        if (baseLetter == LetterL && nextBaseLetter == LetterL)
        {
            element = new CollationElement(
                new PrimaryWeight(GroupLetter, Major: LetterL * 1000 + 500, Minor: null),
                Secondary: 0,
                Tertiary: caseClass * 2 + nextCaseClass);
            consumed = charLength + nextLength;
            return true;
        }

        if (baseLetter == LetterC && nextBaseLetter == LetterH)
        {
            element = new CollationElement(
                new PrimaryWeight(GroupLetter, Major: LetterC * 1000 + 500, Minor: null),
                Secondary: 0,
                Tertiary: caseClass * 2 + nextCaseClass);
            consumed = charLength + nextLength;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the single collation element beginning at <paramref name="index"/>,
    /// consuming either one codepoint (a bare ASCII letter, a precomposed
    /// accented letter, an ASCII digit, or an ASCII punctuation character) or two
    /// (a base ASCII letter immediately followed by a supported canonical
    /// combining mark — the decomposed spelling of an accented letter, or of
    /// Spanish ñ). Throws <see cref="NotSupportedException"/> for anything else.
    /// </summary>
    private CollationElement BuildElement(string text, int index, out int consumed)
    {
        var codepoint = char.ConvertToUtf32(text, index);
        var charLength = char.IsSurrogatePair(text, index) ? 2 : 1;

        // Spanish ñ tailoring: a distinct primary letter between 'n' and 'o',
        // for BOTH the precomposed codepoint and the decomposed "n"/"N" +
        // combining tilde spelling — verified against the pinned collator for
        // both es (default/modern) and es-u-co-trad (see LocaleCollator/
        // LocaleCollationWeights remarks). Every other language treats ñ as an
        // ordinary accented 'n' (secondary difference only), which the general
        // decomposition path below already covers correctly.
        if (string.Equals(_tag.Language, "es", StringComparison.Ordinal))
        {
            if (codepoint is 0x00F1 or 0x00D1) // ñ / Ñ precomposed
            {
                consumed = charLength;
                return BuildSpanishEnye(caseClass: codepoint == 0x00D1 ? LocaleCollationWeights.Upper : LocaleCollationWeights.Lower);
            }

            if (codepoint is 'n' or 'N')
            {
                var nextIndex = index + charLength;
                if (nextIndex < text.Length)
                {
                    var nextCodepoint = char.ConvertToUtf32(text, nextIndex);
                    if (nextCodepoint == 0x0303) // combining tilde
                    {
                        var nextLength = char.IsSurrogatePair(text, nextIndex) ? 2 : 1;
                        consumed = charLength + nextLength;
                        return BuildSpanishEnye(caseClass: codepoint == 'N' ? LocaleCollationWeights.Upper : LocaleCollationWeights.Lower);
                    }
                }
            }
        }

        // Decomposed spelling: a bare ASCII letter immediately followed by one of
        // the verified combining marks (e.g. "e" + U+0301 == precomposed 'é'). This
        // check must run BEFORE the precomposed-letter lookup below, because a bare
        // ASCII letter always resolves via LocaleCollationWeights.TryGetWeight's
        // fast path (accent=None) and would otherwise short-circuit before ever
        // examining the following codepoint.
        if (codepoint is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
        {
            var nextIndex = index + charLength;
            if (nextIndex < text.Length)
            {
                var nextCodepoint = char.ConvertToUtf32(text, nextIndex);
                if (LocaleCollationWeights.TryGetCombiningMarkAccent(nextCodepoint, out var markAccent))
                {
                    var nextLength = char.IsSurrogatePair(text, nextIndex) ? 2 : 1;
                    consumed = charLength + nextLength;
                    var isUpper = codepoint is >= 'A' and <= 'Z';
                    var letterIndex = (isUpper ? codepoint - 'A' : codepoint - 'a') + 1;
                    return new CollationElement(
                        new PrimaryWeight(GroupLetter, Major: letterIndex * 1000, Minor: null),
                        Secondary: markAccent,
                        Tertiary: isUpper ? LocaleCollationWeights.Upper : LocaleCollationWeights.Lower);
                }
            }
        }

        if (LocaleCollationWeights.TryGetWeight(codepoint, out var baseLetter, out var accent, out var caseClassResolved))
        {
            consumed = charLength;
            return new CollationElement(
                new PrimaryWeight(GroupLetter, Major: baseLetter * 1000, Minor: null),
                Secondary: accent,
                Tertiary: caseClassResolved);
        }

        if (LocaleCollationWeights.TryGetPunctuationRank(codepoint, out var punctuationRank))
        {
            consumed = charLength;
            return new CollationElement(
                new PrimaryWeight(GroupPunctuation, Major: punctuationRank, Minor: null),
                Secondary: 0,
                Tertiary: 0);
        }

        if (char.IsAsciiDigit(text[index]))
        {
            // Reached only when _tag.Numeric is false (the numeric-run path in
            // BuildElements already consumed any digit run otherwise): a single
            // digit still needs a real weight, ordered after punctuation and
            // before letters exactly like the numeric-run path, matching the
            // verified default (non-kn) digit ordering.
            consumed = charLength;
            return new CollationElement(
                new PrimaryWeight(GroupDigit, Major: codepoint - '0', Minor: null),
                Secondary: 0,
                Tertiary: 0);
        }

        throw new NotSupportedException(
            $"Locale collation '{_tag.CanonicalName}' does not support character U+{codepoint:X4}: outside the verified ASCII/Latin-accented repertoire this port implements.");
    }

    private static CollationElement BuildSpanishEnye(int caseClass)
        => new(
            new PrimaryWeight(GroupLetter, Major: LetterN * 1000 + 500, Minor: null),
            Secondary: 0,
            Tertiary: caseClass);

    private readonly record struct CollationElement(PrimaryWeight Primary, int Secondary, int Tertiary);

    private readonly record struct PrimaryWeight(int Group, int Major, string? Minor) : IComparable<PrimaryWeight>
    {
        public int CompareTo(PrimaryWeight other)
        {
            var comparison = Group.CompareTo(other.Group);
            if (comparison != 0)
                return comparison;
            comparison = Major.CompareTo(other.Major);
            if (comparison != 0)
                return comparison;
            return string.CompareOrdinal(Minor ?? string.Empty, other.Minor ?? string.Empty);
        }
    }
}
