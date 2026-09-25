#:property TargetFramework=net10.0
#:property InvariantGlobalization=true
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false

// Generates src/Ahtola.Core/Search/ManagedUnicodeData.g.cs from Export-UnicodeProperties.mjs output.
//
// The pinned repertoire is the one .NET 10's CoreLib assigns (Unicode 16.0), which is also the
// Unicode version of Rust 1.88 — the toolchain the pinned Turso release builds Tantivy with. This
// program must run on .NET 10 in globalization-invariant mode so every category and simple case
// mapping below comes from CoreLib's own tables rather than from a host ICU.
//
// Usage: dotnet run GenerateManagedUnicodeData.cs -- <export.json> <output.g.cs>
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run GenerateManagedUnicodeData.cs -- <export.json> <output.g.cs>");
    return 2;
}

if (Environment.Version.Major != 10)
    return Fail($"must run on .NET 10 (CoreLib Unicode 16.0); running on {Environment.Version}");

if (!AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) || !invariant)
    return Fail("must run in globalization-invariant mode");

// U+1C89 was assigned in Unicode 16.0; U+A7CE was assigned in Unicode 17.0.
if (CharUnicodeInfo.GetUnicodeCategory(0x1C89) == UnicodeCategory.OtherNotAssigned
    || CharUnicodeInfo.GetUnicodeCategory(0xA7CE) != UnicodeCategory.OtherNotAssigned)
{
    return Fail("CoreLib does not report the Unicode 16.0 repertoire");
}

using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = document.RootElement;
var sourceUnicode = root.GetProperty("unicode").GetString();
var sourceIcu = root.GetProperty("icu").GetString();

var alphabetic = new bool[0x110000];
foreach (var range in root.GetProperty("alphabetic").EnumerateArray())
{
    var start = range[0].GetInt32();
    var end = range[1].GetInt32();
    for (var cp = start; cp <= end; cp++)
        alphabetic[cp] = true;
}

static bool IsAssigned(int cp) => CharUnicodeInfo.GetUnicodeCategory(cp) != UnicodeCategory.OtherNotAssigned;

static bool IsSurrogate(int cp) => cp is >= 0xD800 and <= 0xDFFF;

const byte Alphanumeric = 1;
const byte Mark = 2;
const byte Unicode61Token = 4;

var flags = new byte[0x110000];
for (var cp = 0; cp <= 0x10FFFF; cp++)
{
    if (IsSurrogate(cp) || !IsAssigned(cp))
        continue;

    var category = CharUnicodeInfo.GetUnicodeCategory(cp);
    byte value = 0;
    if (alphabetic[cp]
        || category is UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber)
    {
        value |= Alphanumeric;
    }

    if (category is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.EnclosingMark)
    {
        value |= Mark;
    }

    if (category is UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter
        or UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber)
    {
        value |= Unicode61Token;
    }

    flags[cp] = value;
}

var rangeStarts = new List<int>();
var rangeFlags = new List<byte>();
for (var cp = 0; cp <= 0x10FFFF; cp++)
{
    if (rangeFlags.Count == 0 || rangeFlags[^1] != flags[cp])
    {
        rangeStarts.Add(cp);
        rangeFlags.Add(flags[cp]);
    }
}

// Full per-character lowercase (Rust char::to_lowercase). The only unconditional multi-character
// lowercase mapping in SpecialCasing.txt is U+0130; everything else must agree with CoreLib's
// simple mapping, which is exactly what this cross-check proves.
var lowerKeys = new List<int>();
var lowerValues = new List<int>();
foreach (var property in root.GetProperty("lowercase").EnumerateObject().OrderBy(static p => int.Parse(p.Name, CultureInfo.InvariantCulture)))
{
    var cp = int.Parse(property.Name, CultureInfo.InvariantCulture);
    if (!IsAssigned(cp))
        continue;

    var mapped = property.Value.EnumerateArray().Select(static v => v.GetInt32()).ToArray();
    if (mapped.Length != 1)
    {
        if (cp == 0x130 && mapped is [0x69, 0x307])
            continue;

        return Fail($"unexpected multi-character lowercase mapping for U+{cp:X4}");
    }

    if (!IsAssigned(mapped[0]))
        return Fail($"U+{cp:X4} lowercases to an unassigned code point");

    lowerKeys.Add(cp);
    lowerValues.Add(mapped[0]);
}

var coreLibLowerDifferences = 0;
for (var cp = 0; cp <= 0x10FFFF; cp++)
{
    if (IsSurrogate(cp) || !IsAssigned(cp) || cp == 0x130)
        continue;

    var coreLib = char.ConvertToUtf32(char.ConvertFromUtf32(cp).ToLowerInvariant(), 0);
    var index = lowerKeys.BinarySearch(cp);
    var exported = index >= 0 ? lowerValues[index] : cp;
    if (coreLib != exported)
    {
        coreLibLowerDifferences++;
        Console.Error.WriteLine($"lowercase disagreement U+{cp:X4}: ICU U+{exported:X4}, CoreLib U+{coreLib:X4}");
    }
}

if (coreLibLowerDifferences != 0)
    return Fail($"{coreLibLowerDifferences} lowercase mappings disagree with CoreLib");

