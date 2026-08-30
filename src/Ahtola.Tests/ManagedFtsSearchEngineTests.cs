using Ahtola.Core;
using Ahtola.Core.Search;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>Unit coverage for the managed postings engine, tokenizers, ranking and limits.</summary>
public sealed class ManagedFtsSearchEngineTests
{
    [Test]
    public void TokenizersProduceTheDocumentedTokenShapes()
    {
        Tokens("Crème brûlée; π=3", ManagedFtsTokenizerKind.Unicode61)
            .Should().Equal("creme", "brulee", "π", "3");
        Tokens("Hello, World!", ManagedFtsTokenizerKind.Default)
            .Should().Equal("hello", "world");
        Tokens("Hello, World!", ManagedFtsTokenizerKind.Simple)
            .Should().Equal("Hello", "World");
        Tokens("Crème brûlée", ManagedFtsTokenizerKind.Ascii)
            .Should().Equal("cr", "me", "br", "l", "e");
        Tokens("Hello, World!", ManagedFtsTokenizerKind.Whitespace)
            .Should().Equal("Hello,", "World!");
        Tokens("Hello, World!", ManagedFtsTokenizerKind.Raw)
            .Should().Equal("Hello, World!");
        Tokens("abcd", ManagedFtsTokenizerKind.Trigram)
            .Should().Equal("abc", "bcd");
    }

    [Test]
    public void TermsAreTruncatedToTheDocumentedMaximum()
    {
        var token = ManagedFtsTokenization
            .Tokenize(
                new string('a', ManagedFtsTokenization.MaxTermLength + 50),
                new ManagedFtsTokenizerOptions(ManagedFtsTokenizerKind.Unicode61))
            .Single();

        token.Text.Length.Should().Be(ManagedFtsTokenization.MaxTermLength);
    }

    [Test]
    public void PostingsRecordTermFrequencyPositionsAndColumns()
    {
        var index = CreateIndex(2);
        index.Upsert(1, [], [SqlValue.Text("alpha beta"), SqlValue.Text("beta beta gamma")]);

        index.DocumentCount.Should().Be(1);
        index.TermCount.Should().Be(3);
        index.Search(Parse(index, "alpha")).Select(static hit => hit.RowId).Should().Equal(1);
        index.Search(Parse(index, "\"beta gamma\"")).Select(static hit => hit.RowId).Should().Equal(1);
        index.Search(Parse(index, "\"alpha gamma\"")).Should().BeEmpty();
    }

    [Test]
    public void BooleanOperatorsMatchTheDocumentedSemantics()
    {
        var index = CreateIndex(1);
        index.Upsert(1, [], [SqlValue.Text("cat dog")]);
        index.Upsert(2, [], [SqlValue.Text("cat bird")]);
        index.Upsert(3, [], [SqlValue.Text("fish")]);

        RowIds(index, "cat AND dog").Should().Equal(1);
        RowIds(index, "cat OR fish").Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        RowIds(index, "cat NOT dog").Should().Equal(2);
        RowIds(index, "NOT cat").Should().Equal(3);
    }

    [Test]
    public void LegacyDoubledQuotesRemainTwoImplicitlyAndedPhrases()
    {
        var index = CreateIndex(1);
        index.Upsert(1, [], [SqlValue.Text("one x two")]);
        index.Upsert(2, [], [SqlValue.Text("one two")]);

        RowIds(index, "\"one\"\"two\"").Should().BeEquivalentTo(new[] { 1L, 2L });
        RowIds(index, "\"one two\"").Should().Equal(2);
    }

    [Test]
    public void LegacyGrammarDoesNotAdoptFts5PhraseConcatenation()
    {
        var index = CreateIndex(1);
        index.Upsert(1, [], [SqlValue.Text("one two")]);

        RowIds(index, "one + two").Should().BeEmpty();
        RowIds(index, "\"one two\"").Should().Equal(1);
    }

    [Test]
    public void RankingIsDeterministicWithRowidTieBreak()
    {
        var index = CreateIndex(1);
        index.Upsert(1, [], [SqlValue.Text("x")]);
        index.Upsert(2, [], [SqlValue.Text("x")]);
        index.Upsert(3, [], [SqlValue.Text("x x x x x")]);

        var hits = index.Search(Parse(index, "x"));
        hits.Select(static hit => hit.RowId).Should().Equal(3, 1, 2);
        hits.Should().Equal(index.Search(Parse(index, "x")));
    }

