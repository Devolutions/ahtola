namespace Ahtola.Core.Collation;

/// <summary>
/// A pure-managed, empirically-verified Unicode Collation Algorithm-style weight
/// table for the Latin script: Basic Latin, ASCII punctuation/whitespace, and the
/// commonly used single-combining-mark accented letters of the Latin-1
/// Supplement and Latin Extended-A blocks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accent secondary-weight order.</b> Every value in <see cref="Accent"/> was
/// verified empirically against the pinned <c>icu_collator</c>/<c>icu_locale</c>
/// 2.3.1 crates (the exact versions <c>turso-src</c> v0.8.0-pre.7/277ddd050
/// resolves via Cargo.lock) by comparing the base letters <c>'a'</c> and
/// <c>'e'</c> each combined with every one of these thirteen combining marks
/// pairwise, confirming the SAME relative order holds for both base letters and
/// for both <c>en</c> and <c>fr-FR</c> (i.e. this is root/UCA-default ordering,
/// not something either locale tailors). This replaced an earlier, incorrect
/// hand-guessed ordering (combining-mark code point order), which a review
/// correctly rejected as an unverified approximation. The verified order is:
/// none &lt; acute &lt; grave &lt; breve &lt; circumflex &lt; caron &lt;
/// ring-above &lt; diaeresis &lt; double-acute &lt; tilde &lt; dot-above &lt;
/// cedilla &lt; ogonek &lt; macron.
/// </para>
/// <para>
/// <b>Precomposed/decomposed equivalence.</b> <see cref="Table"/> maps a
/// precomposed accented codepoint (e.g. U+00E9 'é') to the same
/// (base letter, accent, case) triple that <see cref="TryGetCombiningMarkAccent"/>
/// resolves for the corresponding decomposed form (base letter 'e' followed by
/// combining acute U+0301). <see cref="LocaleCollator"/> builds an identical
/// collation element for both spellings, which is what makes
/// <c>'é' = 'e' || char(0x301)</c> compare equal under any tag — the specific
/// defect a review found in an earlier version of this port that only handled
/// the precomposed spelling.
/// </para>
/// <para>
/// <b>ASCII punctuation/whitespace order.</b> <see cref="PunctuationRankTable"/>
/// is the verified order of the 33 printable non-alphanumeric ASCII characters
/// (space through <c>~</c>), confirmed locale-invariant (identical for
/// <c>en</c>, <c>fr-FR</c>, and <c>es</c>) against the same pinned crates. A
/// naive code point fallback for this set was verified to be WRONG (e.g. UCA
/// orders <c>'-'</c> before <c>','</c>, which code point order does not), so a
/// small closed table is used instead of guessing.
/// </para>
/// <para>
/// <b>Deliberately excluded characters.</b> U+00D8/U+00F8 (Ø/ø) and
/// U+0141/U+0142 (Ł/ł) are NOT included, even though they visually resemble "o
/// with stroke" / "l with stroke": Unicode does not give them a canonical
/// decomposition into base + combining mark, and this port has no verified
/// weight-table position for whatever tailoring the reference collator applies
/// to them. Likewise excluded: ligatures with no decomposition equivalence
/// (U+00E6 æ, U+00DF ß — both verified NOT reducible to their component
/// letters for collation purposes), and every character outside Basic Latin +
/// the covered Latin-1/Extended-A accented set. <see cref="LocaleCollator"/>
/// throws for any of these rather than silently ordering them by code point —
/// a real review correctly rejected the previous "approximate, then fall back
/// to code point order" behavior as installing wrong persisted index ordering
/// for characters this port cannot faithfully collate.
/// </para>
/// </remarks>
internal static class LocaleCollationWeights
{
    /// <summary>
    /// Accent secondary-weight ranks. See the type remarks for how this order
    /// was verified; do not reorder these without re-verifying against the
    /// pinned collator.
    /// </summary>
    internal static class Accent
    {
        internal const int None = 0;
        internal const int Acute = 1;
        internal const int Grave = 2;
        internal const int Breve = 3;
        internal const int Circumflex = 4;
        internal const int Caron = 5;
        internal const int RingAbove = 6;
        internal const int Diaeresis = 7;
        internal const int DoubleAcute = 8;
        internal const int Tilde = 9;
        internal const int DotAbove = 10;
        internal const int Cedilla = 11;
        internal const int Ogonek = 12;
        internal const int Macron = 13;
    }