// Canonical decomposition. Hangul syllables are algorithmic and verified rather than tabulated.
const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7, TCount = 28, NCount = 588, SCount = 11172;
var decompositionKeys = new List<int>();
var decompositionStarts = new List<int>();
var decompositionData = new List<int>();
foreach (var property in root.GetProperty("decompositions").EnumerateObject().OrderBy(static p => int.Parse(p.Name, CultureInfo.InvariantCulture)))
{
    var cp = int.Parse(property.Name, CultureInfo.InvariantCulture);
    if (!IsAssigned(cp))
        continue;

    var mapped = property.Value.EnumerateArray().Select(static v => v.GetInt32()).ToArray();
    if (cp is >= SBase and < SBase + SCount)
    {
        var index = cp - SBase;
        int[] expected = index % TCount == 0
            ? [LBase + index / NCount, VBase + index % NCount / TCount]
            : [LBase + index / NCount, VBase + index % NCount / TCount, TBase + index % TCount];
        if (!mapped.SequenceEqual(expected))
            return Fail($"Hangul syllable U+{cp:X4} does not decompose algorithmically");

        continue;
    }

    if (mapped.Any(static value => !IsAssigned(value)))
        return Fail($"U+{cp:X4} decomposes to an unassigned code point");

    decompositionKeys.Add(cp);
    decompositionStarts.Add(decompositionData.Count);
    decompositionData.AddRange(mapped);
}

decompositionStarts.Add(decompositionData.Count);

var body = new StringBuilder();
AppendIntArray(body, "PropertyRangeStarts", rangeStarts);
AppendByteArray(body, "PropertyRangeFlags", rangeFlags);
AppendIntArray(body, "LowercaseKeys", lowerKeys);
AppendIntArray(body, "LowercaseValues", lowerValues);
AppendIntArray(body, "DecompositionKeys", decompositionKeys);
AppendIntArray(body, "DecompositionStarts", decompositionStarts);
AppendIntArray(body, "DecompositionData", decompositionData);

// Must match ManagedUnicode.ComputeTableFingerprint: each table's little-endian values in order.
var fingerprintBytes = new List<byte>();
foreach (var table in new IReadOnlyList<int>[] { rangeStarts, rangeFlags.Select(static v => (int)v).ToList(), lowerKeys, lowerValues, decompositionKeys, decompositionStarts, decompositionData })
{
    fingerprintBytes.AddRange(BitConverter.GetBytes(table.Count));
    foreach (var value in table)
        fingerprintBytes.AddRange(BitConverter.GetBytes(value));
}

var fingerprint = Convert.ToHexString(SHA256.HashData(fingerprintBytes.ToArray())).ToLowerInvariant();

var output = new StringBuilder();
output.Append("// <auto-generated/>\r\n");
output.Append("// Generated by scripts/unicode/Update-ManagedUnicodeData.ps1. Do not edit by hand.\r\n");
output.Append(CultureInfo.InvariantCulture, $"// Repertoire: Unicode 16.0 (.NET 10 CoreLib). Alphabetic and canonical decompositions exported from ICU {sourceIcu} (Unicode {sourceUnicode}).\r\n");
output.Append("#nullable enable\r\n\r\n");
output.Append("namespace Ahtola.Core.Search;\r\n\r\n");
output.Append("internal static partial class ManagedUnicode\r\n{\r\n");
output.Append("    /// <summary>The Unicode version every tokenizer property is pinned to.</summary>\r\n");
output.Append("    public const string UnicodeVersion = \"16.0\";\r\n\r\n");
output.Append("    /// <summary>SHA-256 over the table values below; a test proves the tables were not hand edited.</summary>\r\n");
output.Append(CultureInfo.InvariantCulture, $"    internal const string TableFingerprint = \"{fingerprint}\";\r\n\r\n");
output.Append(body);
output.Append("}\r\n");

File.WriteAllText(args[1], output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine(
    $"{rangeStarts.Count} property ranges, {lowerKeys.Count} lowercase mappings, " +
    $"{decompositionKeys.Count} decompositions ({decompositionData.Count} code points); fingerprint {fingerprint}");
return 0;

static void AppendIntArray(StringBuilder builder, string name, IReadOnlyList<int> values)
{
    builder.Append(CultureInfo.InvariantCulture, $"    internal static ReadOnlySpan<int> {name} => new int[]\r\n    {{\r\n");
    AppendValues(builder, values.Select(static value => $"0x{value:X}"));
    builder.Append("    };\r\n\r\n");
}

static void AppendByteArray(StringBuilder builder, string name, IReadOnlyList<byte> values)
{
    builder.Append(CultureInfo.InvariantCulture, $"    internal static ReadOnlySpan<byte> {name} => new byte[]\r\n    {{\r\n");
    AppendValues(builder, values.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
    builder.Append("    };\r\n\r\n");
}

static void AppendValues(StringBuilder builder, IEnumerable<string> values)
{
    var line = new StringBuilder("       ");
    foreach (var value in values)
    {
        if (line.Length + value.Length + 2 > 110)
        {
            builder.Append(line.ToString().TrimEnd()).Append("\r\n");
            line.Clear().Append("       ");
        }

        line.Append(' ').Append(value).Append(',');
    }

    if (line.Length > 7)
        builder.Append(line.ToString().TrimEnd()).Append("\r\n");
}

static int Fail(string message)
{
    Console.Error.WriteLine($"GenerateManagedUnicodeData: {message}");
    return 1;
}
