using System.Text;
using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for two code-review findings against the FTS index method's planner and its
/// ranked/unranked row merge:
///
/// (A) The scalar (<c>NOT INDEXED</c>) evaluator short-circuits <c>AND</c> left-to-right, so a
/// residual predicate that precedes an <c>fts_match</c>/<c>fts_score</c> argument can keep that
/// argument from ever being evaluated. A method-index plan that always evaluates the argument up
/// front does not reproduce that short circuit, so it must decline instead.
///
/// (B) The fts index returns ranked hits already sorted by <c>(score DESC, rowid ASC)</c>, but rows
/// it did not rank still have to appear somewhere in the answer for <c>ORDER BY fts_score(...)</c>.
/// Appending every unranked row after every ranked row (regardless of score) answers a tied score
/// -- e.g. all-zero column weights -- differently from the scalar path's stable sort, which the
/// merge has to reproduce instead.
///
/// The corpus below -- 120 rows, with <c>'fox'</c> only in the bodies of rows 40, 80, and 120 -- is
/// the corpus originally used to reproduce both findings, and the row sets asserted below are the
/// recorded reproduction results, not incidental values. It is large enough that the cost model
/// actually prefers the method index over a scan (a handful of rows would not).
/// </summary>
public sealed class ManagedFtsPlannerReviewRegressionTests
{
    private static void SeedCorpus(EmbeddedConnection connection, string? weights = null)
    {
        Execute(connection, CreateDocuments);
        Execute(
            connection,
            weights is null
                ? CreateFtsIndex
                : $"CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (weights = '{weights}');");

        var builder = new StringBuilder("INSERT INTO docs(id, title, body) VALUES ");
        for (var id = 1; id <= 120; id++)
        {
            if (id > 1)
                builder.Append(", ");
            var hasFox = id % 40 == 0;
            builder.Append($"({id}, 'title {id}', '{(hasFox ? "fox " : string.Empty)}body {id}')");
        }

        builder.Append(';');
        Execute(connection, builder.ToString());
    }

    private static string ExplainDetail(EmbeddedConnection connection, string sql)
    {
        var rows = Query(connection, "EXPLAIN QUERY PLAN " + sql);
        return rows.Count == 0 ? string.Empty : rows[^1][3].AsText();
    }