    internal const int Lower = 0;
    internal const int Upper = 1;

    /// <summary>The Latin base-letter index assigned to Spanish 'n'/'ñ' tailoring (see <see cref="LocaleCollator"/>).</summary>
    internal const int LetterN = 'n' - 'a' + 1;

    /// <summary>The Latin base-letter index used by the traditional Spanish "ll" digraph tailoring.</summary>
    internal const int LetterL = 'l' - 'a' + 1;

    /// <summary>The Latin base-letter index used by the traditional Spanish "ch" digraph tailoring.</summary>
    internal const int LetterC = 'c' - 'a' + 1;

    /// <summary>The Latin base-letter index used by the traditional Spanish "ch" digraph tailoring.</summary>
    internal const int LetterH = 'h' - 'a' + 1;

    private static readonly Dictionary<int, (int BaseLetter, int Accent, int Case)> Table = BuildPrecomposedTable();
    private static readonly Dictionary<int, int> CombiningMarkAccent = BuildCombiningMarkTable();
    private static readonly Dictionary<int, int> PunctuationRankTable = BuildPunctuationTable();

    /// <summary>
    /// Attempts to resolve a precomposed <paramref name="codepoint"/> (a bare
    /// ASCII letter, or a single accented Latin-1/Extended-A letter covered by
    /// <see cref="Table"/>) to its base letter (1='a'..26='z'), accent rank, and
    /// case class.
    /// </summary>
    public static bool TryGetWeight(int codepoint, out int baseLetter, out int accent, out int caseClass)
    {
        if (codepoint is >= 'a' and <= 'z')
        {
            baseLetter = codepoint - 'a' + 1;
            accent = Accent.None;
            caseClass = Lower;
            return true;
        }

        if (codepoint is >= 'A' and <= 'Z')
        {
            baseLetter = codepoint - 'A' + 1;
            accent = Accent.None;
            caseClass = Upper;
            return true;
        }

        if (Table.TryGetValue(codepoint, out var entry))
        {
            baseLetter = entry.BaseLetter;
            accent = entry.Accent;
            caseClass = entry.Case;
            return true;
        }

        baseLetter = 0;
        accent = 0;
        caseClass = 0;
        return false;
    }

    /// <summary>
    /// Attempts to resolve a standalone Unicode combining mark codepoint (as it
    /// would appear immediately after a base letter in NFD/decomposed text, e.g.
    /// U+0301 combining acute accent) to the same accent rank
    /// <see cref="TryGetWeight"/> assigns to the equivalent precomposed letter.
    /// </summary>
    public static bool TryGetCombiningMarkAccent(int codepoint, out int accent)
        => CombiningMarkAccent.TryGetValue(codepoint, out accent);

    /// <summary>
    /// Attempts to resolve one of the 33 verified ASCII punctuation/whitespace
    /// characters (space through <c>~</c>) to its relative order rank.
    /// </summary>
    public static bool TryGetPunctuationRank(int codepoint, out int rank)
        => PunctuationRankTable.TryGetValue(codepoint, out rank);

    private static Dictionary<int, int> BuildCombiningMarkTable() => new()
    {
        [0x0301] = Accent.Acute,
        [0x0300] = Accent.Grave,
        [0x0306] = Accent.Breve,
        [0x0302] = Accent.Circumflex,
        [0x030C] = Accent.Caron,
        [0x030A] = Accent.RingAbove,
        [0x0308] = Accent.Diaeresis,
        [0x030B] = Accent.DoubleAcute,
        [0x0303] = Accent.Tilde,
        [0x0307] = Accent.DotAbove,
        [0x0327] = Accent.Cedilla,
        [0x0328] = Accent.Ogonek,
        [0x0304] = Accent.Macron,
    };

    private static Dictionary<int, int> BuildPunctuationTable()
    {
        // Verified order (locale-invariant across en/fr-FR/es against the pinned
        // icu_collator 2.3.1): see type remarks. NOT code point order.
        ReadOnlySpan<char> verifiedOrder =
            [' ', '_', '-', ',', ';', ':', '!', '?', '.', '\'', '"', '(', ')', '[', ']', '{', '}',
             '@', '*', '/', '\\', '&', '#', '%', '`', '^', '+', '<', '=', '>', '|', '~', '$'];

        var table = new Dictionary<int, int>(verifiedOrder.Length);
        for (var rank = 0; rank < verifiedOrder.Length; rank++)
            table[verifiedOrder[rank]] = rank;

        return table;
    }