    [Test]
    public void ColumnWeightsScaleTheScore()
    {
        var weighted = new ManagedFtsSearchIndex(2, ManagedFtsTokenizerOptions.Default, [4.0, 1.0]);
        weighted.ColumnIndexResolver = static name => name == "a" ? 0 : name == "b" ? 1 : null;
        weighted.Upsert(1, [], [SqlValue.Text("target"), SqlValue.Text("filler")]);
        weighted.Upsert(2, [], [SqlValue.Text("filler"), SqlValue.Text("target")]);

        weighted.Search(ManagedFtsQueryLanguage.Parse("target", ManagedFtsTokenizerOptions.Default, static _ => true))
            .Select(static hit => hit.RowId).Should().Equal(1, 2);
    }

    [Test]
    public void DeletesTombstoneAndCompactionReclaimsThem()
    {
        var index = CreateIndex(1);
        for (var rowId = 1; rowId <= 10; rowId++)
            index.Upsert(rowId, [], [SqlValue.Text($"term{rowId} shared")]);

        var before = index.TotalPostings;
        before.Should().BeGreaterThan(0);

        for (var rowId = 1; rowId <= 5; rowId++)
            index.Remove(rowId);

        index.DocumentCount.Should().Be(5);
        index.TombstonedPostings.Should().BeGreaterThan(0);
        index.NeedsCompaction.Should().BeTrue();
        RowIds(index, "shared").Should().BeEquivalentTo(new[] { 6L, 7L, 8L, 9L, 10L });

        var reclaimed = index.Compact();
        reclaimed.Should().BeGreaterThan(0);
        index.TombstonedPostings.Should().Be(0);
        index.TotalPostings.Should().BeLessThan(before);
        RowIds(index, "shared").Should().BeEquivalentTo(new[] { 6L, 7L, 8L, 9L, 10L });
    }

    [Test]
    public void PrefixExpansionIsBoundedByTheDocumentedLimit()
    {
        var index = CreateIndex(1);
        for (var rowId = 1; rowId <= ManagedFtsLimits.MaxPrefixTerms + 5; rowId++)
            index.Upsert(rowId, [], [SqlValue.Text($"pfx{rowId}")]);

        var act = () => index.Search(Parse(index, "pfx*"));
        act.Should().Throw<EmbeddedSqlException>()
            .WithMessage($"*expands to more than {ManagedFtsLimits.MaxPrefixTerms} terms*");
    }

    [Test]
    public void QueryDepthAndTermCountAreBounded()
    {
        var deep = new string('(', ManagedFtsLimits.MaxQueryDepth + 5)
            + "a"
            + new string(')', ManagedFtsLimits.MaxQueryDepth + 5);
        var deepAct = () => ManagedFtsQueryLanguage.Parse(deep, ManagedFtsTokenizerOptions.Default, static _ => true);
        deepAct.Should().Throw<EmbeddedSqlException>().WithMessage("*nesting levels*");

        var wide = string.Join(" OR ", Enumerable.Range(0, ManagedFtsLimits.MaxQueryTerms + 5).Select(static i => $"t{i}"));
        var wideAct = () => ManagedFtsQueryLanguage.Parse(wide, ManagedFtsTokenizerOptions.Default, static _ => true);
        wideAct.Should().Throw<EmbeddedSqlException>().WithMessage("*terms*");
    }

    [Test]
    public void InvalidQueriesAreRejectedWithClearMessages()
    {
        var parse = (string query) => () => ManagedFtsQueryLanguage.Parse(
            query,
            ManagedFtsTokenizerOptions.Default,
            static name => name == "body");

        parse("").Should().Throw<EmbeddedSqlException>().WithMessage("fts query is empty");
        parse("\"unterminated").Should().Throw<EmbeddedSqlException>().WithMessage("*Unterminated*");
        parse("a**").Should().Throw<EmbeddedSqlException>().WithMessage("*only one trailing*");
        parse("missing:term").Should().Throw<EmbeddedSqlException>().WithMessage("no such fts column: missing");
        parse("NEAR/9999(a b)").Should().Throw<EmbeddedSqlException>().WithMessage("*NEAR distance*");
        parse("NEAR(a)").Should().Throw<EmbeddedSqlException>().WithMessage("*at least two terms*");
    }