    [Test]
    public void APrecedingResidualDeclinesTheMethodSoAnEmptyMatchArgumentIsNeverEvaluated()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);
        const string sql = "SELECT id FROM docs WHERE id < 0 AND fts_match(title, body, '') ORDER BY id;";
        const string scanSql =
            "SELECT id FROM docs NOT INDEXED WHERE id < 0 AND fts_match(title, body, '') ORDER BY id;";

        // id < 0 is the left AND operand and matches no row, so the scalar evaluator never reaches
        // (and never evaluates) the empty fts_match argument on its right: the result is [], not a
        // thrown "fts query is empty". The method plan has to decline here so it does not evaluate
        // that argument up front and raise where the scalar path would not have.
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");

        var indexed = QueryIntegers(connection, sql);
        var scanned = QueryIntegers(connection, scanSql);
        indexed.Should().BeEmpty();
        indexed.Should().Equal(scanned);
    }

    [Test]
    public void AResidualPredicateDeclinesScoreOrderingSoAnEmptyScoreArgumentIsNeverEvaluated()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);
        const string sql = "SELECT id FROM docs WHERE id < 0 ORDER BY fts_score(title, body, '') DESC LIMIT 3;";
        const string scanSql =
            "SELECT id FROM docs NOT INDEXED WHERE id < 0 ORDER BY fts_score(title, body, '') DESC LIMIT 3;";

        // ORDER BY only ever runs on rows that survived the whole WHERE clause. id < 0 matches no
        // row, so the scalar path never reaches fts_score's empty argument either. ScoreOrdered and
        // ScoreOrderedLimit must decline whenever a residual WHERE predicate could make that true,
        // rather than evaluating the argument regardless of whether any row reaches ORDER BY.
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");

        var indexed = QueryIntegers(connection, sql);
        var scanned = QueryIntegers(connection, scanSql);
        indexed.Should().BeEmpty();
        indexed.Should().Equal(scanned);
    }

    [Test]
    public void AResidualPredicateAfterTheMatchCallStillPlansAndAgreesWithTheScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        // fts_match is the left AND operand in both queries below, so the scalar path always
        // evaluates it first regardless of what the trailing id predicate says -- a residual after
        // the match call cannot short-circuit past it. The method plan is therefore free to keep
        // filtering by the match call (pattern=Match) and let the residual and ORDER BY run
        // normally over its output.
        const string emptyResultSql = "SELECT id FROM docs WHERE fts_match(title, body, 'fox') AND id < 0 ORDER BY id;";
        ExplainDetail(connection, emptyResultSql).Should().Contain("pattern=Match");
        var indexedEmpty = QueryIntegers(connection, emptyResultSql);
        var scannedEmpty = QueryIntegers(
            connection,
            "SELECT id FROM docs NOT INDEXED WHERE fts_match(title, body, 'fox') AND id < 0 ORDER BY id;");
        indexedEmpty.Should().BeEmpty();
        indexedEmpty.Should().Equal(scannedEmpty);

        const string nonEmptyResultSql = "SELECT id FROM docs WHERE fts_match(title, body, 'fox') AND id > 50 ORDER BY id;";
        ExplainDetail(connection, nonEmptyResultSql).Should().Contain("pattern=Match");
        var indexedNonEmpty = QueryIntegers(connection, nonEmptyResultSql);
        var scannedNonEmpty = QueryIntegers(
            connection,
            "SELECT id FROM docs NOT INDEXED WHERE fts_match(title, body, 'fox') AND id > 50 ORDER BY id;");
        indexedNonEmpty.Should().Equal(80, 120);
        indexedNonEmpty.Should().Equal(scannedNonEmpty);
    }

    [Test]
    public void AnEmptyMatchArgumentThatLeadsTheAndChainStillThrowsIdenticallyOnBothPaths()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        // Unlike the preceding-residual cases above, fts_match is the left AND operand here, so it
        // is unconditionally evaluated on the scalar path regardless of the trailing id < 0: the
        // empty query argument is invalid input, and both paths must raise the identical error
        // rather than the method path silently declining into a different (non-throwing) answer.
        var indexedError = ShouldThrow(
            connection,
            "SELECT id FROM docs WHERE fts_match(title, body, '') AND id < 0 ORDER BY id;");
        var scannedError = ShouldThrow(
            connection,
            "SELECT id FROM docs NOT INDEXED WHERE fts_match(title, body, '') AND id < 0 ORDER BY id;");

        indexedError.Message.Should().Be("fts query is empty");
        scannedError.Message.Should().Be("fts query is empty");
    }

    [Test]
    public void AllZeroColumnWeightsTieBreakByRowIdOnBothPathsAndAtAnyLimit()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection, "title=0,body=0");

        // Every row -- ranked or not -- scores exactly 0.0 with all-zero weights, so merging by
        // (score DESC, rowid ASC) ties everything and answers with the lowest rowids, exactly like
        // the scalar path's stable sort over ascending-rowid scan order. Emitting every ranked hit
        // before any unranked row (the pre-fix behavior) would instead answer with the fox-bearing
        // rows 40, 80, and 120 purely because the index happened to rank them, regardless of score.
        foreach (var (limit, expected) in new (int Limit, long[] Expected)[]
                 {
                     (3, [1, 2, 3]),
                     (10, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]),
                 })
        {
            var indexed = QueryIntegers(
                connection,
                $"SELECT id FROM docs ORDER BY fts_score(title, body, 'fox') DESC LIMIT {limit};");
            var scanned = QueryIntegers(
                connection,
                $"SELECT id FROM docs NOT INDEXED ORDER BY fts_score(title, body, 'fox') DESC LIMIT {limit};");

            indexed.Should().Equal(expected);
            indexed.Should().Equal(scanned);
        }
    }

    [Test]
    public void NonZeroWeightsRankIdenticallyOnBothPaths()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        // With real (default) weights the three fox-bearing rows outrank everything else and sort
        // among themselves by ascending rowid (40, 80, 120), followed by the lowest-rowid
        // non-matching rows, which all tie at 0.0 -- the same order the scalar path produces.
        var indexed = QueryIntegers(
            connection,
            "SELECT id FROM docs ORDER BY fts_score(title, body, 'fox') DESC LIMIT 5;");
        var scanned = QueryIntegers(
            connection,
            "SELECT id FROM docs NOT INDEXED ORDER BY fts_score(title, body, 'fox') DESC LIMIT 5;");

        indexed.Should().Equal(40, 80, 120, 1, 2);
        indexed.Should().Equal(scanned);
    }

    [Test]
    public void TheRankedScoreOrderedQueryActuallyUsesTheMethodIndex()
    {
        // The two tests above prove the merged answer matches the scan, but that agreement would be
        // vacuous if the planner had quietly declined the method index and both sides ran the same
        // scan. Pin the plan shape and the executed-scan counter so the merge logic in
        // GetMethodIndexRows is the code path actually being exercised, for both the non-zero-weight
        // ranking and the all-zero-weight tie-break.
        using (var database = new EmbeddedDatabase())
        {
            using var connection = database.Connect();
            SeedCorpus(connection);

            const string sql = "SELECT id FROM docs ORDER BY fts_score(title, body, 'fox') DESC LIMIT 5;";
            ExplainDetail(connection, sql).Should().Contain("pattern=ScoreOrderedLimit");

            var before = EmbeddedDatabase.MethodIndexScansExecuted;
            QueryIntegers(connection, sql).Should().HaveCount(5);
            EmbeddedDatabase.MethodIndexScansExecuted.Should().Be(before + 1);
        }

        using (var database = new EmbeddedDatabase())
        {
            using var connection = database.Connect();
            SeedCorpus(connection, "title=0,body=0");

            const string sql = "SELECT id FROM docs ORDER BY fts_score(title, body, 'fox') DESC LIMIT 3;";
            ExplainDetail(connection, sql).Should().Contain("pattern=ScoreOrderedLimit");

            var before = EmbeddedDatabase.MethodIndexScansExecuted;
            QueryIntegers(connection, sql).Should().Equal(1, 2, 3);
            EmbeddedDatabase.MethodIndexScansExecuted.Should().Be(before + 1);
        }
    }
}
