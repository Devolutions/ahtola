using Ahtola.Core;
using Ahtola.Core.Search;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// The segmented posting store behind <see cref="ManagedFtsSearchIndex"/>: immutable segments
/// shared across forks, a copy-on-write overlay, tiered merges and one-pass bulk builds. Every
/// layout must answer exactly like a freshly built index over the same documents.
/// </summary>
public sealed class ManagedFtsSegmentStorageTests
{
    private static readonly string[] Vocabulary =
    [
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
        "lambda", "mu", "nu", "xi", "omicron", "pi", "rho", "sigma", "tau", "upsilon",
        "alphabet", "alphanumeric", "betamax", "gammaray", "deltoid",
    ];

    private static readonly string[] QueryTemplates =
    [
        "{0}", "{0} {1}", "{0} AND {1}", "{0} NOT {1}", "\"{0} {1}\"", "{2}*", "c0:{0}", "c1:{0} {1}^2",
        "({0} OR {1}) AND NOT {2}", "c0:\"{0} {1}\"",
    ];

    [TestCase(1)]
    [TestCase(20260925)]
    [TestCase(0x5EED)]
    public void SegmentedIndexAnswersExactlyLikeAFreshBuild(int seed)
    {
        var random = new Random(seed);
        var subject = CreateIndex(tinyThresholds: true);
        var model = new Dictionary<long, (string, string)>();
        var forks = new List<(ManagedFtsSearchIndex Index, Dictionary<long, (string, string)> Model)>();
        var maximumSegments = 0;

        for (var step = 0; step < 600; step++)
        {
            var (index, documents) = forks.Count > 0 && random.Next(4) == 0
                ? forks[random.Next(forks.Count)]
                : (subject, model);
            var operation = random.Next(100);
            if (operation < 60)
            {
                var rowId = random.Next(1, 90);
                var document = (Text(random), Text(random));
                index.Upsert(rowId, [], [SqlValue.Text(document.Item1), SqlValue.Text(document.Item2)]);
                documents[rowId] = document;
            }
            else if (operation < 85)
            {
                var rowId = random.Next(1, 90);
                index.Remove(rowId).Should().Be(documents.Remove(rowId));
            }
            else if (operation < 92 && forks.Count < 4)
            {
                forks.Add((index.Fork(), new Dictionary<long, (string, string)>(documents)));
            }
            else if (operation < 95)
            {
                index.Compact();
            }

            maximumSegments = Math.Max(maximumSegments, subject.SegmentCount);
            if (step % 25 == 0)
            {
                AssertEquivalent(subject, model, random);
                foreach (var (fork, forkModel) in forks)
                    AssertEquivalent(fork, forkModel, random);
            }
        }

        maximumSegments.Should().BeGreaterThan(1, "the tiny thresholds must exercise multiple segments");
        AssertEquivalent(subject, model, random);
        foreach (var (fork, forkModel) in forks)
            AssertEquivalent(fork, forkModel, random);
    }

    [Test]
    public void ForksAreIndependentInBothDirections()
    {
        var parent = CreateIndex(tinyThresholds: true);
        for (var rowId = 1; rowId <= 40; rowId++)
            parent.Upsert(rowId, [], [SqlValue.Text($"shared common{rowId % 3}"), SqlValue.Text("x")]);

        var child = parent.Fork();
        parent.Upsert(100, [], [SqlValue.Text("parentonly"), SqlValue.Text("x")]);
        parent.Remove(1);
        child.Upsert(200, [], [SqlValue.Text("childonly"), SqlValue.Text("x")]);
        child.Remove(2);
        child.Upsert(3, [], [SqlValue.Text("rewritten"), SqlValue.Text("x")]);

        RowIds(parent, "parentonly").Should().Equal(100);
        RowIds(parent, "childonly").Should().BeEmpty();
        RowIds(child, "childonly").Should().Equal(200);
        RowIds(child, "parentonly").Should().BeEmpty();
        RowIds(parent, "shared").Should().HaveCount(39).And.NotContain(1).And.Contain(2).And.Contain(3);
        RowIds(child, "shared").Should().HaveCount(38).And.Contain(1).And.NotContain(2).And.NotContain(3);
        RowIds(child, "rewritten").Should().Equal(3);
        parent.DocumentCount.Should().Be(40);
        child.DocumentCount.Should().Be(40);

        parent.Compact();
        RowIds(child, "shared").Should().HaveCount(38, "compacting one side never rewrites the other's view");
    }

