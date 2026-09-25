using System.Text;

namespace Ahtola.Core.Search;

/// <summary>
/// Pinned Unicode character properties for the FTS tokenizers.
/// </summary>
/// <remarks>
/// <para>
/// Tokenization must not depend on the host. <c>string.Normalize</c> silently returns its input in
/// globalization-invariant mode (the usual Blazor WebAssembly setting), and
/// <c>ToLowerInvariant</c>/<c>Rune.IsLetterOrDigit</c> follow whichever Unicode version the runtime
/// or loaded ICU carries. Any of those would let the same document tokenize differently on a
/// desktop, a Linux server and a browser tab.
/// </para>
/// <para>
/// Every property here is read from <c>ManagedUnicodeData.g.cs</c>, generated for Unicode 16.0 —
/// the version of Rust 1.88, which the pinned Turso release builds Tantivy with — by
/// <c>scripts/unicode/Update-ManagedUnicodeData.ps1</c>. Unassigned code points have no properties.
/// </para>
/// </remarks>
internal static partial class ManagedUnicode
{
    private const byte AlphanumericFlag = 1;
    private const byte MarkFlag = 2;
    private const byte Unicode61TokenFlag = 4;

    private const int HangulSBase = 0xAC00;
    private const int HangulLBase = 0x1100;
    private const int HangulVBase = 0x1161;
    private const int HangulTBase = 0x11A7;
    private const int HangulTCount = 28;
    private const int HangulNCount = 588;
    private const int HangulSCount = 11172;

    /// <summary>Rust's <c>char::is_alphanumeric</c>: the Alphabetic property or a Nd/Nl/No number.</summary>
    public static bool IsAlphanumeric(int codePoint)
        => codePoint < 0x80
            ? (uint)((codePoint | 0x20) - 'a') <= 'z' - 'a' || (uint)(codePoint - '0') <= 9
            : (GetFlags(codePoint) & AlphanumericFlag) != 0;

    /// <summary>A combining mark: general category Mn, Mc or Me.</summary>
    public static bool IsMark(int codePoint)
        => codePoint >= 0x300 && (GetFlags(codePoint) & MarkFlag) != 0;

    /// <summary>The <c>unicode61</c> token class: a letter (L*), a decimal digit (Nd) or a letter number (Nl).</summary>
    public static bool IsUnicode61TokenChar(int codePoint)
        => codePoint < 0x80
            ? (uint)((codePoint | 0x20) - 'a') <= 'z' - 'a' || (uint)(codePoint - '0') <= 9
            : (GetFlags(codePoint) & Unicode61TokenFlag) != 0;

    /// <summary>Rust's ASCII whitespace: space, tab, line feed, form feed and carriage return.</summary>
    public static bool IsAsciiWhitespace(char value) => value is ' ' or '\t' or '\n' or '\f' or '\r';

    /// <summary>
    /// Appends Rust's <c>char::to_lowercase</c> of one code point: the full, context-free mapping
    /// Tantivy's <c>LowerCaser</c> applies. Only U+0130 maps to more than one code point.
    /// </summary>
    public static void AppendLowercase(StringBuilder builder, int codePoint)
    {
        if (codePoint < 0x80)
        {
            builder.Append((char)((uint)(codePoint - 'A') <= 'Z' - 'A' ? codePoint | 0x20 : codePoint));
            return;
        }

        if (codePoint == 0x130)
        {
            builder.Append('i').Append('̇');
            return;
        }

        var index = LowercaseKeys.BinarySearch(codePoint);
        AppendCodePoint(builder, index >= 0 ? LowercaseValues[index] : codePoint);
    }

    /// <summary>Lowercases a string one code point at a time; an unpaired surrogate is kept as is.</summary>
    public static string ToLower(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var index = 0;
        while (index < value.Length && value[index] < 0x80 && (uint)(value[index] - 'A') > 'Z' - 'A')
            index++;

        if (index == value.Length)
            return value;

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, index);
        AppendLowercase(builder, value.AsSpan(index));
        return builder.ToString();
    }

    /// <summary>Lowercases a span one code point at a time into <paramref name="builder"/>.</summary>
    public static void AppendLowercase(StringBuilder builder, ReadOnlySpan<char> value)
    {
        for (var offset = 0; offset < value.Length;)
        {
            var codePoint = ReadCodePoint(value, offset, out var consumed);
            if (codePoint < 0)
                builder.Append(value[offset]);
            else
                AppendLowercase(builder, codePoint);

            offset += consumed;
        }
    }

    /// <summary>
    /// Appends the <c>unicode61</c> fold of one code point: its full canonical decomposition with
    /// every combining mark removed, then lowercased.
    /// </summary>
    public static void AppendFolded(StringBuilder builder, int codePoint)
    {
        if (codePoint < 0x80)
        {
            AppendLowercase(builder, codePoint);
            return;
        }

        if (codePoint is >= HangulSBase and < HangulSBase + HangulSCount)
        {
            // Jamo are letters, never marks, so a syllable folds to its full decomposition.
            var index = codePoint - HangulSBase;
            builder.Append((char)(HangulLBase + index / HangulNCount));
            builder.Append((char)(HangulVBase + index % HangulNCount / HangulTCount));
            if (index % HangulTCount != 0)
                builder.Append((char)(HangulTBase + index % HangulTCount));
            return;
        }

        var decomposition = DecompositionKeys.BinarySearch(codePoint);
        if (decomposition < 0)
        {
            if (!IsMark(codePoint))
                AppendLowercase(builder, codePoint);
            return;
        }

        var starts = DecompositionStarts;
        var data = DecompositionData;
        for (var position = starts[decomposition]; position < starts[decomposition + 1]; position++)
        {
            if (!IsMark(data[position]))
                AppendLowercase(builder, data[position]);
        }
    }

    /// <summary>Folds a span one code point at a time; an unpaired surrogate is kept as is.</summary>
    public static string Fold(ReadOnlySpan<char> value)
    {
        var builder = new StringBuilder(value.Length);
        for (var offset = 0; offset < value.Length;)
        {
            var codePoint = ReadCodePoint(value, offset, out var consumed);
            if (codePoint < 0)
                builder.Append(value[offset]);
            else
                AppendFolded(builder, codePoint);

            offset += consumed;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decodes the code point at <paramref name="offset"/>. Returns -1 with <paramref name="consumed"/>
    /// = 1 for an unpaired surrogate.
    /// </summary>
    public static int ReadCodePoint(ReadOnlySpan<char> value, int offset, out int consumed)
    {
        var first = value[offset];
        if (!char.IsSurrogate(first))
        {
            consumed = 1;
            return first;
        }

        if (char.IsHighSurrogate(first) && offset + 1 < value.Length && char.IsLowSurrogate(value[offset + 1]))
        {
            consumed = 2;
            return char.ConvertToUtf32(first, value[offset + 1]);
        }

        consumed = 1;
        return -1;
    }

    private static void AppendCodePoint(StringBuilder builder, int codePoint)
    {
        if (codePoint < 0x10000)
        {
            builder.Append((char)codePoint);
            return;
        }

        codePoint -= 0x10000;
        builder.Append((char)(0xD800 + (codePoint >> 10)));
        builder.Append((char)(0xDC00 + (codePoint & 0x3FF)));
    }

    private static byte GetFlags(int codePoint)
    {
        if ((uint)codePoint > 0x10FFFF)
            return 0;

        var starts = PropertyRangeStarts;
        var index = starts.BinarySearch(codePoint);
        if (index < 0)
            index = ~index - 1;

        return PropertyRangeFlags[index];
    }
}
