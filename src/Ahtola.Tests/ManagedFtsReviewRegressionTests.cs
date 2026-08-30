using System.Diagnostics;
using Ahtola.Core;
using Ahtola.Core.Search;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the managed FTS review: <c>detail = 'columns'</c> must answer column-specific
/// questions from stored metadata (or refuse them outright) instead of silently reporting no match,
/// a negated branch must never contribute relevance, and snippet extraction must stay linear on a
/// document made of one repeated term.
/// </summary>
public sealed class ManagedFtsReviewRegressionTests
{
    // -------------------------------------------------------------------------------------
    // detail = 'columns': stored metadata or an explicit refusal, never a wrong answer.
    // -------------------------------------------------------------------------------------

    [Test]
    public void AColumnFilteredTermIsFoundWhenTheTermAlsoOccursInAnUnselectedColumn()
    {
        var index = CreateIndex(2, ManagedFtsDetailLevel.Columns, ["title", "body"]);
        index.Upsert(1, [], [SqlValue.Text("cat"), SqlValue.Text("cat cat")]);
        index.Upsert(2, [], [SqlValue.Text("dog"), SqlValue.Text("cat")]);

        // Without per-column frequencies the posting for row 1 spans a selected and an unselected
        // column, positions are not recorded, and the count collapsed to zero: the row containing
        // the term in the requested column was reported as not matching at all.
        RowIds(index, "title:cat").Should().Equal(1);
        RowIds(index, "body:cat").Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    [Test]
    public void AColumnFilteredScoreUsesThePerColumnFrequencyRatherThanTheWholeDocumentCount()
    {
        var index = CreateIndex(2, ManagedFtsDetailLevel.Columns, ["title", "body"]);
        index.Upsert(1, [], [SqlValue.Text("cat"), SqlValue.Text("cat cat cat cat cat")]);
        index.Upsert(2, [], [SqlValue.Text("cat cat cat"), SqlValue.Text("zzz")]);

        // Row 2 has the term three times in `title`; row 1 has it once. A score that attributed the
        // whole-document frequency to the selected column would rank row 1 first.
        var hits = index.Search(Parse("title:cat"));
        hits.Select(static hit => hit.RowId).Should().Equal(2L, 1L);
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Test]
    public void AnAnchoredTermIsRefusedRatherThanAnsweredWrongWithoutPositions()
    {
        var index = CreateIndex(1, ManagedFtsDetailLevel.Columns, ["title"]);
        index.Upsert(1, [], [SqlValue.Text("cat dog")]);

        // Positions are what make "is this the first token" answerable. Reporting no match would be
        // a wrong answer, not a missing feature.
        var search = () => index.Search(Parse("^cat"));
        search.Should().Throw<EmbeddedSqlException>().WithMessage("*does not record positions*");
    }

    [Test]
    public void AnAnchoredTermStillWorksWithFullDetail()
    {
        var index = CreateIndex(1, ManagedFtsDetailLevel.Full, ["title"]);
        index.Upsert(1, [], [SqlValue.Text("cat dog")]);
        index.Upsert(2, [], [SqlValue.Text("dog cat")]);

        RowIds(index, "^cat").Should().Equal(1);
    }

    [Test]
    public void TursoMethodGrammarRejectsTheManagedAnchorExtension()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ManagedIndexMethodTestHarness.Execute(connection, ManagedIndexMethodTestHarness.CreateDocuments);
        ManagedIndexMethodTestHarness.Execute(
            connection,
            "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (detail = 'columns');");
        ManagedIndexMethodTestHarness.Execute(
            connection,
            "INSERT INTO docs(id, title, body) VALUES (1, 'cat dog', 'dog cat');");

        ManagedIndexMethodTestHarness
            .ShouldThrow(connection, "SELECT id FROM docs WHERE fts_match(title, body, '^cat');")
            .Message.Should().Contain("Expected an FTS term");
    }

