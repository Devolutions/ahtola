using System.Globalization;
using System.Text;

namespace Ahtola.Core;

/// <summary>
/// Renders a REAL as text the way SQLite lays a floating point value out.
/// </summary>
/// <remarks>
/// <para>
/// SQLite never renders a floating point value as a bare integer and never uses the platform's
/// exponent syntax, so <c>CAST(0.0 AS TEXT)</c> is "0.0" rather than "0" and 1e20 is "1.0e+20"
/// rather than .NET's "1E+20". Fixed notation is used when the leading significant digit sits
/// between 1e-4 and 1e16; everything outside that window uses <c>d.ddde&#177;NN</c>.
/// </para>
/// <para>
/// The significant digits come from .NET's shortest round-trip conversion. SQLite instead derives
/// them from <c>sqlite3FpDecode</c>, which scales the magnitude with Dekker double-double
/// arithmetic and is deliberately cheap rather than correctly rounded. For values that need 16 or
/// more significant digits SQLite therefore sometimes emits a redundant seventeenth digit, and that
/// digit is sometimes wrong: it prints 3.1079236656039855e-160 where the correctly rounded value is
/// 3.1079236656039854e-160. Reproducing that would mean reproducing its rounding error, so the
/// managed engine emits the shortest text that reads back as the same double instead. Every value
/// round-trips; only the digit count can differ.
/// </para>
/// </remarks>
internal static class SqliteRealText
{
    /// <summary>Converts a floating point value to the text SQLite would produce for it.</summary>
    internal static string Format(double value)
    {
        if (double.IsNaN(value))
            return string.Empty;
        if (double.IsPositiveInfinity(value))
            return "Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";

        // SQLite takes its zero branch before it inspects the sign, so -0.0 renders as "0.0".
        if (value == 0d)
            return "0.0";

        Decompose(value, out var negative, out var digits, out var exponent);

        var builder = new StringBuilder();
        if (negative)
            builder.Append('-');

        if (exponent is >= -4 and <= 16)
        {
            var pointPosition = exponent + 1;
            if (pointPosition <= 0)
            {
                builder.Append("0.");
                builder.Append('0', -pointPosition);
                builder.Append(digits);
            }
            else if (pointPosition >= digits.Length)
            {
                builder.Append(digits);
                builder.Append('0', pointPosition - digits.Length);
                builder.Append(".0");
            }
            else
            {
                builder.Append(digits, 0, pointPosition);
                builder.Append('.');
                builder.Append(digits, pointPosition, digits.Length - pointPosition);
            }

            return builder.ToString();
        }

        builder.Append(digits[0]);
        builder.Append('.');
        builder.Append(digits.Length > 1 ? digits[1..] : "0");
        builder.Append('e');
        builder.Append(exponent < 0 ? '-' : '+');
        var magnitude = System.Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        if (magnitude.Length < 2)
            builder.Append('0');
        builder.Append(magnitude);
        return builder.ToString();
    }

    /// <summary>
    /// Splits a value into its sign, its significant digits, and the decimal exponent of the
    /// leading digit, so 0.125 decomposes to "125" with exponent -1.
    /// </summary>
    private static void Decompose(double value, out bool negative, out string digits, out int exponent)
    {
        negative = double.IsNegative(value);

        var text = System.Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var scale = 0;
        var exponentMarker = text.IndexOf('E');
        if (exponentMarker >= 0)
        {
            scale = int.Parse(text[(exponentMarker + 1)..], CultureInfo.InvariantCulture);
            text = text[..exponentMarker];
        }

        var point = text.IndexOf('.');
        if (point >= 0)
        {
            text = string.Concat(text[..point], text[(point + 1)..]);
        }
        else
        {
            point = text.Length;
        }

        var first = 0;
        while (first < text.Length - 1 && text[first] == '0')
            first++;

        var last = text.Length;
        while (last > first + 1 && text[last - 1] == '0')
            last--;

        digits = text[first..last];
        exponent = point - first - 1 + scale;
    }
}