    [Test]
    public void AnchoredTermsMatchOnlyTheFirstTokenOfAColumn()
    {
        var index = CreateIndex(1);
        index.Upsert(1, [], [SqlValue.Text("alpha beta")]);
        index.Upsert(2, [], [SqlValue.Text("beta alpha")]);

        RowIds(index, "^alpha").Should().Equal(1);
        RowIds(index, "^beta").Should().Equal(2);
    }

    [Test]
    public void HighlightAndSnippetPreserveTheOriginalText()
    {
        var highlighted = ManagedFtsFunctions.Highlight(
            [SqlValue.Text("Crème brûlée and more"), SqlValue.Text("<"), SqlValue.Text(">"), SqlValue.Text("creme")],
            new ManagedFtsTokenizerOptions(ManagedFtsTokenizerKind.Unicode61));
        highlighted.AsText().Should().Be("<Crème> brûlée and more");

        var snippet = ManagedFtsFunctions.Snippet(
        [
            SqlValue.Text("one two three four five six seven eight nine ten"),
            SqlValue.Text("eight"),
            SqlValue.Text("["),
            SqlValue.Text("]"),
            SqlValue.Text("..."),
            SqlValue.Integer(3),
        ]);
        snippet.AsText().Should().Contain("[eight]").And.StartWith("...");
    }

    [Test]
    public void NullArgumentsFollowPinnedScalarRules()
    {
        ManagedFtsFunctions.Match([SqlValue.Text("a"), SqlValue.Null], [null])
            .Should().Be(SqlValue.Integer(0));
        ManagedFtsFunctions.Score([SqlValue.Text("a"), SqlValue.Null], [null])
            .Should().Be(SqlValue.Real(0.0));
        ManagedFtsFunctions.Highlight([SqlValue.Text("a"), SqlValue.Null, SqlValue.Text(""), SqlValue.Text("")])
            .Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void DifferentialAgainstTheInMemoryOracle()
    {
        // The shipped fts5 in-memory index is the oracle for boolean matching semantics.
        var oracle = new ManagedFtsIndex();
        var engine = CreateIndex(1);
        var documents = new[]
        {
            "the quick brown fox",
            "lazy dog sleeps",
            "quick quick fox runs",
            "nothing to see",
        };

        for (var position = 0; position < documents.Length; position++)
        {
            oracle.Upsert(position + 1, [documents[position]]);
            engine.Upsert(position + 1, [], [SqlValue.Text(documents[position])]);
        }

        foreach (var query in new[] { "fox", "quick AND fox", "quick OR dog", "quick NOT fox", "fo*", "\"quick brown\"" })
        {
            var expected = oracle.Search(ManagedFtsQueryParser.Parse(query))
                .Select(static hit => hit.RowId)
                .OrderBy(static rowId => rowId)
                .ToArray();
            RowIds(engine, query).OrderBy(static rowId => rowId).Should().Equal(expected, $"query '{query}'");
        }
    }

    private static ManagedFtsSearchIndex CreateIndex(int columnCount)
    {
        var weights = new double[columnCount];
        Array.Fill(weights, 1.0);
        var index = new ManagedFtsSearchIndex(columnCount, ManagedFtsTokenizerOptions.Default, weights);
        index.ColumnIndexResolver = _ => null;
        return index;
    }

    private static ManagedFtsNode Parse(ManagedFtsSearchIndex index, string query)
        => ManagedFtsQueryLanguage.Parse(query, ManagedFtsTokenizerOptions.Default, static _ => true);

    private static IReadOnlyList<long> RowIds(ManagedFtsSearchIndex index, string query)
        => index.Search(Parse(index, query)).Select(static hit => hit.RowId).ToArray();

    private static IReadOnlyList<string> Tokens(string text, ManagedFtsTokenizerKind kind)
        => ManagedFtsTokenization
            .Tokenize(text, new ManagedFtsTokenizerOptions(kind))
            .Select(static token => token.Text)
            .ToArray();
}
