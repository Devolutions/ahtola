using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core;
using Ahtola.Core.Search;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// FTS tokenization is pinned to generated Unicode 16.0 tables rather than the host's ICU, so it is
/// identical on every OS, runtime and in globalization-invariant browser builds, and follows the
/// Rust <c>char</c> semantics Tantivy's analyzers are defined over.
/// </summary>
public sealed class ManagedFtsUnicodePinningTests
{
    private static readonly ManagedFtsTokenizerOptions DefaultTokenizer = new(ManagedFtsTokenizerKind.Default);
    private static readonly ManagedFtsTokenizerOptions SimpleTokenizer = new(ManagedFtsTokenizerKind.Simple);
    private static readonly ManagedFtsTokenizerOptions WhitespaceTokenizer = new(ManagedFtsTokenizerKind.Whitespace);
    private static readonly ManagedFtsTokenizerOptions Unicode61Tokenizer = new(ManagedFtsTokenizerKind.Unicode61);

    [Test]
    public void GeneratedTablesMatchTheirFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4];

        void Append(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            hash.AppendData(buffer);
        }

        void AppendTable(ReadOnlySpan<int> values)
        {
            Append(values.Length);
            foreach (var value in values)
                Append(value);
        }

        AppendTable(ManagedUnicode.PropertyRangeStarts);
        Append(ManagedUnicode.PropertyRangeFlags.Length);
        foreach (var value in ManagedUnicode.PropertyRangeFlags)
            Append(value);
        AppendTable(ManagedUnicode.LowercaseKeys);
        AppendTable(ManagedUnicode.LowercaseValues);
        AppendTable(ManagedUnicode.DecompositionKeys);
        AppendTable(ManagedUnicode.DecompositionStarts);
        AppendTable(ManagedUnicode.DecompositionData);

        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
            .Should().Be(ManagedUnicode.TableFingerprint, "regenerate with scripts/unicode/Update-ManagedUnicodeData.ps1 instead of editing the tables");
        ManagedUnicode.UnicodeVersion.Should().Be("16.0");
    }

    [Test]
    public void TableShapesAreSortedAndCoverTheCodespace()
    {
        ManagedUnicode.PropertyRangeStarts[0].Should().Be(0);
        ManagedUnicode.PropertyRangeStarts.Length.Should().Be(ManagedUnicode.PropertyRangeFlags.Length);
        IsStrictlyAscending(ManagedUnicode.PropertyRangeStarts).Should().BeTrue();
        IsStrictlyAscending(ManagedUnicode.LowercaseKeys).Should().BeTrue();
        IsStrictlyAscending(ManagedUnicode.DecompositionKeys).Should().BeTrue();
        ManagedUnicode.LowercaseKeys.Length.Should().Be(ManagedUnicode.LowercaseValues.Length);
        ManagedUnicode.DecompositionStarts.Length.Should().Be(ManagedUnicode.DecompositionKeys.Length + 1);
        ManagedUnicode.DecompositionStarts[^1].Should().Be(ManagedUnicode.DecompositionData.Length);
    }

    [Test]
    public void DefaultTokenizerFollowsRustIsAlphanumericNotDotNetLetterOrDigit()
    {
        // Devanagari vowel signs are Other_Alphabetic (Mc/Mn) and stay inside the word; the virama is
        // not Alphabetic, so Tantivy splits there. .NET's IsLetterOrDigit split at every vowel sign.
        Texts(ManagedFtsTokenization.Tokenize("हिन्दी", DefaultTokenizer)).Should().Equal("हिन", "दी");

        // Thai above-vowels and Arabic harakat are Other_Alphabetic as well.
        Texts(ManagedFtsTokenization.Tokenize("กิน", DefaultTokenizer)).Should().Equal("กิน");
        Texts(ManagedFtsTokenization.Tokenize("كَتَبَ", DefaultTokenizer)).Should().Equal("كَتَبَ");

        // No (other number) is Numeric, and circled letters are Other_Alphabetic.
        Texts(ManagedFtsTokenization.Tokenize("x² ½ Ⓐ", DefaultTokenizer)).Should().Equal("x²", "½", "ⓐ");
        Texts(ManagedFtsTokenization.Tokenize("x² ½ Ⓐ", SimpleTokenizer)).Should().Equal("x²", "½", "Ⓐ");
    }

    [Test]
    public void DefaultLowercasingIsRustFullPerCharacterMapping()
    {
        // U+0130 is the one unconditional multi-character lowercase mapping.
        Texts(ManagedFtsTokenization.Tokenize("İstanbul", DefaultTokenizer)).Should().Equal("i̇stanbul");

        // Final sigma is not contextual in Tantivy's LowerCaser.
        Texts(ManagedFtsTokenization.Tokenize("ΟΔΟΣ", DefaultTokenizer)).Should().Equal("οδοσ");

        // A Unicode 16.0 case pair (U+1C89/U+1C8A) is folded regardless of the host ICU version.
        Texts(ManagedFtsTokenization.Tokenize("Ᲊ", DefaultTokenizer)).Should().Equal("ᲊ");
    }

    [Test]
    public void DefaultRemoveLongFilterMeasuresTheSourceBeforeLowercasing()
    {
        // 19 x U+0130 is 38 UTF-8 bytes (kept by Tantivy's < 40 limit) but 57 bytes once lowercased.
        var dotted = new string('İ', 19);
        ManagedFtsTokenization.Tokenize(dotted, DefaultTokenizer).Should().ContainSingle();
        ManagedFtsTokenization.Tokenize(new string('a', 39), DefaultTokenizer).Should().ContainSingle();
        ManagedFtsTokenization.Tokenize(new string('a', 40), DefaultTokenizer).Should().BeEmpty();
    }

    [Test]
    public void WhitespaceTokenizerSplitsOnlyOnRustAsciiWhitespace()
    {
        Texts(ManagedFtsTokenization.Tokenize("a b c d\te\nf\fg\rh\vi", WhitespaceTokenizer))
            .Should().Equal("a b c", "d", "e", "f", "g", "h\vi");
    }

    [Test]
    public void Unicode61FoldingNeverDependsOnHostNormalization()
    {
        Texts(ManagedFtsTokenization.Tokenize("Café Crème Ångström", Unicode61Tokenizer))
            .Should().Equal("cafe", "creme", "angstrom");

        // Hangul syllables fold algorithmically to their jamo; an astral combining mark is stripped.
        Texts(ManagedFtsTokenization.Tokenize("한국", Unicode61Tokenizer))
            .Should().Equal("한국");
        Texts(ManagedFtsTokenization.Tokenize("a\U0001D167b", Unicode61Tokenizer)).Should().Equal("ab");

        ManagedFtsTokenization.NormalizeTerm("RÉSUMÉ", Unicode61Tokenizer).Should().Be("resume");
    }

    [Test]
    public void ManagedDecompositionAgreesWithHostIcuWhereverTheHostDecomposes()
    {
        if (AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant)
            Assert.Ignore("the host has no ICU normalization to compare against");

        // An older host ICU leaves characters it does not know undecomposed, so only compare code
        // points the host actually decomposes; those must agree with the pinned table exactly.
        var compared = 0;
        for (var codePoint = 0x80; codePoint <= 0x10FFFF; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF)
                continue;

            var text = char.ConvertFromUtf32(codePoint);
            string host;
            try
            {
                host = text.Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (host == text)
                continue;

            var expected = new StringBuilder();
            foreach (var rune in host.EnumerateRunes())
            {
                if (!ManagedUnicode.IsMark(rune.Value))
                    ManagedUnicode.AppendLowercase(expected, rune.Value);
            }

            ManagedUnicode.Fold(text).Should().Be(expected.ToString(), $"U+{codePoint:X4}");
            compared++;
        }

        compared.Should().BeGreaterThan(13_000);
    }

    [Test]
    public void TokenizationSourcesDoNotCallHostDependentUnicodeApis()
    {
        var searchDirectory = Path.Combine(ResolveRepositoryRoot(), "src", "Ahtola.Core", "Search");
        foreach (var fileName in new[] { "ManagedFts.cs", "ManagedFtsTokenization.cs", "ManagedUnicode.cs" })
        {
            var source = File.ReadAllText(Path.Combine(searchDirectory, fileName));
            source.Should().NotContain(".Normalize(", fileName);
            source.Should().NotContain("IsLetterOrDigit(rune", fileName);
            source.Should().NotContain("GetUnicodeCategory(", fileName);
            source.Should().NotContain("raw.ToLowerInvariant()", fileName);
            source.Should().NotContain("term.ToLowerInvariant()", fileName);
        }
    }

    [Test]
    public void PinnedTokenizationIsObservableThroughSql()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT, folded TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body);");
        Execute(connection, "CREATE INDEX docs_folded ON docs USING fts(folded) WITH (tokenizer = 'unicode61');");
        Execute(connection, "INSERT INTO docs VALUES (1, 'हिन्दी भाषा', 'Café'), (2, 'x² ½', 'naïve'), (3, 'İstanbul', 'plain');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'हिन') ORDER BY id;").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'x²') ORDER BY id;").Should().Equal(2);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'İSTANBUL') ORDER BY id;").Should().Equal(3);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(folded, 'cafe') ORDER BY id;").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(folded, 'NAIVE') ORDER BY id;").Should().Equal(2);
    }

    private static string[] Texts(IReadOnlyList<ManagedFtsToken> tokens)
        => tokens.Select(static token => token.Text).ToArray();

    private static bool IsStrictlyAscending(ReadOnlySpan<int> values)
    {
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] <= values[index - 1])
                return false;
        }

        return true;
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ahtola.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