    private static Dictionary<int, (int, int, int)> BuildPrecomposedTable()
    {
        var table = new Dictionary<int, (int, int, int)>();

        // Latin-1 Supplement. Base letter index: a=1 .. z=26.
        AddPair(table, 0x00C0, 0x00E0, 'a', Accent.Grave);
        AddPair(table, 0x00C1, 0x00E1, 'a', Accent.Acute);
        AddPair(table, 0x00C2, 0x00E2, 'a', Accent.Circumflex);
        AddPair(table, 0x00C3, 0x00E3, 'a', Accent.Tilde);
        AddPair(table, 0x00C4, 0x00E4, 'a', Accent.Diaeresis);
        AddPair(table, 0x00C5, 0x00E5, 'a', Accent.RingAbove);
        AddPair(table, 0x00C7, 0x00E7, 'c', Accent.Cedilla);
        AddPair(table, 0x00C8, 0x00E8, 'e', Accent.Grave);
        AddPair(table, 0x00C9, 0x00E9, 'e', Accent.Acute);
        AddPair(table, 0x00CA, 0x00EA, 'e', Accent.Circumflex);
        AddPair(table, 0x00CB, 0x00EB, 'e', Accent.Diaeresis);
        AddPair(table, 0x00CC, 0x00EC, 'i', Accent.Grave);
        AddPair(table, 0x00CD, 0x00ED, 'i', Accent.Acute);
        AddPair(table, 0x00CE, 0x00EE, 'i', Accent.Circumflex);
        AddPair(table, 0x00CF, 0x00EF, 'i', Accent.Diaeresis);
        // U+00D1/U+00F1 (Ñ/ñ) resolves here as an ordinary accented 'n' (secondary
        // difference only) for every language EXCEPT Spanish: LocaleCollator checks
        // for Spanish tailoring (a distinct primary letter) BEFORE ever consulting
        // this table, so this entry is reached only when that check did not apply.
        AddPair(table, 0x00D1, 0x00F1, 'n', Accent.Tilde);
        AddPair(table, 0x00D2, 0x00F2, 'o', Accent.Grave);
        AddPair(table, 0x00D3, 0x00F3, 'o', Accent.Acute);
        AddPair(table, 0x00D4, 0x00F4, 'o', Accent.Circumflex);
        AddPair(table, 0x00D5, 0x00F5, 'o', Accent.Tilde);
        AddPair(table, 0x00D6, 0x00F6, 'o', Accent.Diaeresis);
        // U+00D8/U+00F8 (Ø/ø) deliberately excluded — see type remarks.
        AddPair(table, 0x00D9, 0x00F9, 'u', Accent.Grave);
        AddPair(table, 0x00DA, 0x00FA, 'u', Accent.Acute);
        AddPair(table, 0x00DB, 0x00FB, 'u', Accent.Circumflex);
        AddPair(table, 0x00DC, 0x00FC, 'u', Accent.Diaeresis);
        AddPair(table, 0x00DD, 0x00FD, 'y', Accent.Acute);
        table[0x00FF] = ('y' - 'a' + 1, Accent.Diaeresis, Lower); // ÿ has no uppercase pair in Latin-1.

        // Latin Extended-A: commonly used single-combining-mark accented letters.
        AddPair(table, 0x0100, 0x0101, 'a', Accent.Macron);
        AddPair(table, 0x0102, 0x0103, 'a', Accent.Breve);
        AddPair(table, 0x0104, 0x0105, 'a', Accent.Ogonek);
        AddPair(table, 0x0106, 0x0107, 'c', Accent.Acute);
        AddPair(table, 0x0108, 0x0109, 'c', Accent.Circumflex);
        AddPair(table, 0x010A, 0x010B, 'c', Accent.DotAbove);
        AddPair(table, 0x010C, 0x010D, 'c', Accent.Caron);
        AddPair(table, 0x010E, 0x010F, 'd', Accent.Caron);
        AddPair(table, 0x0112, 0x0113, 'e', Accent.Macron);
        AddPair(table, 0x0114, 0x0115, 'e', Accent.Breve);
        AddPair(table, 0x0116, 0x0117, 'e', Accent.DotAbove);
        AddPair(table, 0x0118, 0x0119, 'e', Accent.Ogonek);
        AddPair(table, 0x011A, 0x011B, 'e', Accent.Caron);
        AddPair(table, 0x011C, 0x011D, 'g', Accent.Circumflex);
        AddPair(table, 0x011E, 0x011F, 'g', Accent.Breve);
        AddPair(table, 0x0120, 0x0121, 'g', Accent.DotAbove);
        AddPair(table, 0x0122, 0x0123, 'g', Accent.Cedilla);
        AddPair(table, 0x0124, 0x0125, 'h', Accent.Circumflex);
        AddPair(table, 0x0128, 0x0129, 'i', Accent.Tilde);
        AddPair(table, 0x012A, 0x012B, 'i', Accent.Macron);
        AddPair(table, 0x012E, 0x012F, 'i', Accent.Ogonek);
        AddPair(table, 0x0134, 0x0135, 'j', Accent.Circumflex);
        AddPair(table, 0x0136, 0x0137, 'k', Accent.Cedilla);
        AddPair(table, 0x0139, 0x013A, 'l', Accent.Acute);
        AddPair(table, 0x013B, 0x013C, 'l', Accent.Cedilla);
        AddPair(table, 0x013D, 0x013E, 'l', Accent.Caron);
        // U+0141/U+0142 (Ł/ł) deliberately excluded — see type remarks.
        AddPair(table, 0x0143, 0x0144, 'n', Accent.Acute);
        AddPair(table, 0x0145, 0x0146, 'n', Accent.Cedilla);
        AddPair(table, 0x0147, 0x0148, 'n', Accent.Caron);
        AddPair(table, 0x014C, 0x014D, 'o', Accent.Macron);
        AddPair(table, 0x0150, 0x0151, 'o', Accent.DoubleAcute);
        AddPair(table, 0x0154, 0x0155, 'r', Accent.Acute);
        AddPair(table, 0x0156, 0x0157, 'r', Accent.Cedilla);
        AddPair(table, 0x0158, 0x0159, 'r', Accent.Caron);
        AddPair(table, 0x015A, 0x015B, 's', Accent.Acute);
        AddPair(table, 0x015C, 0x015D, 's', Accent.Circumflex);
        AddPair(table, 0x015E, 0x015F, 's', Accent.Cedilla);
        AddPair(table, 0x0160, 0x0161, 's', Accent.Caron);
        AddPair(table, 0x0162, 0x0163, 't', Accent.Cedilla);
        AddPair(table, 0x0164, 0x0165, 't', Accent.Caron);
        AddPair(table, 0x0168, 0x0169, 'u', Accent.Tilde);
        AddPair(table, 0x016A, 0x016B, 'u', Accent.Macron);
        AddPair(table, 0x016C, 0x016D, 'u', Accent.Breve);
        AddPair(table, 0x016E, 0x016F, 'u', Accent.RingAbove);
        AddPair(table, 0x0170, 0x0171, 'u', Accent.DoubleAcute);
        AddPair(table, 0x0172, 0x0173, 'u', Accent.Ogonek);
        AddPair(table, 0x0174, 0x0175, 'w', Accent.Circumflex);
        AddPair(table, 0x0176, 0x0177, 'y', Accent.Circumflex);
        table[0x0178] = ('y' - 'a' + 1, Accent.Diaeresis, Upper); // Ÿ (uppercase only in Latin Extended-A).
        AddPair(table, 0x0179, 0x017A, 'z', Accent.Acute);
        AddPair(table, 0x017B, 0x017C, 'z', Accent.DotAbove);
        AddPair(table, 0x017D, 0x017E, 'z', Accent.Caron);

        return table;
    }

    private static void AddPair(
        Dictionary<int, (int, int, int)> table,
        int upperCodepoint,
        int lowerCodepoint,
        char baseLetter,
        int accent)
    {
        var index = baseLetter - 'a' + 1;
        table[upperCodepoint] = (index, accent, Upper);
        table[lowerCodepoint] = (index, accent, Lower);
    }
}