    [Test]
    public void BulkLoadBuildsOneSegmentInAnyArrivalOrder()
    {
        var ordered = CreateIndex(tinyThresholds: true);
        var shuffled = CreateIndex(tinyThresholds: true);
        var random = new Random(7);
        var documents = Enumerable.Range(1, 300)
            .Select(rowId => ((long)rowId, Text(random), Text(random)))
            .ToArray();

        ordered.BeginBulkLoad();
        foreach (var (rowId, first, second) in documents)
            ordered.Upsert(rowId, [], [SqlValue.Text(first), SqlValue.Text(second)]);
        ordered.EndBulkLoad();

        shuffled.BeginBulkLoad();
        foreach (var (rowId, first, second) in documents.OrderBy(_ => random.Next()))
            shuffled.Upsert(rowId, [], [SqlValue.Text(first), SqlValue.Text(second)]);

        // A duplicate rowid mid-load falls back to ordinary upsert semantics.
        shuffled.Upsert(5, [], [SqlValue.Text(documents[4].Item2), SqlValue.Text(documents[4].Item3)]);
        shuffled.EndBulkLoad();

        ordered.SegmentCount.Should().Be(1);
        var model = documents.ToDictionary(static document => document.Item1, static document => (document.Item2, document.Item3));
        AssertEquivalent(ordered, model, random);
        AssertEquivalent(shuffled, model, random);
    }

    [Test]
    public void PrefixExpansionSkipsTermsThatOnlyDeletedDocumentsCarry()
    {
        var index = CreateIndex(tinyThresholds: true);
        for (var rowId = 1; rowId <= 30; rowId++)
            index.Upsert(rowId, [], [SqlValue.Text($"stale{rowId}"), SqlValue.Text("x")]);
        index.Upsert(100, [], [SqlValue.Text("stalelive"), SqlValue.Text("x")]);
        for (var rowId = 1; rowId <= 30; rowId++)
            index.Remove(rowId);

        var fork = index.Fork();
        RowIds(index, "stale*").Should().Equal(100);
        RowIds(fork, "stale*").Should().Equal(100);
        index.TombstonedPostings.Should().BeGreaterThan(0, "reads never compact shared state");
    }

    private static void AssertEquivalent(
        ManagedFtsSearchIndex subject,
        Dictionary<long, (string, string)> model,
        Random random)
    {
        var reference = CreateIndex(tinyThresholds: false);
        foreach (var (rowId, (first, second)) in model)
            reference.Upsert(rowId, [], [SqlValue.Text(first), SqlValue.Text(second)]);

        subject.DocumentCount.Should().Be(model.Count);
        subject.RowIds.Order().Should().Equal(model.Keys.Order());
        for (var query = 0; query < 12; query++)
        {
            var template = QueryTemplates[random.Next(QueryTemplates.Length)];
            var text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                template,
                Vocabulary[random.Next(Vocabulary.Length)],
                Vocabulary[random.Next(Vocabulary.Length)],
                Vocabulary[random.Next(Vocabulary.Length)] is { Length: > 3 } word ? word[..3] : "al");
            var parsed = Parse(text);
            subject.Search(parsed).Should().Equal(reference.Search(parsed), text);
            subject.Search(parsed, 3).Should().Equal(reference.Search(parsed, 3), text);
            if (model.Count > 0)
            {
                var rowId = model.Keys.ElementAt(random.Next(model.Count));
                subject.Matches(parsed, rowId).Should().Be(reference.Matches(parsed, rowId), text);
                subject.Score(parsed, rowId).Should().Be(reference.Score(parsed, rowId), text);
            }
        }
    }

    private static ManagedFtsSearchIndex CreateIndex(bool tinyThresholds)
    {
        var index = new ManagedFtsSearchIndex(2, ManagedFtsTokenizerOptions.Default, [1.0, 2.0])
        {
            ColumnIndexResolver = static name => name switch
            {
                "c0" => 0,
                "c1" => 1,
                _ => null,
            },
        };
        if (tinyThresholds)
        {
            index.OverlayFreezeThreshold = 8;
            index.MergeTierBasePostings = 8;
        }
        else
        {
            index.OverlayFreezeThreshold = int.MaxValue;
        }

        return index;
    }

    private static string Text(Random random)
    {
        var words = new string[random.Next(0, 7)];
        for (var index = 0; index < words.Length; index++)
            words[index] = Vocabulary[random.Next(Vocabulary.Length)];
        return string.Join(' ', words);
    }

    private static ManagedFtsNode Parse(string query)
        => ManagedFtsQueryLanguage.Parse(query, ManagedFtsTokenizerOptions.Default, static name => name is "c0" or "c1");

    private static IReadOnlyList<long> RowIds(ManagedFtsSearchIndex index, string query)
        => index.Search(Parse(query)).Select(static hit => hit.RowId).Order().ToArray();
}