    [Test]
    public void ColumnDetailAnswersAColumnFilteredMatchEndToEnd()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ManagedIndexMethodTestHarness.Execute(connection, ManagedIndexMethodTestHarness.CreateDocuments);
        ManagedIndexMethodTestHarness.Execute(
            connection,
            "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (detail = 'columns');");
        ManagedIndexMethodTestHarness.Execute(
            connection,
            """
            INSERT INTO docs(id, title, body) VALUES
              (1, 'cat', 'cat cat'),
              (2, 'dog', 'cat');
            """);

        ManagedIndexMethodTestHarness
            .QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'title:cat') ORDER BY id;")
            .Should().Equal(1L);
    }

    // -------------------------------------------------------------------------------------
    // Negated branches contribute no relevance.
    // -------------------------------------------------------------------------------------

    [Test]
    public void ANegatedBranchDoesNotAddToTheScoreOfARowThatSurvivesThroughAnotherBranch()
    {
        var index = CreateIndex(1, ManagedFtsDetailLevel.Full, ["body"]);

        // Equal document lengths and an equal `alpha` frequency, so BM25 length normalization
        // cannot explain any score difference: only a leaked contribution can.
        index.Upsert(1, [], [SqlValue.Text("alpha beta beta beta")]);
        index.Upsert(2, [], [SqlValue.Text("alpha zzz zzz zzz")]);
        index.Upsert(3, [], [SqlValue.Text("gamma delta delta delta")]);

        // Row 1 matches `alpha` and also matches the excluded `beta` branch. Sharing the query's
        // accumulator with the exclusion credited row 1 with beta's BM25 contribution, so the two
        // alpha rows no longer ranked equally.
        var scored = index.Search(Parse("alpha OR (NOT beta)"));
        var byRow = scored.ToDictionary(static hit => hit.RowId, static hit => hit.Score);

        byRow[1].Should().BeApproximately(byRow[2], 1e-12);
        byRow[3].Should().Be(0.0);
    }

    [Test]
    public void ADoublyNegatedBranchDoesNotLeakItsScoreEither()
    {
        var index = CreateIndex(1, ManagedFtsDetailLevel.Full, ["body"]);
        index.Upsert(1, [], [SqlValue.Text("alpha gamma gamma gamma")]);
        index.Upsert(2, [], [SqlValue.Text("alpha zzz zzz zzz")]);
        index.Upsert(3, [], [SqlValue.Text("alpha beta zzz zzz")]);

        // `beta NOT gamma` selects row 3, so the outer NOT keeps rows 1 and 2. Both terms inside the
        // negation are evaluated, and neither may reach the surviving rows' relevance.
        var scored = index.Search(Parse("alpha AND (NOT (beta NOT gamma))"));
        var byRow = scored.ToDictionary(static hit => hit.RowId, static hit => hit.Score);

        byRow.Keys.Should().BeEquivalentTo(new[] { 1L, 2L });
        byRow[1].Should().BeApproximately(byRow[2], 1e-12);
    }

    [Test]
    public void NegationStillFiltersTheRowSetExactly()
    {
        var index = CreateIndex(1, ManagedFtsDetailLevel.Full, ["body"]);
        index.Upsert(1, [], [SqlValue.Text("cat dog")]);
        index.Upsert(2, [], [SqlValue.Text("cat bird")]);
        index.Upsert(3, [], [SqlValue.Text("fish")]);

        RowIds(index, "cat NOT dog").Should().Equal(2);
        RowIds(index, "NOT cat").Should().Equal(3);
    }

    // -------------------------------------------------------------------------------------
    // Snippet extraction stays linear on a repeated-term document.
    // -------------------------------------------------------------------------------------

    [Test]
    public void SnippetMatchingStaysLinearOnALongRepeatedTermDocument()
    {
        // Every token is also a match, which is exactly the shape that made the token × span cross
        // product quadratic. Timing alone is flaky, so the assertion is a scaling ratio: doubling
        // the document must not quadruple the work.
        var small = Measure(6_000);
        var large = Measure(24_000);

        // Quadratic growth over a 4x input would be ~16x. A generous ceiling still separates the
        // linear sweep from the cross product without being timing-sensitive.
        (large / Math.Max(small, 1.0)).Should().BeLessThan(8.0);
    }

    [Test]
    public void SnippetStillSelectsTheDensestWindowAndWrapsMatches()
    {
        var text = "aaa bbb ccc ddd needle eee needle fff ggg";
        var snippet = ManagedFtsFunctions.Snippet(
            [
                SqlValue.Text(text),
                SqlValue.Text("needle"),
                SqlValue.Text("["),
                SqlValue.Text("]"),
                SqlValue.Text("…"),
                SqlValue.Integer(5),
            ]);

        snippet.AsText().Should().Contain("[needle]").And.Contain("…");
    }

    [Test]
    public void SnippetOverAWholeDocumentReproducesTheSourceExactly()
    {
        var text = "one two three";
        var snippet = ManagedFtsFunctions.Snippet(
            [
                SqlValue.Text(text),
                SqlValue.Text("nomatch"),
                SqlValue.Text("["),
                SqlValue.Text("]"),
                SqlValue.Text("…"),
                SqlValue.Integer(64),
            ]);

        snippet.AsText().Should().Be(text);
    }

    [Test]
    public void HighlightRefusesAnAbsurdNumberOfPrefixTerms()
    {
        var query = string.Join(
            " OR ",
            Enumerable
                .Range(0, ManagedFtsLimits.MaxHighlightPrefixTerms + 1)
                .Select(static index => $"t{index}*"));

        var highlight = () => ManagedFtsFunctions.Highlight(
            [SqlValue.Text("t1 t2 t3"), SqlValue.Text("["), SqlValue.Text("]"), SqlValue.Text(query)]);

        highlight.Should().Throw<EmbeddedSqlException>().WithMessage("*prefix terms*");
    }

    private static double Measure(int tokenCount)
    {
        var text = string.Join(' ', Enumerable.Repeat("needle", tokenCount));
        var arguments = new[]
        {
            SqlValue.Text(text),
            SqlValue.Text("needle"),
            SqlValue.Text("["),
            SqlValue.Text("]"),
            SqlValue.Text("…"),
            SqlValue.Integer(32),
        };

        // Warm up so JIT does not dominate the smaller measurement.
        ManagedFtsFunctions.Snippet(arguments);

        var stopwatch = Stopwatch.StartNew();
        ManagedFtsFunctions.Snippet(arguments);
        stopwatch.Stop();
        return Math.Max(stopwatch.Elapsed.TotalMilliseconds, 0.001);
    }

    private static ManagedFtsSearchIndex CreateIndex(
        int columnCount,
        ManagedFtsDetailLevel detail,
        IReadOnlyList<string> columnNames)
    {
        var weights = new double[columnCount];
        Array.Fill(weights, 1.0);
        var index = new ManagedFtsSearchIndex(columnCount, ManagedFtsTokenizerOptions.Default, weights, detail)
        {
            ColumnIndexResolver = name =>
            {
                for (var position = 0; position < columnNames.Count; position++)
                {
                    if (string.Equals(columnNames[position], name, StringComparison.OrdinalIgnoreCase))
                        return position;
                }

                return null;
            },
        };

        return index;
    }

    private static ManagedFtsNode Parse(string query)
        => ManagedFtsQueryLanguage.Parse(query, ManagedFtsTokenizerOptions.Default, static _ => true);

    private static IReadOnlyList<long> RowIds(ManagedFtsSearchIndex index, string query)
        => index.Search(Parse(query)).Select(static hit => hit.RowId).OrderBy(static rowId => rowId).ToArray();
}
