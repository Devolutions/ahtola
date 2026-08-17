using AwesomeAssertions;
using Ahtola.Core.Search;

namespace Ahtola.Tests;

public sealed class ManagedFtsIndexTests
{
    [Test]
    public void TokenizerNormalizesUnicodeAndRetainsSourceOffsets()
    {
        var tokens = ManagedFtsTokenizer.Tokenize("Crème brûlée; π=3.");

        tokens.Select(static token => token.Text).Should().Equal("creme", "brulee", "π", "3");
        tokens.Select(static token => (token.Offset, token.Length)).Should()
            .Equal((0, 5), (6, 6), (14, 1), (16, 1));
    }

    [Test]
    public void TokenizerKeepsDecomposedCombiningMarksInTheirWord()
    {
        var tokens = ManagedFtsTokenizer.Tokenize("a\u0301b");

        tokens.Should().ContainSingle().Which.Should().Be(new ManagedFtsToken("ab", 0, 3, 0));

        var index = new ManagedFtsIndex();
        index.Upsert(1, ["a\u0301b"]);
        Search(index, "áb").Should().Equal(1);
    }

    [Test]
    public void ParserBindsBooleanPhraseAndPrefixOperators()
    {
        var parsed = ManagedFtsQueryParser.Parse("\"quick brown\" OR fox* NOT lazy");

        parsed.Should().BeOfType<ManagedFtsOr>();
        var or = (ManagedFtsOr)parsed;
        or.Left.Should().BeOfType<ManagedFtsPhrase>();
        or.Right.Should().BeOfType<ManagedFtsAnd>();
    }

    [Test]
    public void IndexSupportsTermsPhrasesPrefixesAndReplacement()
    {
        var index = new ManagedFtsIndex();
        index.Upsert(1, ["The quick brown fox", "jumps"]);
        index.Upsert(2, ["quick fox brown", "sleeps"]);
        index.Upsert(3, ["foxtrot", null]);

        Search(index, "\"quick brown\"").Should().Equal(1);
        Search(index, "fox*").Should().Equal(1, 2, 3);
        Search(index, "quick NOT brown").Should().BeEmpty();

        index.Upsert(1, ["replaced document"]);
        Search(index, "quick OR fox*").Should().Equal(2, 3);
        index.Remove(2).Should().BeTrue();
        Search(index, "fox*").Should().Equal(3);
    }

    private static IReadOnlyList<long> Search(ManagedFtsIndex index, string query)
        => index.Search(ManagedFtsQueryParser.Parse(query))
            .Select(static match => match.RowId)
            .ToArray();
}
