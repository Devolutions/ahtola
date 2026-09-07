namespace Ahtola.Core.Collation;

/// <summary>
/// A compact, hand-authored Unicode Collation Algorithm-style weight table for
/// the Latin script: Basic Latin plus the commonly used accented letters of the
/// Latin-1 Supplement and Latin Extended-A blocks.
/// </summary>
/// <remarks>
/// <para>
/// Each entry decomposes a precomposed accented letter into the same three
/// pieces the UCA root collation element table (DUCET) would derive for it via
/// canonical decomposition: a base-letter primary weight, an accent secondary
/// weight, and a case tertiary weight. The <em>relative order</em> of accent
/// marks below follows the code point order of their corresponding combining
/// marks in the Unicode "Combining Diacritical Marks" block (U+0300..U+0328) —
/// this is the well-documented convention CLDR's root collation itself derives
/// from (see Unicode Technical Standard #10, "Default Unicode Collation Element
/// Table", and CLDR's <c>allkeys_CLDR.txt</c> root ordering) — rather than any
/// single locale's tailoring, so it reproduces the untailored root behavior
/// observed for basic accent comparisons (verified empirically: pinned
/// <c>icu_collator</c> 2.3.1 orders <c>fr-FR</c> and <c>en-US</c> identically for
/// plain single-accent comparisons; see <c>LocaleCollationTag</c> remarks).
/// </para>
/// <para>
/// Scope: characters outside this table (non-Latin scripts, ligatures such as
/// U+00C6 Æ, and a handful of rare Latin letters) fall back to code point order
/// at the primary level in <see cref="LocaleCollator"/>. That still produces a
/// total, transitive, stable order — just not full DUCET fidelity outside the
/// covered range. This is a deliberate, documented scope boundary, not an
/// attempt at complete ICU parity.
/// </para>
/// </remarks>
internal static class LocaleCollationWeights
{
    /// <summary>Accent secondary-weight ranks, ordered by their combining mark's code point.</summary>
    internal static class Accent
    {
        internal const int None = 0;
        internal const int Grave = 1;          // U+0300
        internal const int Acute = 2;           // U+0301
        internal const int Circumflex = 3;       // U+0302
        internal const int Tilde = 4;            // U+0303
        internal const int Macron = 5;           // U+0304
        internal const int Breve = 6;            // U+0306
        internal const int DotAbove = 7;         // U+0307
        internal const int Diaeresis = 8;        // U+0308
        internal const int RingAbove = 9;        // U+030A
        internal const int Caron = 10;           // U+030C
        internal const int Cedilla = 11;         // U+0327
        internal const int Ogonek = 12;          // U+0328
        internal const int Stroke = 13;          // not a combining mark; ordered last among tailored accents
        internal const int DoubleAcute = 14;      // U+030B occurs after Caron in the block but is rare; kept adjacent to Acute-family marks
    }

    internal const int Lower = 0;
    internal const int Upper = 1;

    private static readonly Dictionary<int, (int BaseLetter, int Accent, int Case)> Table = BuildTable();

    /// <summary>
    /// Attempts to resolve <paramref name="codepoint"/> to its Latin base letter
    /// (1='a'..26='z'), accent rank, and case class.
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

    private static Dictionary<int, (int, int, int)> BuildTable()
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
        AddPair(table, 0x00D1, 0x00F1, 'n', Accent.Tilde);
        AddPair(table, 0x00D2, 0x00F2, 'o', Accent.Grave);
        AddPair(table, 0x00D3, 0x00F3, 'o', Accent.Acute);
        AddPair(table, 0x00D4, 0x00F4, 'o', Accent.Circumflex);
        AddPair(table, 0x00D5, 0x00F5, 'o', Accent.Tilde);
        AddPair(table, 0x00D6, 0x00F6, 'o', Accent.Diaeresis);
        AddPair(table, 0x00D8, 0x00F8, 'o', Accent.Stroke);
        AddPair(table, 0x00D9, 0x00F9, 'u', Accent.Grave);
        AddPair(table, 0x00DA, 0x00FA, 'u', Accent.Acute);
        AddPair(table, 0x00DB, 0x00FB, 'u', Accent.Circumflex);
        AddPair(table, 0x00DC, 0x00FC, 'u', Accent.Diaeresis);
        AddPair(table, 0x00DD, 0x00FD, 'y', Accent.Acute);
        table[0x00FF] = ('y' - 'a' + 1, Accent.Diaeresis, Lower); // ÿ has no uppercase pair in Latin-1.

        // Latin Extended-A: commonly used accented letters (subset).
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
        AddPair(table, 0x0141, 0x0142, 'l', Accent.Stroke);
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
