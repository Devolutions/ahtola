using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private static readonly char[] DefaultTrimCharacters = [' '];

    /// <summary>
    /// Applies SQLite's rule that a scalar function returns NULL as soon as any
    /// argument that participates in the result is NULL.
    /// </summary>
    private static bool HasNullArgument(IReadOnlyList<SqlValue> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Kind == SqlValueKind.Null)
                return true;
        }

        return false;
    }

    private static void RequireArgumentCount(
        string functionName,
        IReadOnlyList<SqlValue> arguments,
        int minimum,
        int maximum)
    {
        if (arguments.Count < minimum || arguments.Count > maximum)
            throw new EmbeddedSqlException($"wrong number of arguments to function {functionName}()");
    }

    /// <summary>
    /// SQLite's substr() counts in bytes for blobs and in characters for text.
    /// Returning the operand as a blob preserves that distinction for callers.
    /// </summary>
    private static SqlValue EvaluateSubstring(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("substr", arguments, 2, 3);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var isBlob = arguments[0].Kind == SqlValueKind.Blob;
        var text = isBlob ? null : ToSqlText(arguments[0]);
        var length = isBlob ? arguments[0].AsBlob().Length : text!.EnumerateRunes().LongCount();
        var start = ToSqliteInteger(arguments[1]);
        var hasExplicitLength = arguments.Count == 3;
        var requested = hasExplicitLength ? ToSqliteInteger(arguments[2]) : length;

        // SQLite indexes from 1; a negative start counts back from the end, and a
        // negative length selects the characters *preceding* the start position.
        if (start < 0)
        {
            start = length + start + 1;
            if (start < 1 && !hasExplicitLength)
                start = 1;
        }
        else if (start == 0)
        {
            start = 1;
            requested = hasExplicitLength ? requested - 1 : requested;
        }

        if (requested < 0)
        {
            start += requested;
            requested = -requested;
        }

        var beginIndex = start - 1;
        var endIndex = beginIndex + requested;
        if (beginIndex < 0)
            beginIndex = 0;
        if (endIndex > length)
            endIndex = length;

        var take = endIndex - beginIndex;
        if (take <= 0 || beginIndex >= length)
            return isBlob ? SqlValue.Blob([]) : SqlValue.Text(string.Empty);

        if (isBlob)
            return SqlValue.Blob(arguments[0].AsBlob().Span.Slice((int)beginIndex, (int)take).ToArray());

        var builder = new StringBuilder((int)take);
        var runeIndex = 0L;
        foreach (var rune in text!.EnumerateRunes())
        {
            if (runeIndex >= endIndex)
                break;
            if (runeIndex >= beginIndex)
                builder.Append(rune.ToString());
            runeIndex++;
        }

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateReplace(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("replace", arguments, 3);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var source = ToSqlText(arguments[0]);
        var pattern = ToSqlText(arguments[1]);
        if (pattern.Length == 0)
            return SqlValue.Text(source);

        return SqlValue.Text(source.Replace(pattern, ToSqlText(arguments[2]), StringComparison.Ordinal));
    }

    private static SqlValue EvaluateStringReverse(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("string_reverse", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        // Turso reverses Rust chars, which are Unicode scalar values rather than UTF-8 bytes.
        // Rune traversal is the corresponding .NET operation and keeps supplementary characters intact.
        var source = ToSqlText(arguments[0]);
        var builder = new StringBuilder(source.Length);
        foreach (var rune in source.EnumerateRunes().Reverse())
            builder.Append(rune.ToString());

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateSoundex(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("soundex", arguments, 1);
        if (arguments[0].Kind != SqlValueKind.Text)
            return SqlValue.Text("?000");

        var source = arguments[0].AsText();
        if (source.Length == 0 || source.Any(character => !IsAsciiLetter(character)))
            return SqlValue.Text("?000");

        Span<char> result = stackalloc char[4];
        result[0] = char.ToUpperInvariant(source[0]);
        var resultLength = 1;
        var previousCode = GetSoundexCode(source[0]);
        foreach (var character in source.AsSpan(1))
        {
            if (resultLength == result.Length)
                break;

            var lowercase = char.ToLowerInvariant(character);
            if (lowercase is 'h' or 'w')
                continue;

            var code = GetSoundexCode(lowercase);
            if (code is not null && code != previousCode)
            {
                result[resultLength++] = code.Value;
                previousCode = code;
            }
            else if (code is null)
            {
                previousCode = null;
            }
        }

        while (resultLength < result.Length)
            result[resultLength++] = '0';
        return SqlValue.Text(new string(result));
    }

    private static bool IsAsciiLetter(char character)
        => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static char? GetSoundexCode(char character)
        => char.ToLowerInvariant(character) switch
        {
            'b' or 'f' or 'p' or 'v' => '1',
            'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
            'd' or 't' => '3',
            'l' => '4',
            'm' or 'n' => '5',
            'r' => '6',
            _ => null,
        };

    private static SqlValue EvaluateUnistr(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("unistr", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var source = ToSqlText(arguments[0]);
        var builder = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length;)
        {
            if (source[index] != '\\')
            {
                builder.Append(source[index++]);
                continue;
            }

            if (index + 1 >= source.Length)
                throw new EmbeddedSqlException("invalid Unicode escape");

            var escape = source[index + 1];
            if (escape == '\\')
            {
                builder.Append('\\');
                index += 2;
                continue;
            }

            var digits = escape switch
            {
                '+' => 6,
                'u' => 4,
                'U' => 8,
                _ when IsAsciiHexDigit(escape) => 4,
                _ => 0,
            };
            if (digits == 0
                || !TryParseUnicodeEscape(
                    source.AsSpan(index + (escape is '+' or 'u' or 'U' ? 2 : 1)),
                    digits,
                    out var codePoint)
                || !Rune.TryCreate(codePoint, out var rune))
            {
                throw new EmbeddedSqlException("invalid Unicode escape");
            }

            builder.Append(rune.ToString());
            index += 1 + (escape is '+' or 'u' or 'U' ? 1 : 0) + digits;
        }

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateUnistrQuote(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("unistr_quote", arguments, 1);
        if (arguments[0].Kind != SqlValueKind.Text)
            return EvaluateQuote(arguments);

        var source = arguments[0].AsText();
        var terminator = source.IndexOf('\0');
        var prefix = terminator >= 0 ? source[..terminator] : source;
        if (!prefix.Any(character => character is >= '\x01' and <= '\x1F'))
            return EvaluateQuote([SqlValue.Text(prefix)]);

        var builder = new StringBuilder(prefix.Length + "unistr('')".Length);
        builder.Append("unistr('");
        foreach (var character in prefix)
        {
            switch (character)
            {
                case >= '\x01' and <= '\x1F':
                    builder.Append("\\u00");
                    builder.Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\'':
                    builder.Append("''");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append("')");
        return SqlValue.Text(builder.ToString());
    }

    private static bool TryParseUnicodeEscape(ReadOnlySpan<char> source, int length, out int codePoint)
    {
        if (source.Length < length)
        {
            codePoint = 0;
            return false;
        }

        uint result = 0;
        for (var index = 0; index < length; index++)
        {
            var digit = source[index] switch
            {
                >= '0' and <= '9' => source[index] - '0',
                >= 'a' and <= 'f' => source[index] - 'a' + 10,
                >= 'A' and <= 'F' => source[index] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                codePoint = 0;
                return false;
            }

            result = (result << 4) | (uint)digit;
        }

        if (result > 0x10FFFF)
        {
            codePoint = 0;
            return false;
        }

        codePoint = (int)result;
        return true;
    }

    private static bool IsAsciiHexDigit(char character)
        => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static SqlValue EvaluateRepeat(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("repeat", arguments, 2);
        if (HasNullArgument(arguments))
            return SqlValue.Null;
        if (!TryGetTursoStringFunctionLength(arguments[1], out var count))
            return SqlValue.Null;
        if (count <= 0)
            return SqlValue.Text(string.Empty);

        var source = ToSqlText(arguments[0]);
        if (source.Length == 0)
            return SqlValue.Text(string.Empty);
        if (count > int.MaxValue / source.Length)
            throw new EmbeddedSqlException("string or blob too big");

        var builder = new StringBuilder((int)(source.Length * count));
        for (var index = 0L; index < count; index++)
            builder.Append(source);

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluatePad(
        IReadOnlyList<SqlValue> arguments,
        string functionName,
        bool padLeft)
    {
        RequireArgumentCount(functionName, arguments, 2, 3);
        if (HasNullArgument(arguments))
            return SqlValue.Null;
        if (!TryGetTursoStringFunctionLength(arguments[1], out var requestedLength))
            return SqlValue.Null;
        if (requestedLength <= 0)
            return SqlValue.Text(string.Empty);
        if (requestedLength > int.MaxValue)
            throw new EmbeddedSqlException("string or blob too big");

        var targetLength = (int)requestedLength;
        var source = ToSqlText(arguments[0]);
        var sourceRunes = source.EnumerateRunes().ToArray();
        if (sourceRunes.Length >= targetLength)
            return SqlValue.Text(string.Concat(sourceRunes.Take(targetLength)));

        var fill = arguments.Count == 3 ? ToSqlText(arguments[2]) : " ";
        var fillRunes = fill.EnumerateRunes().ToArray();
        if (fillRunes.Length == 0)
            return SqlValue.Text(source);

        var paddingLength = targetLength - sourceRunes.Length;
        var builder = new StringBuilder(GetPaddedTextCapacity(source, fillRunes, paddingLength));
        if (padLeft)
            AppendCyclicRunes(builder, fillRunes, paddingLength);
        builder.Append(source);
        if (!padLeft)
            AppendCyclicRunes(builder, fillRunes, paddingLength);

        return SqlValue.Text(builder.ToString());
    }

    private static bool TryGetTursoStringFunctionLength(SqlValue value, out long length)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                length = value.AsInteger();
                return true;
            case SqlValueKind.Real:
                length = ClampRealToInteger(value.AsReal());
                return true;
            case SqlValueKind.Text:
                if (long.TryParse(
                    value.AsText(),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out length))
                {
                    return true;
                }

                length = 0;
                return true;
            default:
                length = 0;
                return false;
        }
    }

    private static int GetPaddedTextCapacity(string source, IReadOnlyList<Rune> fill, int paddingLength)
    {
        var completeCycles = paddingLength / fill.Count;
        var remainder = paddingLength % fill.Count;
        var fillCodeUnits = fill.Sum(rune => rune.Utf16SequenceLength);
        var remainderCodeUnits = fill.Take(remainder).Sum(rune => rune.Utf16SequenceLength);
        var available = int.MaxValue - source.Length - remainderCodeUnits;
        if (available < 0 || completeCycles > available / fillCodeUnits)
            throw new EmbeddedSqlException("string or blob too big");

        return source.Length + (completeCycles * fillCodeUnits) + remainderCodeUnits;
    }

    private static void AppendCyclicRunes(StringBuilder builder, IReadOnlyList<Rune> runes, int count)
    {
        for (var index = 0; index < count; index++)
            builder.Append(runes[index % runes.Count].ToString());
    }

    private static SqlValue EvaluateTrim(IReadOnlyList<SqlValue> arguments, string functionName, bool trimStart, bool trimEnd)
    {
        RequireArgumentCount(functionName, arguments, 1, 2);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var source = ToSqlText(arguments[0]);
        var characters = arguments.Count == 2
            ? ToSqlText(arguments[1]).ToCharArray()
            : DefaultTrimCharacters;

        if (characters.Length == 0)
            return SqlValue.Text(source);

        var result = trimStart && trimEnd
            ? source.Trim(characters)
            : trimStart
                ? source.TrimStart(characters)
                : source.TrimEnd(characters);

        return SqlValue.Text(result);
    }

    /// <summary>
    /// Renders a value as an SQL literal, matching SQLite's quote() output for
    /// each storage class.
    /// </summary>
    private static SqlValue EvaluateQuote(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("quote", arguments, 1);
        var value = arguments[0];
        return value.Kind switch
        {
            SqlValueKind.Null => SqlValue.Text("NULL"),
            SqlValueKind.Integer => SqlValue.Text(value.AsInteger().ToString(CultureInfo.InvariantCulture)),
            SqlValueKind.Real => SqlValue.Text(FormatQuotedReal(value.AsReal())),
            SqlValueKind.Blob => SqlValue.Text($"X'{Convert.ToHexString(value.AsBlob().Span)}'"),
            _ => SqlValue.Text($"'{value.AsText().Replace("'", "''", StringComparison.Ordinal)}'"),
        };
    }

    private static string FormatQuotedReal(double value)
    {
        if (double.IsNaN(value))
            return "NULL";
        if (double.IsPositiveInfinity(value))
            return "9.0e+999";
        if (double.IsNegativeInfinity(value))
            return "-9.0e+999";

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static SqlValue EvaluateChar(IReadOnlyList<SqlValue> arguments)
    {
        var builder = new StringBuilder(arguments.Count);
        foreach (var argument in arguments)
        {
            // Turso only converts INTEGER arguments to code points. NULL remains a NUL character,
            // while REAL, TEXT, and BLOB arguments do not contribute a character.
            long codePoint;
            switch (argument.Kind)
            {
                case SqlValueKind.Integer:
                    codePoint = argument.AsInteger();
                    break;
                case SqlValueKind.Null:
                    codePoint = 0;
                    break;
                default:
                    continue;
            }

            // SQLite substitutes U+FFFD for values outside the Unicode range and
            // for surrogate code points, which cannot stand alone.
            if (codePoint is < 0 or > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                builder.Append('\uFFFD');
                continue;
            }

            builder.Append(char.ConvertFromUtf32((int)codePoint));
        }

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateUnicode(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("unicode", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var text = ToSqlText(arguments[0]);
        if (text.Length == 0)
            return SqlValue.Null;

        return SqlValue.Integer(char.ConvertToUtf32(text, 0));
    }

    private static SqlValue EvaluateUnhex(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("unhex", arguments, 1, 2);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        var text = ToSqlText(arguments[0]);
        var ignored = arguments.Count == 2 ? ToSqlText(arguments[1]) : string.Empty;
        if (ignored.Length == 0)
        {
            if ((text.Length & 1) != 0)
                return SqlValue.Null;

            var strict = new byte[text.Length / 2];
            for (var index = 0; index < strict.Length; index++)
            {
                var high = ParseHexDigit(text[index * 2]);
                var low = ParseHexDigit(text[(index * 2) + 1]);
                if (high < 0 || low < 0)
                    return SqlValue.Null;

                strict[index] = (byte)((high << 4) | low);
            }

            return SqlValue.Blob(strict);
        }

        // Separator characters are ignored anywhere in the stream, not only at the ends. A
        // character listed as a separator that is also a hex digit keeps its digit meaning.
        var bytes = new List<byte>(text.Length / 2);
        var position = 0;
        while (true)
        {
            while (position < text.Length && IsUnhexSeparator(text[position], ignored))
                position++;

            if (position >= text.Length)
                return SqlValue.Blob(bytes.ToArray());

            var high = ParseHexDigit(text[position]);
            if (high < 0)
                return SqlValue.Null;
            position++;

            if (position >= text.Length)
                return SqlValue.Null;

            var low = ParseHexDigit(text[position]);
            if (low < 0)
                return SqlValue.Null;
            position++;

            bytes.Add((byte)((high << 4) | low));
        }
    }

    private static bool IsUnhexSeparator(char character, string separators)
        => separators.IndexOf(character) >= 0 && ParseHexDigit(character) < 0;

    private static int ParseHexDigit(char character)
        => character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1,
        };

    private static SqlValue EvaluateZeroBlob(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("zeroblob", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var length = ToSqliteLength(arguments[0]);
        if (length < 0)
            length = 0;
        if (length > int.MaxValue)
            throw new EmbeddedSqlException("string or blob too big");

        return SqlValue.Blob(new byte[(int)length]);
    }

    private static SqlValue EvaluateRandomBlob(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("randomblob", arguments, 1);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var length = ToSqliteLength(arguments[0]);
        if (length < 1)
            length = 1;
        if (length > int.MaxValue)
            throw new EmbeddedSqlException("string or blob too big");

        var bytes = new byte[(int)length];
        RandomNumberGenerator.Fill(bytes);
        return SqlValue.Blob(bytes);
    }

    private static SqlValue EvaluateRandom(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("random", arguments, 0);
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return SqlValue.Integer(BitConverter.ToInt64(bytes));
    }

    private static SqlValue EvaluateConcat(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function concat()");

        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            // concat() ignores NULL arguments rather than propagating them.
            if (argument.Kind == SqlValueKind.Null)
                continue;

            builder.Append(ToSqlText(argument));
        }

        return SqlValue.Text(builder.ToString());
    }

    private static SqlValue EvaluateConcatWithSeparator(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count == 0)
            throw new EmbeddedSqlException("wrong number of arguments to function concat_ws()");
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var separator = ToSqlText(arguments[0]);
        var builder = new StringBuilder();
        var appended = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (arguments[index].Kind == SqlValueKind.Null)
                continue;

            if (appended)
                builder.Append(separator);

            builder.Append(ToSqlText(arguments[index]));
            appended = true;
        }

        return SqlValue.Text(builder.ToString());
    }

    /// <summary>
    /// Coerces a value to an integer, saturating instead of wrapping so that
    /// length-style arguments cannot overflow into a negative size.
    /// </summary>
    private static long ToSqliteLength(SqlValue value)
    {
        var numeric = ApplyNumericAffinity(value);
        return numeric.Kind switch
        {
            SqlValueKind.Integer => numeric.AsInteger(),
            SqlValueKind.Real => ClampRealToInteger(numeric.AsReal()),
            _ => 0,
        };
    }

    private static long ClampRealToInteger(double value)
    {
        if (double.IsNaN(value))
            return 0;

        var truncated = Math.Truncate(value);
        if (truncated >= long.MaxValue)
            return long.MaxValue;
        if (truncated <= long.MinValue)
            return long.MinValue;

        return (long)truncated;
    }
}
