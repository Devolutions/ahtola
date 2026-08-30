using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Search;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>Planner selection, cost comparison, EXPLAIN evidence, and scalar fallback.</summary>
public sealed class ManagedIndexMethodPlannerTests
{
    [Test]
    public void ExplainQueryPlanReportsTheMethodIndexForAMatchPredicate()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        var detail = ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'term7');");

        detail.Should().StartWith("SEARCH docs USING INDEX METHOD fts INDEX docs_fts")
            .And.Contain("pattern=Match");
    }

    [Test]
    public void ExplainQueryPlanReportsScoreOrderedLimitPushdown()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        ExplainDetail(
                connection,
                "SELECT id FROM docs ORDER BY fts_score(title, body, 'term7') DESC LIMIT 3;")
            .Should().Contain("pattern=Score");

        // Filtering by match and ranking by relevance on the same call is the one shape whose
        // truncation order is the statement's own order, so the LIMIT may be pushed into the method.
        ExplainDetail(
                connection,
                "SELECT id FROM docs WHERE fts_match(title, body, 'term7') "
                + "ORDER BY fts_score(title, body, 'term7') DESC LIMIT 3;")
            .Should().Contain("pattern=CombinedOrderedLimit");
    }

    [Test]
    public void AnUnorderedMatchLimitUsesTheDedicatedStreamingShape()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // The method's unordered pattern does not rank before truncating, so a bare LIMIT can stream
        // directly without accidentally keeping the highest-scoring rows.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'term7') LIMIT 3;")
            .Should().Contain("pattern=MatchLimit");

        // ORDER BY id asks for the three lowest ids among *all* matches.
        ExplainDetail(
                connection,
                "SELECT id FROM docs WHERE fts_match(title, body, 'term7') ORDER BY id LIMIT 3;")
            .Should().Contain("pattern=Match").And.NotContain("pattern=MatchLimit");

        // The residual `id > 0` follows the match call, so it cannot short-circuit past it — the
        // scalar path always reaches `fts_match` first — and MatchLimit's own `!hasResidualPredicate`
        // requirement is unaffected by *where* a residual sits. The unlimited combined pattern still
        // filters and supplies scores, while the ordinary pipeline applies the residual and LIMIT.
        ExplainDetail(
                connection,
                "SELECT id FROM docs WHERE fts_match(title, body, 'term7') AND id > 0 "
                + "ORDER BY fts_score(title, body, 'term7') DESC LIMIT 3;")
            .Should().Contain("pattern=CombinedOrdered")
            .And.NotContain("pattern=MatchLimit")
            .And.NotContain("pattern=CombinedOrderedLimit");
    }

    [Test]
    public void ScoreOrderingWithoutALimitFallsBackBecauseItCannotDropUnrankedRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // Ranking never removes rows: ORDER BY score DESC still has to produce every base row, with
        // the non-matching ones ranked last. Without a LIMIT to truncate, the method has to walk the
        // whole table and is therefore never cheaper than the scan it would replace.
        ExplainDetail(connection, "SELECT id FROM docs ORDER BY fts_score(title, body, 'term7') DESC;")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ExplainQueryPlanAliasIsReported()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        ExplainDetail(connection, "SELECT d.id FROM docs AS d WHERE fts_match(d.title, d.body, 'term7');")
            .Should().Contain("SEARCH docs AS d USING INDEX METHOD fts");
    }

    [Test]
    public void ATinyTableFallsBackToAScanBecauseTheMethodIsNotCheaper()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (1, 'a', 'b');");

        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'b');")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ColumnSetsBindRegardlessOfArgumentOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // Only the (title, body) pair is indexed; a single-column call is a plain scalar call.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(body, 'term7');")
            .Should().NotContain("INDEX METHOD");
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(body, title, 'term7');")
            .Should().Contain("INDEX METHOD");
    }

    [Test]
    public void ARowDependentQueryArgumentIsNotPushedIntoTheMethod()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, title);")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void AnAscendingScoreOrderIsNotPushedDown()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        ExplainDetail(connection, "SELECT id FROM docs ORDER BY fts_score(title, body, 'term7') ASC LIMIT 3;")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void MethodAndScanPathsAgreeOnTheSelectedRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        var indexed = QueryIntegers(
            connection,
            "SELECT id FROM docs WHERE fts_match(title, body, 'term7') ORDER BY id;");
        Execute(connection, "DROP INDEX docs_fts;");
        var scanned = QueryIntegers(
            connection,
            "SELECT id FROM docs WHERE fts_match(title, body, 'term7') ORDER BY id;");

        indexed.Should().NotBeEmpty();
        indexed.Should().Equal(scanned);
    }

    [Test]
    public void SecondaryOrderTermsSuppressLimitPushdownButKeepTheRanking()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // A second ORDER BY term could reorder rows the method already truncated away, so the limit
        // cannot be pushed down. What is left is an unlimited ranking pattern, which has to produce
        // every base row and therefore loses to the scan.
        ExplainDetail(
                connection,
                "SELECT id FROM docs ORDER BY fts_score(title, body, 'term7') DESC, id LIMIT 2;")
            .Should().NotContain("INDEX METHOD");

        // The answer is unchanged either way.
        QueryIntegers(
                connection,
                "SELECT id FROM docs ORDER BY fts_score(title, body, 'term7') DESC, id LIMIT 2;")
            .Should().HaveCount(2);
    }

    [Test]
    public void CostEstimateIsReportedAndBoundedByTheLimit()
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 0)],
            []);
        var attachment = ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);
        using var cursor = attachment.Open(new ArrayManagedIndexSource());

        attachment.Definition.TryFindPattern(ManagedIndexPatternShape.Match, out var matchPattern).Should().BeTrue();
        attachment.Definition.TryFindPattern(ManagedIndexPatternShape.MatchLimit, out var limitedPattern).Should().BeTrue();
        var match = cursor.EstimateCost(new ManagedIndexMethodCostContext(matchPattern, 10_000, null, []));
        var limited = cursor.EstimateCost(new ManagedIndexMethodCostContext(limitedPattern, 10_000, 5, []));

        match.Should().NotBeNull();
        limited.Should().NotBeNull();
        limited!.Value.EstimatedRows.Should().BeLessThanOrEqualTo(match!.Value.EstimatedRows);
    }

    [Test]
    public void DeclaredPatternsAreOrderedMostSpecificFirst()
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 0)],
            []);
        var definition = ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration).Definition;

        definition.Patterns.Select(static pattern => pattern.Shape).Should().Equal(
            ManagedIndexPatternShape.Score,
            ManagedIndexPatternShape.CombinedOrderedLimit,
            ManagedIndexPatternShape.CombinedOrdered,
            ManagedIndexPatternShape.CombinedLimit,
            ManagedIndexPatternShape.Combined,
            ManagedIndexPatternShape.MatchLimit,
            ManagedIndexPatternShape.Match);
        definition.MvccSupport.Should().Be(ManagedIndexMethodMvccSupport.TransactionalBackingStore);
        definition.ResultsMaterialized.Should().BeFalse();
        definition.BackingBtree.Should().BeTrue();
        definition.StorageVersion.Should().Be(ManagedFtsIndexMethod.StateVersion);
    }

    [Test]
    public void RegistryResolvesFtsAndRejectsUnknownMethods()
    {
        ManagedIndexMethodRegistry.Names.Should().Contain("fts");
        ManagedIndexMethodRegistry.TryResolve("FTS", out var method).Should().BeTrue();
        method.Name.Should().Be("fts");

        // The vector method is registered now; an unregistered name still fails closed.
        ManagedIndexMethodRegistry.TryResolve("vector", out var vector).Should().BeTrue();
        vector.Name.Should().Be("vector");

        var act = () => ManagedIndexMethodRegistry.Resolve("hnsw");
        act.Should().Throw<EmbeddedSqlException>().WithMessage("no such index method: hnsw");
    }

    [Test]
    public void MvccEnsureRejectsUnsupportedAndReadOnlyMethods()
    {
        var unsupported = new ManagedIndexMethodDefinition(
            "x", "i", [], backingBtree: false, resultsMaterialized: false,
            ManagedIndexMethodMvccSupport.Unsupported, storageVersion: 1);
        var readOnly = new ManagedIndexMethodDefinition(
            "x", "i", [], backingBtree: false, resultsMaterialized: false,
            ManagedIndexMethodMvccSupport.ReadOnly, storageVersion: 1);
        var transactional = new ManagedIndexMethodDefinition(
            "x", "i", [], backingBtree: false, resultsMaterialized: false,
            ManagedIndexMethodMvccSupport.TransactionalBackingStore, storageVersion: 1);

        var write = () => ManagedIndexMethodMvcc.Ensure(unsupported, mvccEnabled: true, forWrite: false);
        write.Should().Throw<EmbeddedSqlException>().WithMessage("*does not support MVCC*");

        var readOnlyWrite = () => ManagedIndexMethodMvcc.Ensure(readOnly, mvccEnabled: true, forWrite: true);
        readOnlyWrite.Should().Throw<EmbeddedSqlException>().WithMessage("*is read-only in MVCC*");

        ManagedIndexMethodMvcc.Ensure(readOnly, mvccEnabled: true, forWrite: false);
        ManagedIndexMethodMvcc.Ensure(transactional, mvccEnabled: true, forWrite: true);
        ManagedIndexMethodMvcc.Ensure(unsupported, mvccEnabled: false, forWrite: true);
    }

    private static string ExplainDetail(EmbeddedConnection connection, string sql)
    {
        var rows = Query(connection, "EXPLAIN QUERY PLAN " + sql);
        return rows.Count == 0 ? string.Empty : rows[^1][3].AsText();
    }

    private static void SeedLargeCorpus(EmbeddedConnection connection)
    {
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "BEGIN;");
        for (var id = 1; id <= 400; id++)
        {
            Execute(
                connection,
                $"INSERT INTO docs(id, title, body) VALUES ({id}, 'title{id % 13}', 'term{id % 37} filler body text');");
        }

        Execute(connection, "COMMIT;");
    }
}
