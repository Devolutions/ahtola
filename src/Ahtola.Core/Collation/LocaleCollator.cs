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

    /// <summary>
    /// Writes a canonical, self-delimited byte key such that
    /// <c>WriteSortKey(a)</c> orders the same as <c>WriteSortKey(b)</c> under
    /// byte-wise comparison whenever <see cref="Compare"/> orders <c>a</c> and
    /// <c>b</c> the same way, and two strings that compare equal always produce
    /// an identical key (including a precomposed accented letter and its
    /// canonically-decomposed spelling — see type remarks). Used both for
    /// persisted index byte ordering and as a canonical equality key, mirroring
    /// Turso's <c>LocaleCollationRegistry::sort_key</c> (which backs
    /// <c>CollationSeq::hash_key</c> for the locale variant).
    /// </summary>
    public byte[] WriteSortKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var elements = BuildElements(text);

        var buffer = new List<byte>(elements.Count * 4 + 8);
        AppendLevel(buffer, elements, e => e.Primary.Group);
        buffer.Add(0);
        AppendLevel(buffer, elements, e => e.Primary.Major);
        buffer.Add(0);
        foreach (var element in elements)
            AppendString(buffer, element.Primary.Minor);
        buffer.Add(0);
        AppendLevel(buffer, elements, e => e.Secondary);
        buffer.Add(0);
        AppendLevel(buffer, elements, e => EffectiveTertiary(e.Tertiary));
        return buffer.ToArray();
    }

    private static void AppendLevel(List<byte> buffer, List<CollationElement> elements, Func<CollationElement, int> selector)
    {
        foreach (var element in elements)
        {
            var value = selector(element);
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
    }

    private static void AppendString(List<byte> buffer, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            buffer.Add(0);
            return;
        }

        foreach (var ch in value)
            buffer.Add((byte)ch);
        buffer.Add(0xFF);
    }

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
