namespace Ahtola.Core.Collation;

/// <summary>
/// A pure-managed, UCA-inspired string comparator for one parsed
/// <see cref="LocaleCollationTag"/>. See <see cref="LocaleCollationWeights"/> for
/// the underlying per-character weight table and <see cref="LocaleCollationTag"/>
/// for which BCP-47 keywords are interpreted and why.
/// </summary>
/// <remarks>
/// Comparison runs three passes over a per-string sequence of collation elements:
/// primary (base letter / digit value / digraph), secondary (accent), and
/// tertiary (case, oriented by <c>kf</c>). This always compares through the
/// tertiary level regardless of the parsed (but inert) <c>ks</c> value — see
/// <see cref="LocaleCollationTag"/> remarks for why that matches Turso's actual
/// pinned-crate behavior rather than a naive reading of the BCP-47 spec text.
/// </remarks>
internal sealed class LocaleCollator
{
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
    /// an identical key. Used both for persisted index byte ordering and as a
    /// canonical equality key, mirroring Turso's <c>LocaleCollationRegistry::sort_key</c>
    /// (which backs <c>CollationSeq::hash_key</c> for the locale variant).
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

            var codepoint = char.ConvertToUtf32(text, index);
            var charLength = char.IsSurrogatePair(text, index) ? 2 : 1;

            if (_tag.UsesTraditionalDigraphs && TryBuildDigraph(text, index, charLength, out var digraph, out var consumed))
            {
                elements.Add(digraph);
                index += consumed;
                continue;
            }

            elements.Add(BuildCharacterElement(codepoint));
            index += charLength;
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
        // Latin letters), per UTS #35's description of the "kn" keyword: "The
        // computed primary weights are all at the start of the digit reordering
        // group." Comparing by (digit count, then digit text) after stripping
        // leading zeros reproduces correct numeric magnitude ordering for
        // arbitrarily long digit runs without integer overflow.
        return new CollationElement(
            new PrimaryWeight(Group: 0, Major: stripped.Length, Minor: stripped.ToString()),
            Secondary: 0,
            Tertiary: 0);
    }

    private static bool TryBuildDigraph(string text, int index, int charLength, out CollationElement element, out int consumed)
    {
        element = default;
        consumed = 0;

        var codepoint = char.ConvertToUtf32(text, index);
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

        var letterL = 'l' - 'a' + 1;
        var letterC = 'c' - 'a' + 1;
        var letterH = 'h' - 'a' + 1;

        if (baseLetter == letterL && nextBaseLetter == letterL)
        {
            element = new CollationElement(
                new PrimaryWeight(Group: 1, Major: letterL * 1000 + 500, Minor: null),
                Secondary: 0,
                Tertiary: caseClass * 2 + nextCaseClass);
            consumed = charLength + nextLength;
            return true;
        }

        if (baseLetter == letterC && nextBaseLetter == letterH)
        {
            element = new CollationElement(
                new PrimaryWeight(Group: 1, Major: letterC * 1000 + 500, Minor: null),
                Secondary: 0,
                Tertiary: caseClass * 2 + nextCaseClass);
            consumed = charLength + nextLength;
            return true;
        }

        return false;
    }

    private static CollationElement BuildCharacterElement(int codepoint)
    {
        if (LocaleCollationWeights.TryGetWeight(codepoint, out var baseLetter, out var accent, out var caseClass))
        {
            return new CollationElement(
                new PrimaryWeight(Group: 1, Major: baseLetter * 1000, Minor: null),
                Secondary: accent,
                Tertiary: caseClass);
        }

        // Fallback for anything outside the supported Latin range (digits when
        // kn=false, punctuation, symbols, and non-Latin scripts): order by code
        // point. This still yields a total, transitive, stable order — see the
        // scope note on LocaleCollationWeights.
        return new CollationElement(
            new PrimaryWeight(Group: 2, Major: codepoint, Minor: null),
            Secondary: 0,
            Tertiary: 0);
    }

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
